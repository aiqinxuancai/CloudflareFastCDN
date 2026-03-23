using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CloudflareFastCDN.Utils
{
    public class Httping
    {
        private const string DefaultProbeUrl = "https://www.visa.cn/";
        private const int DefaultSpeedTestBytes = 4 * 1024 * 1024;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan SpeedTestTimeout = TimeSpan.FromSeconds(10);
        private static readonly string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        public async Task<(int success, TimeSpan totalDelay)> Ping(IPAddress ip, int pingCount = 3)
        {
            using var client = CreateClient(ip);

            int success = 0;
            TimeSpan totalDelay = TimeSpan.Zero;

            for (int i = 0; i < pingCount; i++)
            {
                var pingResult = await SendProbeAsync(client);
                if (!pingResult.success)
                {
                    continue;
                }

                success++;
                totalDelay += pingResult.delay;
            }

            return (success, totalDelay);
        }

        public async Task<(bool success, TimeSpan delay)> SinglePing(IPAddress ip)
        {
            using var client = CreateClient(ip);
            return await SendProbeAsync(client);
        }

        public async Task<(bool success, bool missing, double mbps, long bytesRead, TimeSpan duration)> SpeedTest(IPAddress ip, int maxBytes = DefaultSpeedTestBytes)
        {
            using var client = CreateClient(ip, SpeedTestTimeout);
            return await DownloadSpeedTestAsync(client, maxBytes);
        }

        private static HttpClient CreateClient(IPAddress ip)
        {
            return CreateClient(ip, ProbeTimeout);
        }

        private static HttpClient CreateClient(IPAddress ip, TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = timeout,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            var client = new HttpClient(handler)
            {
                Timeout = timeout,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private static async Task<(bool success, TimeSpan delay)> SendProbeAsync(HttpClient client)
        {
            var headResult = await SendAsync(client, HttpMethod.Head);
            if (headResult.success || !ShouldFallbackToGet(headResult.statusCode))
            {
                return (headResult.success, headResult.delay);
            }

            var getResult = await SendAsync(client, HttpMethod.Get);
            return (getResult.success, getResult.delay);
        }

        private static async Task<(bool success, TimeSpan delay, HttpStatusCode statusCode)> SendAsync(HttpClient client, HttpMethod method)
        {
            using var request = new HttpRequestMessage(method, GetProbeUri());
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                stopwatch.Stop();

                if (IsValidStatusCode(response.StatusCode))
                {
                    return (true, stopwatch.Elapsed, response.StatusCode);
                }

                Debug.WriteLine($"HTTP probe failed, status code: {(int)response.StatusCode}");
                return (false, TimeSpan.Zero, response.StatusCode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (false, TimeSpan.Zero, 0);
            }
        }

        private static async Task<(bool success, bool missing, double mbps, long bytesRead, TimeSpan duration)> DownloadSpeedTestAsync(HttpClient client, int maxBytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GetSpeedTestUri());

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, true, 0, 0, TimeSpan.Zero);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Debug.WriteLine($"Speed test failed, status code: {(int)response.StatusCode}");
                    return (false, false, 0, 0, TimeSpan.Zero);
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                byte[] buffer = new byte[64 * 1024];
                long totalBytesRead = 0;
                var stopwatch = Stopwatch.StartNew();

                while (totalBytesRead < maxBytes)
                {
                    var bytesToRead = (int)Math.Min(buffer.Length, maxBytes - totalBytesRead);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead));
                    if (read == 0)
                    {
                        break;
                    }

                    totalBytesRead += read;
                }

                stopwatch.Stop();

                if (totalBytesRead <= 0 || stopwatch.Elapsed <= TimeSpan.Zero)
                {
                    return (false, false, 0, totalBytesRead, stopwatch.Elapsed);
                }

                var mbps = totalBytesRead * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;
                return (true, false, mbps, totalBytesRead, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (false, false, 0, 0, TimeSpan.Zero);
            }
        }

        private static bool ShouldFallbackToGet(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.MethodNotAllowed || statusCode == HttpStatusCode.NotImplemented;
        }

        private static bool IsValidStatusCode(HttpStatusCode statusCode)
        {
            var numericCode = (int)statusCode;
            return numericCode >= 200 && numericCode < 400;
        }

        private static Uri GetProbeUri()
        {
            return Uri.TryCreate(AppConfig.HttpProbeUrl, UriKind.Absolute, out var probeUri)
                ? probeUri
                : new Uri(DefaultProbeUrl);
        }

        private static Uri GetSpeedTestUri()
        {
            return new Uri(GetProbeUri(), "/speedtest");
        }
    }
}
