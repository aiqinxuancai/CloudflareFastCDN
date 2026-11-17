using CloudflareFastCDN.Services;
using CloudflareFastCDN.Utils;
using CloudflareFastCDN.Web;
using System.Text.Json;

namespace CloudflareFastCDN.Settings
{
    /// <summary>
    /// 统一管理运行设置。全部设置保存在本地配置文件中，Web 页面的修改也写回该文件；
    /// 旧版的环境变量与命令行参数只在首次启动（或检测到旧版配置文件）时迁移一次，之后不再读取。
    /// </summary>
    internal static class SettingsManager
    {
        private const string MigrationLogPrefix = "迁移旧版配置：";

        private static readonly object SyncRoot = new();
        private static AppSettings _current = new();
        private static TaskCompletionSource _changed = NewChangeSignal();
        private static int _version;
        private static int _dnsVersion;

        public static string SettingsFile { get; } = AppPaths.DataFile("settings.json");

        /// <summary>
        /// 当前生效的设置快照。快照不会被原地修改，保存设置时整体替换。
        /// </summary>
        public static AppSettings Current => Volatile.Read(ref _current);

        public static int Version
        {
            get
            {
                lock (SyncRoot)
                {
                    return _version;
                }
            }
        }

        /// <summary>
        /// 需要更新的域名、名次或其服务商凭证发生变化时递增。
        /// </summary>
        public static int DnsVersion
        {
            get
            {
                lock (SyncRoot)
                {
                    return _dnsVersion;
                }
            }
        }

        /// <param name="environment">读取旧版环境变量的方法，仅用于迁移；测试时可替换。</param>
        public static void Initialize(string[] args, bool isDocker, Func<string, string?>? environment = null)
        {
            var arguments = isDocker
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : ParseCommandLineArgs(args);
            var legacySources = new List<LegacySource>
            {
                new("环境变量", environment ?? Environment.GetEnvironmentVariable),
                new("命令行参数", name => arguments.GetValueOrDefault(name))
            };

            AppSettings settings;
            lock (SyncRoot)
            {
                settings = LoadOrMigrate(legacySources);
                Volatile.Write(ref _current, settings);
            }

            AppConfig.WebPort = settings.Web.Port;
            AppConfig.WebHost = string.IsNullOrEmpty(settings.Web.Host)
                ? (isDocker ? "0.0.0.0" : "127.0.0.1")
                : settings.Web.Host;
            PublishDomainGroups(settings);
        }

        /// <summary>
        /// 返回一个在下一次设置保存后完成的任务。
        /// </summary>
        public static Task WaitForChangeAsync()
        {
            lock (SyncRoot)
            {
                return _changed.Task;
            }
        }

        public static List<DnsTarget> GetDnsTargets()
        {
            return DnsProviders.GetTargets(Current);
        }

        public static List<KeyValuePair<string, string>> BuildSummary()
        {
            var settings = Current;
            var summary = new List<KeyValuePair<string, string>>();
            foreach (var definition in SettingDefinitions.All)
            {
                var value = definition.Get(settings);
                var display = definition.Type switch
                {
                    SettingType.Secret => SettingValidation.MaskSecret(value),
                    SettingType.Headers => FormatHeaderNames(settings.Http.ProbeHeaders),
                    SettingType.Host when string.IsNullOrEmpty(value) => "(自动)",
                    _ => value
                };
                summary.Add(new KeyValuePair<string, string>(definition.Key, display));
            }

            foreach (var provider in DnsProviders.All)
            {
                var domains = provider.Settings(settings).Domains;
                if (domains.Count > 0)
                {
                    summary.Add(new KeyValuePair<string, string>($"dns.{provider.GroupId}.domains",
                        string.Join(", ", domains.Select(domain => $"{domain.Name}({(domain.Rank.HasValue ? $"Top{domain.Rank}" : "未排名")})"))));
                }
            }

            return summary;
        }

        public static SettingsResponse GetSettings()
        {
            var settings = Current;
            var response = new SettingsResponse
            {
                SettingsFile = Path.GetFullPath(SettingsFile),
                MaxRank = SettingDefinitions.MaxRank,
                Startup = new List<StartupItem>
                {
                    new() { Key = "当前监听", Value = $"{AppConfig.WebHost}:{AppConfig.WebPort}" },
                    new() { Key = "日志时区", Value = TimestampedConsole.EffectiveTimeZoneId }
                },
                Domains = DnsProviders.All
                    .SelectMany(provider => provider.Settings(settings).Domains
                        .Select(domain => new DomainItem { Provider = provider.Id, Name = domain.Name, Rank = domain.Rank }))
                    .ToList()
            };

            foreach (var group in SettingDefinitions.Groups)
            {
                var settingsGroup = new SettingsGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    Kind = group.Kind switch
                    {
                        SettingGroupKind.Provider => "provider",
                        SettingGroupKind.Ranking => "ranking",
                        _ => "fields"
                    },
                    Provider = DnsProviders.All.FirstOrDefault(provider => provider.GroupId == group.Id)?.Id,
                    Description = group.Description
                };

                foreach (var definition in SettingDefinitions.All.Where(definition => definition.Group == group.Id))
                {
                    var value = definition.Get(settings);
                    var sensitive = definition.Type is SettingType.Secret or SettingType.Headers;
                    settingsGroup.Items.Add(new SettingItem
                    {
                        Key = definition.Key,
                        Label = definition.Label,
                        Type = FormatType(definition.Type),
                        Description = definition.Description,
                        DefaultValue = sensitive || definition.DefaultValue.Length == 0 ? null : definition.DefaultValue,
                        Min = definition.Min == int.MinValue ? null : definition.Min,
                        Max = definition.Max == int.MaxValue ? null : definition.Max,
                        RestartRequired = definition.RestartRequired,
                        Value = sensitive ? null : value,
                        IsSet = !string.IsNullOrWhiteSpace(value),
                        Hint = string.IsNullOrWhiteSpace(value) ? null : definition.Type switch
                        {
                            SettingType.Secret => SettingValidation.MaskSecret(value),
                            SettingType.Headers => FormatHeaderNames(settings.Http.ProbeHeaders),
                            _ => null
                        }
                    });
                }

                response.Groups.Add(settingsGroup);
            }

            return response;
        }

        public static SettingsUpdateResponse Update(SettingsUpdateRequest request)
        {
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (SyncRoot)
            {
                var current = _current;
                var next = Clone(current);
                foreach (var (key, rawValue) in request.Values ?? new())
                {
                    var definition = SettingDefinitions.Find(key);
                    if (definition == null)
                    {
                        errors[key] = "未知的设置项";
                        continue;
                    }

                    var error = SettingValidation.TryNormalize(definition, rawValue ?? string.Empty, out var normalized);
                    if (error != null)
                    {
                        errors[definition.Key] = error;
                        continue;
                    }

                    SettingValidation.Apply(definition, next, normalized);
                }

                if (request.Domains != null)
                {
                    var error = ApplyDomains(next, request.Domains);
                    if (error != null)
                    {
                        errors["domains"] = error;
                    }
                }

                if (errors.Count == 0)
                {
                    ValidateCandidateCounts(next, request.Values?.Keys ?? Enumerable.Empty<string>(), errors);
                }

                if (errors.Count > 0)
                {
                    return new SettingsUpdateResponse { Ok = false, Message = "部分设置无效，未保存任何修改", Errors = errors };
                }

                var changedKeys = SettingDefinitions.All
                    .Where(definition => !string.Equals(definition.Get(current), definition.Get(next), StringComparison.Ordinal))
                    .Select(definition => definition.Key)
                    .ToList();
                if (DomainsFingerprint(current) != DomainsFingerprint(next))
                {
                    changedKeys.Add("域名与排名");
                }

                if (changedKeys.Count == 0)
                {
                    return new SettingsUpdateResponse { Ok = true, Message = "设置没有变化", Errors = errors };
                }

                try
                {
                    WriteSettingsFile(next);
                }
                catch (Exception ex)
                {
                    return new SettingsUpdateResponse { Ok = false, Message = $"保存配置文件失败：{ex.Message}", Errors = errors };
                }

                var dnsChanged = DnsFingerprint(current) != DnsFingerprint(next);
                Volatile.Write(ref _current, next);
                Console.WriteLine($"Web管理页面更新了设置：{string.Join(", ", changedKeys)}");

                _version++;
                if (dnsChanged)
                {
                    _dnsVersion++;
                }

                PublishDomainGroups(next);
                var changed = _changed;
                _changed = NewChangeSignal();
                changed.TrySetResult();

                return new SettingsUpdateResponse
                {
                    Ok = true,
                    Message = "设置已保存",
                    DnsChanged = dnsChanged,
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// 用页面提交的完整域名列表替换所有服务商的域名；返回错误信息，成功时返回 null。
        /// </summary>
        private static string? ApplyDomains(AppSettings settings, IEnumerable<DomainItem?> items)
        {
            var domainsByProvider = DnsProviders.All.ToDictionary(provider => provider.Id, _ => new List<DomainEntry>());
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var provider = DnsProviders.Find(item.Provider ?? string.Empty);
                if (provider == null)
                {
                    return $"未知的 DNS 服务商：{item.Provider}";
                }

                if (!SettingValidation.TryNormalizeDomain(item.Name, out var name))
                {
                    return $"{provider.DisplayName} 的域名格式无效：{item.Name}";
                }

                if (item.Rank is < 1 or > SettingDefinitions.MaxRank)
                {
                    return $"{name} 的排名超出范围，应为 Top1 ~ Top{SettingDefinitions.MaxRank}";
                }

                var domains = domainsByProvider[provider.Id];
                if (domains.Any(domain => domain.Name == name))
                {
                    return $"{provider.DisplayName} 中重复添加了域名 {name}";
                }

                domains.Add(new DomainEntry { Name = name, Rank = item.Rank });
            }

            foreach (var provider in DnsProviders.All)
            {
                provider.Settings(settings).Domains = domainsByProvider[provider.Id];
            }

            return null;
        }

        private static AppSettings LoadOrMigrate(IReadOnlyList<LegacySource> legacySources)
        {
            var path = Path.GetFullPath(SettingsFile);
            Dictionary<string, string>? legacyFileValues = null;
            var persist = true;

            if (File.Exists(SettingsFile))
            {
                string text;
                try
                {
                    text = File.ReadAllText(SettingsFile);
                }
                catch (Exception ex)
                {
                    // 无法读取时不要用迁移结果覆盖用户的文件
                    Console.WriteLine($"读取配置文件 {path} 失败，本次运行使用默认设置：{ex.Message}");
                    var defaults = new AppSettings();
                    Sanitize(defaults);
                    return defaults;
                }

                try
                {
                    legacyFileValues = TryReadLegacyFile(text);
                    if (legacyFileValues == null)
                    {
                        var settings = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                        foreach (var warning in Sanitize(settings))
                        {
                            Console.WriteLine(warning);
                        }

                        ReportIgnoredLegacySources(legacySources, path);
                        return settings;
                    }

                    Console.WriteLine($"检测到旧版配置文件 {path}，开始迁移为新格式");
                    if (BackupSettingsFile(".v1.bak") is { } backup)
                    {
                        Console.WriteLine($"旧版配置文件已备份为 {backup}");
                    }
                    else
                    {
                        persist = false;
                    }
                }
                catch (JsonException ex)
                {
                    legacyFileValues = null;
                    if (BackupSettingsFile(".invalid") is { } backup)
                    {
                        Console.WriteLine($"配置文件 {path} 不是有效的 JSON（{ex.Message}），已备份为 {backup} 并重新生成");
                    }
                    else
                    {
                        Console.WriteLine($"配置文件 {path} 不是有效的 JSON（{ex.Message}），且备份失败；本次运行使用默认设置，不会覆盖该文件");
                        persist = false;
                    }
                }
            }

            var migrated = new AppSettings();
            var sources = new List<LegacySource>(legacySources);
            if (legacyFileValues != null)
            {
                var values = legacyFileValues;
                sources.Add(new LegacySource("旧版配置文件", name => values.GetValueOrDefault(name)));
            }
#if DEBUG
            sources.Add(new LegacySource("调试文件", name => name switch
            {
                "CLOUDFLARE_KEY" => ReadDebugFile("CLOUDFLARE_KEY.txt"),
                "CLOUDFLARE_DOMAINS" => ReadDebugFile("DOMAINS.txt"),
                _ => null
            }));
#endif

            var result = LegacySettingsMigration.Apply(migrated, sources);
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine(MigrationLogPrefix + warning);
            }

            Sanitize(migrated);
            if (!persist)
            {
                return migrated;
            }

            try
            {
                WriteSettingsFile(migrated);
                Console.WriteLine(result.Migrated.Count > 0
                    ? $"已将{result.Migrated.Count}项旧版配置迁移到配置文件 {path}：{string.Join(", ", result.Migrated)}；之后以配置文件为准，环境变量和命令行参数不再生效"
                    : $"已创建配置文件 {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入配置文件 {path} 失败，本次运行使用迁移后的设置，下次启动会重新迁移：{ex.Message}");
            }

            return migrated;
        }

        /// <summary>
        /// 旧版配置文件的格式为 {"values": {"KEY": "VALUE"}}，没有 version 字段；不是旧版格式时返回 null。
        /// </summary>
        private static Dictionary<string, string>? TryReadLegacyFile(string text)
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("根节点必须是对象");
            }

            JsonElement? values = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (property.Name.Equals("values", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
                {
                    values = property.Value;
                }
            }

            if (values == null)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in values.Value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return result;
        }

        private static void ReportIgnoredLegacySources(IEnumerable<LegacySource> sources, string path)
        {
            foreach (var source in sources)
            {
                var names = LegacySettingsMigration.AllLegacyKeys
                    .Where(name => !string.IsNullOrWhiteSpace(source.Lookup(name)))
                    .ToList();
                if (names.Count > 0)
                {
                    Console.WriteLine($"{source.Name}中仍设置了旧版配置 {string.Join(", ", names)}，这些值已不再生效（设置以 {path} 为准），可以删除");
                }
            }
        }

        /// <summary>
        /// 补全缺失的分组并把无效值恢复为默认值，返回需要提示的警告。手工编辑的配置文件可能包含任意内容。
        /// </summary>
        internal static List<string> Sanitize(AppSettings settings)
        {
            var warnings = new List<string>();
            settings.Version = AppSettings.CurrentVersion;
            settings.Dns ??= new DnsSettings();
            settings.Dns.Cloudflare ??= new CloudflareSettings();
            settings.Dns.TencentCloud ??= new TencentCloudSettings();
            settings.Dns.AlibabaCloud ??= new AlibabaCloudSettings();
            settings.Selection ??= new SelectionSettings();
            settings.Http ??= new HttpSettings();
            settings.Http.ProbeHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.Schedule ??= new ScheduleSettings();
            settings.Web ??= new WebSettings();

            foreach (var definition in SettingDefinitions.All)
            {
                var error = SettingValidation.TryNormalize(definition, definition.Get(settings) ?? string.Empty, out var normalized);
                if (error != null)
                {
                    warnings.Add($"配置文件中 {definition.Key} 的值无效（{error}），本次运行使用默认值");
                    normalized = string.Empty;
                }

                SettingValidation.Apply(definition, settings, normalized);
            }

            foreach (var provider in DnsProviders.All)
            {
                var providerSettings = provider.Settings(settings);
                var domains = new List<DomainEntry>();
                foreach (var entry in providerSettings.Domains ?? new List<DomainEntry>())
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    if (!SettingValidation.TryNormalizeDomain(entry.Name, out var name))
                    {
                        warnings.Add($"配置文件中 {provider.DisplayName} 的域名格式无效，已忽略：{entry.Name}");
                        continue;
                    }

                    if (domains.Any(domain => domain.Name == name))
                    {
                        warnings.Add($"配置文件中 {provider.DisplayName} 重复配置了域名 {name}，已忽略重复项");
                        continue;
                    }

                    var rank = entry.Rank;
                    if (rank is < 1 or > SettingDefinitions.MaxRank)
                    {
                        warnings.Add($"配置文件中 {name} 的排名 {rank} 超出范围（1 ~ {SettingDefinitions.MaxRank}），已视为未排名");
                        rank = null;
                    }

                    domains.Add(new DomainEntry { Name = name, Rank = rank });
                }

                providerSettings.Domains = domains;
            }

            var selection = settings.Selection;
            if (selection.CachedCandidateCount > selection.HttpCandidateCount)
            {
                warnings.Add($"配置文件中 selection.cachedCandidateCount（{selection.CachedCandidateCount}）大于 selection.httpCandidateCount（{selection.HttpCandidateCount}），本次运行按 {selection.HttpCandidateCount} 处理");
                selection.CachedCandidateCount = selection.HttpCandidateCount;
            }

            var highestRank = HighestRank(settings);
            if (highestRank > selection.HttpCandidateCount)
            {
                warnings.Add($"更新排名中使用了 Top{highestRank}，但每轮候选 IP 数只有 {selection.HttpCandidateCount}，超出的名次可能分配不到 IP");
            }

            return warnings;
        }

        /// <summary>
        /// 检查相互关联的设置：子网缓存名额不能超过候选总数，候选总数不能少于已使用的最高名次。
        /// 错误挂在本次修改的设置项上，便于页面定位。
        /// </summary>
        private static void ValidateCandidateCounts(AppSettings settings, IEnumerable<string> changedKeys, Dictionary<string, string> errors)
        {
            const string candidateKey = "selection.httpCandidateCount";
            const string cachedKey = "selection.cachedCandidateCount";
            var changed = changedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selection = settings.Selection;

            if (selection.CachedCandidateCount > selection.HttpCandidateCount)
            {
                errors[changed.Contains(cachedKey) || !changed.Contains(candidateKey) ? cachedKey : candidateKey] =
                    $"子网缓存 IP 数（{selection.CachedCandidateCount}）不能大于每轮候选 IP 数（{selection.HttpCandidateCount}）";
            }

            var highestRank = HighestRank(settings);
            if (highestRank > selection.HttpCandidateCount)
            {
                if (changed.Contains(candidateKey))
                {
                    errors[candidateKey] = $"更新排名中已使用 Top{highestRank}，每轮候选 IP 数不能小于 {highestRank}";
                }
                else
                {
                    errors["domains"] = $"Top{highestRank} 超过了每轮候选 IP 数（{selection.HttpCandidateCount}），请先在优选参数中调大每轮候选 IP 数";
                }
            }
        }

        private static int HighestRank(AppSettings settings)
        {
            return DnsProviders.GetTargets(settings).Select(target => target.Rank).DefaultIfEmpty(0).Max();
        }

        /// <summary>
        /// 需要更新的域名、名次及其服务商凭证；只添加未排名的域名不会触发重新优选。
        /// </summary>
        private static string DnsFingerprint(AppSettings settings)
        {
            var targets = DnsProviders.GetTargets(settings);
            var parts = new List<string>();
            foreach (var provider in DnsProviders.All.Where(provider => targets.Any(target => target.ProviderName == provider.Id)))
            {
                parts.Add(provider.Id);
                parts.AddRange(provider.CredentialDefinitions.Select(definition => definition.Get(settings)));
            }

            parts.AddRange(targets.Select(target => $"{target.ProviderName}/{target.Domain}/{target.Rank}").Order(StringComparer.Ordinal));
            return string.Join('\u0001', parts);
        }

        private static string DomainsFingerprint(AppSettings settings)
        {
            return string.Join('\u0001', DnsProviders.All.SelectMany(provider => provider.Settings(settings).Domains
                .Select(domain => $"{provider.Id}/{domain.Name}/{domain.Rank}")));
        }

        private static void PublishDomainGroups(AppSettings settings)
        {
            DashboardState.SetDomainGroups(DnsProviders.GetTargets(settings)
                .GroupBy(target => (target.Rank, target.ProviderName))
                .Select(group => new DomainGroupInfo
                {
                    Rank = group.Key.Rank,
                    Provider = group.Key.ProviderName,
                    Domains = group.Select(target => target.Domain).ToList()
                })
                .ToList());
        }

        private static AppSettings Clone(AppSettings settings)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
        }

        private static void WriteSettingsFile(AppSettings settings)
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempFile = SettingsFile + ".tmp";
            File.WriteAllBytes(tempFile, JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings));
            if (!OperatingSystem.IsWindows())
            {
                // 文件中包含 DNS 凭证和登录密码
                File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tempFile, SettingsFile, overwrite: true);
        }

        private static string? BackupSettingsFile(string suffix)
        {
            try
            {
                var backup = SettingsFile + suffix;
                File.Copy(SettingsFile, backup, overwrite: true);
                return Path.GetFullPath(backup);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"备份配置文件失败：{ex.Message}");
                return null;
            }
        }

#if DEBUG
        private static string? ReadDebugFile(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
#endif

        private static Dictionary<string, string> ParseCommandLineArgs(string[] args)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var arg in args)
            {
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var splitArg = arg[2..].Split('=', 2);
                if (splitArg.Length == 2)
                {
                    parameters[splitArg[0]] = splitArg[1];
                }
            }

            return parameters;
        }

        private static string FormatHeaderNames(IReadOnlyDictionary<string, string> headers)
        {
            return headers.Count == 0
                ? "(empty)"
                : string.Join(",", headers.Keys);
        }

        private static string FormatType(SettingType type)
        {
            return type switch
            {
                SettingType.Secret => "secret",
                SettingType.Integer => "integer",
                SettingType.Boolean => "boolean",
                SettingType.Url => "url",
                SettingType.Headers => "headers",
                _ => "text"
            };
        }

        private static TaskCompletionSource NewChangeSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
