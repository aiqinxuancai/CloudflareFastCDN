using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace CloudflareFastCDN.Utils
{
    public interface IIPProcessor
    {
        List<IPAddress> LoadIPRanges();
    }

    public class IPProcessor : IIPProcessor
    {
        private const string DefaultInputFile = "ip.txt";

        public bool TestAll { get; set; }
        public string IPFile { get; set; } = DefaultInputFile;
        public string IPText { get; set; }

        private readonly RandomNumberGenerator _rng;

        public IPProcessor(RandomNumberGenerator rng = null)
        {
            _rng = rng ?? RandomNumberGenerator.Create();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsIPv4(string ip) => ip.Contains('.');

        private byte RandIPEndWith(byte num)
        {
            if (num == 0) return 0;
            byte[] randomNumber = new byte[1];
            _rng.GetBytes(randomNumber);
            return (byte)(randomNumber[0] % (num + 1));
        }

        public List<IPAddress> LoadIPRanges()
        {
            var ranges = new IPRanges();
            IEnumerable<string> ips = !string.IsNullOrEmpty(IPText)
                ? IPText.Split(',').Select(ip => ip.Trim()).Where(ip => !string.IsNullOrEmpty(ip))
                : File.ReadLines(IPFile ?? DefaultInputFile).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l));

            foreach (var ip in ips)
            {
                ProcessIP(ranges, ip);
            }

            return ranges.Ips;
        }

        private void ProcessIP(IPRanges ranges, string ip)
        {
            ranges.ParseCIDR(ip);
            if (IsIPv4(ip))
            {
                GenerateRandomIPv4(ranges);
            }
            else
            {
                GenerateRandomIPv6(ranges);
            }
        }

        private void GenerateRandomIPv4(IPRanges ranges)
        {
            if (ranges.Mask == "/32")
            {
                ranges.Ips.Add(new IPAddress(ranges.FirstIP));
                return;
            }

            (byte minIP, byte maxIP, int prefixLength) = ranges.GetIPv4Range();
            Span<byte> ip = stackalloc byte[4];
            ranges.NetworkAddress.TryWriteBytes(ip, out _);

            int numSubnets = 1 << (24 - prefixLength);

            for (int i = 0; i < numSubnets; i++)
            {
                if (TestAll)
                {
                    for (int j = minIP; j <= maxIP; j++)
                    {
                        ip[3] = (byte)j;
                        ranges.AppendIPv4(ip);
                    }
                }
                else
                {
                    ip[3] = (byte)(minIP + RandIPEndWith((byte)(maxIP - minIP)));
                    ranges.AppendIPv4(ip);
                }

                // Move to next subnet
                ip[2]++;
                if (ip[2] == 0)
                {
                    ip[1]++;
                    if (ip[1] == 0)
                    {
                        ip[0]++;
                    }
                }
            }
        }

        private void GenerateRandomIPv6(IPRanges ranges)
        {
            if (ranges.Mask == "/128")
            {
                ranges.Ips.Add(new IPAddress(ranges.FirstIP));
                return;
            }

            int prefixLength = int.Parse(ranges.Mask.TrimStart('/'));
            int hostBits = 128 - prefixLength;

            Span<byte> ip = stackalloc byte[16];
            ranges.NetworkAddress.TryWriteBytes(ip, out _);

            int numIterations = Math.Min(100, 1 << Math.Min(hostBits, 20)); // Limit iterations

            for (int i = 0; i < numIterations; i++)
            {
                for (int j = 15; j >= 16 - (hostBits + 7) / 8; j--)
                {
                    _rng.GetBytes(ip.Slice(j, 1));
                }

                ranges.Ips.Add(new IPAddress(ip.ToArray()));
            }
        }
    }

    public class IPRanges
    {
        public List<IPAddress> Ips { get; } = new List<IPAddress>();
        public string Mask { get; private set; }
        public byte[] FirstIP { get; private set; }
        public IPAddress NetworkAddress { get; private set; }
        public IPAddress SubnetMask { get; private set; }

        public string FixIP(string ip)
        {
            if (!ip.Contains('/'))
            {
                Mask = ip.Contains('.') ? "/32" : "/128";
                return ip + Mask;
            }

            Mask = ip[ip.IndexOf('/')..];
            return ip;
        }

        public void ParseCIDR(string ip)
        {
            string fixedIP = FixIP(ip);
            string[] parts = fixedIP.Split('/');

            if (!IPAddress.TryParse(parts[0], out IPAddress parsedIP))
            {
                throw new ArgumentException($"Invalid IP address: {parts[0]}", nameof(ip));
            }

            FirstIP = parsedIP.GetAddressBytes();
            int prefixLength = int.Parse(parts[1]);

            if (ip.Contains('.'))
            {
                SubnetMask = GetSubnetMask(prefixLength);
                NetworkAddress = new IPAddress(FirstIP.Zip(SubnetMask.GetAddressBytes(), (a, b) => (byte)(a & b)).ToArray());
            }
            else
            {
                NetworkAddress = parsedIP;
                SubnetMask = null;
            }
        }

        private static IPAddress GetSubnetMask(int prefixLength)
        {
            uint mask = 0xffffffff;
            mask <<= (32 - prefixLength);
            return new IPAddress(BitConverter.GetBytes(mask).Reverse().ToArray());
        }

        public void AppendIPv4(ReadOnlySpan<byte> ip)
        {
            Ips.Add(new IPAddress(ip.ToArray()));
        }

        public (byte MinIP, byte MaxIP, int PrefixLength) GetIPv4Range()
        {
            byte minIP = (byte)(FirstIP[3] & SubnetMask.GetAddressBytes()[3]);
            int prefixLength = int.Parse(Mask.TrimStart('/'));
            int hostBits = 32 - prefixLength;
            int hosts = (1 << hostBits) - 1;
            return (minIP, (byte)Math.Min(hosts, 255), prefixLength);
        }
    }
}