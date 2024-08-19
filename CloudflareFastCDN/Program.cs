using CloudflareFastCDN.Services;
using CloudflareFastCDN.Utils;
using System.Net;


namespace CloudflareFastCDN
{
    internal class Program
    {
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
            AppConfig.PingThreads = ParseIntWithDefault(config.PingThreads, 16);
            AppConfig.MaxIps = ParseIntWithDefault(config.MaxIps, 400);
            AppConfig.RunMinutes = ParseIntWithDefault(config.RunMinutes, 30);

            while (true)
            {
                await SingleSelect();
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟");
                await Task.Delay(TimeSpan.FromMinutes(AppConfig.RunMinutes));
            }
        }

        static (string CloudflareKey, string Domains, string PingThreads, string MaxIps, string RunMinutes) LoadConfiguration(string[] args, bool isDocker)
        {
            string cfKey, domains, pingThreads, maxIps, runMinutes;

#if DEBUG
            cfKey = File.ReadAllText("CLOUDFLARE_KEY.txt");
            domains = File.ReadAllText("DOMAINS.txt");
            pingThreads = "16";
            maxIps = "400";
            runMinutes = "30";
#else
    cfKey = Environment.GetEnvironmentVariable("CLOUDFLARE_KEY");
    domains = Environment.GetEnvironmentVariable("DOMAINS");
    pingThreads = Environment.GetEnvironmentVariable("PING_THREADS");
    maxIps = Environment.GetEnvironmentVariable("MAX_IPS");
    runMinutes = Environment.GetEnvironmentVariable("RUN_MINUTES");
#endif

            if (!isDocker && string.IsNullOrWhiteSpace(cfKey))
            {
                var parameters = ParseCommandLineArgs(args);
                cfKey = parameters.GetValueOrDefault("CLOUDFLARE_KEY", cfKey);
                domains = parameters.GetValueOrDefault("DOMAINS", domains);
                pingThreads = parameters.GetValueOrDefault("PING_THREADS", pingThreads);
                maxIps = parameters.GetValueOrDefault("MAX_IPS", maxIps);
                runMinutes = parameters.GetValueOrDefault("RUN_MINUTES", runMinutes);
            }

            return (cfKey, domains, pingThreads, maxIps, runMinutes);
        }



        static int ParseIntWithDefault(string value, int defaultValue)
        {
            if (int.TryParse(value, out int result))
            {
                return result == 0 ? defaultValue : result;
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

            var ipAddresses = processor.LoadIPRanges();


            ipAddresses = SampleData(ipAddresses, AppConfig.MaxIps);

            //TODO 精简检测的IP数量

            IcmpPing task = new IcmpPing(ipAddresses);
            Console.WriteLine($"开始第1轮检查：全量4次Ping");
            // 先全部执行4ping
            var a = task.RunAsync().Result;
            var topPings = a.Where(a => a.Sended == 4 && a.Received == a.Sended).ToList();
            var top100Pings = topPings.Take(100);

            Console.WriteLine($"开始第2轮检查：Top100,10次Ping");
            // 对前100再次10ping
            IcmpPing task100 = new IcmpPing(top100Pings.Select(a => a.IP).ToList(), 10);
            var b = task100.RunAsync().Result;
            var top100PingsSelect = b.Where(a => a.Sended == 10 && a.Received == a.Sended).ToList();

            Console.WriteLine($"开始第3轮检查：Top100精选后,20次Ping");
            // 再进行一次 20ping
            IcmpPing taskLast = new IcmpPing(top100PingsSelect.Select(a => a.IP).ToList(), 20);
            var c = taskLast.RunAsync().Result;
            var topLastPingsSelect = c.Where(a => a.Sended == 20 && a.Received == a.Sended).ToList();

            //检查看看前5个是不是通的
            Console.WriteLine($"开始最终检查：HTTP协议是否通畅");
            int count = 0;
            List<PingData> top5List = new List<PingData>();
            foreach (var ip in topLastPingsSelect)
            {
                count++;
                var httpPing = new Httping();

                var pingResult = await httpPing.SinglePing(ip.IP);
                Console.WriteLine($"最终结果 [{count}] {ip.IP} HTTP畅通：{pingResult.success} HTTP延时：{pingResult.delay.TotalMilliseconds}ms");
                if (pingResult.success)
                {
                    ip.Delay = pingResult.delay;
                    top5List.Add(ip);
                }

                if (count >= 5)
                {
                    break;
                }
            }

            //在其中选择一个最好的结果
            top5List.Sort((a, b) => a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));

            PingData top1Data = top5List.FirstOrDefault();

            if (top1Data != null)
            {
                //执行更新DNS
                foreach (var domain in AppConfig.Domains)
                {
                    try
                    {
                        Console.WriteLine($"开始更新域名 {domain} {top1Data.IP}");
                        await CloudflareAPIManager.Instance.AddOrUpdateARecord(domain, top1Data.IP.ToString());
                        Console.WriteLine($"已完成更新域名 {domain}");
                    }
                    catch (Exception ex) 
                    {
                        Console.WriteLine(ex.ToString());
                    }
                    
                }
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
