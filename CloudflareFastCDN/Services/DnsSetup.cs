using CloudflareFastCDN.Settings;

namespace CloudflareFastCDN.Services
{
    internal sealed class DnsUpdateProvider
    {
        public DnsUpdateProvider(string providerName, IDnsRecordManager manager)
        {
            ProviderName = providerName;
            Manager = manager;
        }

        public string ProviderName { get; }
        public IDnsRecordManager Manager { get; }
    }

    /// <summary>
    /// 一份可执行的 DNS 配置：需要维护的域名（含名次）以及它们所属服务商的更新器。
    /// </summary>
    internal sealed record DnsSetup(IReadOnlyList<DnsUpdateProvider> Providers, IReadOnlyList<DnsTarget> Targets)
    {
        public static bool TryCreate(AppSettings settings, out DnsSetup setup, out string error)
        {
            setup = new DnsSetup(Array.Empty<DnsUpdateProvider>(), Array.Empty<DnsTarget>());
            error = string.Empty;

            var targets = DnsProviders.GetTargets(settings);
            if (targets.Count == 0)
            {
                error = DnsProviders.All.Any(provider => provider.Settings(settings).Domains.Count > 0)
                    ? "已添加域名，但还没有为任何域名设置更新排名"
                    : "尚未添加域名，请在 DNS 服务商中添加域名并设置更新排名";
                return false;
            }

            var providers = new List<DnsUpdateProvider>();
            foreach (var provider in DnsProviders.All.Where(provider => targets.Any(target => target.ProviderName == provider.Id)))
            {
                var manager = provider.TryCreateManager(settings);
                if (manager == null)
                {
                    error = provider.MissingCredentialsMessage;
                    return false;
                }

                providers.Add(new DnsUpdateProvider(provider.Id, manager));
            }

            setup = new DnsSetup(providers, targets);
            return true;
        }
    }
}
