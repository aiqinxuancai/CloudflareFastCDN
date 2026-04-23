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
            ApplyConfiguration(config);

            List<DnsUpdateProvider> dnsUpdateProviders;
            try
            {
                dnsUpdateProviders = BuildDnsUpdateProviders();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }

            PrintStartupConfiguration(isDocker, dnsUpdateProviders);

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            while (true)
            {
                var selectedIp = await SingleSelect(dnsUpdateProviders);
                await WaitForNextSelection(selectedIp);
            }
        }

        private static ConfigurationData LoadConfiguration(string[] args, bool isDocker)
        {
            string? debugCloudflareKey = null;
            string? debugDomains = null;
#if DEBUG
            debugCloudflareKey = TryReadTextFile("CLOUDFLARE_KEY.txt");
            debugDomains = TryReadTextFile("DOMAINS.txt");
#endif

            var parameters = !isDocker ? ParseCommandLineArgs(args) : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            string? ResolveSetting(string key, string? defaultValue = null)
            {
                var environmentValue = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(environmentValue))
                {
                    return environmentValue;
                }

                if (!isDocker &&
                    parameters.TryGetValue(key, out var argumentValue) &&
                    !string.IsNullOrWhiteSpace(argumentValue))
                {
                    return argumentValue;
                }

                return defaultValue;
            }

            return new ConfigurationData
            {
                CloudflareKey = ResolveSetting("CLOUDFLARE_KEY", debugCloudflareKey),
                TencentCloudSecretId = ResolveSetting("TENCENTCLOUD_SECRET_ID"),
                TencentCloudSecretKey = ResolveSetting("TENCENTCLOUD_SECRET_KEY"),
                AlibabaCloudAccessKeyId = ResolveSetting("ALIBABACLOUD_ACCESS_KEY_ID"),
                AlibabaCloudAccessKeySecret = ResolveSetting("ALIBABACLOUD_ACCESS_KEY_SECRET"),
                CloudflareDomains = FirstNonEmpty(ResolveSetting("CLOUDFLARE_DOMAINS"), ResolveSetting("DOMAINS", debugDomains)),
                CloudflareDomains2 = FirstNonEmpty(ResolveSetting("CLOUDFLARE_DOMAINS2"), ResolveSetting("DOMAINS2")),
                CloudflareDomains3 = FirstNonEmpty(ResolveSetting("CLOUDFLARE_DOMAINS3"), ResolveSetting("DOMAINS3")),
                TencentCloudDomains = ResolveSetting("TENCENTCLOUD_DOMAINS"),
                TencentCloudDomains2 = ResolveSetting("TENCENTCLOUD_DOMAINS2"),
                TencentCloudDomains3 = ResolveSetting("TENCENTCLOUD_DOMAINS3"),
                AlibabaCloudDomains = ResolveSetting("ALIBABACLOUD_DOMAINS"),
                AlibabaCloudDomains2 = ResolveSetting("ALIBABACLOUD_DOMAINS2"),
                AlibabaCloudDomains3 = ResolveSetting("ALIBABACLOUD_DOMAINS3"),
                PingThreads = ResolveSetting("PING_THREADS", "8"),
                MaxIps = ResolveSetting("MAX_IPS", "400"),
                PingIntervalMs = ResolveSetting("PING_INTERVAL_MS", "150"),
                HttpProbeUrl = ResolveSetting("HTTP_PROBE_URL", "https://www.visa.cn/"),
                HttpProbeTimeoutMs = ResolveSetting("HTTP_PROBE_TIMEOUT_MS", "4000"),
                HttpSpeedTestTimeoutMs = ResolveSetting("HTTP_SPEEDTEST_TIMEOUT_MS", "10000"),
                HttpSpeedTestIdleTimeoutMs = ResolveSetting("HTTP_SPEEDTEST_IDLE_TIMEOUT_MS", "3000"),
                RunMinutes = ResolveSetting("RUN_MINUTES", "30"),
                BandwidthPriority = ResolveSetting("BANDWIDTH_PRIORITY", "false"),
                UpdateIPList = ResolveSetting("UPDATE_IP_LIST", "false"),
                EnableSupplementalHttpCheck = ResolveSetting("ENABLE_SUPPLEMENTAL_HTTP_CHECK", "false")
            };
        }

        private static void ApplyConfiguration(ConfigurationData config)
        {
            AppConfig.CloudflareKey = config.CloudflareKey?.Trim() ?? string.Empty;
            AppConfig.TencentCloudSecretId = config.TencentCloudSecretId?.Trim() ?? string.Empty;
            AppConfig.TencentCloudSecretKey = config.TencentCloudSecretKey?.Trim() ?? string.Empty;
            AppConfig.AlibabaCloudAccessKeyId = config.AlibabaCloudAccessKeyId?.Trim() ?? string.Empty;
            AppConfig.AlibabaCloudAccessKeySecret = config.AlibabaCloudAccessKeySecret?.Trim() ?? string.Empty;

            AppConfig.CloudflareDomains = ParseDomainList(config.CloudflareDomains);
            AppConfig.CloudflareDomains2 = ParseDomainList(config.CloudflareDomains2);
            AppConfig.CloudflareDomains3 = ParseDomainList(config.CloudflareDomains3);
            AppConfig.TencentCloudDomains = ParseDomainList(config.TencentCloudDomains);
            AppConfig.TencentCloudDomains2 = ParseDomainList(config.TencentCloudDomains2);
            AppConfig.TencentCloudDomains3 = ParseDomainList(config.TencentCloudDomains3);
            AppConfig.AlibabaCloudDomains = ParseDomainList(config.AlibabaCloudDomains);
            AppConfig.AlibabaCloudDomains2 = ParseDomainList(config.AlibabaCloudDomains2);
            AppConfig.AlibabaCloudDomains3 = ParseDomainList(config.AlibabaCloudDomains3);

            AppConfig.PingThreads = ParseNonZeroIntWithDefault(config.PingThreads, 8);
            AppConfig.MaxIps = ParseNonZeroIntWithDefault(config.MaxIps, 400);
            AppConfig.PingIntervalMs = ParseNonZeroIntWithDefault(config.PingIntervalMs, 150);
            AppConfig.HttpProbeUrl = string.IsNullOrWhiteSpace(config.HttpProbeUrl) ? "https://www.visa.cn/" : config.HttpProbeUrl.Trim();
            AppConfig.HttpProbeTimeoutMs = ParsePositiveIntWithDefault(config.HttpProbeTimeoutMs, 4000);
            AppConfig.HttpSpeedTestTimeoutMs = ParsePositiveIntWithDefault(config.HttpSpeedTestTimeoutMs, 10000);
            AppConfig.HttpSpeedTestIdleTimeoutMs = ParsePositiveIntWithDefault(config.HttpSpeedTestIdleTimeoutMs, 3000);
            AppConfig.RunMinutes = ParseNonZeroIntWithDefault(config.RunMinutes, 30);
            AppConfig.BandwidthPriority = ParseBoolWithDefault(config.BandwidthPriority, false);
            AppConfig.UpdateIPList = ParseBoolWithDefault(config.UpdateIPList, false);
            AppConfig.EnableSupplementalHttpCheck = ParseBoolWithDefault(config.EnableSupplementalHttpCheck, false);
        }

        private static List<DnsUpdateProvider> BuildDnsUpdateProviders()
        {
            var providers = new List<DnsUpdateProvider>();

            if (HasAnyDomains(AppConfig.CloudflareDomains, AppConfig.CloudflareDomains2, AppConfig.CloudflareDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.CloudflareKey))
                {
                    throw new InvalidOperationException("配置了 CLOUDFLARE_DOMAINS*，但缺少 CLOUDFLARE_KEY");
                }

                providers.Add(new DnsUpdateProvider(
                    "Cloudflare",
                    "CLOUDFLARE",
                    new CloudflareAPIManager(AppConfig.CloudflareKey),
                    AppConfig.CloudflareDomains,
                    AppConfig.CloudflareDomains2,
                    AppConfig.CloudflareDomains3));
            }

            if (HasAnyDomains(AppConfig.TencentCloudDomains, AppConfig.TencentCloudDomains2, AppConfig.TencentCloudDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.TencentCloudSecretId) ||
                    string.IsNullOrWhiteSpace(AppConfig.TencentCloudSecretKey))
                {
                    throw new InvalidOperationException("配置了 TENCENTCLOUD_DOMAINS*，但缺少 TENCENTCLOUD_SECRET_ID 或 TENCENTCLOUD_SECRET_KEY");
                }

                providers.Add(new DnsUpdateProvider(
                    "TencentCloud",
                    "TENCENTCLOUD",
                    new TencentCloudDnsManager(AppConfig.TencentCloudSecretId, AppConfig.TencentCloudSecretKey),
                    AppConfig.TencentCloudDomains,
                    AppConfig.TencentCloudDomains2,
                    AppConfig.TencentCloudDomains3));
            }

            if (HasAnyDomains(AppConfig.AlibabaCloudDomains, AppConfig.AlibabaCloudDomains2, AppConfig.AlibabaCloudDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.AlibabaCloudAccessKeyId) ||
                    string.IsNullOrWhiteSpace(AppConfig.AlibabaCloudAccessKeySecret))
                {
                    throw new InvalidOperationException("配置了 ALIBABACLOUD_DOMAINS*，但缺少 ALIBABACLOUD_ACCESS_KEY_ID 或 ALIBABACLOUD_ACCESS_KEY_SECRET");
                }

                providers.Add(new DnsUpdateProvider(
                    "AlibabaCloud",
                    "ALIBABACLOUD",
                    new AlibabaCloudDnsManager(AppConfig.AlibabaCloudAccessKeyId, AppConfig.AlibabaCloudAccessKeySecret),
                    AppConfig.AlibabaCloudDomains,
                    AppConfig.AlibabaCloudDomains2,
                    AppConfig.AlibabaCloudDomains3));
            }

            if (providers.Count == 0)
            {
                throw new InvalidOperationException("缺少 DNS 域名配置，请至少设置 CLOUDFLARE_DOMAINS、TENCENTCLOUD_DOMAINS、ALIBABACLOUD_DOMAINS 或兼容变量 DOMAINS 之一");
            }

            return providers;
        }

        private static Dictionary<string, string?> ParseCommandLineArgs(string[] args)
        {
            var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var splitArg = arg[2..].Split('=', 2);
                if (splitArg.Length == 2)
                {
                    parameters[splitArg[0]] = splitArg[1];
                }
            }

            return parameters;
        }

        private static string[] ParseDomainList(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value
                    .Split(',')
                    .Select(domain => domain.Trim())
                    .Where(domain => !string.IsNullOrWhiteSpace(domain))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        private static string? TryReadTextFile(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static int ParseNonZeroIntWithDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, out var result) && result != 0
                ? result
                : defaultValue;
        }

        private static int ParsePositiveIntWithDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, out var result) && result > 0
                ? result
                : defaultValue;
        }

        private static bool ParseBoolWithDefault(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out var result) ? result : defaultValue;
        }

        private static void PrintStartupConfiguration(bool isDocker, IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            Console.WriteLine("启动配置：");
            Console.WriteLine($"  运行环境: {(isDocker ? "Docker" : "Local")}");
            Console.WriteLine($"  DNS提供商: {string.Join(", ", dnsUpdateProviders.Select(provider => provider.ProviderName))}");
            Console.WriteLine($"  CLOUDFLARE_KEY: {MaskSecret(AppConfig.CloudflareKey)}");
            Console.WriteLine($"  TENCENTCLOUD_SECRET_ID: {MaskSecret(AppConfig.TencentCloudSecretId)}");
            Console.WriteLine($"  TENCENTCLOUD_SECRET_KEY: {MaskSecret(AppConfig.TencentCloudSecretKey)}");
            Console.WriteLine($"  ALIBABACLOUD_ACCESS_KEY_ID: {MaskSecret(AppConfig.AlibabaCloudAccessKeyId)}");
            Console.WriteLine($"  ALIBABACLOUD_ACCESS_KEY_SECRET: {MaskSecret(AppConfig.AlibabaCloudAccessKeySecret)}");

            PrintDomainGroup("CLOUDFLARE_DOMAINS", AppConfig.CloudflareDomains);
            PrintDomainGroup("CLOUDFLARE_DOMAINS2", AppConfig.CloudflareDomains2);
            PrintDomainGroup("CLOUDFLARE_DOMAINS3", AppConfig.CloudflareDomains3);
            PrintDomainGroup("TENCENTCLOUD_DOMAINS", AppConfig.TencentCloudDomains);
            PrintDomainGroup("TENCENTCLOUD_DOMAINS2", AppConfig.TencentCloudDomains2);
            PrintDomainGroup("TENCENTCLOUD_DOMAINS3", AppConfig.TencentCloudDomains3);
            PrintDomainGroup("ALIBABACLOUD_DOMAINS", AppConfig.AlibabaCloudDomains);
            PrintDomainGroup("ALIBABACLOUD_DOMAINS2", AppConfig.AlibabaCloudDomains2);
            PrintDomainGroup("ALIBABACLOUD_DOMAINS3", AppConfig.AlibabaCloudDomains3);

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

        private static void PrintDomainGroup(string variableName, IReadOnlyCollection<string> domains)
        {
            if (domains.Count > 0)
            {
                Console.WriteLine($"  {variableName}: {string.Join(",", domains)}");
            }
        }

        private static string MaskSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            return $"{value[..Math.Min(4, value.Length)]}***";
        }

        private static bool HasAnyDomains(params string[][] domainGroups)
        {
            return domainGroups.Any(domainGroup => domainGroup.Length > 0);
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

        public static List<T> SampleData<T>(IList<T> sourceData, int sampleSize)
        {
            if (sampleSize >= sourceData.Count)
                return new List<T>(sourceData);

            Random random = new Random();
            List<T> shuffledData = new List<T>(sourceData);

            for (int i = shuffledData.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T temp = shuffledData[i];
                shuffledData[i] = shuffledData[j];
                shuffledData[j] = temp;
            }

            return shuffledData.Take(sampleSize).ToList();
        }

        private static async Task<IPAddress?> SingleSelect(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            var processor = new IPProcessor();

            var subnetCache = new SubnetCache();
            subnetCache.Load();

            var ipAddresses = processor.LoadIPRanges();
            ipAddresses = SampleData(ipAddresses, AppConfig.MaxIps);

            IcmpPing task = new IcmpPing(ipAddresses, FirstRoundPingCount);
            Console.WriteLine($"开始第1轮检查：全量{FirstRoundPingCount}次Ping");
            var firstRoundResults = await task.RunAsync();
            var topPings = firstRoundResults.Where(item => item.Sended == FirstRoundPingCount && item.Received == item.Sended).ToList();
            var top100Pings = topPings.Take(100);

            Console.WriteLine($"开始第2轮检查：Top100,{SecondRoundPingCount}次Ping，允许最多丢包{SecondRoundMaxPacketLoss}/{SecondRoundPingCount}");
            IcmpPing top100PingTask = new IcmpPing(top100Pings.Select(item => item.IP).ToList(), SecondRoundPingCount);
            var secondRoundResults = await top100PingTask.RunAsync();
            var top100PingsSelect = secondRoundResults
                .Where(item => item.Sended == SecondRoundPingCount && item.Sended - item.Received <= SecondRoundMaxPacketLoss)
                .ToList();

            var httpCandidates = top100PingsSelect.Take(HttpCandidateCount).ToList();

            var sampledSubnets = subnetCache.RandomSampleIPs(SubnetSampleCount);
            if (sampledSubnets.Any())
            {
                Console.WriteLine($"开始子网缓存阶段：从{subnetCache.SubnetCount}个缓存/24子网中随机取出{sampledSubnets.Count}个IP进行TCP Ping验证");
                TcpPing subnetTcpPing = new TcpPing(sampledSubnets.Select(item => item.IP).ToList(), SubnetProbePingCount);
                var subnetResults = await subnetTcpPing.RunAsync();
                var subnetPassed = subnetResults.Where(result => result.Received > 0).ToList();
                Console.WriteLine($"子网缓存阶段：{subnetPassed.Count}/{sampledSubnets.Count}个IP通过TCP Ping，加入最终候选");

                bool evicted = subnetCache.RecordTcpPingOutcomes(sampledSubnets, subnetPassed.Select(result => result.IP));
                if (evicted || subnetPassed.Count < sampledSubnets.Count)
                    subnetCache.Save();

                foreach (var passedIp in subnetPassed)
                {
                    if (!httpCandidates.Any(candidate => candidate.IP.Equals(passedIp.IP)))
                        httpCandidates.Add(passedIp);
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
                    topHttpList.Sort((left, right) => left.Delay.TotalMicroseconds.CompareTo(right.Delay.TotalMicroseconds));
                    topNData = topHttpList.Take(3).ToList();

                    if (topNData.Count > 0)
                    {
                        Console.WriteLine($"当前为连接速度优先，按HTTP延迟选择IP {topNData[0].IP} {topNData[0].Delay.TotalMilliseconds:0.00}ms");
                    }
                }
                else
                {
                    var speedRankedList = new List<(PingData Data, double Mbps, long BytesRead, TimeSpan Duration)>();
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
                        speedRankedList.Sort((left, right) =>
                        {
                            var speedCompare = right.Mbps.CompareTo(left.Mbps);
                            return speedCompare != 0 ? speedCompare : left.Data.Delay.CompareTo(right.Data.Delay);
                        });

                        topNData = speedRankedList.Take(3).Select(result => result.Data).ToList();
                        Console.WriteLine($"最终按下载带宽选择IP {topNData[0].IP} {speedRankedList[0].Mbps:0.00} Mbps");
                    }
                    else
                    {
                        topHttpList.Sort((left, right) => left.Delay.TotalMicroseconds.CompareTo(right.Delay.TotalMicroseconds));
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

            var selectedIp = topNData.FirstOrDefault()?.IP;

            if (topNData.Count > 0)
            {
                var top1Data = topNData[0];
                var subnet24 = subnetCache.GetSubnet24(top1Data.IP);
                if (subnet24 != null)
                {
                    subnetCache.AddOrUpdate(subnet24, top1Data.Delay.TotalMilliseconds);
                    subnetCache.Save();
                    Console.WriteLine($"已缓存子网 {subnet24} (HTTP延迟 {top1Data.Delay.TotalMilliseconds:0.00}ms，当前共{subnetCache.SubnetCount}个缓存子网)");
                }

                for (int rank = 0; rank < 3; rank++)
                {
                    foreach (var dnsUpdateProvider in dnsUpdateProviders)
                    {
                        var domains = dnsUpdateProvider.GetDomains(rank);
                        if (domains.Length == 0)
                        {
                            continue;
                        }

                        if (rank >= topNData.Count)
                        {
                            Console.WriteLine($"可用IP不足，跳过 {dnsUpdateProvider.GetVariableName(rank)} 的DNS更新（需要第{rank + 1}名，实际只有{topNData.Count}个）");
                            continue;
                        }

                        var rankedIp = topNData[rank];
                        foreach (var domain in domains)
                        {
                            try
                            {
                                Console.WriteLine($"开始更新[{dnsUpdateProvider.ProviderName}]域名 {domain} {rankedIp.IP}（第{rank + 1}名）");
                                var updated = await dnsUpdateProvider.Manager.AddOrUpdateARecord(domain, rankedIp.IP.ToString());
                                if (updated)
                                {
                                    Console.WriteLine($"已完成更新[{dnsUpdateProvider.ProviderName}]域名 {domain}");
                                }
                                else
                                {
                                    Console.WriteLine($"更新[{dnsUpdateProvider.ProviderName}]域名失败 {domain}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                            }
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
        }

        private sealed class ConfigurationData
        {
            public string? CloudflareKey { get; init; }
            public string? TencentCloudSecretId { get; init; }
            public string? TencentCloudSecretKey { get; init; }
            public string? AlibabaCloudAccessKeyId { get; init; }
            public string? AlibabaCloudAccessKeySecret { get; init; }
            public string? CloudflareDomains { get; init; }
            public string? CloudflareDomains2 { get; init; }
            public string? CloudflareDomains3 { get; init; }
            public string? TencentCloudDomains { get; init; }
            public string? TencentCloudDomains2 { get; init; }
            public string? TencentCloudDomains3 { get; init; }
            public string? AlibabaCloudDomains { get; init; }
            public string? AlibabaCloudDomains2 { get; init; }
            public string? AlibabaCloudDomains3 { get; init; }
            public string? PingThreads { get; init; }
            public string? MaxIps { get; init; }
            public string? PingIntervalMs { get; init; }
            public string? HttpProbeUrl { get; init; }
            public string? HttpProbeTimeoutMs { get; init; }
            public string? HttpSpeedTestTimeoutMs { get; init; }
            public string? HttpSpeedTestIdleTimeoutMs { get; init; }
            public string? RunMinutes { get; init; }
            public string? BandwidthPriority { get; init; }
            public string? UpdateIPList { get; init; }
            public string? EnableSupplementalHttpCheck { get; init; }
        }

        private sealed class DnsUpdateProvider
        {
            private readonly string[][] _domainGroups;

            public DnsUpdateProvider(string providerName, string variablePrefix, IDnsRecordManager manager, params string[][] domainGroups)
            {
                ProviderName = providerName;
                VariablePrefix = variablePrefix;
                Manager = manager;
                _domainGroups = domainGroups;
            }

            public string ProviderName { get; }
            public string VariablePrefix { get; }
            public IDnsRecordManager Manager { get; }

            public string[] GetDomains(int rank)
            {
                return rank >= 0 && rank < _domainGroups.Length
                    ? _domainGroups[rank]
                    : Array.Empty<string>();
            }

            public string GetVariableName(int rank)
            {
                return $"{VariablePrefix}_DOMAINS{(rank == 0 ? string.Empty : (rank + 1).ToString())}";
            }
        }
    }
}
