using CloudflareFastCDN.Utils;

namespace CloudflareFastCDN.Web
{
    /// <summary>
    /// Web 管理页面查看与手动增删子网缓存。
    /// </summary>
    internal static class SubnetCacheApi
    {
        public static SubnetCacheResponse GetCache()
        {
            var cache = SubnetCache.Shared;
            // 先取版本再取内容：两者之间发生变化时页面下次轮询会再刷新一次
            var version = cache.Version;
            return new SubnetCacheResponse
            {
                Version = version,
                MaxSubnets = SubnetCache.MaxSubnets,
                EvictThreshold = SubnetCache.EvictThreshold,
                SampleCount = Math.Min(AppConfig.CachedCandidateCount, AppConfig.HttpCandidateCount),
                Subnets = cache.Snapshot().Select(record => new SubnetCacheEntry
                {
                    Subnet = record.Subnet,
                    DelayMs = record.DelayMs > 0 ? Math.Round(record.DelayMs, 2) : null,
                    ConsecutiveFailures = record.ConsecutiveFailures,
                    Manual = record.Manual,
                    AddedAt = record.AddedAt
                }).ToList()
            };
        }

        public static SubnetActionResponse Add(SubnetUpdateRequest request)
        {
            return ToResponse(SubnetCache.Shared.AddManual(request.Subnet));
        }

        public static SubnetActionResponse Remove(SubnetUpdateRequest request)
        {
            return ToResponse(SubnetCache.Shared.Remove(request.Subnet));
        }

        private static SubnetActionResponse ToResponse(SubnetEditResult result)
        {
            return new SubnetActionResponse
            {
                Ok = result.Ok,
                Message = result.Message,
                Cache = GetCache()
            };
        }
    }

    internal sealed class SubnetCacheResponse
    {
        public int Version { get; set; }
        public int MaxSubnets { get; set; }
        public int EvictThreshold { get; set; }

        /// <summary>下一轮优选从缓存最前面取出验证的子网数量。</summary>
        public int SampleCount { get; set; }

        public List<SubnetCacheEntry> Subnets { get; set; } = new();
    }

    internal sealed class SubnetCacheEntry
    {
        public string Subnet { get; set; } = string.Empty;

        /// <summary>发现该子网时的 HTTP 延迟；手动添加且尚未测得时为空。</summary>
        public double? DelayMs { get; set; }

        public int ConsecutiveFailures { get; set; }
        public bool Manual { get; set; }
        public DateTimeOffset? AddedAt { get; set; }
    }

    internal sealed class SubnetUpdateRequest
    {
        public string? Subnet { get; set; }
    }

    internal sealed class SubnetActionResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
        public SubnetCacheResponse? Cache { get; set; }
    }
}
