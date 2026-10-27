using CloudflareFastCDN.Services;
using CloudflareFastCDN.Settings;
using CloudflareFastCDN.Utils;
using CloudflareFastCDN.Web;
using System.Net;
using System.Net.Sockets;

namespace CloudflareFastCDN
{
    internal class Program
    {
        private const int FirstRoundPingCount = 4;
        private const int SecondRoundPingCount = 5;
        private const int SecondRoundMaxPacketLoss = 0;
        private const double SecondRoundCandidateRatio = 0.25d;
        private const int FinalHttpCandidateCount = 10;
        private const int FinalSecondRoundCandidateCount = 7;
        private const int FinalCachedCandidateCount = 3;
        private const int HttpProbeCount = 3;
        private const int HttpMinSuccessCount = 2;
        private const double AssignedDelayMaxMultiplier = 2d;
        private static readonly TimeSpan AssignedDelayMinimumThreshold = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FinalProbeMinInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FinalProbeMaxInterval = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan FinalCandidateMinInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FinalCandidateMaxInterval = TimeSpan.FromSeconds(45);
        private const int SubnetProbePingCount = 2;
        private const int SubnetSampleCount = 3;
        private static readonly TimeSpan SupplementalHttpCheckInterval = TimeSpan.FromMinutes(5);
        private const int SupplementalHttpCheckCount = 3;

        private static List<DnsUpdateProvider>? _dnsUpdateProviders;
        private static int _dnsUpdateProvidersVersion = -1;

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            bool isDocker = File.Exists("/.dockerenv");
            TimestampedConsole.Configure(isDocker);
            SettingsManager.Initialize(args, isDocker);
            PrintStartupConfiguration(isDocker);

            DashboardState.Initialize();
            var webServer = await WebServer.StartAsync();
            if (webServer == null && !TryBuildDnsUpdateProviders(out _, out var configurationError))
            {
                // 没有 Web 管理页面时无法在运行中补全配置，保持原有的直接退出行为
                Console.WriteLine(configurationError);
                return;
            }

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            var dnsUpdateProviders = await GetDnsUpdateProviders();
            SelectionResult selection;
            if (AppConfig.ForceInitialFullSelection)
            {
                Console.WriteLine("FORCE_INITIAL_FULL_SELECTION 已启用，首次启动直接进行全量优选");
                selection = await SingleSelect(dnsUpdateProviders, "首次启动");
            }
            else
            {
                selection = await TryCreateInitialSelectionFromDomains(dnsUpdateProviders)
                    ?? await SingleSelect(dnsUpdateProviders, "首次启动");
            }

            while (true)
            {
                var fullSelectionTrigger = await WaitForNextSelection(selection);
                if (fullSelectionTrigger != null)
                {
                    selection = await SingleSelect(await GetDnsUpdateProviders(), fullSelectionTrigger);
                }
            }
        }

        private static void PrintStartupConfiguration(bool isDocker)
        {
            Console.WriteLine("启动配置：");
            Console.WriteLine($"  运行环境: {(isDocker ? "Docker" : "Local")}");
            Console.WriteLine($"  日志时间格式: {TimestampedConsole.TimestampFormatPattern}");
            Console.WriteLine($"  日志时区: {TimestampedConsole.EffectiveTimeZoneId}");
            if (isDocker)
            {
                Console.WriteLine($"  TZ: {TimestampedConsole.RequestedTimeZoneId ?? "(not set)"}");
            }
            Console.WriteLine($"  设置文件: {Path.GetFullPath(SettingsManager.SettingsFile)}");
            Console.WriteLine($"  WEB_PORT: {AppConfig.WebPort}");
            Console.WriteLine($"  WEB_HOST: {AppConfig.WebHost}");
            Console.WriteLine($"  DNS提供商: {(TryBuildDnsUpdateProviders(out var providers, out _) ? string.Join(", ", providers.Select(provider => provider.ProviderName)) : "(未配置)")}");
            foreach (var item in SettingsManager.BuildSummary())
            {
                Console.WriteLine($"  {item.Key}: {item.Value}");
            }
        }

        /// <summary>
        /// 获取当前DNS设置对应的更新器；配置不完整时进入待配置状态，直到Web页面保存了有效配置。
        /// </summary>
        private static async Task<List<DnsUpdateProvider>> GetDnsUpdateProviders()
        {
            string? reportedError = null;
            while (true)
            {
                var settingsChanged = SettingsManager.WaitForChangeAsync();
                var dnsVersion = SettingsManager.DnsVersion;
                if (_dnsUpdateProviders != null && dnsVersion == _dnsUpdateProvidersVersion)
                {
                    return _dnsUpdateProviders;
                }

                if (TryBuildDnsUpdateProviders(out var providers, out var error))
                {
                    if (reportedError != null)
                    {
                        Console.WriteLine($"DNS配置已完成：{string.Join(", ", providers.Select(provider => provider.ProviderName))}");
                    }

                    _dnsUpdateProviders = providers;
                    _dnsUpdateProvidersVersion = dnsVersion;
                    return providers;
                }

                if (error != reportedError)
                {
                    Console.WriteLine($"{error}；请在Web管理页面的设置中完成配置");
                    reportedError = error;
                }

                DashboardState.SetPhase(DashboardPhase.NotConfigured, error);
                await settingsChanged;
            }
        }

        private static bool TryBuildDnsUpdateProviders(out List<DnsUpdateProvider> providers, out string error)
        {
            providers = new List<DnsUpdateProvider>();
            error = string.Empty;

            if (HasAnyDomains(AppConfig.CloudflareDomains, AppConfig.CloudflareDomains2, AppConfig.CloudflareDomains3))
            {
                if (string.IsNullOrWhiteSpace(AppConfig.CloudflareKey))
                {
                    error = "配置了 CLOUDFLARE_DOMAINS*，但缺少 CLOUDFLARE_KEY";
                    return false;
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
                    error = "配置了 TENCENTCLOUD_DOMAINS*，但缺少 TENCENTCLOUD_SECRET_ID 或 TENCENTCLOUD_SECRET_KEY";
                    return false;
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
                    error = "配置了 ALIBABACLOUD_DOMAINS*，但缺少 ALIBABACLOUD_ACCESS_KEY_ID 或 ALIBABACLOUD_ACCESS_KEY_SECRET";
                    return false;
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
                error = "缺少 DNS 域名配置，请至少设置 Cloudflare、腾讯云或阿里云其中一家的域名及凭证";
                return false;
            }

            return true;
        }

        private static bool HasAnyDomains(params string[][] domainGroups)
        {
            return domainGroups.Any(domainGroup => domainGroup.Length > 0);
        }

        private static async Task<SelectionResult?> TryCreateInitialSelectionFromDomains(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            Console.WriteLine("首次启动先解析并测试已配置域名的当前IPv4地址");
            DashboardState.SetPhase(DashboardPhase.InitialCheck, "解析已配置域名");

            var resolvedIps = new Dictionary<IPAddress, ResolvedDomainIp>();
            foreach (var dnsUpdateProvider in dnsUpdateProviders)
            {
                for (int rank = 0; rank < 3; rank++)
                {
                    foreach (var domain in dnsUpdateProvider.GetDomains(rank))
                    {
                        IPAddress[] addresses;
                        try
                        {
                            addresses = await Dns.GetHostAddressesAsync(domain);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"首次域名预检解析失败：{domain}，原因：{FormatError(ex.Message)}，开始全量优选");
                            return null;
                        }

                        var ipv4Addresses = addresses
                            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                            .Distinct()
                            .ToList();
                        if (ipv4Addresses.Count == 0)
                        {
                            Console.WriteLine($"首次域名预检未解析到IPv4地址：{domain}，开始全量优选");
                            return null;
                        }

                        Console.WriteLine($"首次域名预检解析：{domain} -> {string.Join(", ", ipv4Addresses)}");
                        foreach (var ip in ipv4Addresses)
                        {
                            if (!resolvedIps.TryGetValue(ip, out var resolvedIp))
                            {
                                resolvedIp = new ResolvedDomainIp(ip, rank + 1);
                                resolvedIps.Add(ip, resolvedIp);
                            }
                            else
                            {
                                resolvedIp.Rank = Math.Min(resolvedIp.Rank, rank + 1);
                            }

                            resolvedIp.Domains.Add(domain);
                        }
                    }
                }
            }

            if (resolvedIps.Count == 0)
            {
                Console.WriteLine("首次域名预检没有可测试的IPv4地址，开始全量优选");
                return null;
            }

            var assignedIps = new List<AssignedIp>();
            var httpPing = new Httping();
            var hasFailure = false;
            foreach (var resolvedIp in resolvedIps.Values)
            {
                DashboardState.SetPhase(DashboardPhase.InitialCheck, $"HTTP检查 Top{resolvedIp.Rank} {resolvedIp.IP}");
                var result = await httpPing.SinglePing(resolvedIp.IP, pauseAfterBackoff: false);
                DashboardState.RecordCheck(resolvedIp.Rank, resolvedIp.IP.ToString(), result.success ? result.delay.TotalMilliseconds : null, "首次预检");
                if (!result.success)
                {
                    hasFailure = true;
                    Console.WriteLine($"首次域名预检失败：{resolvedIp.IP}（{string.Join(", ", resolvedIp.Domains)}），原因：{FormatError(result.error)}");
                    continue;
                }

                assignedIps.Add(new AssignedIp(resolvedIp.IP, result.delay, resolvedIp.Rank));
                Console.WriteLine($"首次域名预检正常：Top{resolvedIp.Rank} {resolvedIp.IP}（{string.Join(", ", resolvedIp.Domains)}），HTTP延迟：{result.delay.TotalMilliseconds:0.00}ms");
            }

            if (hasFailure)
            {
                Console.WriteLine("首次域名预检存在无法连接的IPv4地址，开始全量优选");
                return null;
            }

            Console.WriteLine($"首次域名预检的{assignedIps.Count}个IPv4地址均正常，跳过首次全量优选");
            DashboardState.SetAssignedIps(assignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds)));
            return new SelectionResult(assignedIps);
        }

        /// <summary>
        /// 等待下一周期；返回需要执行全量优选的触发原因，返回 null 表示本周期无需全量优选。
        /// </summary>
        private static async Task<string?> WaitForNextSelection(SelectionResult selection)
        {
            var regularInterval = TimeSpan.FromMinutes(AppConfig.RunMinutes);
            if (regularInterval <= TimeSpan.Zero)
            {
                Console.WriteLine("RUN_MINUTES 小于等于0，立即开始下一轮优选");
                return "定时优选";
            }

            var nextRunAt = TimestampedConsole.Now.Add(regularInterval);
            DashboardState.SetWaiting(nextRunAt);

            if (!AppConfig.EnableSupplementalHttpCheck)
            {
                Console.WriteLine(selection.AssignedIps.Count == 0
                    ? $"等待{AppConfig.RunMinutes}分钟；本轮没有已分配IP，到期后重新优选"
                    : $"等待{AppConfig.RunMinutes}分钟；到期后先复检{selection.AssignedIps.Count}个已分配IP");
                var request = await RuntimeControl.WaitAsync(regularInterval);
                return await HandleWaitFinished(selection, request);
            }

            if (selection.AssignedIps.Count == 0)
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；本轮没有可用IP，将在{SupplementalHttpCheckInterval.TotalMinutes:0}分钟后触发重新优选");
            }
            else
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；期间每{SupplementalHttpCheckInterval.TotalMinutes:0}分钟对 {selection.AssignedIps[0].IP} 进行补充HTTP检查，到期后复检{selection.AssignedIps.Count}个已分配IP");
            }

            while (true)
            {
                var remaining = nextRunAt - TimestampedConsole.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    return await HandleWaitFinished(selection, ManualRequest.None);
                }

                var delay = remaining < SupplementalHttpCheckInterval
                    ? remaining
                    : SupplementalHttpCheckInterval;
                var request = await RuntimeControl.WaitAsync(delay);

                if (request.Command != ManualCommand.None || TimestampedConsole.Now >= nextRunAt)
                {
                    return await HandleWaitFinished(selection, request);
                }

                if (selection.AssignedIps.Count == 0)
                {
                    Console.WriteLine("本轮没有可用于补充HTTP检查的IP，触发重新优选并重新计时");
                    return "无可用IP";
                }

                DashboardState.SetPhase(DashboardPhase.SupplementalCheck, $"补充HTTP检查 {selection.AssignedIps[0].IP}");
                var healthy = await RunSupplementalHttpCheck(selection.AssignedIps[0]);
                if (!healthy)
                {
                    Console.WriteLine($"补充HTTP检查连续{SupplementalHttpCheckCount}次失败，立即开始重新优选并重新计时");
                    return "补充检查失败";
                }

                DashboardState.SetWaiting(nextRunAt);
            }
        }

        private static async Task<string?> HandleWaitFinished(SelectionResult selection, ManualRequest request)
        {
            if (request.Command == ManualCommand.FullSelection)
            {
                Console.WriteLine($"立即开始全量优选，原因：{request.Trigger}");
                return request.Trigger;
            }

            var manual = request.Command == ManualCommand.Revalidate;
            if (await ValidateAssignedIps(selection.AssignedIps, manual))
            {
                return null;
            }

            if (selection.AssignedIps.Count == 0)
            {
                return "无可用IP";
            }

            return manual ? "手动复检未通过" : "定时复检未通过";
        }

        private static async Task<bool> ValidateAssignedIps(IReadOnlyList<AssignedIp> assignedIps, bool manual)
        {
            RuntimeControl.ClearPendingRevalidation();
            var reason = manual ? "收到手动复检请求" : "到达定时优选时间";
            if (assignedIps.Count == 0)
            {
                Console.WriteLine($"{reason}，但没有已分配IP，开始全量优选");
                return false;
            }

            Console.WriteLine($"{reason}，先复检{assignedIps.Count}个已分配IP；每个IP检测{HttpProbeCount}次，单次间隔{FinalProbeMinInterval.TotalSeconds:0}-{FinalProbeMaxInterval.TotalSeconds:0}秒，延迟达到动态阈值 max({AssignedDelayMinimumThreshold.TotalMilliseconds:0}ms, 基线×{AssignedDelayMaxMultiplier:0.#}) 则重新优选");
            var httpPing = new Httping();

            for (int i = 0; i < assignedIps.Count; i++)
            {
                var assignedIp = assignedIps[i];
                DashboardState.SetPhase(DashboardPhase.Revalidating, $"复检 Top{assignedIp.Rank} {assignedIp.IP}（{i + 1}/{assignedIps.Count}）");
                var result = await httpPing.Ping(assignedIp.IP, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var averageDelay = result.success > 0
                    ? TimeSpan.FromMilliseconds(result.totalDelay.TotalMilliseconds / result.success)
                    : TimeSpan.Zero;

                var passed = result.success >= HttpProbeCount;
                var updatedBaseline = passed && averageDelay > TimeSpan.Zero && averageDelay < assignedIp.BaselineDelay
                    ? averageDelay
                    : assignedIp.BaselineDelay;
                DashboardState.RecordCheck(
                    assignedIp.Rank,
                    assignedIp.IP.ToString(),
                    passed ? averageDelay.TotalMilliseconds : null,
                    manual ? "手动复检" : "定时复检",
                    updatedBaseline.TotalMilliseconds);

                if (result.success < HttpProbeCount)
                {
                    Console.WriteLine($"已分配IP复检失败：Top{assignedIp.Rank} {assignedIp.IP} HTTP成功：{result.success}/{HttpProbeCount}，原因：{FormatError(result.error)}，开始全量优选");
                    return false;
                }

                if (IsAssignedDelayInvalid(assignedIp.BaselineDelay, averageDelay))
                {
                    Console.WriteLine($"已分配IP复检延迟异常：Top{assignedIp.Rank} {assignedIp.IP} 当前{averageDelay.TotalMilliseconds:0.00}ms，基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms，开始全量优选");
                    return false;
                }

                if (averageDelay > TimeSpan.Zero && averageDelay < assignedIp.BaselineDelay)
                {
                    Console.WriteLine($"已分配IP复检正常：Top{assignedIp.Rank} {assignedIp.IP} HTTP成功：{result.success}/{HttpProbeCount} 当前{averageDelay.TotalMilliseconds:0.00}ms，低于基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms，基线更新为当前值");
                    assignedIp.BaselineDelay = averageDelay;
                }
                else
                {
                    Console.WriteLine($"已分配IP复检正常：Top{assignedIp.Rank} {assignedIp.IP} HTTP成功：{result.success}/{HttpProbeCount} 当前{averageDelay.TotalMilliseconds:0.00}ms，基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms");
                }

                if (i < assignedIps.Count - 1)
                {
                    var delay = GetRandomDelay(FinalCandidateMinInterval, FinalCandidateMaxInterval);
                    Console.WriteLine($"等待{delay.TotalSeconds:0}秒后复检下一个已分配IP");
                    await Task.Delay(delay);
                }
            }

            Console.WriteLine("已分配IP复检均正常，本周期跳过全量优选");
            return true;
        }

        private static async Task<bool> RunSupplementalHttpCheck(AssignedIp assignedIp)
        {
            var selectedIp = assignedIp.IP;
            Console.WriteLine($"补充HTTP检查：{selectedIp}，最多{SupplementalHttpCheckCount}次，任意一次成功即通过");

            var httpPing = new Httping();
            for (int attempt = 1; attempt <= SupplementalHttpCheckCount; attempt++)
            {
                var result = await httpPing.SinglePing(selectedIp);
                DashboardState.RecordCheck(assignedIp.Rank, selectedIp.ToString(), result.success ? result.delay.TotalMilliseconds : null, "补充检查");
                if (result.success)
                {
                    Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 成功，HTTP延迟：{result.delay.TotalMilliseconds:0.00}ms");
                    return true;
                }

                Console.WriteLine($"补充HTTP检查 [{attempt}/{SupplementalHttpCheckCount}] 失败：{FormatError(result.error)}");
            }

            return false;
        }

        private static int CalculateSecondRoundCandidateCount(int sampledIpCount)
        {
            return sampledIpCount <= 0
                ? 0
                : (int)Math.Ceiling(sampledIpCount * SecondRoundCandidateRatio);
        }

        private static bool IsAssignedDelayInvalid(TimeSpan baselineDelay, TimeSpan currentDelay)
        {
            if (baselineDelay <= TimeSpan.Zero)
            {
                return false;
            }

            var multipliedThreshold = baselineDelay.TotalMilliseconds * AssignedDelayMaxMultiplier;
            var thresholdMilliseconds = Math.Max(AssignedDelayMinimumThreshold.TotalMilliseconds, multipliedThreshold);
            return currentDelay.TotalMilliseconds >= thresholdMilliseconds;
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

        private static async Task<SelectionResult> SingleSelect(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders, string trigger)
        {
            RuntimeControl.ClearPending();
            DashboardState.BeginRun(trigger, AppConfig.BandwidthPriority ? "带宽优先" : "延迟优先");
            Console.WriteLine($"开始全量优选，触发原因：{trigger}");

            var selection = await RunFullSelection(dnsUpdateProviders);
            DashboardState.CompleteRun(selection.AssignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds)));
            return selection;
        }

        private static async Task<SelectionResult> RunFullSelection(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders)
        {
            var processor = new IPProcessor();

            var subnetCache = new SubnetCache();
            subnetCache.Load();

            var ipAddresses = processor.LoadIPRanges();
            ipAddresses = SampleData(ipAddresses, AppConfig.MaxIps);

            IcmpPing task = new IcmpPing(ipAddresses, FirstRoundPingCount);
            Console.WriteLine($"开始第1轮检查：全量{FirstRoundPingCount}次Ping");
            DashboardState.UpdateRun(run => run.Sampled = ipAddresses.Count, $"第1轮ICMP Ping（{ipAddresses.Count}个IP）");
            var firstRoundResults = await task.RunAsync();
            var topPings = firstRoundResults.Where(item => item.Sended == FirstRoundPingCount && item.Received == item.Sended).ToList();
            var secondRoundCandidateLimit = CalculateSecondRoundCandidateCount(ipAddresses.Count);
            var secondRoundPings = topPings.Take(secondRoundCandidateLimit).ToList();

            Console.WriteLine($"开始第2轮检查：第1轮前{secondRoundPings.Count}名（抽样{ipAddresses.Count}个，前{SecondRoundCandidateRatio:P0}上限{secondRoundCandidateLimit}个），{SecondRoundPingCount}次Ping，允许最多丢包{SecondRoundMaxPacketLoss}/{SecondRoundPingCount}");
            DashboardState.UpdateRun(run => run.FirstRoundPassed = topPings.Count, $"第2轮ICMP Ping（{secondRoundPings.Count}个IP）");
            IcmpPing secondRoundPingTask = new IcmpPing(secondRoundPings.Select(item => item.IP).ToList(), SecondRoundPingCount);
            var secondRoundResults = await secondRoundPingTask.RunAsync();
            var secondRoundPassed = secondRoundResults
                .Where(item => item.Sended == SecondRoundPingCount && item.Sended - item.Received <= SecondRoundMaxPacketLoss)
                .ToList();
            DashboardState.UpdateRun(run => run.SecondRoundPassed = secondRoundPassed.Count);

            var httpCandidates = secondRoundPassed.Take(FinalSecondRoundCandidateCount).ToList();
            var remainingSecondRoundCandidates = secondRoundPassed
                .Skip(FinalSecondRoundCandidateCount)
                .ToList();

            var cachedCandidateIps = new HashSet<IPAddress>();
            var sampledSubnets = subnetCache.RandomSampleIPs(SubnetSampleCount);
            if (sampledSubnets.Any())
            {
                Console.WriteLine($"开始子网缓存阶段：从{subnetCache.SubnetCount}个缓存/24子网中随机取出{sampledSubnets.Count}个IP进行TCP Ping验证");
                DashboardState.UpdateRun(run => run.CachedSampled = sampledSubnets.Count, $"子网缓存TCP Ping（{sampledSubnets.Count}个IP）");
                TcpPing subnetTcpPing = new TcpPing(sampledSubnets.Select(item => item.IP).ToList(), SubnetProbePingCount);
                var subnetResults = await subnetTcpPing.RunAsync();
                var subnetPassed = subnetResults.Where(result => result.Received > 0).ToList();
                Console.WriteLine($"子网缓存阶段：{subnetPassed.Count}/{sampledSubnets.Count}个IP通过TCP Ping，加入最终候选");
                DashboardState.UpdateRun(run => run.CachedPassed = subnetPassed.Count);
                cachedCandidateIps.UnionWith(subnetPassed.Select(result => result.IP));

                bool evicted = subnetCache.RecordTcpPingOutcomes(sampledSubnets, subnetPassed.Select(result => result.IP));
                if (evicted || subnetPassed.Count < sampledSubnets.Count)
                    subnetCache.Save();

                foreach (var passedIp in subnetPassed.Take(FinalCachedCandidateCount))
                {
                    if (!httpCandidates.Any(candidate => candidate.IP.Equals(passedIp.IP)))
                        httpCandidates.Add(passedIp);
                }
            }

            foreach (var secondRoundIp in remainingSecondRoundCandidates)
            {
                if (httpCandidates.Count >= FinalHttpCandidateCount)
                    break;

                if (!httpCandidates.Any(candidate => candidate.IP.Equals(secondRoundIp.IP)))
                    httpCandidates.Add(secondRoundIp);
            }

            if (httpCandidates.Count > FinalHttpCandidateCount)
            {
                Console.WriteLine($"最终检查候选超过{FinalHttpCandidateCount}个，仅保留前{FinalHttpCandidateCount}个IP");
                httpCandidates = httpCandidates.Take(FinalHttpCandidateCount).ToList();
            }

            if (!httpCandidates.Any())
            {
                Console.WriteLine("没有IP进入HTTP验证阶段");
                return SelectionResult.Empty;
            }

            Console.WriteLine($"开始最终检查：{httpCandidates.Count}个候选IP进行HTTP验证，每个IP检测{HttpProbeCount}次，单次间隔{FinalProbeMinInterval.TotalSeconds:0}-{FinalProbeMaxInterval.TotalSeconds:0}秒，至少成功{HttpMinSuccessCount}次，当前模式：{(AppConfig.BandwidthPriority ? "带宽优先" : "延迟优先")}");
            DashboardState.UpdateRun(run => run.HttpCandidates = httpCandidates.Count);
            int count = 0;
            List<PingData> topHttpList = new List<PingData>();
            var httpPing = new Httping();
            foreach (var ip in httpCandidates)
            {
                count++;
                DashboardState.SetSelectionDetail($"HTTP验证 {count}/{httpCandidates.Count} {ip.IP}");
                var pingDelay = ip.Delay;
                var pingResult = await httpPing.PingWithOutcome(ip.IP, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
                var averageDelay = pingResult.success > 0
                    ? TimeSpan.FromMilliseconds(pingResult.totalDelay.TotalMilliseconds / pingResult.success)
                    : TimeSpan.Zero;

                var errorMessage = pingResult.success >= HttpProbeCount ? string.Empty : $" 失败原因：{FormatError(pingResult.error)}";
                var skipMessage = pingResult.skipNode ? "，已跳过此节点" : string.Empty;
                Console.WriteLine($"最终结果 [{count}] {ip.IP} HTTP成功：{pingResult.success}/{HttpProbeCount} HTTP均延时：{averageDelay.TotalMilliseconds}ms{errorMessage}{skipMessage}");
                var candidatePassed = !pingResult.skipNode && pingResult.success >= HttpMinSuccessCount;
                if (candidatePassed)
                {
                    ip.Delay = averageDelay;
                    topHttpList.Add(ip);
                }

                DashboardState.UpdateRun(run =>
                {
                    run.Candidates.Add(new CandidateResult
                    {
                        Ip = ip.IP.ToString(),
                        Source = cachedCandidateIps.Contains(ip.IP) ? "子网缓存" : "ICMP",
                        PingMs = pingDelay > TimeSpan.Zero ? Math.Round(pingDelay.TotalMilliseconds, 2) : null,
                        HttpSuccess = pingResult.success,
                        HttpTotal = HttpProbeCount,
                        HttpAvgMs = pingResult.success > 0 ? Math.Round(averageDelay.TotalMilliseconds, 2) : null,
                        Passed = candidatePassed,
                        Error = pingResult.success >= HttpProbeCount ? null : FormatError(pingResult.error) + skipMessage
                    });
                    run.HttpPassed = topHttpList.Count;
                });

                if (count < httpCandidates.Count)
                {
                    var delay = GetRandomDelay(FinalCandidateMinInterval, FinalCandidateMaxInterval);
                    Console.WriteLine($"等待{delay.TotalSeconds:0}秒后测试下一个HTTP候选节点");
                    await Task.Delay(delay);
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
                    for (int speedIndex = 0; speedIndex < topHttpList.Count; speedIndex++)
                    {
                        var ip = topHttpList[speedIndex];
                        DashboardState.SetSelectionDetail($"下载测速 {speedIndex + 1}/{topHttpList.Count} {ip.IP}");
                        var speedResult = await httpPing.SpeedTest(ip.IP);
                        if (!speedResult.success)
                        {
                            if (speedResult.missing)
                            {
                                Console.WriteLine($"测速文件不存在，跳过下载测速 {ip.IP} /speedtest，原因：{FormatError(speedResult.error)}");
                            }
                            else
                            {
                                Console.WriteLine($"下载测速失败 {ip.IP}，原因：{FormatError(speedResult.error)}");
                            }

                        }
                        else
                        {
                            speedRankedList.Add((ip, speedResult.mbps, speedResult.bytesRead, speedResult.duration));
                            var testedIp = ip.IP.ToString();
                            DashboardState.UpdateRun(run =>
                            {
                                var candidate = run.Candidates.FirstOrDefault(item => item.Ip == testedIp);
                                if (candidate != null)
                                {
                                    candidate.Mbps = Math.Round(speedResult.mbps, 2);
                                }
                            });
                            Console.WriteLine($"下载测速 [{speedRankedList.Count}] {ip.IP} 速度：{speedResult.mbps:0.00} Mbps 已下载：{speedResult.bytesRead / 1024d / 1024d:0.00} MB 用时：{speedResult.duration.TotalMilliseconds:0}ms");
                        }

                        if (speedIndex < topHttpList.Count - 1)
                        {
                            var delay = GetRandomDelay(FinalCandidateMinInterval, FinalCandidateMaxInterval);
                            Console.WriteLine($"等待{delay.TotalSeconds:0}秒后测速下一个候选节点");
                            await Task.Delay(delay);
                        }
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

            var assignedIpsByRank = new Dictionary<int, PingData>();

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
                            var updated = false;
                            try
                            {
                                Console.WriteLine($"开始更新[{dnsUpdateProvider.ProviderName}]域名 {domain} {rankedIp.IP}（第{rank + 1}名）");
                                DashboardState.SetSelectionDetail($"更新DNS {domain}");
                                updated = await dnsUpdateProvider.Manager.AddOrUpdateARecord(domain, rankedIp.IP.ToString());
                                if (updated)
                                {
                                    Console.WriteLine($"已完成更新[{dnsUpdateProvider.ProviderName}]域名 {domain}");
                                    assignedIpsByRank.TryAdd(rank, rankedIp);
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

                            DashboardState.UpdateRun(run => run.DnsUpdates.Add(new DnsUpdateResult
                            {
                                Provider = dnsUpdateProvider.ProviderName,
                                Domain = domain,
                                Rank = rank + 1,
                                Ip = rankedIp.IP.ToString(),
                                Success = updated
                            }));
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("最终阶段没有IP通过HTTP验证，跳过DNS更新");
            }

            Console.WriteLine("单次执行完毕");
            var assignedIps = assignedIpsByRank
                .OrderBy(item => item.Key)
                .Select(item => new AssignedIp(item.Value.IP, item.Value.Delay, item.Key + 1))
                .ToList();

            if (assignedIps.Count > 0)
            {
                Console.WriteLine($"本轮实际分配IP：{string.Join(", ", assignedIps.Select(item => $"Top{item.Rank} {item.IP}({item.BaselineDelay.TotalMilliseconds:0.00}ms)"))}");
            }
            else
            {
                Console.WriteLine("本轮没有记录到成功分配的IP");
            }

            return new SelectionResult(assignedIps);
        }

        private static TimeSpan GetRandomDelay(TimeSpan minDelay, TimeSpan maxDelay)
        {
            if (maxDelay <= minDelay)
            {
                return minDelay;
            }

            var milliseconds = Random.Shared.NextInt64((long)minDelay.TotalMilliseconds, (long)maxDelay.TotalMilliseconds + 1);
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private sealed record SelectionResult(IReadOnlyList<AssignedIp> AssignedIps)
        {
            public static SelectionResult Empty { get; } = new(Array.Empty<AssignedIp>());
        }

        private sealed class AssignedIp
        {
            public AssignedIp(IPAddress ip, TimeSpan baselineDelay, int rank)
            {
                IP = ip;
                BaselineDelay = baselineDelay;
                Rank = rank;
            }

            public IPAddress IP { get; }
            public TimeSpan BaselineDelay { get; set; }
            public int Rank { get; }
        }

        private static string FormatError(string? error)
        {
            return string.IsNullOrWhiteSpace(error) ? "Unknown" : error;
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

        private sealed class ResolvedDomainIp
        {
            public ResolvedDomainIp(IPAddress ip, int rank)
            {
                IP = ip;
                Rank = rank;
            }

            public IPAddress IP { get; }
            public int Rank { get; set; }
            public HashSet<string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
