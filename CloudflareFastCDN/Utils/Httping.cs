using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CloudflareFastCDN.Utils
{
    public class Httping
    {
        private const string DefaultProbeUrl = "https://www.visa.cn/";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
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

        private static HttpClient CreateClient(IPAddress ip)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = Timeout,
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
                Timeout = Timeout,
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
    }
}
