
using CloudflareFastCDN.Utils;
using Flurl;
using Flurl.Http;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;

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
                //.WithHeader("X-Auth-Email", APIEmail)
                .WithHeader("Content-Type", "application/json")
            );
        }

        public async Task<string> GetZoneId(string domain)
        {
            var responseContent =
                await BaseUrl
                .AppendPathSegment("zones")
                //.AppendQueryParam("name", domain)
                .GetStringAsync();

            var responseObject = JObject.Parse(responseContent);

            if (!bool.Parse(responseObject["success"].ToString()))
            {
                throw new Exception("Failed to get zone ID");
            }


            foreach (var item in responseObject["result"])
            {
                //TODO 需要更严格的匹配
                if (domain.EndsWith((string)item["name"]))
                {
                    return (string)item["id"];
                }
            }


            return string.Empty;
        }

        public async Task<List<string>> GetZonesDnsRecordId(string zoneId, string recordName)
        {
            var responseContent =
                await BaseUrl
                .AppendPathSegment("zones")
                .AppendPathSegment(zoneId)
                .AppendPathSegment("dns_records")
                .GetStringAsync();

            var responseObject = JObject.Parse(responseContent);

            if (!bool.Parse(responseObject["success"].ToString()))
            {
                throw new Exception("Failed to get zone ID");
            }

            
            var results = new List<string>();
            foreach (var item in responseObject["result"])
            {
                string zoneName = (string)item["zone_name"];

                if (((string)item["name"]).Replace($".{zoneName}", "") == recordName)
                {
                    results.Add((string)item["id"]);
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
            //TODO 子域名有问题？
            var zoneId = await GetZoneId(domain);

            if (string.IsNullOrWhiteSpace(zoneId))
            { 
                return false;
            }

            var recordIds = await GetZonesDnsRecordId(zoneId, recordName);

            var json = new JObject(
                new JProperty("type", "TXT"),
                new JProperty("name", recordName),
                new JProperty("content", content),
                new JProperty("ttl", 60)
            );

            var jsonStr = json.ToString();

            foreach (var item in recordIds)
            {
                await DeleteRecord(zoneId, item);
            }


            //Add
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

        /// <summary>
        /// 更新A记录
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="recordName"></param>
        /// <param name="ipAddress"></param>
        /// <returns></returns>
        public async Task<bool> AddOrUpdateARecord(string domain, string recordName, string ipAddress)
        {
            var zoneId = await GetZoneId(domain);

            if (string.IsNullOrWhiteSpace(zoneId))
            {
                return false;
            }

            var recordIds = await GetZonesDnsRecordId(zoneId, recordName);

            var json = new JObject(
                new JProperty("type", "A"),
                new JProperty("name", recordName),
                new JProperty("content", ipAddress),
                new JProperty("ttl", 1) // 使用自动 TTL
            );

            var jsonStr = json.ToString();

            if (recordIds.Count > 0)
            {
                // 更新现有记录
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
                // 添加新记录
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

            var json = new JObject(
                new JProperty("type", "A"),
                new JProperty("name", recordName),
                new JProperty("content", ipAddress),
                new JProperty("ttl", 1) // 使用自动 TTL
            );

            var jsonStr = json.ToString();

            if (recordIds.Count > 0)
            {
                // 更新现有记录
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
                // 添加新记录
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
                // 如果域名部分少于3，则认为整个字符串是根域名
                return ("@", fullDomain);
            }

            var tld = string.Join(".", parts.TakeLast(2));
            var match = Regex.Match(fullDomain, @"(.+)\." + Regex.Escape(tld) + "$");

            if (match.Success)
            {
                var recordName = match.Groups[1].Value;
                return (recordName, tld);
            }

            // 如果无法正确拆分，返回默认值
            return ("@", fullDomain);
        }
    }
}