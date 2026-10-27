using CloudflareFastCDN.Utils;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudflareFastCDN.Web
{
    internal static class WebServer
    {
        private const string ActionHeaderName = "X-CFCDN-Action";
        private const int DefaultLogLimit = 500;
        private const int MaxLogLimit = 3000;

        public static async Task<WebApplication?> StartAsync()
        {
            if (AppConfig.WebPort <= 0)
            {
                Console.WriteLine("Web管理页面未启用（WEB_PORT=0）");
                return null;
            }

            var listenAnyIp = AppConfig.WebHost is "*" or "+" or "0.0.0.0";
            var listenLocalhost = AppConfig.WebHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            IPAddress? listenAddress = null;
            if (!listenAnyIp && !listenLocalhost && !IPAddress.TryParse(AppConfig.WebHost, out listenAddress))
            {
                Console.WriteLine($"Web管理页面启动失败：WEB_HOST={AppConfig.WebHost} 不是有效的IP地址");
                return null;
            }

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>()
            });
            builder.Logging.ClearProviders();
            // 不接管 Ctrl+C / SIGTERM，保持进程原有的退出行为
            builder.Services.AddSingleton<IHostLifetime, PassiveHostLifetime>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (listenAnyIp)
                {
                    options.ListenAnyIP(AppConfig.WebPort);
                }
                else if (listenLocalhost)
                {
                    options.ListenLocalhost(AppConfig.WebPort);
                }
                else
                {
                    options.Listen(listenAddress!, AppConfig.WebPort);
                }
            });

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers.CacheControl = "no-store";
                headers.ContentSecurityPolicy = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";

                if (!IsAuthorized(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    headers.WWWAuthenticate = "Basic realm=\"CloudflareFastCDN\", charset=\"UTF-8\"";
                    return;
                }

                await next(context);
            });

            var indexHtml = LoadIndexHtml();
            app.MapGet("/", () => Results.Bytes(indexHtml, "text/html; charset=utf-8"));
            app.MapGet("/api/status", () => Results.Bytes(DashboardState.GetStatusJson(), "application/json"));
            app.MapGet("/api/history", () => Results.Bytes(DashboardState.GetHistoryJson(), "application/json"));
            app.MapGet("/api/logs", (HttpContext context) =>
            {
                long.TryParse(context.Request.Query["after"], out var after);
                if (!int.TryParse(context.Request.Query["limit"], out var limit) || limit <= 0)
                {
                    limit = DefaultLogLimit;
                }

                var page = LogBuffer.Read(Math.Max(0, after), Math.Min(limit, MaxLogLimit));
                return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(page, WebJsonContext.Default.LogPage), "application/json");
            });
            app.MapPost("/api/actions/select", (HttpContext context) => HandleAction(context, RuntimeControl.RequestFullSelection));
            app.MapPost("/api/actions/revalidate", (HttpContext context) => HandleAction(context, RuntimeControl.RequestRevalidation));

            try
            {
                await app.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Web管理页面启动失败：{ex.Message}");
                await app.DisposeAsync();
                return null;
            }

            var displayHost = listenAnyIp ? "<本机IP>" : AppConfig.WebHost;
            Console.WriteLine($"Web管理页面已启动：http://{displayHost}:{AppConfig.WebPort}/");
            if (string.IsNullOrEmpty(AppConfig.WebPassword))
            {
                Console.WriteLine("WEB_PASSWORD 未设置，Web管理页面无需登录即可访问；如需暴露到公网请务必设置密码");
            }

            return app;
        }

        private static IResult HandleAction(HttpContext context, Func<ActionResponse> action)
        {
            // 自定义请求头会触发跨域预检，用于阻止第三方页面借浏览器凭据发起管理操作
            if (!context.Request.Headers.ContainsKey(ActionHeaderName))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var response = action();
            return Results.Json(
                response,
                WebJsonContext.Default.ActionResponse,
                statusCode: response.Accepted ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        }

        private static bool IsAuthorized(HttpRequest request)
        {
            if (string.IsNullOrEmpty(AppConfig.WebPassword))
            {
                return true;
            }

            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string credentials;
            try
            {
                credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            }
            catch (FormatException)
            {
                return false;
            }

            var separatorIndex = credentials.IndexOf(':');
            if (separatorIndex < 0)
            {
                return false;
            }

            var usernameMatches = FixedTimeEquals(credentials[..separatorIndex], AppConfig.WebUsername);
            var passwordMatches = FixedTimeEquals(credentials[(separatorIndex + 1)..], AppConfig.WebPassword);
            return usernameMatches & passwordMatches;
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(left)),
                SHA256.HashData(Encoding.UTF8.GetBytes(right)));
        }

        private static byte[] LoadIndexHtml()
        {
            using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("CloudflareFastCDN.Web.index.html")
                ?? throw new InvalidOperationException("缺少内嵌资源 index.html");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private sealed class PassiveHostLifetime : IHostLifetime
        {
            public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
