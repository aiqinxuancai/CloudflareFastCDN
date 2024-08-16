using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudflareFastCDN.Utils
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;


    public class Httping
    {
        private static readonly Regex OutRegexp = new Regex("[A-Z]{3}");
        private static readonly string URL = "https://visa.cn";
        private static readonly int PingCount = 4;

        public async Task<(int, TimeSpan)> Ping(IPAddress ip)
        {
            var uri = new Uri(URL);
            var originalHost = uri.Host;
            var handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(4),
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            var request = new HttpRequestMessage(HttpMethod.Head, URL);
            request.Headers.Host = originalHost;
            var builder = new UriBuilder(uri)
            {
                Host = ip.ToString() ,
 
            };
            request.RequestUri = builder.Uri;

            HttpResponseMessage response = null;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (0, TimeSpan.Zero);
            }
            Debug.WriteLine(((int)response.StatusCode));
            if (!IsValidStatusCode(response.StatusCode))
            {
                return (0, TimeSpan.Zero);
            }

            int success = 0;
            TimeSpan totalDelay = TimeSpan.Zero;
            for (int i = 0; i < PingCount; i++)
            {
                request = new HttpRequestMessage(HttpMethod.Head, builder.Uri)
                {
                    Headers = { Host = originalHost }
                };

                if (i == PingCount - 1)
                {
                    request.Headers.ConnectionClose = true;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    success++;
                    totalDelay += stopwatch.Elapsed;
                }
                catch
                {
                    continue;
                }
            }

            return (success, totalDelay);
        }

        public async Task<(bool success, TimeSpan delay)> SinglePing(IPAddress ip)
        {
            var uri = new Uri(URL);
            var originalHost = uri.Host;
            var handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(4),
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_12_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            var request = new HttpRequestMessage(HttpMethod.Head, URL);
            request.Headers.Host = originalHost;
            var builder = new UriBuilder(uri)
            {
                Host = ip.ToString(),
            };
            request.RequestUri = builder.Uri;

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                stopwatch.Stop();

                if (IsValidStatusCode(response.StatusCode))
                {
                    return (true, stopwatch.Elapsed);
                }
                else
                {
                    return (false, TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return (false, TimeSpan.Zero);
            }
        }

        private bool IsValidStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.OK || statusCode == HttpStatusCode.MovedPermanently || statusCode == HttpStatusCode.Found;
        }


    }

}
