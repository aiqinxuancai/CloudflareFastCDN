using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CloudflareFastCDN.Services
{
    internal sealed class CloudflareAPIManager : IDnsRecordManager
    {
        private const string BaseUrl = "https://api.cloudflare.com/client/v4/";
        private const int ApiPageSize = 100;

        private readonly HttpClient _httpClient;
        private IReadOnlyList<ZoneInfo>? _cachedZones;

        public CloudflareAPIManager(string apiKey)
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl)
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public string ProviderName => "Cloudflare";

        public async Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress)
        {
            var zone = await GetMatchingZoneAsync(fullDomain);
            if (zone == null)
            {
                Console.WriteLine($"Cloudflare 未找到域名 {fullDomain} 对应的 Zone");
                return false;
            }

            var existingRecords = await GetDnsRecordsAsync(zone.Id, "A", fullDomain);

            if (existingRecords.Count > 0 &&
                existingRecords.All(record =>
                    string.Equals(record.Content, ipAddress, StringComparison.OrdinalIgnoreCase) &&
                    record.Proxied == false))
            {
                Console.WriteLine($"DNS record {fullDomain} already points to {ipAddress}, skipping update");
                return true;
            }

            var payload = new
            {
                type = "A",
                name = fullDomain,
                content = ipAddress,
                ttl = 1,
                proxied = false
            };

            if (existingRecords.Count > 0)
            {
                foreach (var record in existingRecords)
                {
                    await SendRequestAsync(HttpMethod.Put, $"zones/{zone.Id}/dns_records/{record.Id}", payload);
                }
            }
            else
            {
                await SendRequestAsync(HttpMethod.Post, $"zones/{zone.Id}/dns_records", payload);
            }

            return true;
        }

        private async Task<ZoneInfo?> GetMatchingZoneAsync(string fullDomain)
        {
            var zones = await GetZonesAsync();
            var normalizedFullDomain = DnsDomainResolver.NormalizeDomain(fullDomain);

            return zones
                .Where(zone =>
                    string.Equals(normalizedFullDomain, zone.Name, StringComparison.OrdinalIgnoreCase) ||
                    normalizedFullDomain.EndsWith($".{zone.Name}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(zone => zone.Name.Length)
                .FirstOrDefault();
        }

        private async Task<IReadOnlyList<ZoneInfo>> GetZonesAsync()
        {
            if (_cachedZones != null)
            {
                return _cachedZones;
            }

            var zones = new List<ZoneInfo>();
            int page = 1;

            while (true)
            {
                var response = await SendRequestAsync(HttpMethod.Get, $"zones?page={page}&per_page={ApiPageSize}");
                if (!response.TryGetProperty("result", out var resultElement) ||
                    resultElement.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var pageCount = 0;
                foreach (var item in resultElement.EnumerateArray())
                {
                    var zoneId = GetStringProperty(item, "id");
                    var zoneName = GetStringProperty(item, "name");
                    if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(zoneName))
                    {
                        continue;
                    }

                    zones.Add(new ZoneInfo(zoneId, DnsDomainResolver.NormalizeDomain(zoneName)));
                    pageCount++;
                }

                if (pageCount < ApiPageSize)
                {
                    break;
                }

                page++;
            }

            _cachedZones = zones;
            return _cachedZones;
        }

        private async Task<List<DnsRecordInfo>> GetDnsRecordsAsync(string zoneId, string? recordType = null, string? fullRecordName = null)
        {
            var results = new List<DnsRecordInfo>();
            int page = 1;

            while (true)
            {
                var query = new List<string>
                {
                    $"page={page}",
                    $"per_page={ApiPageSize}"
                };

                if (!string.IsNullOrWhiteSpace(recordType))
                {
                    query.Add($"type={Uri.EscapeDataString(recordType)}");
                }

                if (!string.IsNullOrWhiteSpace(fullRecordName))
                {
                    query.Add($"name={Uri.EscapeDataString(fullRecordName)}");
                }

                var response = await SendRequestAsync(HttpMethod.Get, $"zones/{zoneId}/dns_records?{string.Join("&", query)}");
                if (!response.TryGetProperty("result", out var resultElement) ||
                    resultElement.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                int pageCount = 0;
                foreach (var item in resultElement.EnumerateArray())
                {
                    var recordId = GetStringProperty(item, "id");
                    var recordName = GetStringProperty(item, "name");
                    var type = GetStringProperty(item, "type");
                    var content = GetStringProperty(item, "content");
                    var proxied = GetBoolProperty(item, "proxied");

                    if (string.IsNullOrWhiteSpace(recordId) ||
                        string.IsNullOrWhiteSpace(recordName) ||
                        string.IsNullOrWhiteSpace(type))
                    {
                        continue;
                    }

                    results.Add(new DnsRecordInfo(recordId, recordName, type, content ?? string.Empty, proxied));
                    pageCount++;
                }

                if (pageCount < ApiPageSize)
                {
                    break;
                }

                page++;
            }

            return results;
        }

        private async Task<JsonElement> SendRequestAsync(HttpMethod method, string relativePath, object? payload = null)
        {
            using var request = new HttpRequestMessage(method, relativePath);
            if (payload != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            var responseObject = JsonDocument.Parse(responseContent).RootElement.Clone();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Cloudflare 请求失败({response.StatusCode}): {responseContent}");
            }

            if (!responseObject.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
            {
                throw new Exception($"Cloudflare 接口错误: {responseContent}");
            }

            return responseObject;
        }

        private static string? GetStringProperty(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private static bool? GetBoolProperty(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False
                ? property.GetBoolean()
                : null;
        }

        private sealed record ZoneInfo(string Id, string Name);
        private sealed record DnsRecordInfo(string Id, string Name, string Type, string Content, bool? Proxied);
    }
}
