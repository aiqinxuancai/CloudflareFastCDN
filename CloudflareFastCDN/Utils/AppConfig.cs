using CloudflareFastCDN.Settings;

namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 运行参数的只读视图，始终读取 <see cref="SettingsManager.Current"/>，Web 页面保存后立即生效。
    /// 监听端口与地址只在启动时读取一次，修改后需重启。
    /// </summary>
    internal static class AppConfig
    {
        private static AppSettings Settings => SettingsManager.Current;

        public static int PingThreads => Settings.Selection.PingThreads;
        public static int MaxIps => Settings.Selection.MaxIps;
        public static int HttpCandidateCount => Settings.Selection.HttpCandidateCount;
        public static int CachedCandidateCount => Settings.Selection.CachedCandidateCount;
        public static int PingIntervalMs => Settings.Selection.PingIntervalMs;
        public static bool BandwidthPriority => Settings.Selection.BandwidthPriority;
        public static bool UpdateIPList => Settings.Selection.UpdateIpList;

        public static string HttpProbeUrl => Settings.Http.ProbeUrl;
        public static IReadOnlyDictionary<string, string> HttpProbeHeaders => Settings.Http.ProbeHeaders;
        public static int HttpProbeTimeoutMs => Settings.Http.ProbeTimeoutMs;
        public static int HttpSpeedTestTimeoutMs => Settings.Http.SpeedTestTimeoutMs;
        public static int HttpSpeedTestIdleTimeoutMs => Settings.Http.SpeedTestIdleTimeoutMs;

        public static int RunMinutes => Settings.Schedule.RunMinutes;
        public static bool EnableSupplementalHttpCheck => Settings.Schedule.SupplementalHttpCheck;
        public static bool ForceInitialFullSelection => Settings.Schedule.ForceInitialFullSelection;

        public static string WebUsername => Settings.Web.Username;
        public static string WebPassword => Settings.Web.Password;

        /// <summary>启动时确定的监听端口，0 表示未启用 Web 管理页面。</summary>
        public static int WebPort { get; set; } = 19401;

        /// <summary>启动时确定的监听地址。</summary>
        public static string WebHost { get; set; } = "127.0.0.1";
    }
}
