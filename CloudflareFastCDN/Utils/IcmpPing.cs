
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;

namespace CloudflareFastCDN.Utils
{
    public class IcmpPing
    {
        private const int PingTimeout = 1000;
        private const int MaxRoutine = 1000;

        private static int Threads = AppConfig.PingThreads;
        private static int PingCount = 4;

        private List<IPAddress> ips;
        private List<PingData> csv;
        private SemaphoreSlim semaphore;

        public IcmpPing(List<IPAddress> ipList, int pingCount = 4)
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

            Console.WriteLine("开始ICMP Ping");

            var tasks = ips.Select(ip => Start(ip)).ToArray();
            await Task.WhenAll(tasks);

            csv.Sort((a, b) => a.Delay.TotalMicroseconds.CompareTo(b.Delay.TotalMicroseconds));
            return csv;
        }

        private async Task Start(IPAddress ip)
        {
            await semaphore.WaitAsync();
            try
            {
                await PingHandler(ip);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<(bool, TimeSpan)> PingAsync(IPAddress ip)
        {
            using (var pinger = new Ping())
            {
                try
                {
                    var reply = await pinger.SendPingAsync(ip, PingTimeout);
                    return (reply.Status == IPStatus.Success, reply.RoundtripTime > 0 ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : TimeSpan.Zero);
                }
                catch
                {
                    return (false, TimeSpan.Zero);
                }
            }
        }

        private async Task<(int, TimeSpan)> CheckConnection(IPAddress ip)
        {
            int recv = 0;
            TimeSpan totalDelay = TimeSpan.Zero;

            for (int i = 0; i < PingCount; i++)
            {
                var (ok, delay) = await PingAsync(ip);
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
                Console.WriteLine($"完成测试 {data.IP} 延时{data.Delay.TotalMilliseconds:00}ms 丢包:{data.Sended - data.Received}/{data.Sended}");
                csv.Add(data);
            }
        }

        private async Task PingHandler(IPAddress ip)
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

}
