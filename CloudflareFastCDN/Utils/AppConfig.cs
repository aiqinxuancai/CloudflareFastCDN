namespace CloudflareFastCDN.Utils
{
    internal class AppConfig
    {
        public static string CloudflareKey { get; set; } = string.Empty;
        public static string TencentCloudSecretId { get; set; } = string.Empty;
        public static string TencentCloudSecretKey { get; set; } = string.Empty;
        public static string AlibabaCloudAccessKeyId { get; set; } = string.Empty;
        public static string AlibabaCloudAccessKeySecret { get; set; } = string.Empty;

        public static string[] CloudflareDomains { get; set; } = Array.Empty<string>();
        public static string[] CloudflareDomains2 { get; set; } = Array.Empty<string>();
        public static string[] CloudflareDomains3 { get; set; } = Array.Empty<string>();
        public static string[] TencentCloudDomains { get; set; } = Array.Empty<string>();
        public static string[] TencentCloudDomains2 { get; set; } = Array.Empty<string>();
        public static string[] TencentCloudDomains3 { get; set; } = Array.Empty<string>();
        public static string[] AlibabaCloudDomains { get; set; } = Array.Empty<string>();
        public static string[] AlibabaCloudDomains2 { get; set; } = Array.Empty<string>();
        public static string[] AlibabaCloudDomains3 { get; set; } = Array.Empty<string>();

        public static int PingThreads { get; set; } = 8;
        public static int MaxIps { get; set; } = 400;
        public static int PingIntervalMs { get; set; } = 150;
        public static string HttpProbeUrl { get; set; } = "https://www.visa.cn/";
        public static IReadOnlyDictionary<string, string> HttpProbeHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static int HttpProbeTimeoutMs { get; set; } = 4000;
        public static int HttpSpeedTestTimeoutMs { get; set; } = 10000;
        public static int HttpSpeedTestIdleTimeoutMs { get; set; } = 3000;
        public static int RunMinutes { get; set; } = 60;
        public static bool BandwidthPriority { get; set; } = false;
        public static bool UpdateIPList { get; set; } = false;
        public static bool EnableSupplementalHttpCheck { get; set; } = false;
    }
}
