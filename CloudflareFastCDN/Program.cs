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

#if DEBUG
            string cfKey = File.ReadAllText("CLOUDFLARE_KEY.txt");
            string domains = File.ReadAllText("DOMAINS.txt");
            string pingThreads = "200";
            string maxIps = "400";
#else
            string cfKey = Environment.GetEnvironmentVariable("CLOUDFLARE_KEY");
            string domains = Environment.GetEnvironmentVariable("DOMAINS");
            string pingThreads = Environment.GetEnvironmentVariable("PING_THREADS");
            string maxIps = Environment.GetEnvironmentVariable("MAX_IPS");
#endif

            if (!isDocker && string.IsNullOrWhiteSpace(cfKey))
            {
                //从命令行中读取并将其添加到一个dict中，以供读取 命令行例子：--CLOUDFLARE_KEY=你的CFKEY
                Dictionary<string, string> parameters = ParseCommandLineArgs(args);

                parameters.TryGetValue("CLOUDFLARE_KEY", out cfKey);
                parameters.TryGetValue("DOMAINS", out domains);
                parameters.TryGetValue("PING_THREADS", out pingThreads);
                parameters.TryGetValue("MAX_IPS", out maxIps);
            }

            if (string.IsNullOrWhiteSpace(cfKey))
            {
                Console.WriteLine("缺少CFKEY");
                return;
            }
            if (string.IsNullOrWhiteSpace(domains))
            {
                Console.WriteLine("缺少DOMAINS");
                return;
            }

            AppConfig.CloudflareKey = cfKey;
            AppConfig.Domains = domains.Split(',');

            if (int.TryParse(pingThreads, out int pingThreadsInt))
            {
                AppConfig.PingThreads = pingThreadsInt;
                if (AppConfig.PingThreads == 0)
                {
                    AppConfig.PingThreads = 1;
                }
            }
            else
            {
                AppConfig.PingThreads = 16;
            }

            if (int.TryParse(maxIps, out int maxIpsInt))
            {
                AppConfig.MaxIps = maxIpsInt;
                if (AppConfig.MaxIps == 0)
                {
                    AppConfig.MaxIps = 400;
                }
            }
            else
            {
                AppConfig.MaxIps = 400;
            }



            //docker 执行？    
            while (true)
            {
                //30分钟执行一次
                await SingleSelect();
                await Task.Delay(1000 * 60 * 30);
            }
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
        private static T[] SelectEvenlyDistributed<T>(T[] sourceArray, int count)
        {
            if (count >= sourceArray.Length)
                return sourceArray;

            T[] result = new T[count];
            double step = (double)(sourceArray.Length - 1) / (count - 1);

            for (int i = 0; i < count; i++)
            {
                int index = (int)Math.Round(i * step);
                result[i] = sourceArray[index];
            }

            return result;
        }

        private static async Task SingleSelect()
        {
            var processor = new IPProcessor();

            var ipAddresses = processor.LoadIPRanges();


            ipAddresses = SelectEvenlyDistributed(ipAddresses.ToArray(), AppConfig.MaxIps).ToList();

            //TODO 精简检测的IP数量

            IcmpPing task = new IcmpPing(ipAddresses);
            // 先全部执行4ping
            var a = task.RunAsync().Result;
            var topPings = a.Where(a => a.Sended == 4 && a.Received == a.Sended).ToList();
            var top100Pings = topPings.Take(100);

            // 对前100再次10ping
            IcmpPing task100 = new IcmpPing(top100Pings.Select(a => a.IP).ToList(), 10);
            var b = task100.RunAsync().Result;
            var top100PingsSelect = b.Where(a => a.Sended == 10 && a.Received == a.Sended).ToList();

            // 再进行一次 20ping
            IcmpPing taskLast = new IcmpPing(top100PingsSelect.Select(a => a.IP).ToList(), 20);
            var c = taskLast.RunAsync().Result;
            var topLastPingsSelect = c.Where(a => a.Sended == 20 && a.Received == a.Sended).ToList();

            //检查看看前5个是不是通的
            int count = 0;
            List<PingData> top5List = new List<PingData>();
            foreach (var ip in topLastPingsSelect)
            {
                count++;
                var httpPing = new Httping();

                var pingResult = await httpPing.SinglePing(ip.IP);
                Console.WriteLine($"最终结果 {count} {ip.IP} {pingResult.success} {pingResult.delay.TotalMilliseconds}ms");
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

            Console.WriteLine("执行完毕");

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
