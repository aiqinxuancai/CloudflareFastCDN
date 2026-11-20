using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudflareFastCDN.Utils
{
    public class SubnetRecord
    {
        public string Subnet { get; set; } = string.Empty;   // e.g. "104.16.1.0/24"
        public double DelayMs { get; set; }  // HTTP delay of the IP that discovered this subnet; 0 = not measured yet (manual entries)
        public int ConsecutiveFailures { get; set; } = 0; // TcpPing failure streak from subnet cache phase

        /// <summary>通过 Web 管理页面手动添加；手动子网固定排在最前，不会因缓存已满被挤出。</summary>
        public bool Manual { get; set; }

        /// <summary>加入缓存的时间；旧版缓存文件中没有该字段。</summary>
        public DateTimeOffset? AddedAt { get; set; }

        public SubnetRecord Clone() => (SubnetRecord)MemberwiseClone();
    }

    public readonly record struct SubnetEditResult(bool Ok, string Message);

    /// <summary>
    /// 历史优选结果的 /24 子网缓存。主循环和 Web 管理页面共用 <see cref="Shared"/> 实例，所有成员线程安全。
    /// </summary>
    public partial class SubnetCache
    {
        public const int MaxSubnets = 10;
        public const int EvictThreshold = 3;
        public const string CacheFileName = "subnet_cache.json";

        private static readonly Lazy<SubnetCache> SharedInstance = new(() =>
        {
            var cache = new SubnetCache();
            cache.Load();
            return cache;
        });

        /// <summary>程序共用的缓存实例，首次访问时从数据目录加载。</summary>
        public static SubnetCache Shared => SharedInstance.Value;

        private readonly object _sync = new();
        private readonly string? _ipFile;
        private readonly string _cacheFile;
        private List<SubnetRecord> _subnets = new();
        private List<(IPAddress Network, int Prefix)> _cfRanges = new();
        private int _version;

        public int SubnetCount
        {
            get
            {
                lock (_sync)
                {
                    return _subnets.Count;
                }
            }
        }

        /// <summary>每次缓存内容变化时递增，供管理页面判断是否需要刷新。</summary>
        public int Version => Volatile.Read(ref _version);

        /// <param name="ipFile">网段列表文件；为空时每次读取 <see cref="AppPaths.IpListFile"/>。</param>
        /// <param name="cacheFile">缓存文件；为空时使用数据目录中的 subnet_cache.json。</param>
        public SubnetCache(string? ipFile = null, string? cacheFile = null)
        {
            _ipFile = ipFile;
            _cacheFile = cacheFile ?? AppPaths.DataFile(CacheFileName);
        }

        public void Load()
        {
            ReloadRanges();

            List<SubnetRecord> subnets;
            try
            {
                subnets = File.Exists(_cacheFile)
                    ? JsonSerializer.Deserialize(File.ReadAllText(_cacheFile), SubnetCacheJsonContext.Default.ListSubnetRecord) ?? new()
                    : new();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取子网缓存失败: {ex.Message}");
                subnets = new();
            }

            lock (_sync)
            {
                _subnets = subnets.Where(record => !string.IsNullOrEmpty(record.Subnet)).ToList();
                SortLocked();
                _version++;
            }
        }

        /// <summary>重新读取 Cloudflare 网段列表（每轮优选开始时调用，网段列表可能在运行期间被更新）。</summary>
        public void ReloadRanges()
        {
            var ipFile = _ipFile ?? AppPaths.IpListFile;
            var ranges = new List<(IPAddress Network, int Prefix)>();
            if (File.Exists(ipFile))
            {
                foreach (var line in File.ReadLines(ipFile))
                {
                    var l = line.Trim();
                    if (string.IsNullOrEmpty(l))
                        continue;

                    if (l.Contains('/'))
                    {
                        var parts = l.Split('/');
                        if (IPAddress.TryParse(parts[0], out var net) && int.TryParse(parts[1], out var prefix))
                            ranges.Add((net, prefix));
                    }
                    else if (IPAddress.TryParse(l, out var single))
                    {
                        ranges.Add((single, 32));
                    }
                }
            }

            lock (_sync)
            {
                _cfRanges = ranges;
            }
        }

        /// <summary>按抽样顺序返回缓存内容的副本。</summary>
        public List<SubnetRecord> Snapshot()
        {
            lock (_sync)
            {
                return _subnets.Select(record => record.Clone()).ToList();
            }
        }

        /// <summary>
        /// Given an IPv4 address, returns its /24 subnet string (e.g. "1.2.3.0/24") if a CF CIDR
        /// with prefix length ≤ 24 covers it (meaning a full /24 is available to sample from).
        /// Returns null if no matching CF range is found or the range is smaller than /24.
        /// </summary>
        public string? GetSubnet24(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return null;

            List<(IPAddress Network, int Prefix)> ranges;
            lock (_sync)
            {
                ranges = _cfRanges;
            }

            var bytes = ip.GetAddressBytes();

            foreach (var (network, prefix) in ranges)
            {
                if (network.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                if (!IsInRange(ip, network, prefix))
                    continue;

                // Only cache when CF covers a full /24 or larger block
                if (prefix <= 24)
                    return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";

                return null;
            }

            return null;
        }

        private static bool IsInRange(IPAddress ip, IPAddress network, int prefix)
        {
            var ipBytes = ip.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (ipBytes.Length != netBytes.Length)
                return false;

            uint mask = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
            uint ipInt = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);
            uint netInt = (uint)(netBytes[0] << 24 | netBytes[1] << 16 | netBytes[2] << 8 | netBytes[3]);
            return (ipInt & mask) == (netInt & mask);
        }

        /// <summary>
        /// Adds or updates a /24 subnet entry. Maintains sampling order (manual entries first, then ascending delay)
        /// and caps at MaxSubnets.
        /// If the subnet already exists and the new delay is better (lower), updates and re-sorts.
        /// If it doesn't exist, inserts in sorted position and removes the slowest automatic entry if over the cap.
        /// </summary>
        public void AddOrUpdate(string subnet24, double delayMs)
        {
            lock (_sync)
            {
                var existing = _subnets.FirstOrDefault(s => s.Subnet == subnet24);
                if (existing != null)
                {
                    // 手动添加的子网在第一次测得延迟前记为 0
                    if (existing.DelayMs <= 0 || delayMs < existing.DelayMs)
                        existing.DelayMs = delayMs;
                    existing.ConsecutiveFailures = 0; // confirmed good again
                }
                else
                {
                    _subnets.Add(new SubnetRecord { Subnet = subnet24, DelayMs = delayMs, AddedAt = DateTimeOffset.Now });
                }

                SortLocked();
                TrimLocked();
                _version++;
            }
        }

        /// <summary>
        /// 手动添加一个 /24 子网。可以输入 "a.b.c.0/24" 或该子网内的任意 IP；
        /// 子网必须被网段列表中 /24 或更大的网段覆盖。缓存已满时移除延迟最高的自动子网，全部是手动子网时拒绝添加。
        /// 成功后立即保存。
        /// </summary>
        public SubnetEditResult AddManual(string? input)
        {
            if (!TryNormalizeSubnet24(input, out var subnet, out var error))
                return new SubnetEditResult(false, error);

            var baseIp = IPAddress.Parse(subnet[..subnet.IndexOf('/')]);
            if (GetSubnet24(baseIp) == null)
                return new SubnetEditResult(false, $"{subnet} 不在 Cloudflare IP 段列表（ip.txt）中，或所在网段小于 /24");

            string? evicted = null;
            lock (_sync)
            {
                if (_subnets.Any(s => s.Subnet == subnet))
                    return new SubnetEditResult(false, $"{subnet} 已在子网缓存中");

                if (_subnets.Count >= MaxSubnets)
                {
                    var slowestAuto = _subnets.LastOrDefault(s => !s.Manual);
                    if (slowestAuto == null)
                        return new SubnetEditResult(false, $"子网缓存已满（最多 {MaxSubnets} 个），请先删除不需要的子网");

                    _subnets.Remove(slowestAuto);
                    evicted = slowestAuto.Subnet;
                }

                _subnets.Add(new SubnetRecord { Subnet = subnet, Manual = true, AddedAt = DateTimeOffset.Now });
                SortLocked();
                _version++;
            }

            Save();
            Console.WriteLine(evicted == null
                ? $"已手动添加子网缓存 {subnet}"
                : $"已手动添加子网缓存 {subnet}，缓存已满，移除了延迟最高的 {evicted}");
            return new SubnetEditResult(true, evicted == null
                ? $"已添加 {subnet}"
                : $"已添加 {subnet}；缓存已满，已移除延迟最高的 {evicted}");
        }

        /// <summary>删除一个子网（自动或手动添加的均可），成功后立即保存。</summary>
        public SubnetEditResult Remove(string? input)
        {
            if (!TryNormalizeSubnet24(input, out var subnet, out var error))
                return new SubnetEditResult(false, error);

            lock (_sync)
            {
                if (_subnets.RemoveAll(s => s.Subnet == subnet) == 0)
                    return new SubnetEditResult(false, $"{subnet} 不在子网缓存中");
                _version++;
            }

            Save();
            Console.WriteLine($"已手动删除子网缓存 {subnet}");
            return new SubnetEditResult(true, $"已删除 {subnet}");
        }

        /// <summary>
        /// 把 "a.b.c.d" 或 "a.b.c.d/24" 规范化为 "a.b.c.0/24"。只接受点分十进制的 IPv4 地址和 /24 前缀。
        /// </summary>
        public static bool TryNormalizeSubnet24(string? input, out string subnet, out string error)
        {
            subnet = string.Empty;
            var text = input?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                error = "请输入子网，例如 104.16.1.0/24";
                return false;
            }

            var slashIdx = text.IndexOf('/');
            var address = slashIdx >= 0 ? text[..slashIdx].Trim() : text;
            if (slashIdx >= 0 && text[(slashIdx + 1)..].Trim() != "24")
            {
                error = $"{text} 无效：仅支持 /24 子网，也可以直接输入子网内的任意 IP";
                return false;
            }

            if (!Ipv4Pattern().IsMatch(address)
                || !IPAddress.TryParse(address, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"{text} 不是有效的 IPv4 地址或 /24 子网";
                return false;
            }

            var bytes = ip.GetAddressBytes();
            subnet = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
            error = string.Empty;
            return true;
        }

        [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$")]
        private static partial Regex Ipv4Pattern();

        /// <summary>
        /// Randomly samples one IP from each cached subnet (up to <paramref name="count"/> subnets).
        /// Returns (IP, Subnet) pairs so callers can correlate TcpPing results back to subnets.
        /// </summary>
        public List<(IPAddress IP, string Subnet)> RandomSampleIPs(int count)
        {
            var result = new List<(IPAddress, string)>();
            var rng = new Random();

            lock (_sync)
            {
                foreach (var record in _subnets.Take(count))
                {
                    var ip = GetRandomIPFromSubnet24(record.Subnet, rng);
                    if (ip != null)
                        result.Add((ip, record.Subnet));
                }
            }

            return result;
        }

        /// <summary>
        /// Updates consecutive-failure counters based on TcpPing results from the subnet cache phase.
        /// Subnets whose sampled IP passed have their counter reset to 0.
        /// Subnets whose sampled IP failed have their counter incremented; once it reaches
        /// <paramref name="evictThreshold"/> they are evicted from the cache.
        /// Subnets removed from the cache in the meantime (e.g. via the web page) are ignored.
        /// Returns true if any subnets were evicted.
        /// </summary>
        public bool RecordTcpPingOutcomes(
            IEnumerable<(IPAddress IP, string Subnet)> sampled,
            IEnumerable<IPAddress> passedIPs,
            int evictThreshold = EvictThreshold)
        {
            var passedSet = new HashSet<IPAddress>(passedIPs);
            var toRemove = new List<string>();

            lock (_sync)
            {
                foreach (var (ip, subnet) in sampled)
                {
                    var record = _subnets.FirstOrDefault(s => s.Subnet == subnet);
                    if (record == null)
                        continue;

                    if (passedSet.Contains(ip))
                    {
                        record.ConsecutiveFailures = 0;
                    }
                    else
                    {
                        record.ConsecutiveFailures++;
                        if (record.ConsecutiveFailures >= evictThreshold)
                        {
                            toRemove.Add(subnet);
                            Console.WriteLine($"子网缓存淘汰：{subnet} 连续{record.ConsecutiveFailures}次TCP Ping失败，已移除");
                        }
                    }
                }

                foreach (var subnet in toRemove)
                    _subnets.RemoveAll(s => s.Subnet == subnet);

                _version++;
            }

            return toRemove.Count > 0;
        }

        private static IPAddress? GetRandomIPFromSubnet24(string subnet24, Random rng)
        {
            var slashIdx = subnet24.IndexOf('/');
            var baseStr = slashIdx >= 0 ? subnet24[..slashIdx] : subnet24;
            if (!IPAddress.TryParse(baseStr, out var baseIP))
                return null;

            var bytes = baseIP.GetAddressBytes();
            bytes[3] = (byte)rng.Next(1, 255); // host part 1–254
            return new IPAddress(bytes);
        }

        /// <summary>抽样顺序：手动子网在前，其余按延迟升序；排序稳定，相同延迟保持原有顺序。</summary>
        private void SortLocked()
        {
            _subnets = _subnets
                .OrderByDescending(s => s.Manual)
                .ThenBy(s => s.DelayMs)
                .ToList();
        }

        /// <summary>超出上限时从末尾移除自动子网；手动子网不会被挤出。</summary>
        private void TrimLocked()
        {
            while (_subnets.Count > MaxSubnets)
            {
                var index = _subnets.FindLastIndex(s => !s.Manual);
                if (index < 0)
                    break;
                _subnets.RemoveAt(index);
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // 在锁内写入，避免主循环与管理页面同时保存时互相覆盖
                lock (_sync)
                {
                    var json = JsonSerializer.Serialize(_subnets, SubnetCacheJsonContext.Default.ListSubnetRecord);
                    File.WriteAllText(_cacheFile, json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存子网缓存失败: {ex.Message}");
            }
        }
    }

    /// <summary>缓存文件沿用原有的属性名（PascalCase），兼容旧版文件；使用源生成以支持 AOT 发布。</summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(List<SubnetRecord>))]
    internal partial class SubnetCacheJsonContext : JsonSerializerContext
    {
    }
}
