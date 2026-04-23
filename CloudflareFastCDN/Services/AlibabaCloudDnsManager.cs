using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudflareFastCDN.Services
{
    internal sealed class AlibabaCloudDnsManager : IDnsRecordManager
    {
        private const string Endpoint = "https://alidns.aliyuncs.com/";
        private const string Version = "2015-01-09";
        private const string DefaultLine = "default";
        private const int DefaultTtl = 600;
        private const int PageSize = 100;

        private readonly HttpClient _httpClient;
        private readonly string _accessKeyId;
        private readonly string _accessKeySecret;
        private IReadOnlyList<string>? _cachedDomains;

        public AlibabaCloudDnsManager(string accessKeyId, string accessKeySecret)
        {
            _accessKeyId = accessKeyId;
            _accessKeySecret = accessKeySecret;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(Endpoint)
            };
        }

        public string ProviderName => "AlibabaCloud";

        public async Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress)
        {
            var resolvedDomain = await ResolveDomainAsync(fullDomain);
            if (resolvedDomain == null)
            {
                Console.WriteLine($"阿里云解析未找到域名 {fullDomain} 对应的托管主域");
                return false;
            }

            var existingRecords = await GetDnsRecordsAsync(resolvedDomain.Value.RootDomain, resolvedDomain.Value.RecordName);
            var defaultLineRecords = existingRecords
                .Where(record => string.Equals(record.Line, DefaultLine, StringComparison.OrdinalIgnoreCase))
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
                    await SendRequestAsync("UpdateDomainRecord", new Dictionary<string, string?>
                    {
                        ["RecordId"] = record.RecordId,
                        ["RR"] = resolvedDomain.Value.RecordName,
                        ["Type"] = "A",
                        ["Value"] = ipAddress,
                        ["Line"] = DefaultLine,
                        ["TTL"] = DefaultTtl.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }
            else
            {
                await SendRequestAsync("AddDomainRecord", new Dictionary<string, string?>
                {
                    ["DomainName"] = resolvedDomain.Value.RootDomain,
                    ["RR"] = resolvedDomain.Value.RecordName,
                    ["Type"] = "A",
                    ["Value"] = ipAddress,
                    ["Line"] = DefaultLine,
                    ["TTL"] = DefaultTtl.ToString(CultureInfo.InvariantCulture)
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
            int pageNumber = 1;

            while (true)
            {
                var response = await SendRequestAsync("DescribeDomains", new Dictionary<string, string?>
                {
                    ["PageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                    ["PageSize"] = PageSize.ToString(CultureInfo.InvariantCulture)
                });

                var pageCount = 0;
                foreach (var item in EnumerateArray(response, "Domains", "Domain"))
                {
                    var domainName = GetStringProperty(item, "DomainName");
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

                pageNumber++;
            }

            _cachedDomains = domains
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return _cachedDomains;
        }

        private async Task<List<RecordInfo>> GetDnsRecordsAsync(string rootDomain, string recordName)
        {
            var records = new List<RecordInfo>();
            int pageNumber = 1;

            while (true)
            {
                var response = await SendRequestAsync("DescribeDomainRecords", new Dictionary<string, string?>
                {
                    ["DomainName"] = rootDomain,
                    ["PageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                    ["PageSize"] = PageSize.ToString(CultureInfo.InvariantCulture),
                    ["TypeKeyWord"] = "A"
                });

                int pageCount = 0;
                foreach (var item in EnumerateArray(response, "DomainRecords", "Record"))
                {
                    pageCount++;

                    var rr = GetStringProperty(item, "RR");
                    var type = GetStringProperty(item, "Type");
                    var value = GetStringProperty(item, "Value");
                    var line = GetStringProperty(item, "Line");
                    var recordId = GetStringProperty(item, "RecordId");

                    if (!string.Equals(type, "A", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(recordId) ||
                        !string.Equals(rr, recordName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    records.Add(new RecordInfo(recordId, rr ?? string.Empty, value ?? string.Empty, line ?? string.Empty));
                }

                if (pageCount < PageSize)
                {
                    break;
                }

                pageNumber++;
            }

            return records;
        }

        private async Task<JsonElement> SendRequestAsync(string action, IDictionary<string, string?> actionParameters)
        {
            var parameters = new SortedDictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AccessKeyId"] = _accessKeyId,
                ["Action"] = action,
                ["Format"] = "JSON",
                ["SignatureMethod"] = "HMAC-SHA1",
                ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
                ["SignatureVersion"] = "1.0",
                ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["Version"] = Version
            };

            foreach (var parameter in actionParameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Value))
                {
                    parameters[parameter.Key] = parameter.Value;
                }
            }

            var canonicalizedQueryString = string.Join("&", parameters.Select(parameter =>
                $"{PercentEncode(parameter.Key)}={PercentEncode(parameter.Value ?? string.Empty)}"));

            var stringToSign = $"GET&%2F&{PercentEncode(canonicalizedQueryString)}";
            var signature = Convert.ToBase64String(SignString($"{_accessKeySecret}&", stringToSign));
            var queryString = $"{canonicalizedQueryString}&Signature={PercentEncode(signature)}";

            using var response = await _httpClient.GetAsync($"?{queryString}");
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseObject = JsonDocument.Parse(responseContent).RootElement.Clone();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"阿里云解析请求失败({response.StatusCode}): {responseContent}");
            }

            if (responseObject.TryGetProperty("Code", out var codeElement))
            {
                var code = codeElement.GetString();
                var message = GetStringProperty(responseObject, "Message");
                throw new Exception($"阿里云解析接口错误: {code} {message}");
            }

            return responseObject;
        }

        private static IEnumerable<JsonElement> EnumerateArray(JsonElement rootElement, params string[] path)
        {
            var current = rootElement;
            foreach (var segment in path)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return Enumerable.Empty<JsonElement>();
                }
            }

            return current.ValueKind == JsonValueKind.Array
                ? current.EnumerateArray().ToArray()
                : Enumerable.Empty<JsonElement>();
        }

        private static byte[] SignString(string key, string value)
        {
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        }

        private static string PercentEncode(string value)
        {
            return Uri.EscapeDataString(value)
                .Replace("+", "%20", StringComparison.Ordinal)
                .Replace("*", "%2A", StringComparison.Ordinal)
                .Replace("%7E", "~", StringComparison.Ordinal);
        }

        private static string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private sealed record RecordInfo(string RecordId, string RecordName, string Value, string Line);
    }
}
