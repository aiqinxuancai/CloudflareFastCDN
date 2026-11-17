namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 程序使用的文件位置。可写数据（配置、历史、缓存）在 Docker 下保存到数据卷 /data，直接运行时保存到当前目录；
    /// 随程序发布的只读文件始终从程序所在目录读取，不受启动目录影响。
    /// </summary>
    internal static class AppPaths
    {
        public const string IpListFileName = "ip.txt";

        public static bool IsDocker { get; } = File.Exists("/.dockerenv");

        public static string DataFile(string fileName)
        {
            return IsDocker ? Path.Combine("/data", fileName) : fileName;
        }

        public static string BundledFile(string fileName)
        {
            return Path.Combine(AppContext.BaseDirectory, fileName);
        }

        /// <summary>
        /// Cloudflare IPv4 网段列表：优先使用数据目录中的副本（「启动时更新 IP 段」下载的列表或用户自定义的列表），
        /// 否则使用随程序发布的列表。
        /// </summary>
        public static string IpListFile
        {
            get
            {
                var dataFile = DataFile(IpListFileName);
                return File.Exists(dataFile) ? dataFile : BundledFile(IpListFileName);
            }
        }
    }
}
