using CloudflareFastCDN.Utils;
using Flurl;
using Flurl.Http;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace CloudflareFastCDN.Services
{
    internal class CloudflareAPIManager
    {
        private static readonly Lazy<CloudflareAPIManager> lazy =
            new Lazy<CloudflareAPIManager>(() => new CloudflareAPIManager());

        public static CloudflareAPIManager Instance => lazy.Value;

        private const string BaseUrl = "https://api.cloudflare.com/client/v4";

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
            var responseContent =
                await BaseUrl
                .AppendPathSegment("zones")
                .GetStringAsync();

            var responseObject = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!responseObject.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
            {
                throw new Exception($"Failed to get zone ID. Response: {responseContent}");
            }

            if (!responseObject.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
            {
                throw new Exception($"Invalid zone response. Response: {responseContent}");
            }

            foreach (var item in resultElement.EnumerateArray())
            {
                var zoneName = GetStringProperty(item, "name");
                var zoneId = GetStringProperty(item, "id");

                if (!string.IsNullOrWhiteSpace(zoneName) && domain.EndsWith(zoneName, StringComparison.OrdinalIgnoreCase))
                {
                    return zoneId ?? string.Empty;
                }
            }

            return string.Empty;
        }

        public async Task<List<string>> GetZonesDnsRecordId(string zoneId, string recordName, string? rootDomain = null)
        {
            var responseContent =
                await BaseUrl
                .AppendPathSegment("zones")
                .AppendPathSegment(zoneId)
                .AppendPathSegment("dns_records")
                .GetStringAsync();

            var responseObject = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!responseObject.TryGetProperty("success", out var successElement) || !successElement.GetBoolean())
            {
                throw new Exception($"Failed to get DNS records. Response: {responseContent}");
            }

            if (!responseObject.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
            {
                throw new Exception($"Invalid DNS record response. Response: {responseContent}");
            }

            var results = new List<string>();
            foreach (var item in resultElement.EnumerateArray())
            {
                var recordId = GetStringProperty(item, "id");
                var fullRecordName = GetStringProperty(item, "name");

                if (string.IsNullOrWhiteSpace(recordId) || string.IsNullOrWhiteSpace(fullRecordName))
                {
                    continue;
                }

                var normalizedRecordName = NormalizeRecordName(fullRecordName, rootDomain);
                if (string.Equals(normalizedRecordName, recordName, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(recordId);
                }
            }

            return results;
        }

        public async Task<bool> DeleteRecord(string zoneId, string recordId)
        {
            var response = await BaseUrl
                        .AppendPathSegment($"zones/{zoneId}/dns_records/{recordId}")
                        .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                        .DeleteAsync();

            var result = await response.GetStringAsync();

            return true;
        }

        public async Task<bool> AddOrUpdateTxtRecord(string domain, string recordName, string content)
        {
            var zoneId = await GetZoneId(domain);

            if (string.IsNullOrWhiteSpace(zoneId))
            {
                return false;
            }

            var recordIds = await GetZonesDnsRecordId(zoneId, recordName, domain);

            var json = new
            {
                type = "TXT",
                name = recordName,
                content = content,
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

            var result = await response.GetStringAsync();

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

            var recordIds = await GetZonesDnsRecordId(zoneId, recordName, domain);

            var json = new
            {
                type = "A",
                name = recordName,
                content = ipAddress,
                ttl = 1
            };

            var jsonStr = JsonSerializer.Serialize(json);

            if (recordIds.Count > 0)
            {
                foreach (var recordId in recordIds)
                {
                    var response = await BaseUrl
                        .AppendPathSegment($"zones/{zoneId}/dns_records/{recordId}")
                        .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                        .PutStringAsync(jsonStr);

                    var result = await response.GetStringAsync();

                    if (response.StatusCode != 200)
                    {
                        Console.WriteLine($"Failed to update A record: {recordId}");
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

                var result = await response.GetStringAsync();

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

            var zoneId = await GetZoneId(rootDomain);

            if (string.IsNullOrWhiteSpace(zoneId))
            {
                return false;
            }

            var recordIds = await GetZonesDnsRecordId(zoneId, recordName);

            var json = new
            {
                type = "A",
                name = recordName,
                content = ipAddress,
                ttl = 1
            };

            var jsonStr = JsonSerializer.Serialize(json);

            if (recordIds.Count > 0)
            {
                foreach (var recordId in recordIds)
                {
                    var response = await BaseUrl
                        .AppendPathSegment($"zones/{zoneId}/dns_records/{recordId}")
                        .OnError(async a => { Debug.WriteLine(await a.Response.GetStringAsync()); })
                        .PutStringAsync(jsonStr);

                    var result = await response.GetStringAsync();

                    if (response.StatusCode != 200)
                    {
                        Console.WriteLine($"Failed to update A record: {recordId}");
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

                var result = await response.GetStringAsync();

                if (response.StatusCode != 200)
                {
                    Console.WriteLine("Failed to add A record");
                    return false;
                }
            }

            return true;
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

        private static string NormalizeRecordName(string fullRecordName, string? rootDomain)
        {
            if (string.IsNullOrWhiteSpace(rootDomain))
            {
                return fullRecordName;
            }

            if (string.Equals(fullRecordName, rootDomain, StringComparison.OrdinalIgnoreCase))
            {
                return "@";
            }

            var suffix = $".{rootDomain}";
            if (fullRecordName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return fullRecordName[..^suffix.Length];
            }

            return fullRecordName;
        }
    }
}
