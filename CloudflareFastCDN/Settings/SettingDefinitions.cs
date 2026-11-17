using CloudflareFastCDN.Services;
using CloudflareFastCDN.Utils;

namespace CloudflareFastCDN.Settings
{
    internal enum SettingType
    {
        Text,
        Secret,
        Integer,
        Boolean,
        Url,
        Headers,
        Host
    }

    internal enum SettingGroupKind
    {
        Fields,
        Provider,
        Ranking
    }

    internal sealed record SettingGroupDefinition(string Id, string Name, SettingGroupKind Kind, string? Description = null);

    /// <summary>
    /// 一个可在 Web 页面编辑的标量设置。Key 与配置文件中的 JSON 路径一致，便于对照手工编辑。
    /// </summary>
    internal sealed class SettingDefinition
    {
        public required string Key { get; init; }
        public required string Group { get; init; }
        public required string Label { get; init; }
        public required SettingType Type { get; init; }
        public string DefaultValue { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public int Min { get; init; } = int.MinValue;
        public int Max { get; init; } = int.MaxValue;
        public bool RestartRequired { get; init; }

        /// <summary>旧版环境变量 / 命令行参数 / settings.json 中使用的名称，仅用于一次性迁移。</summary>
        public IReadOnlyList<string> LegacyKeys { get; init; } = Array.Empty<string>();

        /// <summary>读取当前值（规范化后的字符串形式）。</summary>
        public required Func<AppSettings, string> Get { get; init; }

        /// <summary>写入已通过校验的规范化值。</summary>
        public required Action<AppSettings, string> Set { get; init; }
    }

    internal static class SettingDefinitions
    {
        /// <summary>
        /// 域名可使用的最大名次，与概览图表可区分的系列颜色数量一致。
        /// 实际可用名次还受每轮候选 IP 数（<see cref="SelectionSettings.HttpCandidateCount"/>）限制。
        /// </summary>
        public const int MaxRank = 8;

        public const int MaxHttpCandidateCount = 30;

        public const string GroupCloudflare = "cloudflare";
        public const string GroupTencentCloud = "tencentCloud";
        public const string GroupAlibabaCloud = "alibabaCloud";
        public const string GroupRanking = "ranking";
        public const string GroupSelection = "selection";
        public const string GroupHttp = "http";
        public const string GroupSchedule = "schedule";
        public const string GroupWeb = "web";

        public static IReadOnlyList<SettingGroupDefinition> Groups { get; } = new List<SettingGroupDefinition>
        {
            new(GroupCloudflare, "Cloudflare", SettingGroupKind.Provider),
            new(GroupTencentCloud, "腾讯云 DNSPod", SettingGroupKind.Provider),
            new(GroupAlibabaCloud, "阿里云解析", SettingGroupKind.Provider),
            new(GroupRanking, "更新排名", SettingGroupKind.Ranking,
                $"每轮优选后，放在 TopN 的域名会更新为第 N 名的 IP；任意服务商的域名都可以放到任意名次（最多 Top{MaxRank}，且不超过每轮候选 IP 数），未排名的域名不会被更新。"),
            new(GroupSelection, "优选参数", SettingGroupKind.Fields),
            new(GroupHttp, "HTTP 检测", SettingGroupKind.Fields),
            new(GroupSchedule, "运行策略", SettingGroupKind.Fields),
            new(GroupWeb, "Web 管理", SettingGroupKind.Fields),
        };

        public static IReadOnlyList<SettingDefinition> All { get; } = new List<SettingDefinition>
        {
            Secret("dns.cloudflare.apiToken", GroupCloudflare, "API Token", "需要具有 DNS 编辑权限",
                s => s.Dns.Cloudflare.ApiToken, (s, v) => s.Dns.Cloudflare.ApiToken = v, "CLOUDFLARE_KEY"),

            Secret("dns.tencentCloud.secretId", GroupTencentCloud, "SecretId", "腾讯云 API 密钥 SecretId",
                s => s.Dns.TencentCloud.SecretId, (s, v) => s.Dns.TencentCloud.SecretId = v, "TENCENTCLOUD_SECRET_ID"),
            Secret("dns.tencentCloud.secretKey", GroupTencentCloud, "SecretKey", "腾讯云 API 密钥 SecretKey",
                s => s.Dns.TencentCloud.SecretKey, (s, v) => s.Dns.TencentCloud.SecretKey = v, "TENCENTCLOUD_SECRET_KEY"),

            Secret("dns.alibabaCloud.accessKeyId", GroupAlibabaCloud, "AccessKey ID", "阿里云 API AccessKey ID",
                s => s.Dns.AlibabaCloud.AccessKeyId, (s, v) => s.Dns.AlibabaCloud.AccessKeyId = v, "ALIBABACLOUD_ACCESS_KEY_ID"),
            Secret("dns.alibabaCloud.accessKeySecret", GroupAlibabaCloud, "AccessKey Secret", "阿里云 API AccessKey Secret",
                s => s.Dns.AlibabaCloud.AccessKeySecret, (s, v) => s.Dns.AlibabaCloud.AccessKeySecret = v, "ALIBABACLOUD_ACCESS_KEY_SECRET"),

            Integer("selection.maxIps", GroupSelection, "每轮抽样 IP 数", 400, 1, 10000, "每轮全量优选从 Cloudflare 网段中最多抽样的 IP 数量",
                s => s.Selection.MaxIps, (s, v) => s.Selection.MaxIps = v, "MAX_IPS"),
            Integer("selection.httpCandidateCount", GroupSelection, "每轮候选 IP 数", 10, 1, MaxHttpCandidateCount,
                "每轮选出进入最终 HTTP 验证的 IP 数量（ICMP 筛选结果与子网缓存合计），通过验证的 IP 依次排出 Top 名次；不能小于更新排名中使用的最高名次。每个候选约需 1 分钟验证，数量越多单轮耗时越长",
                s => s.Selection.HttpCandidateCount, (s, v) => s.Selection.HttpCandidateCount = v),
            Integer("selection.cachedCandidateCount", GroupSelection, "子网缓存 IP 数", 3, 0, SubnetCache.MaxSubnets,
                $"每轮从历史优选结果的 /24 子网缓存中取出验证的 IP 数量（缓存最多保存 {SubnetCache.MaxSubnets} 个子网），通过 TCP Ping 的直接进入最终验证并占用候选名额；不能大于每轮候选 IP 数，0 表示不从缓存取 IP",
                s => s.Selection.CachedCandidateCount, (s, v) => s.Selection.CachedCandidateCount = v),
            Integer("selection.pingThreads", GroupSelection, "Ping 并发数", 8, 1, 256, "ICMP / TCP Ping 的并发线程数",
                s => s.Selection.PingThreads, (s, v) => s.Selection.PingThreads = v, "PING_THREADS"),
            Integer("selection.pingIntervalMs", GroupSelection, "Ping 间隔（毫秒）", 150, 1, 10000, "同一 IP 两次 Ping 之间的间隔",
                s => s.Selection.PingIntervalMs, (s, v) => s.Selection.PingIntervalMs = v, "PING_INTERVAL_MS"),
            Boolean("selection.bandwidthPriority", GroupSelection, "带宽优先", "开启后下载 /speedtest 测速并按带宽排序，测速失败自动回退为延迟优先",
                s => s.Selection.BandwidthPriority, (s, v) => s.Selection.BandwidthPriority = v, "BANDWIDTH_PRIORITY"),
            Boolean("selection.updateIpList", GroupSelection, "启动时更新 IP 段", "启动时从 Cloudflare 官方更新 IPv4 网段列表",
                s => s.Selection.UpdateIpList, (s, v) => s.Selection.UpdateIpList = v, "UPDATE_IP_LIST", restartRequired: true),

            new()
            {
                Key = "http.probeUrl", Group = GroupHttp, Label = "HTTP 验证地址", Type = SettingType.Url,
                DefaultValue = "https://www.visa.cn/", Description = "建议使用已开启 Cloudflare 代理（黄云）的自有站点",
                LegacyKeys = new[] { "HTTP_PROBE_URL" },
                Get = s => s.Http.ProbeUrl, Set = (s, v) => s.Http.ProbeUrl = v
            },
            new()
            {
                Key = "http.probeHeaders", Group = GroupHttp, Label = "附加请求头", Type = SettingType.Headers,
                Description = "HTTP 验证和测速请求附加的请求头，每行一个，格式为 Header-Name: Value",
                LegacyKeys = new[] { "HTTP_PROBE_HEADERS" },
                Get = s => FormatHeaders(s.Http.ProbeHeaders), Set = (s, v) => s.Http.ProbeHeaders = ParseHeaders(v)
            },
            Integer("http.probeTimeoutMs", GroupHttp, "验证超时（毫秒）", 4000, 100, 120000, "单次 HTTP 验证的超时时间",
                s => s.Http.ProbeTimeoutMs, (s, v) => s.Http.ProbeTimeoutMs = v, "HTTP_PROBE_TIMEOUT_MS"),
            Integer("http.speedTestTimeoutMs", GroupHttp, "测速总超时（毫秒）", 10000, 100, 600000, "单次下载测速的总超时时间",
                s => s.Http.SpeedTestTimeoutMs, (s, v) => s.Http.SpeedTestTimeoutMs = v, "HTTP_SPEEDTEST_TIMEOUT_MS"),
            Integer("http.speedTestIdleTimeoutMs", GroupHttp, "测速空闲超时（毫秒）", 3000, 100, 600000, "下载测速时读取数据的空闲超时时间",
                s => s.Http.SpeedTestIdleTimeoutMs, (s, v) => s.Http.SpeedTestIdleTimeoutMs = v, "HTTP_SPEEDTEST_IDLE_TIMEOUT_MS"),

            Integer("schedule.runMinutes", GroupSchedule, "检测周期（分钟）", 60, 1, 10080, "每轮完成后等待的分钟数，到期后先复检已分配 IP",
                s => s.Schedule.RunMinutes, (s, v) => s.Schedule.RunMinutes = v, "RUN_MINUTES"),
            Boolean("schedule.supplementalHttpCheck", GroupSchedule, "补充 HTTP 检查", "等待期间每 5 分钟检查一次当前最优 IP，连续 3 次失败则提前重新优选",
                s => s.Schedule.SupplementalHttpCheck, (s, v) => s.Schedule.SupplementalHttpCheck = v, "ENABLE_SUPPLEMENTAL_HTTP_CHECK"),
            Boolean("schedule.forceInitialFullSelection", GroupSchedule, "启动时强制全量优选", "跳过首次启动的域名 IP 预检，直接执行全量优选",
                s => s.Schedule.ForceInitialFullSelection, (s, v) => s.Schedule.ForceInitialFullSelection = v, "FORCE_INITIAL_FULL_SELECTION", restartRequired: true),

            Integer("web.port", GroupWeb, "监听端口", 19401, 0, 65535, "0 表示关闭 Web 管理页面（之后只能直接编辑配置文件）；Docker 部署时需与端口映射一致",
                s => s.Web.Port, (s, v) => s.Web.Port = v, "WEB_PORT", restartRequired: true),
            new()
            {
                Key = "web.host", Group = GroupWeb, Label = "监听地址", Type = SettingType.Host, RestartRequired = true,
                Description = "留空自动选择：Docker 下为 0.0.0.0，直接运行时为 127.0.0.1；从其他设备访问时填写 0.0.0.0",
                LegacyKeys = new[] { "WEB_HOST" },
                Get = s => s.Web.Host, Set = (s, v) => s.Web.Host = v
            },
            new()
            {
                Key = "web.username", Group = GroupWeb, Label = "登录用户名", Type = SettingType.Text,
                DefaultValue = "admin", Description = "Web 管理页面的登录用户名",
                LegacyKeys = new[] { "WEB_USERNAME" },
                Get = s => s.Web.Username, Set = (s, v) => s.Web.Username = v
            },
            Secret("web.password", GroupWeb, "登录密码", "留空则无需登录即可访问；暴露到局域网或公网时务必设置",
                s => s.Web.Password, (s, v) => s.Web.Password = v, "WEB_PASSWORD"),
        };

        public static SettingDefinition? Find(string key)
        {
            return All.FirstOrDefault(definition => definition.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        public static string FormatHeaders(IReadOnlyDictionary<string, string>? headers)
        {
            return headers == null
                ? string.Empty
                : string.Join(";", headers.Select(header => $"{header.Key}:{header.Value}"));
        }

        /// <summary>解析已规范化的 "Name:Value;Name2:Value2" 字符串。</summary>
        public static Dictionary<string, string> ParseHeaders(string value)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = item.IndexOf(':');
                if (separatorIndex > 0)
                {
                    headers[item[..separatorIndex].Trim()] = item[(separatorIndex + 1)..].Trim();
                }
            }

            return headers;
        }

        private static SettingDefinition Secret(string key, string group, string label, string description,
            Func<AppSettings, string> get, Action<AppSettings, string> set, string legacyKey)
        {
            return new SettingDefinition
            {
                Key = key, Group = group, Label = label, Type = SettingType.Secret, Description = description,
                LegacyKeys = new[] { legacyKey }, Get = get, Set = set
            };
        }

        private static SettingDefinition Integer(string key, string group, string label, int defaultValue, int min, int max, string description,
            Func<AppSettings, int> get, Action<AppSettings, int> set, string? legacyKey = null, bool restartRequired = false)
        {
            return new SettingDefinition
            {
                Key = key, Group = group, Label = label, Type = SettingType.Integer, DefaultValue = defaultValue.ToString(),
                Description = description, Min = min, Max = max, RestartRequired = restartRequired,
                LegacyKeys = legacyKey == null ? Array.Empty<string>() : new[] { legacyKey },
                Get = s => get(s).ToString(), Set = (s, v) => set(s, int.Parse(v))
            };
        }

        private static SettingDefinition Boolean(string key, string group, string label, string description,
            Func<AppSettings, bool> get, Action<AppSettings, bool> set, string legacyKey, bool restartRequired = false)
        {
            return new SettingDefinition
            {
                Key = key, Group = group, Label = label, Type = SettingType.Boolean, DefaultValue = "false",
                Description = description, RestartRequired = restartRequired, LegacyKeys = new[] { legacyKey },
                Get = s => get(s) ? "true" : "false", Set = (s, v) => set(s, v == "true")
            };
        }
    }

    /// <summary>
    /// 支持的 DNS 服务商。服务商的凭证是 <see cref="SettingDefinitions.All"/> 中同一分组的设置项，域名单独维护。
    /// </summary>
    internal sealed class DnsProviderDefinition
    {
        /// <summary>服务商标识，同时用于日志、DNS 更新记录和配置接口。</summary>
        public required string Id { get; init; }
        public required string GroupId { get; init; }
        public required string DisplayName { get; init; }
        public required string MissingCredentialsMessage { get; init; }

        /// <summary>旧版按名次拆分的域名变量，下标为名次 - 1，每个名次可有多个别名。</summary>
        public required IReadOnlyList<string[]> LegacyDomainKeys { get; init; }

        public required Func<AppSettings, DnsProviderSettings> Settings { get; init; }

        /// <summary>凭证不完整时返回 null。</summary>
        public required Func<AppSettings, IDnsRecordManager?> TryCreateManager { get; init; }

        public IEnumerable<SettingDefinition> CredentialDefinitions =>
            SettingDefinitions.All.Where(definition => definition.Group == GroupId);
    }

    internal static class DnsProviders
    {
        public static IReadOnlyList<DnsProviderDefinition> All { get; } = new List<DnsProviderDefinition>
        {
            new()
            {
                Id = "Cloudflare",
                GroupId = SettingDefinitions.GroupCloudflare,
                DisplayName = "Cloudflare",
                MissingCredentialsMessage = "Cloudflare 的域名已设置更新排名，但缺少 API Token",
                LegacyDomainKeys = new[]
                {
                    new[] { "CLOUDFLARE_DOMAINS", "DOMAINS" },
                    new[] { "CLOUDFLARE_DOMAINS2", "DOMAINS2" },
                    new[] { "CLOUDFLARE_DOMAINS3", "DOMAINS3" }
                },
                Settings = s => s.Dns.Cloudflare,
                TryCreateManager = s => string.IsNullOrWhiteSpace(s.Dns.Cloudflare.ApiToken)
                    ? null
                    : new CloudflareAPIManager(s.Dns.Cloudflare.ApiToken)
            },
            new()
            {
                Id = "TencentCloud",
                GroupId = SettingDefinitions.GroupTencentCloud,
                DisplayName = "腾讯云 DNSPod",
                MissingCredentialsMessage = "腾讯云 DNSPod 的域名已设置更新排名，但缺少 SecretId 或 SecretKey",
                LegacyDomainKeys = new[]
                {
                    new[] { "TENCENTCLOUD_DOMAINS" },
                    new[] { "TENCENTCLOUD_DOMAINS2" },
                    new[] { "TENCENTCLOUD_DOMAINS3" }
                },
                Settings = s => s.Dns.TencentCloud,
                TryCreateManager = s => string.IsNullOrWhiteSpace(s.Dns.TencentCloud.SecretId) || string.IsNullOrWhiteSpace(s.Dns.TencentCloud.SecretKey)
                    ? null
                    : new TencentCloudDnsManager(s.Dns.TencentCloud.SecretId, s.Dns.TencentCloud.SecretKey)
            },
            new()
            {
                Id = "AlibabaCloud",
                GroupId = SettingDefinitions.GroupAlibabaCloud,
                DisplayName = "阿里云解析",
                MissingCredentialsMessage = "阿里云解析的域名已设置更新排名，但缺少 AccessKey ID 或 AccessKey Secret",
                LegacyDomainKeys = new[]
                {
                    new[] { "ALIBABACLOUD_DOMAINS" },
                    new[] { "ALIBABACLOUD_DOMAINS2" },
                    new[] { "ALIBABACLOUD_DOMAINS3" }
                },
                Settings = s => s.Dns.AlibabaCloud,
                TryCreateManager = s => string.IsNullOrWhiteSpace(s.Dns.AlibabaCloud.AccessKeyId) || string.IsNullOrWhiteSpace(s.Dns.AlibabaCloud.AccessKeySecret)
                    ? null
                    : new AlibabaCloudDnsManager(s.Dns.AlibabaCloud.AccessKeyId, s.Dns.AlibabaCloud.AccessKeySecret)
            }
        };

        public static DnsProviderDefinition? Find(string id)
        {
            return All.FirstOrDefault(provider => provider.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 需要维护的域名及其名次，按名次、服务商、添加顺序排列。
        /// </summary>
        public static List<DnsTarget> GetTargets(AppSettings settings)
        {
            return All
                .SelectMany((provider, providerIndex) => provider.Settings(settings).Domains
                    .Select((domain, domainIndex) => (provider, providerIndex, domain, domainIndex)))
                .Where(item => item.domain.Rank.HasValue)
                .OrderBy(item => item.domain.Rank)
                .ThenBy(item => item.providerIndex)
                .ThenBy(item => item.domainIndex)
                .Select(item => new DnsTarget(item.provider.Id, item.domain.Name, item.domain.Rank!.Value))
                .ToList();
        }
    }
}
