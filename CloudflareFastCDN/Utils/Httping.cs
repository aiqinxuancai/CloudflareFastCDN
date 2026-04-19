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
        private static readonly string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        public async Task<(int success, TimeSpan totalDelay)> Ping(IPAddress ip, int pingCount = 3)
        {
            var probeTimeout = TimeSpan.FromMilliseconds(AppConfig.HttpProbeTimeoutMs);
            using var client = CreateClient(ip, probeTimeout);

            int success = 0;
            TimeSpan totalDelay = TimeSpan.Zero;

            for (int i = 0; i < pingCount; i++)
            {
                var pingResult = await SendProbeAsync(client, probeTimeout);
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
            var probeTimeout = TimeSpan.FromMilliseconds(AppConfig.HttpProbeTimeoutMs);
            using var client = CreateClient(ip, probeTimeout);
            return await SendProbeAsync(client, probeTimeout);
        }

        public async Task<(bool success, bool missing, double mbps, long bytesRead, TimeSpan duration)> SpeedTest(IPAddress ip, int maxBytes = DefaultSpeedTestBytes)
        {
            var speedTestTimeout = TimeSpan.FromMilliseconds(AppConfig.HttpSpeedTestTimeoutMs);
            var speedTestIdleTimeout = TimeSpan.FromMilliseconds(AppConfig.HttpSpeedTestIdleTimeoutMs);
            using var client = CreateClient(ip, speedTestTimeout);
            return await DownloadSpeedTestAsync(client, maxBytes, speedTestTimeout, speedTestIdleTimeout);
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
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private static async Task<(bool success, TimeSpan delay)> SendProbeAsync(HttpClient client, TimeSpan timeout)
        {
            var headResult = await SendAsync(client, HttpMethod.Head, timeout);
            if (headResult.success || !ShouldFallbackToGet(headResult.statusCode))
            {
                return (headResult.success, headResult.delay);
            }

            var getResult = await SendAsync(client, HttpMethod.Get, timeout);
            return (getResult.success, getResult.delay);
        }

        private static async Task<(bool success, TimeSpan delay, HttpStatusCode statusCode)> SendAsync(HttpClient client, HttpMethod method, TimeSpan timeout)
        {
            using var request = new HttpRequestMessage(method, GetProbeUri());
            var stopwatch = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource(timeout);

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                stopwatch.Stop();

                if (IsValidStatusCode(response.StatusCode))
                {
                    return (true, stopwatch.Elapsed, response.StatusCode);
                }

                Debug.WriteLine($"HTTP probe failed, status code: {(int)response.StatusCode}");
                return (false, TimeSpan.Zero, response.StatusCode);
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
            {
                Debug.WriteLine($"HTTP probe timed out after {timeout.TotalMilliseconds:0}ms: {ex.Message}");
                return (false, TimeSpan.Zero, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (false, TimeSpan.Zero, 0);
            }
        }

        private static async Task<(bool success, bool missing, double mbps, long bytesRead, TimeSpan duration)> DownloadSpeedTestAsync(HttpClient client, int maxBytes, TimeSpan totalTimeout, TimeSpan idleTimeout)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GetSpeedTestUri());
            using var totalTimeoutCts = new CancellationTokenSource(totalTimeout);

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, totalTimeoutCts.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, true, 0, 0, TimeSpan.Zero);
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Debug.WriteLine($"Speed test failed, status code: {(int)response.StatusCode}");
                    return (false, false, 0, 0, TimeSpan.Zero);
                }

                using var stream = await response.Content.ReadAsStreamAsync(totalTimeoutCts.Token);
                byte[] buffer = new byte[64 * 1024];
                long totalBytesRead = 0;
                var stopwatch = Stopwatch.StartNew();

                while (totalBytesRead < maxBytes)
                {
                    var bytesToRead = (int)Math.Min(buffer.Length, maxBytes - totalBytesRead);
                    using var idleTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(totalTimeoutCts.Token);
                    idleTimeoutCts.CancelAfter(idleTimeout);

                    // ResponseHeadersRead only covers headers; this prevents the body read from hanging forever.
                    var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), idleTimeoutCts.Token);
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
            catch (OperationCanceledException ex) when (totalTimeoutCts.IsCancellationRequested)
            {
                Debug.WriteLine($"Speed test timed out after {totalTimeout.TotalMilliseconds:0}ms: {ex.Message}");
                return (false, false, 0, 0, TimeSpan.Zero);
            }
            catch (OperationCanceledException ex)
            {
                Debug.WriteLine($"Speed test stalled for more than {idleTimeout.TotalMilliseconds:0}ms: {ex.Message}");
                return (false, false, 0, 0, TimeSpan.Zero);
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
