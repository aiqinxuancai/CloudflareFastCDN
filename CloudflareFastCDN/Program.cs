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
        private const int SubnetProbePingCount = 2;
        private const int SubnetSampleCount = 10;
        private static readonly TimeSpan SupplementalHttpCheckInterval = TimeSpan.FromMinutes(5);
        private const int SupplementalHttpCheckCount = 3;

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

            AppConfig.CloudflareKey = config.CloudflareKey!;
            AppConfig.Domains = config.Domains!.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToArray();
            AppConfig.Domains2 = string.IsNullOrWhiteSpace(config.Domains2) ? null : config.Domains2.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToArray();
            AppConfig.Domains3 = string.IsNullOrWhiteSpace(config.Domains3) ? null : config.Domains3.Split(',').Select(d => d.Trim()).Where(d => !string.IsNullOrEmpty(d)).ToArray();
            AppConfig.PingThreads = ParseWithDefault(config.PingThreads, 8);
            AppConfig.MaxIps = ParseWithDefault(config.MaxIps, 400);
            AppConfig.PingIntervalMs = ParseWithDefault(config.PingIntervalMs, 150);
            AppConfig.HttpProbeUrl = string.IsNullOrWhiteSpace(config.HttpProbeUrl) ? "https://www.visa.cn/" : config.HttpProbeUrl.Trim();
            AppConfig.HttpProbeTimeoutMs = ParsePositiveIntWithDefault(config.HttpProbeTimeoutMs, 4000);
            AppConfig.HttpSpeedTestTimeoutMs = ParsePositiveIntWithDefault(config.HttpSpeedTestTimeoutMs, 10000);
            AppConfig.HttpSpeedTestIdleTimeoutMs = ParsePositiveIntWithDefault(config.HttpSpeedTestIdleTimeoutMs, 3000);
            AppConfig.RunMinutes = ParseWithDefault(config.RunMinutes, 30);
            AppConfig.BandwidthPriority = ParseWithDefault(config.BandwidthPriority, false);
            AppConfig.UpdateIPList = ParseWithDefault(config.UpdateIPList, false);
            AppConfig.EnableSupplementalHttpCheck = ParseWithDefault(config.EnableSupplementalHttpCheck, false);

            PrintStartupConfiguration(isDocker);

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            while (true)
            {
                var selectedIp = await SingleSelect();
                await WaitForNextSelection(selectedIp);
            }
        }

        static (string? CloudflareKey, string? Domains, string? Domains2, string? Domains3, string? PingThreads, string? MaxIps, string? PingIntervalMs, string? HttpProbeUrl, string? HttpProbeTimeoutMs, string? HttpSpeedTestTimeoutMs, string? HttpSpeedTestIdleTimeoutMs, string? RunMinutes, string? BandwidthPriority, string? UpdateIPList, string? EnableSupplementalHttpCheck) LoadConfiguration(string[] args, bool isDocker)
        {
            string? cfKey, domains, domains2, domains3, pingThreads, maxIps, pingIntervalMs, httpProbeUrl, httpProbeTimeoutMs, httpSpeedTestTimeoutMs, httpSpeedTestIdleTimeoutMs, runMinutes, bandwidthPriority, updateIPList, enableSupplementalHttpCheck;

#if DEBUG
            cfKey = File.ReadAllText("CLOUDFLARE_KEY.txt");
            domains = File.ReadAllText("DOMAINS.txt");
            domains2 = null;
            domains3 = null;
            pingThreads = "8";
            maxIps = "400";
            pingIntervalMs = "150";
            httpProbeUrl = "https://www.visa.cn/";
            httpProbeTimeoutMs = "4000";
            httpSpeedTestTimeoutMs = "10000";
            httpSpeedTestIdleTimeoutMs = "3000";
            runMinutes = "30";
            bandwidthPriority = "false";
            updateIPList = "false";
            enableSupplementalHttpCheck = "false";
#else
    cfKey = Environment.GetEnvironmentVariable("CLOUDFLARE_KEY");
    domains = Environment.GetEnvironmentVariable("DOMAINS");
    domains2 = Environment.GetEnvironmentVariable("DOMAINS2");
    domains3 = Environment.GetEnvironmentVariable("DOMAINS3");
    pingThreads = Environment.GetEnvironmentVariable("PING_THREADS");
    maxIps = Environment.GetEnvironmentVariable("MAX_IPS");
    pingIntervalMs = Environment.GetEnvironmentVariable("PING_INTERVAL_MS");
    httpProbeUrl = Environment.GetEnvironmentVariable("HTTP_PROBE_URL");
    httpProbeTimeoutMs = Environment.GetEnvironmentVariable("HTTP_PROBE_TIMEOUT_MS");
    httpSpeedTestTimeoutMs = Environment.GetEnvironmentVariable("HTTP_SPEEDTEST_TIMEOUT_MS");
    httpSpeedTestIdleTimeoutMs = Environment.GetEnvironmentVariable("HTTP_SPEEDTEST_IDLE_TIMEOUT_MS");
    runMinutes = Environment.GetEnvironmentVariable("RUN_MINUTES");
    bandwidthPriority = Environment.GetEnvironmentVariable("BANDWIDTH_PRIORITY");
    updateIPList = Environment.GetEnvironmentVariable("UPDATE_IP_LIST");
    enableSupplementalHttpCheck = Environment.GetEnvironmentVariable("ENABLE_SUPPLEMENTAL_HTTP_CHECK");
#endif

            if (!isDocker && string.IsNullOrWhiteSpace(cfKey))
            {
                var parameters = ParseCommandLineArgs(args);
                cfKey = parameters.GetValueOrDefault("CLOUDFLARE_KEY", cfKey);
                domains = parameters.GetValueOrDefault("DOMAINS", domains);
                domains2 = parameters.GetValueOrDefault("DOMAINS2", domains2);
                domains3 = parameters.GetValueOrDefault("DOMAINS3", domains3);
                pingThreads = parameters.GetValueOrDefault("PING_THREADS", pingThreads);
                maxIps = parameters.GetValueOrDefault("MAX_IPS", maxIps);
                pingIntervalMs = parameters.GetValueOrDefault("PING_INTERVAL_MS", pingIntervalMs);
                httpProbeUrl = parameters.GetValueOrDefault("HTTP_PROBE_URL", httpProbeUrl);
                httpProbeTimeoutMs = parameters.GetValueOrDefault("HTTP_PROBE_TIMEOUT_MS", httpProbeTimeoutMs);
                httpSpeedTestTimeoutMs = parameters.GetValueOrDefault("HTTP_SPEEDTEST_TIMEOUT_MS", httpSpeedTestTimeoutMs);
                httpSpeedTestIdleTimeoutMs = parameters.GetValueOrDefault("HTTP_SPEEDTEST_IDLE_TIMEOUT_MS", httpSpeedTestIdleTimeoutMs);
                runMinutes = parameters.GetValueOrDefault("RUN_MINUTES", runMinutes);
                bandwidthPriority = parameters.GetValueOrDefault("BANDWIDTH_PRIORITY", bandwidthPriority);
                updateIPList = parameters.GetValueOrDefault("UPDATE_IP_LIST", updateIPList);
                enableSupplementalHttpCheck = parameters.GetValueOrDefault("ENABLE_SUPPLEMENTAL_HTTP_CHECK", enableSupplementalHttpCheck);
            }

            return (cfKey, domains, domains2, domains3, pingThreads, maxIps, pingIntervalMs, httpProbeUrl, httpProbeTimeoutMs, httpSpeedTestTimeoutMs, httpSpeedTestIdleTimeoutMs, runMinutes, bandwidthPriority, updateIPList, enableSupplementalHttpCheck);
        }



        static T ParseWithDefault<T>(string? value, T defaultValue)
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

        static int ParsePositiveIntWithDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, out int result) && result > 0
                ? result
                : defaultValue;
        }

        private static Dictionary<string, string?> ParseCommandLineArgs(string[] args)
        {
            Dictionary<string, string?> parameters = new Dictionary<string, string?>();

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

        private static void PrintStartupConfiguration(bool isDocker)
        {
            var maskedKey = string.IsNullOrWhiteSpace(AppConfig.CloudflareKey)
                ? "(empty)"
                : $"{AppConfig.CloudflareKey[..Math.Min(4, AppConfig.CloudflareKey.Length)]}***";

            Console.WriteLine("启动配置：");
            Console.WriteLine($"  运行环境: {(isDocker ? "Docker" : "Local")}");
            Console.WriteLine($"  CLOUDFLARE_KEY: {maskedKey}");
            Console.WriteLine($"  DOMAINS: {string.Join(",", AppConfig.Domains)}");
            if (AppConfig.Domains2 != null)
                Console.WriteLine($"  DOMAINS2: {string.Join(",", AppConfig.Domains2)}");
            if (AppConfig.Domains3 != null)
                Console.WriteLine($"  DOMAINS3: {string.Join(",", AppConfig.Domains3)}");
            Console.WriteLine($"  PING_THREADS: {AppConfig.PingThreads}");
            Console.WriteLine($"  MAX_IPS: {AppConfig.MaxIps}");
            Console.WriteLine($"  PING_INTERVAL_MS: {AppConfig.PingIntervalMs}");
            Console.WriteLine($"  HTTP_PROBE_URL: {AppConfig.HttpProbeUrl}");
            Console.WriteLine($"  HTTP_PROBE_TIMEOUT_MS: {AppConfig.HttpProbeTimeoutMs}");
            Console.WriteLine($"  HTTP_SPEEDTEST_TIMEOUT_MS: {AppConfig.HttpSpeedTestTimeoutMs}");
            Console.WriteLine($"  HTTP_SPEEDTEST_IDLE_TIMEOUT_MS: {AppConfig.HttpSpeedTestIdleTimeoutMs}");
            Console.WriteLine($"  RUN_MINUTES: {AppConfig.RunMinutes}");
            Console.WriteLine($"  BANDWIDTH_PRIORITY: {AppConfig.BandwidthPriority}");
            Console.WriteLine($"  UPDATE_IP_LIST: {AppConfig.UpdateIPList}");
            Console.WriteLine($"  ENABLE_SUPPLEMENTAL_HTTP_CHECK: {AppConfig.EnableSupplementalHttpCheck}");
        }

        private static async Task WaitForNextSelection(IPAddress? selectedIp)
        {
            var regularInterval = TimeSpan.FromMinutes(AppConfig.RunMinutes);
            if (regularInterval <= TimeSpan.Zero)
            {
                Console.WriteLine("RUN_MINUTES 小于等于0，立即开始下一轮优选");
                return;
            }

            if (!AppConfig.EnableSupplementalHttpCheck)
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟");
                await Task.Delay(regularInterval);
                return;
            }

            var nextRunAt = DateTimeOffset.Now.Add(regularInterval);
            if (selectedIp == null)
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；本轮没有可用IP，将在{SupplementalHttpCheckInterval.TotalMinutes:0}分钟后触发重新优选");
            }
            else
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；期间每{SupplementalHttpCheckInterval.TotalMinutes:0}分钟对 {selectedIp} 进行补充HTTP检查");
            }

            while (true)
            {
                var remaining = nextRunAt - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    Console.WriteLine("到达定时优选时间，开始下一轮优选");
                    return;
                }

                var delay = remaining < SupplementalHttpCheckInterval
                    ? remaining
                    : SupplementalHttpCheckInterval;
                await Task.Delay(delay);

                if (DateTimeOffset.Now >= nextRunAt)
                {
                    Console.WriteLine("到达定时优选时间，开始下一轮优选");
                    return;
                }

                if (selectedIp == null)
                {
                    Console.WriteLine("本轮没有可用于补充HTTP检查的IP，触发重新优选并重新计时");
                    return;
                }

                var healthy = await RunSupplementalHttpCheck(selectedIp);
                if (!healthy)
                {
                    Console.WriteLine($"补充HTTP检查连续{SupplementalHttpCheckCount}次失败，立即开始重新优选并重新计时");
                    return;
                }
            }
        }

        private static async Task<bool> RunSupplementalHttpCheck(IPAddress selectedIp)
        {
            Console.WriteLine($"补充HTTP检查：{selectedIp}，最多{SupplementalHttpCheckCount}次，任意一次成功即通过");

            var httpPing = new Httping();
            for (int attempt = 1; attempt <= SupplementalHttpCheckCount; attempt++)
            {
                var result = await httpPing.SinglePing(selectedIp);
                if (result.success)
                {
                    Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 成功，HTTP延迟：{result.delay.TotalMilliseconds:0.00}ms");
                    return true;
                }

                Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 失败");
            }

            return false;
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

        private static async Task<IPAddress?> SingleSelect()
        {
            var processor = new IPProcessor();

            var subnetCache = new SubnetCache();
            subnetCache.Load();

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

            // 子网缓存阶段：从已缓存的/24子网中随机取IP进行TcpPing，通过的加入最终候选
            var sampledSubnets = subnetCache.RandomSampleIPs(SubnetSampleCount);
            if (sampledSubnets.Any())
            {
                Console.WriteLine($"开始子网缓存阶段：从{subnetCache.SubnetCount}个缓存/24子网中随机取出{sampledSubnets.Count}个IP进行TCP Ping验证");
                TcpPing subnetTcpPing = new TcpPing(sampledSubnets.Select(s => s.IP).ToList(), SubnetProbePingCount);
                var subnetResults = await subnetTcpPing.RunAsync();
                var subnetPassed = subnetResults.Where(r => r.Received > 0).ToList();
                Console.WriteLine($"子网缓存阶段：{subnetPassed.Count}/{sampledSubnets.Count}个IP通过TCP Ping，加入最终候选");

                // 更新连续失败计数，淘汰连续3次失败的子网
                bool evicted = subnetCache.RecordTcpPingOutcomes(sampledSubnets, subnetPassed.Select(r => r.IP));
                if (evicted || subnetPassed.Count < sampledSubnets.Count)
                    subnetCache.Save();

                foreach (var passedIP in subnetPassed)
                {
                    if (!httpCandidates.Any(c => c.IP.Equals(passedIP.IP)))
                        httpCandidates.Add(passedIP);
                }
            }

            if (!httpCandidates.Any())
            {
                Console.WriteLine("没有IP进入HTTP验证阶段");
                return null;
            }

            Console.WriteLine($"开始最终检查：{httpCandidates.Count}个候选IP进行HTTP验证，每个IP检测{HttpProbeCount}次，至少成功{HttpMinSuccessCount}次，当前模式：{(AppConfig.BandwidthPriority ? "带宽优先" : "延迟优先")}");
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

            List<PingData> topNData;
            if (topHttpList.Any())
            {
                if (!AppConfig.BandwidthPriority)
                {
                    topHttpList.Sort((a, b) => a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));
                    topNData = topHttpList.Take(3).ToList();

                    if (topNData.Count > 0)
                    {
                        Console.WriteLine($"当前为连接速度优先，按HTTP延迟选择IP {topNData[0].IP} {topNData[0].Delay.TotalMilliseconds:0.00}ms");
                    }
                }
                else
                {
                    var speedRankedList = new List<(PingData data, double mbps, long bytesRead, TimeSpan duration)>();
                    foreach (var ip in topHttpList)
                    {
                        var speedResult = await httpPing.SpeedTest(ip.IP);
                        if (!speedResult.success)
                        {
                            if (speedResult.missing)
                            {
                                Console.WriteLine($"测速文件不存在，跳过下载测速 {ip.IP} /speedtest");
                            }
                            else
                            {
                                Console.WriteLine($"下载测速失败 {ip.IP}");
                            }

                            continue;
                        }

                        speedRankedList.Add((ip, speedResult.mbps, speedResult.bytesRead, speedResult.duration));
                        Console.WriteLine($"下载测速 [{speedRankedList.Count}] {ip.IP} 速度：{speedResult.mbps:0.00} Mbps 已下载：{speedResult.bytesRead / 1024d / 1024d:0.00} MB 用时：{speedResult.duration.TotalMilliseconds:0}ms");
                    }

                    if (speedRankedList.Any())
                    {
                        speedRankedList.Sort((a, b) =>
                        {
                            var speedCompare = b.mbps.CompareTo(a.mbps);
                            return speedCompare != 0 ? speedCompare : a.data.Delay.CompareTo(b.data.Delay);
                        });

                        topNData = speedRankedList.Take(3).Select(r => r.data).ToList();
                        Console.WriteLine($"最终按下载带宽选择IP {topNData[0].IP} {speedRankedList[0].mbps:0.00} Mbps");
                    }
                    else
                    {
                        // /speedtest 不存在或下载测速全部失败时，回退到原有 HTTP 延迟逻辑
                        topHttpList.Sort((a, b) => a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));
                        topNData = topHttpList.Take(3).ToList();

                        if (topNData.Count > 0)
                        {
                            Console.WriteLine($"下载测速不可用，回退到HTTP延迟选择IP {topNData[0].IP} {topNData[0].Delay.TotalMilliseconds:0.00}ms");
                        }
                    }
                }
            }
            else
            {
                topNData = new List<PingData>();
            }

            var domainGroups = new[] { AppConfig.Domains, AppConfig.Domains2, AppConfig.Domains3 };
            var selectedIp = topNData.FirstOrDefault()?.IP;

            if (topNData.Count > 0)
            {
                var top1Data = topNData[0];
                // 缓存最优IP所在/24子网（按HTTP延迟排序存储，最多10条）
                var subnet24 = subnetCache.GetSubnet24(top1Data.IP);
                if (subnet24 != null)
                {
                    subnetCache.AddOrUpdate(subnet24, top1Data.Delay.TotalMilliseconds);
                    subnetCache.Save();
                    Console.WriteLine($"已缓存子网 {subnet24} (HTTP延迟 {top1Data.Delay.TotalMilliseconds:0.00}ms，当前共{subnetCache.SubnetCount}个缓存子网)");
                }

                // 更新 DOMAINS / DOMAINS2 / DOMAINS3 对应排名 1 / 2 / 3 的 IP
                for (int rank = 0; rank < domainGroups.Length; rank++)
                {
                    var domains = domainGroups[rank];
                    if (domains == null || domains.Length == 0) continue;
                    if (rank >= topNData.Count)
                    {
                        Console.WriteLine($"可用IP不足，跳过 DOMAINS{(rank == 0 ? "" : rank.ToString())} 的DNS更新（需要第{rank + 1}名，实际只有{topNData.Count}个）");
                        break;
                    }

                    var ip = topNData[rank];
                    foreach (var domain in domains)
                    {
                        try
                        {
                            Console.WriteLine($"开始更新域名 {domain} {ip.IP}（第{rank + 1}名）");
                            var updated = await CloudflareAPIManager.Instance.AddOrUpdateARecord(domain, ip.IP.ToString());
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
            }
            else
            {
                Console.WriteLine("最终阶段没有IP通过HTTP验证，跳过DNS更新");
            }

            Console.WriteLine("单次执行完毕");
            return selectedIp;

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
