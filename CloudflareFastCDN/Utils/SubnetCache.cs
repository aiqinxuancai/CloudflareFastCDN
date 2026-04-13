using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    public class SubnetRecord
    {
        public string Subnet { get; set; }   // e.g. "104.16.1.0/24"
        public double DelayMs { get; set; }  // HTTP delay of the IP that discovered this subnet
        public int ConsecutiveFailures { get; set; } = 0; // TcpPing failure streak from subnet cache phase
    }

    public class SubnetCache
    {
        private const int MaxSubnets = 10;
        private static readonly string CacheFile =
            File.Exists("/.dockerenv")
                ? "/data/subnet_cache.json"
                : "subnet_cache.json";

        private readonly string _ipFile;
        private List<SubnetRecord> _subnets = new();
        private List<(IPAddress Network, int Prefix)> _cfRanges = new();

        public int SubnetCount => _subnets.Count;

        public SubnetCache(string ipFile = "ip.txt")
        {
            _ipFile = ipFile;
        }

        public void Load()
        {
            LoadCFRanges();

            if (!File.Exists(CacheFile))
                return;

            try
            {
                var json = File.ReadAllText(CacheFile);
                _subnets = JsonSerializer.Deserialize<List<SubnetRecord>>(json) ?? new();
            }
            catch
            {
                _subnets = new();
            }
        }

        private void LoadCFRanges()
        {
            if (!File.Exists(_ipFile))
                return;

            foreach (var line in File.ReadLines(_ipFile))
            {
                var l = line.Trim();
                if (string.IsNullOrEmpty(l))
                    continue;

                if (l.Contains('/'))
                {
                    var parts = l.Split('/');
                    if (IPAddress.TryParse(parts[0], out var net) && int.TryParse(parts[1], out var prefix))
                        _cfRanges.Add((net, prefix));
                }
                else if (IPAddress.TryParse(l, out var single))
                {
                    _cfRanges.Add((single, 32));
                }
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

            var bytes = ip.GetAddressBytes();

            foreach (var (network, prefix) in _cfRanges)
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
        /// Adds or updates a /24 subnet entry. Maintains ascending delay order and caps at MaxSubnets.
        /// If the subnet already exists and the new delay is better (lower), updates and re-sorts.
        /// If it doesn't exist, inserts in sorted position and removes the slowest tail entry if over the cap.
        /// </summary>
        public void AddOrUpdate(string subnet24, double delayMs)
        {
            var existing = _subnets.FirstOrDefault(s => s.Subnet == subnet24);
            if (existing != null)
            {
                if (delayMs < existing.DelayMs)
                    existing.DelayMs = delayMs;
                existing.ConsecutiveFailures = 0; // confirmed good again
                _subnets.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
                return;
            }

            _subnets.Add(new SubnetRecord { Subnet = subnet24, DelayMs = delayMs });
            _subnets.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));

            while (_subnets.Count > MaxSubnets)
                _subnets.RemoveAt(_subnets.Count - 1);
        }

        /// <summary>
        /// Randomly samples one IP from each cached subnet (up to <paramref name="count"/> subnets).
        /// Returns (IP, Subnet) pairs so callers can correlate TcpPing results back to subnets.
        /// </summary>
        public List<(IPAddress IP, string Subnet)> RandomSampleIPs(int count)
        {
            var result = new List<(IPAddress, string)>();
            var rng = new Random();

            foreach (var record in _subnets.Take(count))
            {
                var ip = GetRandomIPFromSubnet24(record.Subnet, rng);
                if (ip != null)
                    result.Add((ip, record.Subnet));
            }

            return result;
        }

        /// <summary>
        /// Updates consecutive-failure counters based on TcpPing results from the subnet cache phase.
        /// Subnets whose sampled IP passed have their counter reset to 0.
        /// Subnets whose sampled IP failed have their counter incremented; once it reaches
        /// <paramref name="evictThreshold"/> they are evicted from the cache.
        /// Returns true if any subnets were evicted.
        /// </summary>
        public bool RecordTcpPingOutcomes(
            IEnumerable<(IPAddress IP, string Subnet)> sampled,
            IEnumerable<IPAddress> passedIPs,
            int evictThreshold = 3)
        {
            var passedSet = new HashSet<IPAddress>(passedIPs);
            var toRemove = new List<string>();

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

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(CacheFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_subnets, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CacheFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存子网缓存失败: {ex.Message}");
            }
        }
    }
}
