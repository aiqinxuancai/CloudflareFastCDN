using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudflareFastCDN.Utils
{
    internal class AppConfig
    {
        public AppConfig() { }

        public static string CloudflareKey { get; set; }

        public static string[] Domains { get; set; }


        public static int PingThreads { get; set; } = 16;

        public static int MaxIps { get; set; } = 400;

        public static int PingIntervalMs { get; set; } = 150;

        public static string HttpProbeUrl { get; set; } = "https://www.visa.cn/";

        public static int RunMinutes { get; set; } = 30;

        public static string SelectionPriority { get; set; } = "latency";

        public static bool IsBandwidthPriority =>
            string.Equals(SelectionPriority, "bandwidth", StringComparison.OrdinalIgnoreCase);

        public static bool UpdateIPList { get; set; } = false;



    }
}
