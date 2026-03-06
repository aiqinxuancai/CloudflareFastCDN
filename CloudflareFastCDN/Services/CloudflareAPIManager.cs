using CloudflareFastCDN.Utils;
using Flurl;
using Flurl.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudflareFastCDN.Services
{
    internal class CloudflareAPIManager
    {
        private static readonly Lazy<CloudflareAPIManager> lazy =
            new Lazy<CloudflareAPIManager>(() => new CloudflareAPIManager());

        private const string BaseUrl = "https://api.cloudflare.com/client/v4";
        private const int ApiPageSize = 100;

        public static CloudflareAPIManager Instance => lazy.Value;

        public static string APIKey = AppConfig.CloudflareKey;

        private CloudflareAPIManager()
        {
            FlurlHttp.Clients.WithDefaults(a =>
                a
                .WithHeader("Authorization", "Bearer " + APIKey)
                .WithHeader("Content-Type", "application/json")
            );
        }

        public async Task<string> GetZoneId(string domain)
        {
            int page = 1;

            while (true)
            {
                var request = BaseUrl
                    .AppendPathSegment("zones")
                    .SetQueryParam("page", page)
                    .SetQueryParam("per_page", ApiPageSize);

                var responseObject = await GetSuccessfulResponse(request, "Failed to get zone ID");
                foreach (var item in GetResultItems(responseObject, "Invalid zone response"))
                {
                    var zoneName = GetStringProperty(item, "name");
                    var zoneId = GetStringProperty(item, "id");

                    if (!string.IsNullOrWhiteSpace(zoneName) &&
                        domain.EndsWith(zoneName, StringComparison.OrdinalIgnoreCase))
                    {
                        return zoneId ?? string.Empty;
                    }
                }

                if (page >= GetTotalPages(responseObject))
                {
                    break;
                }

                page++;
            }

            return string.Empty;
        }

        public async Task<List<string>> GetZonesDnsRecordId(string zoneId, string recordName, string? rootDomain = null, string? recordType = null)
        {
            var fullRecordName = GetFullRecordName(recordName, rootDomain);
            var records = await GetDnsRecords(zoneId, recordType, fullRecordName);
            return records.Select(a => a.Id).ToList();
        }

        public async Task<bool> DeleteRecord(string zoneId, string recordId)
        {
            var response = await BaseUrl
                .AppendPathSegment($"zones/{zoneId}/dns_records/{recordId}")
                .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                .DeleteAsync();

            await response.GetStringAsync();
            return true;
        }

        public async Task<bool> AddOrUpdateTxtRecord(string domain, string recordName, string content)
        {
            var zoneId = await GetZoneId(domain);
            if (string.IsNullOrWhiteSpace(zoneId))
            {
                return false;
            }

            var fullRecordName = GetFullRecordName(recordName, domain);
            var recordIds = await GetZonesDnsRecordId(zoneId, recordName, domain, "TXT");

            var json = new
            {
                type = "TXT",
                name = fullRecordName,
                content,
                ttl = 60
            };

            var jsonStr = JsonSerializer.Serialize(json);

            foreach (var item in recordIds)
            {
                await DeleteRecord(zoneId, item);
            }

            var response = await BaseUrl
                .AppendPathSegment($"zones/{zoneId}/dns_records")
                .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                .PostStringAsync(jsonStr);

            await response.GetStringAsync();

            if (response.StatusCode != 200)
            {
                Console.WriteLine("Failed to add TXT record");
                return false;
            }

            return true;
        }

        public async Task<bool> AddOrUpdateARecord(string domain, string recordName, string ipAddress)
        {
            var zoneId = await GetZoneId(domain);
            if (string.IsNullOrWhiteSpace(zoneId))
            {
                return false;
            }

            var fullRecordName = GetFullRecordName(recordName, domain);
            var existingRecords = await GetDnsRecords(zoneId, "A", fullRecordName);

            if (existingRecords.Count > 0 &&
                existingRecords.All(a => string.Equals(a.Content, ipAddress, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"DNS record {fullRecordName} already points to {ipAddress}, skipping update");
                return true;
            }

            var json = new
            {
                type = "A",
                name = fullRecordName,
                content = ipAddress,
                ttl = 1
            };

            var jsonStr = JsonSerializer.Serialize(json);

            if (existingRecords.Count > 0)
            {
                foreach (var record in existingRecords)
                {
                    var response = await BaseUrl
                        .AppendPathSegment($"zones/{zoneId}/dns_records/{record.Id}")
                        .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                        .PutStringAsync(jsonStr);

                    await response.GetStringAsync();

                    if (response.StatusCode != 200)
                    {
                        Console.WriteLine($"Failed to update A record: {record.Id}");
                        return false;
                    }
                }
            }
            else
            {
                var response = await BaseUrl
                    .AppendPathSegment($"zones/{zoneId}/dns_records")
                    .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                    .PostStringAsync(jsonStr);

                await response.GetStringAsync();

                if (response.StatusCode != 200)
                {
                    Console.WriteLine("Failed to add A record");
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress)
        {
            var (recordName, rootDomain) = SplitDomain(fullDomain);
            return await AddOrUpdateARecord(rootDomain, recordName, ipAddress);
        }

        private async Task<List<DnsRecordInfo>> GetDnsRecords(string zoneId, string? recordType = null, string? fullRecordName = null)
        {
            var results = new List<DnsRecordInfo>();
            int page = 1;

            while (true)
            {
                var request = BaseUrl
                    .AppendPathSegment("zones")
                    .AppendPathSegment(zoneId)
                    .AppendPathSegment("dns_records")
                    .SetQueryParam("page", page)
                    .SetQueryParam("per_page", ApiPageSize);

                if (!string.IsNullOrWhiteSpace(recordType))
                {
                    request = request.SetQueryParam("type", recordType);
                }

                if (!string.IsNullOrWhiteSpace(fullRecordName))
                {
                    request = request.SetQueryParam("name", fullRecordName);
                }

                var responseObject = await GetSuccessfulResponse(request, "Failed to get DNS records");
                foreach (var item in GetResultItems(responseObject, "Invalid DNS record response"))
                {
                    var recordId = GetStringProperty(item, "id");
                    var recordName = GetStringProperty(item, "name");
                    var type = GetStringProperty(item, "type");
                    var content = GetStringProperty(item, "content");

                    if (string.IsNullOrWhiteSpace(recordId) ||
                        string.IsNullOrWhiteSpace(recordName) ||
                        string.IsNullOrWhiteSpace(type))
                    {
                        continue;
                    }

                    results.Add(new DnsRecordInfo(recordId, recordName, type, content ?? string.Empty));
                }

                if (page >= GetTotalPages(responseObject))
                {
                    break;
                }

                page++;
            }

            return results;
        }

        private async Task<JsonElement> GetSuccessfulResponse(Url request, string errorMessage)
        {
            var responseContent = await request.GetStringAsync();
            var responseObject = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!responseObject.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
            {
                throw new Exception($"{errorMessage}. Response: {responseContent}");
            }

            return responseObject;
        }

        private static JsonElement.ArrayEnumerator GetResultItems(JsonElement responseObject, string errorMessage)
        {
            if (!responseObject.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
            {
                throw new Exception(errorMessage);
            }

            return resultElement.EnumerateArray();
        }

        private static int GetTotalPages(JsonElement responseObject)
        {
            if (!responseObject.TryGetProperty("result_info", out var resultInfo))
            {
                return 1;
            }

            if (!resultInfo.TryGetProperty("total_pages", out var totalPagesElement))
            {
                return 1;
            }

            return totalPagesElement.ValueKind == JsonValueKind.Number && totalPagesElement.TryGetInt32(out var totalPages) && totalPages > 0
                ? totalPages
                : 1;
        }

        private static string GetFullRecordName(string recordName, string? rootDomain)
        {
            if (string.IsNullOrWhiteSpace(rootDomain))
            {
                return recordName;
            }

            if (string.IsNullOrWhiteSpace(recordName) || recordName == "@")
            {
                return rootDomain;
            }

            if (recordName.EndsWith($".{rootDomain}", StringComparison.OrdinalIgnoreCase))
            {
                return recordName;
            }

            return $"{recordName}.{rootDomain}";
        }

        private (string RecordName, string RootDomain) SplitDomain(string fullDomain)
        {
            var parts = fullDomain.Split('.');
            if (parts.Length < 3)
            {
                return ("@", fullDomain);
            }

            var tld = string.Join(".", parts.TakeLast(2));
            var match = Regex.Match(fullDomain, @"(.+)\." + Regex.Escape(tld) + "$");

            if (match.Success)
            {
                var recordName = match.Groups[1].Value;
                return (recordName, tld);
            }

            return ("@", fullDomain);
        }

        private static string? GetStringProperty(JsonElement item, string propertyName)
        {
            if (!item.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private sealed record DnsRecordInfo(string Id, string Name, string Type, string Content);
    }
}
