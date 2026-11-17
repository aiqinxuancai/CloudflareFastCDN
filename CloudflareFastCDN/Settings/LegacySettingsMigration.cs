namespace CloudflareFastCDN.Settings
{
    /// <summary>
    /// 一个旧版配置来源，例如环境变量、命令行参数或旧版 settings.json 中的 values 字典。
    /// </summary>
    internal sealed record LegacySource(string Name, Func<string, string?> Lookup);

    internal sealed class MigrationResult
    {
        /// <summary>已迁移的旧版名称及其来源。</summary>
        public List<string> Migrated { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// 把旧版按环境变量命名的配置一次性迁移为新版配置文件。来源按传入顺序决定优先级，
    /// 与旧版「环境变量 > 命令行参数 > Web 页面保存的设置」的生效规则保持一致。
    /// </summary>
    internal static class LegacySettingsMigration
    {
        /// <summary>所有旧版设置名称，用于提示仍然残留的环境变量。</summary>
        public static IEnumerable<string> AllLegacyKeys =>
            SettingDefinitions.All.SelectMany(definition => definition.LegacyKeys)
                .Concat(DnsProviders.All.SelectMany(provider => provider.LegacyDomainKeys.SelectMany(keys => keys)));

        public static MigrationResult Apply(AppSettings settings, IReadOnlyList<LegacySource> sources)
        {
            var result = new MigrationResult();

            foreach (var definition in SettingDefinitions.All)
            {
                var found = Find(sources, definition.LegacyKeys);
                if (found == null)
                {
                    continue;
                }

                var (name, source, value) = found.Value;
                var error = SettingValidation.TryNormalize(definition, value, out var normalized);
                if (error != null)
                {
                    result.Warnings.Add($"{source} {name} 的值无效（{error}），已使用默认值");
                    continue;
                }

                SettingValidation.Apply(definition, settings, normalized);
                result.Migrated.Add($"{name}（{source}）");
            }

            foreach (var provider in DnsProviders.All)
            {
                var domains = provider.Settings(settings).Domains;
                for (int rankIndex = 0; rankIndex < provider.LegacyDomainKeys.Count; rankIndex++)
                {
                    var found = Find(sources, provider.LegacyDomainKeys[rankIndex]);
                    if (found == null)
                    {
                        continue;
                    }

                    var (name, source, value) = found.Value;
                    var rank = rankIndex + 1;
                    foreach (var rawDomain in SettingValidation.SplitDomainList(value))
                    {
                        if (!SettingValidation.TryNormalizeDomain(rawDomain, out var domain))
                        {
                            result.Warnings.Add($"{source} {name} 中的域名格式无效，已忽略：{rawDomain}");
                            continue;
                        }

                        var existing = domains.FirstOrDefault(entry => entry.Name == domain);
                        if (existing != null)
                        {
                            // 同一域名只能指向一个 IP，保留更靠前的名次
                            result.Warnings.Add($"{source} {name} 中的 {domain} 已在 Top{existing.Rank} 中配置，忽略重复的 Top{rank}");
                            continue;
                        }

                        domains.Add(new DomainEntry { Name = domain, Rank = rank });
                    }

                    result.Migrated.Add($"{name}（{source}）");
                }
            }

            return result;
        }

        private static (string Name, string Source, string Value)? Find(IReadOnlyList<LegacySource> sources, IReadOnlyList<string> names)
        {
            foreach (var source in sources)
            {
                foreach (var name in names)
                {
                    var value = source.Lookup(name);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return (name, source.Name, value.Trim());
                    }
                }
            }

            return null;
        }
    }
}
