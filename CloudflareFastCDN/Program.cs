using CloudflareFastCDN.Services;
using CloudflareFastCDN.Utils;
using System.Net;


namespace CloudflareFastCDN
{
    internal class Program
    {
        private const int FirstRoundPingCount = 4;
        private const int SecondRoundPingCount = 10;
        private const int SecondRoundMaxPacketLoss = 1;
        private const int HttpCandidateCount = 10;
        private const int HttpProbeCount = 3;
        private const int HttpMinSuccessCount = 2;

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }


        static async Task MainAsync(string[] args)
        {
            bool isDocker = File.Exists("/.dockerenv");
            var config = LoadConfiguration(args, isDocker);

            if (string.IsNullOrWhiteSpace(config.CloudflareKey))
            {
                Console.WriteLine("缺少CFKEY");
                return;
            }
            if (string.IsNullOrWhiteSpace(config.Domains))
            {
                Console.WriteLine("缺少DOMAINS");
                return;
            }

            AppConfig.CloudflareKey = config.CloudflareKey;
            AppConfig.Domains = config.Domains.Split(',');
            AppConfig.PingThreads = ParseWithDefault(config.PingThreads, 16);
            AppConfig.MaxIps = ParseWithDefault(config.MaxIps, 400);
            AppConfig.PingIntervalMs = ParseWithDefault(config.PingIntervalMs, 150);
            AppConfig.HttpProbeUrl = string.IsNullOrWhiteSpace(config.HttpProbeUrl) ? "https://www.visa.cn/" : config.HttpProbeUrl.Trim();
            AppConfig.RunMinutes = ParseWithDefault(config.RunMinutes, 30);
            AppConfig.UpdateIPList = ParseWithDefault(config.UpdateIPList, false);

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            while (true)
            {
                await SingleSelect();
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟");
                await Task.Delay(TimeSpan.FromMinutes(AppConfig.RunMinutes));
            }
        }

        static (string CloudflareKey, string Domains, string PingThreads, string MaxIps, string PingIntervalMs, string HttpProbeUrl, string RunMinutes, string UpdateIPList) LoadConfiguration(string[] args, bool isDocker)
        {
            string cfKey, domains, pingThreads, maxIps, pingIntervalMs, httpProbeUrl, runMinutes, updateIPList;

#if DEBUG
            cfKey = File.ReadAllText("CLOUDFLARE_KEY.txt");
            domains = File.ReadAllText("DOMAINS.txt");
            pingThreads = "16";
            maxIps = "400";
            pingIntervalMs = "150";
            httpProbeUrl = "https://www.visa.cn/";
            runMinutes = "30";
            updateIPList = "false";
#else
    cfKey = Environment.GetEnvironmentVariable("CLOUDFLARE_KEY");
    domains = Environment.GetEnvironmentVariable("DOMAINS");
    pingThreads = Environment.GetEnvironmentVariable("PING_THREADS");
    maxIps = Environment.GetEnvironmentVariable("MAX_IPS");
    pingIntervalMs = Environment.GetEnvironmentVariable("PING_INTERVAL_MS");
    httpProbeUrl = Environment.GetEnvironmentVariable("HTTP_PROBE_URL");
    runMinutes = Environment.GetEnvironmentVariable("RUN_MINUTES");
    updateIPList = Environment.GetEnvironmentVariable("UPDATE_IP_LIST");
#endif

            if (!isDocker && string.IsNullOrWhiteSpace(cfKey))
            {
                var parameters = ParseCommandLineArgs(args);
                cfKey = parameters.GetValueOrDefault("CLOUDFLARE_KEY", cfKey);
                domains = parameters.GetValueOrDefault("DOMAINS", domains);
                pingThreads = parameters.GetValueOrDefault("PING_THREADS", pingThreads);
                maxIps = parameters.GetValueOrDefault("MAX_IPS", maxIps);
                pingIntervalMs = parameters.GetValueOrDefault("PING_INTERVAL_MS", pingIntervalMs);
                httpProbeUrl = parameters.GetValueOrDefault("HTTP_PROBE_URL", httpProbeUrl);
                runMinutes = parameters.GetValueOrDefault("RUN_MINUTES", runMinutes);
                updateIPList = parameters.GetValueOrDefault("UPDATE_IP_LIST", updateIPList);
            }

            return (cfKey, domains, pingThreads, maxIps, pingIntervalMs, httpProbeUrl, runMinutes, updateIPList);
        }



        static T ParseWithDefault<T>(string value, T defaultValue)
        {
            if (typeof(T) == typeof(int))
            {
                if (int.TryParse(value, out int intResult))
                {
                    return (T)(object)(intResult == 0 ? (int)(object)defaultValue : intResult);
                }
            }
            else if (typeof(T) == typeof(bool))
            {
                if (bool.TryParse(value, out bool boolResult))
                {
                    return (T)(object)boolResult;
                }
            }
            return defaultValue;
        }

        private static Dictionary<string, string> ParseCommandLineArgs(string[] args)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>();

            foreach (string arg in args)
            {
                if (arg.StartsWith("--"))
                {
                    string[] splitArg = arg.Substring(2).Split('=');
                    if (splitArg.Length == 2)
                    {
                        parameters[splitArg[0]] = splitArg[1];
                    }
                }
            }

            return parameters;
        }

        /// <summary>
        /// 均匀取出
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sourceArray"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public static List<T> SampleData<T>(IList<T> sourceData, int sampleSize)
        {
            if (sampleSize >= sourceData.Count)
                return new List<T>(sourceData);

            Random random = new Random();
            List<T> shuffledData = new List<T>(sourceData);

            // Fisher-Yates 洗牌算法
            for (int i = shuffledData.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T temp = shuffledData[i];
                shuffledData[i] = shuffledData[j];
                shuffledData[j] = temp;
            }

            return shuffledData.Take(sampleSize).ToList();
        }

        private static async Task SingleSelect()
        {
            var processor = new IPProcessor();

            //更新IP


            var ipAddresses = processor.LoadIPRanges();


            ipAddresses = SampleData(ipAddresses, AppConfig.MaxIps);

            //TODO 精简检测的IP数量

            IcmpPing task = new IcmpPing(ipAddresses, FirstRoundPingCount);
            Console.WriteLine($"开始第1轮检查：全量{FirstRoundPingCount}次Ping");
            // 先全部执行首轮 ping
            var a = await task.RunAsync();
            var topPings = a.Where(a => a.Sended == FirstRoundPingCount && a.Received == a.Sended).ToList();
            var top100Pings = topPings.Take(100);

            Console.WriteLine($"开始第2轮检查：Top100,{SecondRoundPingCount}次Ping，允许最多丢包{SecondRoundMaxPacketLoss}/{SecondRoundPingCount}");
            // 对前100再次 10 ping
            IcmpPing task100 = new IcmpPing(top100Pings.Select(a => a.IP).ToList(), SecondRoundPingCount);
            var b = await task100.RunAsync();
            var top100PingsSelect = b
                .Where(a => a.Sended == SecondRoundPingCount && a.Sended - a.Received <= SecondRoundMaxPacketLoss)
                .ToList();

            var httpCandidates = top100PingsSelect.Take(HttpCandidateCount).ToList();
            if (!httpCandidates.Any())
            {
                Console.WriteLine("第二轮后没有IP进入HTTP验证阶段");
                return;
            }

            Console.WriteLine($"开始最终检查：前{HttpCandidateCount}个候选IP进行HTTP验证，每个IP检测{HttpProbeCount}次，至少成功{HttpMinSuccessCount}次");
            int count = 0;
            List<PingData> topHttpList = new List<PingData>();
            var httpPing = new Httping();
            foreach (var ip in httpCandidates)
            {
                count++;
                var pingResult = await httpPing.Ping(ip.IP, HttpProbeCount);
                var averageDelay = pingResult.success > 0
                    ? TimeSpan.FromMilliseconds(pingResult.totalDelay.TotalMilliseconds / pingResult.success)
                    : TimeSpan.Zero;

                Console.WriteLine($"最终结果 [{count}] {ip.IP} HTTP成功：{pingResult.success}/{HttpProbeCount} HTTP均延时：{averageDelay.TotalMilliseconds}ms");
                if (pingResult.success >= HttpMinSuccessCount)
                {
                    ip.Delay = averageDelay;
                    topHttpList.Add(ip);
                }
            }

            //在其中选择一个最好的结果
            topHttpList.Sort((a, b) => a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));

            PingData top1Data = topHttpList.FirstOrDefault();

            if (top1Data != null)
            {
                //执行更新DNS
                foreach (var domain in AppConfig.Domains)
                {
                    try
                    {
                        Console.WriteLine($"开始更新域名 {domain} {top1Data.IP}");
                        var updated = await CloudflareAPIManager.Instance.AddOrUpdateARecord(domain, top1Data.IP.ToString());
                        if (updated)
                        {
                            Console.WriteLine($"已完成更新域名 {domain}");
                        }
                        else
                        {
                            Console.WriteLine($"更新域名失败 {domain}");
                        }
                    }
                    catch (Exception ex) 
                    {
                        Console.WriteLine(ex.ToString());
                    }
                    
                }
            }
            else
            {
                Console.WriteLine("最终阶段没有IP通过HTTP验证，跳过DNS更新");
            }

            Console.WriteLine("单次执行完毕");

            //取前100个进行http测试
            //foreach (var ip in topPings)
            //{
            //    var ping = new Httping();
            //    var bn = ping.Ping(ip.IP).Result;
            //    Console.WriteLine($"{ip.IP} TCP延时{ip.Delay.TotalMilliseconds} HTTP延时{bn.Item2.TotalMilliseconds} HTTP成功{bn.Item1}");
            //}
        }

    }
}
