using System.Text.Json.Serialization;

namespace CloudflareFastCDN.Settings
{
    /// <summary>
    /// 本地配置文件 settings.json 的完整内容。程序运行期间把它当作不可变快照使用：
    /// 修改设置时先克隆、校验，写入文件成功后再整体替换。
    /// </summary>
    internal sealed class AppSettings
    {
        public const int CurrentVersion = 2;

        /// <summary>配置文件格式版本；旧版（只有 values 字典）没有这个字段。</summary>
        public int Version { get; set; } = CurrentVersion;

        public DnsSettings Dns { get; set; } = new();
        public SelectionSettings Selection { get; set; } = new();
        public HttpSettings Http { get; set; } = new();
        public ScheduleSettings Schedule { get; set; } = new();
        public WebSettings Web { get; set; } = new();
    }

    internal sealed class DnsSettings
    {
        public CloudflareSettings Cloudflare { get; set; } = new();
        public TencentCloudSettings TencentCloud { get; set; } = new();
        public AlibabaCloudSettings AlibabaCloud { get; set; } = new();
    }

    internal abstract class DnsProviderSettings
    {
        /// <summary>属于该服务商的域名；只有设置了排名的域名才会被更新。</summary>
        [JsonPropertyOrder(100)]
        public List<DomainEntry> Domains { get; set; } = new();
    }

    internal sealed class CloudflareSettings : DnsProviderSettings
    {
        public string ApiToken { get; set; } = string.Empty;
    }

    internal sealed class TencentCloudSettings : DnsProviderSettings
    {
        public string SecretId { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
    }

    internal sealed class AlibabaCloudSettings : DnsProviderSettings
    {
        public string AccessKeyId { get; set; } = string.Empty;
        public string AccessKeySecret { get; set; } = string.Empty;
    }

    internal sealed class DomainEntry
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>使用第几名的优选 IP 更新（1 起），为空表示未排名、不更新。</summary>
        public int? Rank { get; set; }
    }

    internal sealed class SelectionSettings
    {
        public int MaxIps { get; set; } = 400;

        /// <summary>每轮进入最终 HTTP 验证的候选 IP 数量（ICMP 筛选结果与子网缓存合计）。</summary>
        public int HttpCandidateCount { get; set; } = 10;

        /// <summary>每轮从子网缓存中抽取验证的 IP 数量，0 表示不使用子网缓存。</summary>
        public int CachedCandidateCount { get; set; } = 3;

        public int PingThreads { get; set; } = 8;
        public int PingIntervalMs { get; set; } = 150;
        public bool BandwidthPriority { get; set; }
        public bool UpdateIpList { get; set; }
    }

    internal sealed class HttpSettings
    {
        public string ProbeUrl { get; set; } = "https://www.visa.cn/";
        public Dictionary<string, string> ProbeHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int ProbeTimeoutMs { get; set; } = 4000;
        public int SpeedTestTimeoutMs { get; set; } = 10000;
        public int SpeedTestIdleTimeoutMs { get; set; } = 3000;
    }

    internal sealed class ScheduleSettings
    {
        public int RunMinutes { get; set; } = 60;
        public bool SupplementalHttpCheck { get; set; }
        public bool ForceInitialFullSelection { get; set; }
    }

    internal sealed class WebSettings
    {
        public int Port { get; set; } = 19401;

        /// <summary>为空时自动选择：Docker 下 0.0.0.0，直接运行时 127.0.0.1。</summary>
        public string Host { get; set; } = string.Empty;

        public string Username { get; set; } = "admin";
        public string Password { get; set; } = string.Empty;
    }

    // ---------- Web API ----------

    internal sealed class SettingsResponse
    {
        public string SettingsFile { get; set; } = string.Empty;
        public List<StartupItem> Startup { get; set; } = new();
        public int MaxRank { get; set; }
        public List<SettingsGroup> Groups { get; set; } = new();
        public List<DomainItem> Domains { get; set; } = new();
    }

    internal sealed class StartupItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    internal sealed class SettingsGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>fields / provider / ranking</summary>
        public string Kind { get; set; } = "fields";
        /// <summary>Kind 为 provider 时对应的服务商标识。</summary>
        public string? Provider { get; set; }
        public string? Description { get; set; }
        public List<SettingItem> Items { get; set; } = new();
    }

    internal sealed class SettingItem
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? DefaultValue { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public bool RestartRequired { get; set; }
        /// <summary>当前生效值；敏感项不返回。</summary>
        public string? Value { get; set; }
        public bool IsSet { get; set; }
        /// <summary>敏感项的脱敏提示，例如密钥前缀或请求头名称。</summary>
        public string? Hint { get; set; }
    }

    internal sealed class DomainItem
    {
        public string Provider { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? Rank { get; set; }
    }

    internal sealed class SettingsUpdateRequest
    {
        public Dictionary<string, string?> Values { get; set; } = new();

        /// <summary>完整的域名列表（含排名）；为空表示域名没有变化。</summary>
        public List<DomainItem>? Domains { get; set; }
    }

    internal sealed class SettingsUpdateResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool DnsChanged { get; set; }
        public bool SelectionQueued { get; set; }
        public Dictionary<string, string> Errors { get; set; } = new();
    }

    // 配置文件可能被手工编辑，读取时容忍注释、尾逗号、大小写差异和字符串形式的数字
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(SettingsResponse))]
    [JsonSerializable(typeof(SettingsUpdateRequest))]
    [JsonSerializable(typeof(SettingsUpdateResponse))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
