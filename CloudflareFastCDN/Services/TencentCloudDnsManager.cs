using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudflareFastCDN.Services
{
    internal sealed class TencentCloudDnsManager : IDnsRecordManager
    {
        private const string Host = "dnspod.tencentcloudapi.com";
        private const string Endpoint = "https://dnspod.tencentcloudapi.com/";
        private const string Service = "dnspod";
        private const string Version = "2021-03-23";
        private const string DefaultRecordLine = "默认";
        private const long DefaultTtl = 600;
        private const int PageSize = 100;

        private readonly HttpClient _httpClient;
        private readonly string _secretId;
        private readonly string _secretKey;
        private IReadOnlyList<string>? _cachedDomains;

        public TencentCloudDnsManager(string secretId, string secretKey)
        {
            _secretId = secretId;
            _secretKey = secretKey;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(Endpoint)
            };
        }

        public string ProviderName => "TencentCloud";

        public async Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress)
        {
            var resolvedDomain = await ResolveDomainAsync(fullDomain);
            if (resolvedDomain == null)
            {
                Console.WriteLine($"腾讯云 DNSPod 未找到域名 {fullDomain} 对应的托管主域");
                return false;
            }

            var existingRecords = await GetDnsRecordsAsync(resolvedDomain.Value.RootDomain, resolvedDomain.Value.RecordName);
            var defaultLineRecords = existingRecords
                .Where(record => string.Equals(record.Line, DefaultRecordLine, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (defaultLineRecords.Count > 0 &&
                defaultLineRecords.All(record => string.Equals(record.Value, ipAddress, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"DNS record {fullDomain} already points to {ipAddress}, skipping update");
                return true;
            }

            if (defaultLineRecords.Count > 0)
            {
                foreach (var record in defaultLineRecords)
                {
                    await SendRequestAsync("ModifyRecord", new
                    {
                        Domain = resolvedDomain.Value.RootDomain,
                        RecordId = record.RecordId,
                        SubDomain = resolvedDomain.Value.RecordName,
                        RecordType = "A",
                        RecordLine = DefaultRecordLine,
                        Value = ipAddress,
                        TTL = DefaultTtl
                    });
                }
            }
            else
            {
                await SendRequestAsync("CreateRecord", new
                {
                    Domain = resolvedDomain.Value.RootDomain,
                    SubDomain = resolvedDomain.Value.RecordName,
                    RecordType = "A",
                    RecordLine = DefaultRecordLine,
                    Value = ipAddress,
                    TTL = DefaultTtl
                });
            }

            return true;
        }

        private async Task<(string RootDomain, string RecordName)?> ResolveDomainAsync(string fullDomain)
        {
            var domains = await GetDomainsAsync();
            return DnsDomainResolver.Resolve(fullDomain, domains);
        }

        private async Task<IReadOnlyList<string>> GetDomainsAsync()
        {
            if (_cachedDomains != null)
            {
                return _cachedDomains;
            }

            var domains = new List<string>();
            int offset = 0;

            while (true)
            {
                var response = await SendRequestAsync("DescribeDomainList", new
                {
                    Offset = offset,
                    Limit = PageSize
                });

                var responseObject = response.GetProperty("Response");
                if (!responseObject.TryGetProperty("DomainList", out var domainListElement) ||
                    domainListElement.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                int pageCount = 0;
                foreach (var item in domainListElement.EnumerateArray())
                {
                    var domainName = GetStringProperty(item, "Name");
                    if (string.IsNullOrWhiteSpace(domainName))
                    {
                        continue;
                    }

                    domains.Add(domainName);
                    pageCount++;
                }

                if (pageCount < PageSize)
                {
                    break;
                }

                offset += pageCount;
            }

            _cachedDomains = domains
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return _cachedDomains;
        }

        private async Task<List<RecordInfo>> GetDnsRecordsAsync(string rootDomain, string recordName)
        {
            var records = new List<RecordInfo>();
            int offset = 0;

            while (true)
            {
                var response = await SendRequestAsync("DescribeRecordList", new
                {
                    Domain = rootDomain,
                    RecordType = "A",
                    Offset = offset,
                    Limit = PageSize,
                    ErrorOnEmpty = "no"
                });

                var responseObject = response.GetProperty("Response");
                if (!responseObject.TryGetProperty("RecordList", out var recordListElement) ||
                    recordListElement.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                int pageCount = 0;
                foreach (var item in recordListElement.EnumerateArray())
                {
                    pageCount++;

                    var name = GetStringProperty(item, "Name");
                    var type = GetStringProperty(item, "Type");
                    var value = GetStringProperty(item, "Value");
                    var line = GetStringProperty(item, "Line");
                    var recordId = GetLongProperty(item, "RecordId");

                    if (!string.Equals(type, "A", StringComparison.OrdinalIgnoreCase) ||
                        recordId == null ||
                        !string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    records.Add(new RecordInfo(recordId.Value, name ?? string.Empty, value ?? string.Empty, line ?? string.Empty));
                }

                if (pageCount < PageSize)
                {
                    break;
                }

                offset += pageCount;
            }

            return records;
        }

        private async Task<JsonElement> SendRequestAsync(string action, object payload)
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var authorization = BuildAuthorization(action, payloadJson, timestamp, date);

            using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("Host", Host);
            request.Headers.TryAddWithoutValidation("X-TC-Action", action);
            request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
            request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

            using var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseObject = JsonDocument.Parse(responseContent).RootElement.Clone();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"腾讯云 DNSPod 请求失败({response.StatusCode}): {responseContent}");
            }

            if (responseObject.TryGetProperty("Response", out var responseElement) &&
                responseElement.TryGetProperty("Error", out var errorElement))
            {
                var code = GetStringProperty(errorElement, "Code");
                var message = GetStringProperty(errorElement, "Message");
                throw new Exception($"腾讯云 DNSPod 接口错误: {code} {message}");
            }

            return responseObject;
        }

        private string BuildAuthorization(string action, string payloadJson, long timestamp, string date)
        {
            const string algorithm = "TC3-HMAC-SHA256";
            const string signedHeaders = "content-type;host;x-tc-action";
            var hashedPayload = Sha256Hex(payloadJson);
            var canonicalHeaders = $"content-type:application/json; charset=utf-8\nhost:{Host}\nx-tc-action:{action.ToLowerInvariant()}\n";
            var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{hashedPayload}";
            var credentialScope = $"{date}/{Service}/tc3_request";
            var stringToSign = $"{algorithm}\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

            var secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{_secretKey}"), date);
            var secretService = HmacSha256(secretDate, Service);
            var secretSigning = HmacSha256(secretService, "tc3_request");
            var signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();

            return $"{algorithm} Credential={_secretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        }

        private static byte[] HmacSha256(byte[] key, string message)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        }

        private static string Sha256Hex(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private static long? GetLongProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out number))
            {
                return number;
            }

            return null;
        }

        private sealed record RecordInfo(long RecordId, string Name, string Value, string Line);
    }
}
