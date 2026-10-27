namespace CloudflareFastCDN.Settings
{
    internal enum SettingType
    {
        Text,
        Secret,
        Integer,
        Boolean,
        Url,
        DomainList,
        Headers
    }

    internal sealed record SettingDefinition(
        string Key,
        string Group,
        string Label,
        SettingType Type,
        string DefaultValue,
        string Description,
        int Min = int.MinValue,
        int Max = int.MaxValue,
        bool RestartRequired = false,
        bool AffectsDns = false,
        string? Alias = null);

    internal static class SettingDefinitions
    {
        public const string GroupCloudflare = "Cloudflare";
        public const string GroupTencentCloud = "腾讯云 DNSPod";
        public const string GroupAlibabaCloud = "阿里云解析";
        public const string GroupSelection = "优选参数";
        public const string GroupHttp = "HTTP 检测";
        public const string GroupSchedule = "运行策略";
        public const string GroupWeb = "Web 管理";

        public static IReadOnlyList<SettingDefinition> All { get; } = new List<SettingDefinition>
        {
            new("CLOUDFLARE_KEY", GroupCloudflare, "API Token", SettingType.Secret, "", "需要具有 DNS 编辑权限", AffectsDns: true),
            new("CLOUDFLARE_DOMAINS", GroupCloudflare, "第 1 名 IP 的域名", SettingType.DomainList, "", "使用优选第 1 名 IP 更新的域名", AffectsDns: true, Alias: "DOMAINS"),
            new("CLOUDFLARE_DOMAINS2", GroupCloudflare, "第 2 名 IP 的域名", SettingType.DomainList, "", "使用优选第 2 名 IP 更新的域名", AffectsDns: true, Alias: "DOMAINS2"),
            new("CLOUDFLARE_DOMAINS3", GroupCloudflare, "第 3 名 IP 的域名", SettingType.DomainList, "", "使用优选第 3 名 IP 更新的域名", AffectsDns: true, Alias: "DOMAINS3"),

            new("TENCENTCLOUD_SECRET_ID", GroupTencentCloud, "SecretId", SettingType.Secret, "", "腾讯云 API 密钥 SecretId", AffectsDns: true),
            new("TENCENTCLOUD_SECRET_KEY", GroupTencentCloud, "SecretKey", SettingType.Secret, "", "腾讯云 API 密钥 SecretKey", AffectsDns: true),
            new("TENCENTCLOUD_DOMAINS", GroupTencentCloud, "第 1 名 IP 的域名", SettingType.DomainList, "", "使用优选第 1 名 IP 更新的域名", AffectsDns: true),
            new("TENCENTCLOUD_DOMAINS2", GroupTencentCloud, "第 2 名 IP 的域名", SettingType.DomainList, "", "使用优选第 2 名 IP 更新的域名", AffectsDns: true),
            new("TENCENTCLOUD_DOMAINS3", GroupTencentCloud, "第 3 名 IP 的域名", SettingType.DomainList, "", "使用优选第 3 名 IP 更新的域名", AffectsDns: true),

            new("ALIBABACLOUD_ACCESS_KEY_ID", GroupAlibabaCloud, "AccessKey ID", SettingType.Secret, "", "阿里云 API AccessKey ID", AffectsDns: true),
            new("ALIBABACLOUD_ACCESS_KEY_SECRET", GroupAlibabaCloud, "AccessKey Secret", SettingType.Secret, "", "阿里云 API AccessKey Secret", AffectsDns: true),
            new("ALIBABACLOUD_DOMAINS", GroupAlibabaCloud, "第 1 名 IP 的域名", SettingType.DomainList, "", "使用优选第 1 名 IP 更新的域名", AffectsDns: true),
            new("ALIBABACLOUD_DOMAINS2", GroupAlibabaCloud, "第 2 名 IP 的域名", SettingType.DomainList, "", "使用优选第 2 名 IP 更新的域名", AffectsDns: true),
            new("ALIBABACLOUD_DOMAINS3", GroupAlibabaCloud, "第 3 名 IP 的域名", SettingType.DomainList, "", "使用优选第 3 名 IP 更新的域名", AffectsDns: true),

            new("MAX_IPS", GroupSelection, "每轮抽样 IP 数", SettingType.Integer, "400", "每轮全量优选从 Cloudflare 网段中最多抽样的 IP 数量", Min: 1, Max: 10000),
            new("PING_THREADS", GroupSelection, "Ping 并发数", SettingType.Integer, "8", "ICMP / TCP Ping 的并发线程数", Min: 1, Max: 256),
            new("PING_INTERVAL_MS", GroupSelection, "Ping 间隔（毫秒）", SettingType.Integer, "150", "同一 IP 两次 Ping 之间的间隔", Min: 1, Max: 10000),
            new("BANDWIDTH_PRIORITY", GroupSelection, "带宽优先", SettingType.Boolean, "false", "开启后下载 /speedtest 测速并按带宽排序，测速失败自动回退为延迟优先"),
            new("UPDATE_IP_LIST", GroupSelection, "启动时更新 IP 段", SettingType.Boolean, "false", "启动时从 Cloudflare 官方更新 IPv4 网段列表", RestartRequired: true),

            new("HTTP_PROBE_URL", GroupHttp, "HTTP 验证地址", SettingType.Url, "https://www.visa.cn/", "建议使用已开启 Cloudflare 代理（黄云）的自有站点"),
            new("HTTP_PROBE_HEADERS", GroupHttp, "附加请求头", SettingType.Headers, "", "HTTP 验证和测速请求附加的请求头，每行一个，格式为 Header-Name: Value"),
            new("HTTP_PROBE_TIMEOUT_MS", GroupHttp, "验证超时（毫秒）", SettingType.Integer, "4000", "单次 HTTP 验证的超时时间", Min: 100, Max: 120000),
            new("HTTP_SPEEDTEST_TIMEOUT_MS", GroupHttp, "测速总超时（毫秒）", SettingType.Integer, "10000", "单次下载测速的总超时时间", Min: 100, Max: 600000),
            new("HTTP_SPEEDTEST_IDLE_TIMEOUT_MS", GroupHttp, "测速空闲超时（毫秒）", SettingType.Integer, "3000", "下载测速时读取数据的空闲超时时间", Min: 100, Max: 600000),

            new("RUN_MINUTES", GroupSchedule, "检测周期（分钟）", SettingType.Integer, "60", "每轮完成后等待的分钟数，到期后先复检已分配 IP", Min: 1, Max: 10080),
            new("ENABLE_SUPPLEMENTAL_HTTP_CHECK", GroupSchedule, "补充 HTTP 检查", SettingType.Boolean, "false", "等待期间每 5 分钟检查一次当前最优 IP，连续 3 次失败则提前重新优选"),
            new("FORCE_INITIAL_FULL_SELECTION", GroupSchedule, "启动时强制全量优选", SettingType.Boolean, "false", "跳过首次启动的域名 IP 预检，直接执行全量优选", RestartRequired: true),

            new("WEB_USERNAME", GroupWeb, "登录用户名", SettingType.Text, "admin", "Web 管理页面的登录用户名"),
            new("WEB_PASSWORD", GroupWeb, "登录密码", SettingType.Secret, "", "留空则无需登录即可访问；暴露到局域网或公网时务必设置"),
        };

        public static SettingDefinition? Find(string key)
        {
            return All.FirstOrDefault(definition => definition.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
