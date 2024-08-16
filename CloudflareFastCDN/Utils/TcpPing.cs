using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CloudflareFastCDN.Utils
{
    public class TcpPing
    {
        private const int TcpConnectTimeout = 1000;
        private const int MaxRoutine = 1000;

        private static int Threads = AppConfig.PingThreads;
        private static int TcpPort = 443;
        private static int PingCount = 4;

        private List<IPAddress> ips;
        private List<PingData> csv;
        private SemaphoreSlim semaphore;

        public TcpPing(List<IPAddress> ipList, int pingCount = 4)
        {
            PingCount = pingCount;
            ips = ipList;
            csv = new List<PingData>();
            semaphore = new SemaphoreSlim(Threads);
        }


        public async Task<List<PingData>> RunAsync()
        {
            if (!ips.Any())
            {
                return csv;
            }

            Console.WriteLine($"开始Tcping，端口{TcpPort})");

            var tasks = ips.Select(ip => Start(ip)).ToArray();
            await Task.WhenAll(tasks);

            csv.Sort((a, b) =>  a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));
            return csv;
        }

        private async Task Start(IPAddress ip)
        {
            await semaphore.WaitAsync();
            try
            {
                await TcpingHandler(ip);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<(bool, TimeSpan)> Tcping(IPAddress ip)
        {
            var startTime = DateTime.Now;
            var fullAddress = new IPEndPoint(ip, TcpPort);

            using (var client = new TcpClient())
            {
                try
                {
                    var connectTask = client.ConnectAsync(fullAddress.Address, fullAddress.Port);
                    var success = await Task.WhenAny(connectTask, Task.Delay(TcpConnectTimeout)) == connectTask;
                    if (!success)
                    {
                        return (false, TimeSpan.Zero);
                    }
                }
                catch
                {
                    return (false, TimeSpan.Zero);
                }
            }

            var duration = DateTime.Now - startTime;
            return (true, duration);
        }

        private async Task<(int, TimeSpan)> CheckConnection(IPAddress ip)
        {
            int recv = 0;
            TimeSpan totalDelay = TimeSpan.Zero;

            for (int i = 0; i < PingCount; i++)
            {
                var (ok, delay) = await Tcping(ip);
                if (ok)
                {
                    recv++;
                    totalDelay += delay;
                }
            }

            return (recv, totalDelay);
        }

        private void AppendIPData(PingData data)
        {
            lock (csv)
            {
                Console.WriteLine($"完成测试 {data.IP} 延时{data.Delay.TotalMilliseconds:00}ms 丢包:{data.Sended - data.Received}");
                csv.Add(data);
            }
        }

        private async Task TcpingHandler(IPAddress ip)
        {
            var (recv, totalDelay) = await CheckConnection(ip);

            if (recv == 0) return;

            var data = new PingData
            {
                IP = ip,
                Sended = PingCount,
                Received = recv,
                Delay = TimeSpan.FromMilliseconds(totalDelay.TotalMilliseconds / recv)
            };

            AppendIPData(data);
        }
    }

    public class PingData : IComparable<PingData>
    {
        public IPAddress IP { get; set; }
        public int Sended { get; set; }
        public int Received { get; set; }
        public TimeSpan Delay { get; set; }

        public int CompareTo(PingData other)
        {
            return Delay.CompareTo(other.Delay);
        }
    }
}