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
        private const int HttpProbeCount = 3;
        private const int HttpMinSuccessCount = 2;
        private const double AssignedDelayMaxMultiplier = 2d;
        private static readonly TimeSpan AssignedDelayMinimumThreshold = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan FinalProbeMinInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FinalProbeMaxInterval = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan FinalCandidateMinInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FinalCandidateMaxInterval = TimeSpan.FromSeconds(45);
        private const int SubnetProbePingCount = 2;
        private static readonly TimeSpan SupplementalHttpCheckInterval = TimeSpan.FromMinutes(5);
        private const int SupplementalHttpCheckCount = 3;

        private static DnsSetup? _dnsSetup;
        private static int _dnsSetupVersion = -1;

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            bool isDocker = AppPaths.IsDocker;
            TimestampedConsole.Configure(isDocker);
            SettingsManager.Initialize(args, isDocker);
            PrintStartupConfiguration(isDocker);

            DashboardState.Initialize();
            var webServer = await WebServer.StartAsync();
            if (webServer == null && !DnsSetup.TryCreate(SettingsManager.Current, out _, out var configurationError))
            {
                // 没有 Web 管理页面时无法在运行中补全配置，保持原有的直接退出行为
                Console.WriteLine($"{configurationError}；请编辑配置文件 {Path.GetFullPath(SettingsManager.SettingsFile)} 后重启");
                return;
            }

            if (AppConfig.UpdateIPList)
            {
                await IPProcessor.UpdateIPList();
            }

            var dnsSetup = await GetDnsSetup();
            SelectionResult selection;
            if (AppConfig.ForceInitialFullSelection)
            {
                Console.WriteLine("已启用「启动时强制全量优选」，首次启动直接进行全量优选");
                selection = await SingleSelect(dnsSetup, "首次启动");
            }
            else
            {
                selection = await TryCreateInitialSelectionFromDomains(dnsSetup)
                    ?? await SingleSelect(dnsSetup, "首次启动");
            }

            while (true)
            {
                var request = await WaitForNextSelection(selection);
                if (request != null)
                {
                    selection = await SingleSelect(await GetDnsSetup(), request.Trigger,
                        request.FailedIps == null ? null : selection, request.FailedIps);
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
            Console.WriteLine($"  配置文件: {Path.GetFullPath(SettingsManager.SettingsFile)}");
            Console.WriteLine($"  Web监听: {AppConfig.WebHost}:{AppConfig.WebPort}");
            Console.WriteLine(DnsSetup.TryCreate(SettingsManager.Current, out var dnsSetup, out var dnsError)
                ? $"  DNS更新: {FormatTargets(dnsSetup.Targets)}"
                : $"  DNS更新: (未配置：{dnsError})");
            foreach (var item in SettingsManager.BuildSummary())
            {
                Console.WriteLine($"  {item.Key}: {item.Value}");
            }
        }

        private static string FormatTargets(IEnumerable<DnsTarget> targets)
        {
            return string.Join("；", targets
                .GroupBy(target => target.Rank)
                .Select(group => $"Top{group.Key} {string.Join(", ", group.Select(target => $"[{target.ProviderName}]{target.Domain}"))}"));
        }

        /// <summary>
        /// 获取当前DNS设置对应的更新器与域名；配置不完整时进入待配置状态，直到Web页面保存了有效配置。
        /// </summary>
        private static async Task<DnsSetup> GetDnsSetup()
        {
            string? reportedError = null;
            while (true)
            {
                var settingsChanged = SettingsManager.WaitForChangeAsync();
                var dnsVersion = SettingsManager.DnsVersion;
                if (_dnsSetup != null && dnsVersion == _dnsSetupVersion)
                {
                    return _dnsSetup;
                }

                if (DnsSetup.TryCreate(SettingsManager.Current, out var setup, out var error))
                {
                    if (reportedError != null)
                    {
                        Console.WriteLine($"DNS配置已完成：{FormatTargets(setup.Targets)}");
                    }

                    _dnsSetup = setup;
                    _dnsSetupVersion = dnsVersion;
                    return setup;
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

        private static async Task<SelectionResult?> TryCreateInitialSelectionFromDomains(DnsSetup dnsSetup)
        {
            Console.WriteLine("首次启动先解析并测试已配置域名的当前IPv4地址");
            DashboardState.SetPhase(DashboardPhase.InitialCheck, "解析已配置域名");

            var resolvedIps = new Dictionary<IPAddress, ResolvedDomainIp>();
            foreach (var target in dnsSetup.Targets)
            {
                var domain = target.Domain;
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
                        resolvedIp = new ResolvedDomainIp(ip);
                        resolvedIps.Add(ip, resolvedIp);
                    }

                    resolvedIp.Targets.Add(target);
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

                foreach (var targetsByRank in resolvedIp.Targets.GroupBy(target => target.Rank))
                {
                    assignedIps.Add(new AssignedIp(resolvedIp.IP, result.delay, targetsByRank.Key, targetsByRank.ToList()));
                }
                Console.WriteLine($"首次域名预检正常：Top{resolvedIp.Rank} {resolvedIp.IP}（{string.Join(", ", resolvedIp.Domains)}），HTTP延迟：{result.delay.TotalMilliseconds:0.00}ms");
            }

            if (hasFailure)
            {
                Console.WriteLine("首次域名预检存在无法连接的IPv4地址，开始全量优选");
                return null;
            }

            Console.WriteLine($"首次域名预检的{resolvedIps.Count}个IPv4地址均正常，跳过首次全量优选");
            DashboardState.SetAssignedIps(assignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds)));
            return new SelectionResult(assignedIps.OrderBy(item => item.Rank).ToList());
        }

        /// <summary>
        /// 已配置域名但上一轮没有分配到IP的名次（例如可用IP不足或DNS更新失败）。
        /// </summary>
        private static List<int> GetMissingRanks(SelectionResult selection)
        {
            return selection.GetTargetsToUpdate(SettingsManager.GetDnsTargets(), new HashSet<IPAddress>())
                .Select(target => target.Rank)
                .Distinct()
                .Order()
                .ToList();
        }

        private static string FormatRanks(IEnumerable<int> ranks)
        {
            return string.Join("、", ranks.Select(rank => $"Top{rank}"));
        }

        private static string DescribeExpiryAction(SelectionResult selection)
        {
            var missingRanks = GetMissingRanks(selection);
            return missingRanks.Count > 0
                ? $"到期后先复检已分配IP，并补齐{FormatRanks(missingRanks)}缺失的域名分配"
                : $"到期后先复检{selection.AssignedIps.Count}个已分配IP";
        }

        /// <summary>
        /// 等待下一周期；返回优选请求，返回 null 表示本周期无需更换IP。
        /// </summary>
        private static async Task<SelectionRequest?> WaitForNextSelection(SelectionResult selection)
        {
            // 配置加载时已保证检测周期至少为1分钟
            var regularInterval = TimeSpan.FromMinutes(AppConfig.RunMinutes);
            var nextRunAt = TimestampedConsole.Now.Add(regularInterval);
            DashboardState.SetWaiting(nextRunAt);

            if (!AppConfig.EnableSupplementalHttpCheck)
            {
                Console.WriteLine(selection.AssignedIps.Count == 0
                    ? $"等待{AppConfig.RunMinutes}分钟；本轮没有已分配IP，到期后重新优选"
                    : $"等待{AppConfig.RunMinutes}分钟；{DescribeExpiryAction(selection)}");
                var request = await RuntimeControl.WaitAsync(regularInterval);
                return await HandleWaitFinished(selection, request);
            }

            if (selection.AssignedIps.Count == 0)
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；本轮没有可用IP，将在{SupplementalHttpCheckInterval.TotalMinutes:0}分钟后触发重新优选");
            }
            else
            {
                Console.WriteLine($"等待{AppConfig.RunMinutes}分钟；期间每{SupplementalHttpCheckInterval.TotalMinutes:0}分钟对 {selection.AssignedIps[0].IP} 进行补充HTTP检查，{DescribeExpiryAction(selection)}");
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
                    return new SelectionRequest("无可用IP", new HashSet<IPAddress>());
                }

                DashboardState.SetPhase(DashboardPhase.SupplementalCheck, $"补充HTTP检查 {selection.AssignedIps[0].IP}");
                var healthy = await RunSupplementalHttpCheck(selection.AssignedIps[0]);
                if (!healthy)
                {
                    Console.WriteLine($"补充HTTP检查连续{SupplementalHttpCheckCount}次失败，仅更换 {selection.AssignedIps[0].IP} 对应的域名分配并重新计时");
                    return new SelectionRequest("补充检查失败", new HashSet<IPAddress> { selection.AssignedIps[0].IP });
                }

                DashboardState.SetWaiting(nextRunAt);
            }
        }

        private static async Task<SelectionRequest?> HandleWaitFinished(SelectionResult selection, ManualRequest request)
        {
            if (request.Command == ManualCommand.FullSelection)
            {
                Console.WriteLine($"立即开始全量优选，原因：{request.Trigger}");
                return new SelectionRequest(request.Trigger);
            }

            var manual = request.Command == ManualCommand.Revalidate;
            var failedIps = await ValidateAssignedIps(selection.AssignedIps, manual);
            var missingRanks = GetMissingRanks(selection);
            if (failedIps.Count == 0 && missingRanks.Count == 0)
            {
                return null;
            }

            if (selection.AssignedIps.Count == 0)
            {
                return new SelectionRequest("无可用IP", failedIps);
            }

            if (missingRanks.Count > 0)
            {
                Console.WriteLine($"{FormatRanks(missingRanks)}存在缺失的域名分配，本轮仅补缺和更换异常IP");
            }

            return new SelectionRequest(failedIps.Count > 0
                ? (manual ? "手动复检未通过" : "定时复检未通过")
                : "补齐缺失分配", failedIps);
        }

        internal static async Task<HashSet<IPAddress>> ValidateAssignedIps(IReadOnlyList<AssignedIp> assignedIps, bool manual,
            Func<IPAddress, Task<(int success, TimeSpan totalDelay, string error)>>? probeAsync = null,
            Func<TimeSpan, Task>? delayAsync = null)
        {
            RuntimeControl.ClearPendingRevalidation();
            var failedIps = new HashSet<IPAddress>();
            var reason = manual ? "收到手动复检请求" : "到达定时优选时间";
            if (assignedIps.Count == 0)
            {
                Console.WriteLine($"{reason}，但没有已分配IP，将为缺失的域名分配优选IP");
                return failedIps;
            }

            Console.WriteLine($"{reason}，先复检{assignedIps.Count}个已分配IP；每个IP检测{HttpProbeCount}次，单次间隔{FinalProbeMinInterval.TotalSeconds:0}-{FinalProbeMaxInterval.TotalSeconds:0}秒，延迟达到动态阈值 max({AssignedDelayMinimumThreshold.TotalMilliseconds:0}ms, 基线×{AssignedDelayMaxMultiplier:0.#}) 则仅更换异常IP");
            var httpPing = new Httping();
            probeAsync ??= ip => httpPing.Ping(ip, HttpProbeCount, FinalProbeMinInterval, FinalProbeMaxInterval);
            delayAsync ??= delay => Task.Delay(delay);

            for (int i = 0; i < assignedIps.Count; i++)
            {
                var assignedIp = assignedIps[i];
                DashboardState.SetPhase(DashboardPhase.Revalidating, $"复检 Top{assignedIp.Rank} {assignedIp.IP}（{i + 1}/{assignedIps.Count}）");
                var result = await probeAsync(assignedIp.IP);
                var averageDelay = result.success > 0
                    ? TimeSpan.FromMilliseconds(result.totalDelay.TotalMilliseconds / result.success)
                    : TimeSpan.Zero;

                var passed = result.success >= HttpProbeCount && !IsAssignedDelayInvalid(assignedIp.BaselineDelay, averageDelay);
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
                    Console.WriteLine($"已分配IP复检失败：Top{assignedIp.Rank} {assignedIp.IP} HTTP成功：{result.success}/{HttpProbeCount}，原因：{FormatError(result.error)}，标记为待更换并继续复检");
                    failedIps.Add(assignedIp.IP);
                }
                else if (!passed)
                {
                    Console.WriteLine($"已分配IP复检延迟异常：Top{assignedIp.Rank} {assignedIp.IP} 当前{averageDelay.TotalMilliseconds:0.00}ms，基线{assignedIp.BaselineDelay.TotalMilliseconds:0.00}ms，标记为待更换并继续复检");
                    failedIps.Add(assignedIp.IP);
                }
                else if (averageDelay > TimeSpan.Zero && averageDelay < assignedIp.BaselineDelay)
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
                    await delayAsync(delay);
                }
            }

            Console.WriteLine(failedIps.Count == 0
                ? "已分配IP复检均正常，保留现有IP"
                : $"已完成全部IP复检，发现{failedIps.Count}个异常IP，仅更换对应的域名分配");
            return failedIps;
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

        private static async Task<SelectionResult> SingleSelect(DnsSetup dnsSetup, string trigger,
            SelectionResult? previousSelection = null, IReadOnlySet<IPAddress>? failedIps = null)
        {
            // 局部修复不能覆盖复检期间排队的手动全量优选或DNS配置变更请求。
            if (previousSelection == null)
            {
                RuntimeControl.ClearPending();
            }
            else
            {
                RuntimeControl.ClearPendingRevalidation();
            }

            var dnsUpdateProviders = dnsSetup.Providers;
            var configuredTargets = dnsSetup.Targets.Distinct().ToList();
            var targets = previousSelection == null
                ? configuredTargets
                : previousSelection.GetTargetsToUpdate(configuredTargets, failedIps ?? new HashSet<IPAddress>());
            if (targets.Count == 0)
            {
                return previousSelection ?? SelectionResult.Empty;
            }

            DashboardState.BeginRun(trigger, AppConfig.BandwidthPriority ? "带宽优先" : "延迟优先");
            Console.WriteLine(previousSelection == null
                ? $"开始全量优选，触发原因：{trigger}"
                : $"开始局部优选，触发原因：{trigger}；仅更新{FormatRanks(targets.Select(target => target.Rank).Distinct())}的{targets.Count}个异常或缺失域名分配");

            var excludedIps = previousSelection?.AssignedIps.Select(item => item.IP).ToHashSet() ?? new HashSet<IPAddress>();
            var replacements = await RunFullSelection(dnsUpdateProviders, targets, excludedIps, previousSelection == null);
            var selection = previousSelection?.Merge(replacements) ?? replacements;
            DashboardState.CompleteRun(
                replacements.AssignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds)),
                selection.AssignedIps.Select(item => (item.Rank, item.IP.ToString(), item.BaselineDelay.TotalMilliseconds)));
            return selection;
        }

        private static async Task<SelectionResult> RunFullSelection(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders,
            IReadOnlyList<DnsTarget> targets, IReadOnlySet<IPAddress> excludedIps, bool fullSelection)
        {
            // 全量优选时 TopN 使用第 N 名的IP；局部优选时缺失的名次依次使用新IP
            var requiredCount = fullSelection
                ? targets.Max(target => target.Rank)
                : targets.Select(target => target.Rank).Distinct().Count();
            // 本轮开始时读取一次，避免优选过程中修改设置导致前后不一致
            var httpCandidateCount = AppConfig.HttpCandidateCount;
            var cachedCandidateCount = Math.Min(AppConfig.CachedCandidateCount, httpCandidateCount);
            var processor = new IPProcessor();

            // 与 Web 管理页面共用同一个缓存实例，页面上的增删会立即影响本轮之后的抽样
            var subnetCache = SubnetCache.Shared;
            subnetCache.ReloadRanges();

            var ipAddresses = processor.LoadIPRanges().Where(ip => !excludedIps.Contains(ip)).ToList();
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

            var cachedCandidateIps = new HashSet<IPAddress>();
            var subnetPassed = new List<PingData>();
            var sampledSubnets = cachedCandidateCount > 0
                ? subnetCache.RandomSampleIPs(cachedCandidateCount)
                    .Where(item => !excludedIps.Contains(item.IP))
                    .ToList()
                : new List<(IPAddress IP, string Subnet)>();
            if (sampledSubnets.Any())
            {
                Console.WriteLine($"开始子网缓存阶段：从{subnetCache.SubnetCount}个缓存/24子网中随机取出{sampledSubnets.Count}个IP进行TCP Ping验证");
                DashboardState.UpdateRun(run => run.CachedSampled = sampledSubnets.Count, $"子网缓存TCP Ping（{sampledSubnets.Count}个IP）");
                TcpPing subnetTcpPing = new TcpPing(sampledSubnets.Select(item => item.IP).ToList(), SubnetProbePingCount);
                var subnetResults = await subnetTcpPing.RunAsync();
                subnetPassed = subnetResults.Where(result => result.Received > 0).ToList();
                Console.WriteLine($"子网缓存阶段：{subnetPassed.Count}/{sampledSubnets.Count}个IP通过TCP Ping，加入最终候选");
                DashboardState.UpdateRun(run => run.CachedPassed = subnetPassed.Count);
                cachedCandidateIps.UnionWith(subnetPassed.Select(result => result.IP));

                bool evicted = subnetCache.RecordTcpPingOutcomes(sampledSubnets, subnetPassed.Select(result => result.IP));
                if (evicted || subnetPassed.Count < sampledSubnets.Count)
                    subnetCache.Save();
            }

            var httpCandidates = ComposeHttpCandidates(secondRoundPassed, subnetPassed, httpCandidateCount, cachedCandidateCount);
            Console.WriteLine($"最终候选：{httpCandidates.Count}个IP（上限{httpCandidateCount}个，其中子网缓存{httpCandidates.Count(candidate => cachedCandidateIps.Contains(candidate.IP))}个）");

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
                    topNData = topHttpList.Take(requiredCount).ToList();

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

                        topNData = speedRankedList.Take(requiredCount).Select(result => result.Data).ToList();
                        Console.WriteLine($"最终按下载带宽选择IP {topNData[0].IP} {speedRankedList[0].Mbps:0.00} Mbps");
                    }
                    else
                    {
                        topHttpList.Sort((left, right) => left.Delay.TotalMicroseconds.CompareTo(right.Delay.TotalMicroseconds));
                        topNData = topHttpList.Take(requiredCount).ToList();

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
            }
            else
            {
                Console.WriteLine("最终阶段没有IP通过HTTP验证，跳过DNS更新");
            }

            var selection = await UpdateDnsAssignments(dnsUpdateProviders, targets, topNData, fullSelection);
            Console.WriteLine("单次执行完毕");
            if (selection.AssignedIps.Count > 0)
            {
                Console.WriteLine($"本轮实际分配IP：{string.Join(", ", selection.AssignedIps.Select(item => $"Top{item.Rank} {item.IP}({item.BaselineDelay.TotalMilliseconds:0.00}ms)"))}");
            }
            else
            {
                Console.WriteLine("本轮没有记录到成功分配的新IP，局部优选时保留原有分配并在下周期重试");
            }

            return selection;
        }

        /// <summary>
        /// 组合进入最终 HTTP 验证的候选：为子网缓存预留名额，其余取第2轮 ICMP 的靠前结果；
        /// 子网缓存通过的IP不足时，空出的名额继续由 ICMP 结果补足。
        /// </summary>
        internal static List<PingData> ComposeHttpCandidates(IReadOnlyList<PingData> secondRoundPassed, IReadOnlyList<PingData> cachedPassed,
            int candidateCount, int cachedCount)
        {
            var reservedForIcmp = Math.Max(0, candidateCount - cachedCount);
            var candidates = secondRoundPassed.Take(reservedForIcmp).ToList();
            foreach (var cached in cachedPassed.Take(cachedCount))
            {
                if (candidates.Count < candidateCount && !candidates.Any(candidate => candidate.IP.Equals(cached.IP)))
                    candidates.Add(cached);
            }

            foreach (var secondRoundIp in secondRoundPassed.Skip(reservedForIcmp))
            {
                if (candidates.Count >= candidateCount)
                    break;

                if (!candidates.Any(candidate => candidate.IP.Equals(secondRoundIp.IP)))
                    candidates.Add(secondRoundIp);
            }

            return candidates;
        }

        internal static async Task<SelectionResult> UpdateDnsAssignments(IReadOnlyList<DnsUpdateProvider> dnsUpdateProviders,
            IReadOnlyList<DnsTarget> targets, IReadOnlyList<PingData> candidates, bool fullSelection = false)
        {
            var assignedIps = new List<AssignedIp>();
            var targetRanks = targets.Select(target => target.Rank).Distinct().Order().ToList();
            for (int targetIndex = 0; targetIndex < targetRanks.Count; targetIndex++)
            {
                var rank = targetRanks[targetIndex];
                var candidateIndex = fullSelection ? rank - 1 : targetIndex;
                if (candidateIndex >= candidates.Count)
                {
                    Console.WriteLine($"可用新IP不足，跳过 Top{rank} 的DNS更新，保留原有分配并在下周期重试");
                    continue;
                }

                var rankedIp = candidates[candidateIndex];
                var updatedTargets = new List<DnsTarget>();
                foreach (var target in targets.Where(target => target.Rank == rank))
                {
                    var dnsUpdateProvider = dnsUpdateProviders.First(provider => provider.ProviderName == target.ProviderName);
                    var domain = target.Domain;
                    var updated = false;
                    try
                    {
                        Console.WriteLine($"开始更新[{dnsUpdateProvider.ProviderName}]域名 {domain} {rankedIp.IP}（Top{rank}）");
                        DashboardState.SetSelectionDetail($"更新DNS {domain}");
                        updated = await dnsUpdateProvider.Manager.AddOrUpdateARecord(domain, rankedIp.IP.ToString());
                        if (updated)
                        {
                            Console.WriteLine($"已完成更新[{dnsUpdateProvider.ProviderName}]域名 {domain}");
                            updatedTargets.Add(target);
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
                        Rank = rank,
                        Ip = rankedIp.IP.ToString(),
                        Success = updated
                    }));
                }

                if (updatedTargets.Count > 0)
                {
                    assignedIps.Add(new AssignedIp(rankedIp.IP, rankedIp.Delay, rank, updatedTargets));
                }
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

        private static string FormatError(string? error)
        {
            return string.IsNullOrWhiteSpace(error) ? "Unknown" : error;
        }

        private sealed class ResolvedDomainIp
        {
            public ResolvedDomainIp(IPAddress ip)
            {
                IP = ip;
            }

            public IPAddress IP { get; }
            public int Rank => Targets.Min(target => target.Rank);
            public List<DnsTarget> Targets { get; } = new();
            public IEnumerable<string> Domains => Targets.Select(target => target.Domain).Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
