using CloudflareFastCDN.Utils;
using CloudflareFastCDN.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudflareFastCDN.Settings
{
    /// <summary>
    /// 统一管理运行设置。取值优先级：环境变量 > 命令行参数 > Web 页面保存的设置 > 默认值；
    /// 由环境变量或命令行参数指定的项目会被锁定，Web 页面无法修改。
    /// </summary>
    internal static partial class SettingsManager
    {
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, LockInfo> Locks = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);
        private static TaskCompletionSource _changed = NewChangeSignal();
        private static int _version;
        private static int _dnsVersion;

        public static string SettingsFile { get; } =
            File.Exists("/.dockerenv")
                ? "/data/settings.json"
                : "settings.json";

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
        /// DNS 服务商凭证或域名发生变化时递增。
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

        public static void Initialize(string[] args, bool isDocker)
        {
            var parameters = !isDocker ? ParseCommandLineArgs(args) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? ResolveStartup(string key)
            {
                var environmentValue = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(environmentValue))
                {
                    return environmentValue.Trim();
                }

                return parameters.TryGetValue(key, out var argumentValue) && !string.IsNullOrWhiteSpace(argumentValue)
                    ? argumentValue.Trim()
                    : null;
            }

            lock (SyncRoot)
            {
                _defaults = SettingDefinitions.All.ToDictionary(definition => definition.Key, definition => definition.DefaultValue, StringComparer.OrdinalIgnoreCase);
#if DEBUG
                ApplyDebugDefault("CLOUDFLARE_KEY", "CLOUDFLARE_KEY.txt");
                ApplyDebugDefault("CLOUDFLARE_DOMAINS", "DOMAINS.txt");
#endif

                foreach (var definition in SettingDefinitions.All)
                {
                    foreach (var name in new[] { definition.Key, definition.Alias })
                    {
                        if (name == null)
                        {
                            continue;
                        }

                        var environmentValue = Environment.GetEnvironmentVariable(name);
                        if (!string.IsNullOrWhiteSpace(environmentValue))
                        {
                            Locks[definition.Key] = new LockInfo(environmentValue.Trim(), "环境变量", name);
                            break;
                        }

                        if (parameters.TryGetValue(name, out var argumentValue) && !string.IsNullOrWhiteSpace(argumentValue))
                        {
                            Locks[definition.Key] = new LockInfo(argumentValue.Trim(), "命令行参数", name);
                            break;
                        }
                    }
                }

                LoadSavedSettings();
            }

            AppConfig.WebPort = int.TryParse(ResolveStartup("WEB_PORT"), out var webPort) && webPort is >= 0 and <= 65535 ? webPort : 19401;
            AppConfig.WebHost = ResolveStartup("WEB_HOST") ?? (isDocker ? "0.0.0.0" : "127.0.0.1");
            Apply();
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

        public static IReadOnlyList<string> LockedKeys
        {
            get
            {
                lock (SyncRoot)
                {
                    return Locks.Keys.ToList();
                }
            }
        }

        public static List<KeyValuePair<string, string>> BuildSummary()
        {
            lock (SyncRoot)
            {
                var summary = new List<KeyValuePair<string, string>>();
                foreach (var definition in SettingDefinitions.All)
                {
                    var value = GetEffectiveValue(definition);
                    if (definition.Type == SettingType.DomainList && string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var display = definition.Type switch
                    {
                        SettingType.Secret => MaskSecret(value),
                        SettingType.Headers => FormatHeaderNames(ParseHeaderList(value)),
                        SettingType.Boolean => ParseBoolWithDefault(value, false).ToString(),
                        _ => value
                    };
                    var source = Locks.TryGetValue(definition.Key, out var lockInfo)
                        ? $"（{lockInfo.Source} {lockInfo.Name}）"
                        : _saved.ContainsKey(definition.Key) ? "（Web 设置）" : string.Empty;
                    summary.Add(new KeyValuePair<string, string>(definition.Key, display + source));
                }

                return summary;
            }
        }

        public static SettingsResponse GetSettings()
        {
            lock (SyncRoot)
            {
                var response = new SettingsResponse
                {
                    SettingsFile = Path.GetFullPath(SettingsFile),
                    Startup = new List<StartupItem>
                    {
                        new() { Key = "WEB_PORT", Value = AppConfig.WebPort.ToString() },
                        new() { Key = "WEB_HOST", Value = AppConfig.WebHost },
                        new() { Key = "日志时区", Value = TimestampedConsole.EffectiveTimeZoneId }
                    }
                };

                foreach (var group in SettingDefinitions.All.GroupBy(definition => definition.Group))
                {
                    var settingsGroup = new SettingsGroup { Name = group.Key };
                    foreach (var definition in group)
                    {
                        var value = GetEffectiveValue(definition);
                        var sensitive = IsSensitive(definition);
                        Locks.TryGetValue(definition.Key, out var lockInfo);
                        settingsGroup.Items.Add(new SettingItem
                        {
                            Key = definition.Key,
                            Label = definition.Label,
                            Type = FormatType(definition.Type),
                            Description = definition.Description,
                            DefaultValue = sensitive ? null : _defaults[definition.Key],
                            Min = definition.Min == int.MinValue ? null : definition.Min,
                            Max = definition.Max == int.MaxValue ? null : definition.Max,
                            RestartRequired = definition.RestartRequired,
                            Value = sensitive ? null : value,
                            IsSet = !string.IsNullOrWhiteSpace(value),
                            Hint = definition.Type switch
                            {
                                SettingType.Secret => string.IsNullOrWhiteSpace(value) ? null : MaskSecret(value),
                                SettingType.Headers => string.IsNullOrWhiteSpace(value) ? null : FormatHeaderNames(ParseHeaderList(value)),
                                _ => null
                            },
                            Source = lockInfo != null ? "locked" : _saved.ContainsKey(definition.Key) ? "saved" : "default",
                            LockedBy = lockInfo == null ? null : $"{lockInfo.Source} {lockInfo.Name}"
                        });
                    }

                    response.Groups.Add(settingsGroup);
                }

                return response;
            }
        }

        public static SettingsUpdateResponse Update(Dictionary<string, string?> values)
        {
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (SyncRoot)
            {
                foreach (var (key, rawValue) in values)
                {
                    var definition = SettingDefinitions.Find(key);
                    if (definition == null)
                    {
                        errors[key] = "未知的设置项";
                        continue;
                    }

                    if (Locks.TryGetValue(definition.Key, out var lockInfo))
                    {
                        errors[definition.Key] = $"已由{lockInfo.Source} {lockInfo.Name} 锁定，无法在页面中修改";
                        continue;
                    }

                    var error = TryNormalize(definition, rawValue?.Trim() ?? string.Empty, out var value);
                    if (error != null)
                    {
                        errors[definition.Key] = error;
                        continue;
                    }

                    normalized[definition.Key] = value;
                }

                if (errors.Count > 0)
                {
                    return new SettingsUpdateResponse { Ok = false, Message = "部分设置无效，未保存任何修改", Errors = errors };
                }

                var dnsBefore = SnapshotDnsSettings();
                var updated = new Dictionary<string, string>(_saved, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in normalized)
                {
                    // 清空即恢复默认值
                    if (string.IsNullOrEmpty(value))
                    {
                        updated.Remove(key);
                    }
                    else
                    {
                        updated[key] = value;
                    }
                }

                try
                {
                    WriteSavedSettings(updated);
                }
                catch (Exception ex)
                {
                    return new SettingsUpdateResponse { Ok = false, Message = $"保存设置文件失败：{ex.Message}", Errors = errors };
                }

                var changedKeys = normalized.Keys
                    .Where(key => !string.Equals(_saved.GetValueOrDefault(key), updated.GetValueOrDefault(key), StringComparison.Ordinal))
                    .ToList();
                _saved = updated;
                Apply();

                var dnsChanged = !dnsBefore.SequenceEqual(SnapshotDnsSettings());
                if (changedKeys.Count > 0)
                {
                    Console.WriteLine($"Web管理页面更新了设置：{string.Join(", ", changedKeys)}");
                }

                _version++;
                if (dnsChanged)
                {
                    _dnsVersion++;
                }

                var changed = _changed;
                _changed = NewChangeSignal();
                changed.TrySetResult();

                return new SettingsUpdateResponse
                {
                    Ok = true,
                    Message = changedKeys.Count == 0 ? "设置没有变化" : "设置已保存",
                    DnsChanged = dnsChanged,
                    Errors = errors
                };
            }
        }

        private static void Apply()
        {
            string Value(string key) => GetEffectiveValue(SettingDefinitions.Find(key)!);

            AppConfig.CloudflareKey = Value("CLOUDFLARE_KEY");
            AppConfig.TencentCloudSecretId = Value("TENCENTCLOUD_SECRET_ID");
            AppConfig.TencentCloudSecretKey = Value("TENCENTCLOUD_SECRET_KEY");
            AppConfig.AlibabaCloudAccessKeyId = Value("ALIBABACLOUD_ACCESS_KEY_ID");
            AppConfig.AlibabaCloudAccessKeySecret = Value("ALIBABACLOUD_ACCESS_KEY_SECRET");

            AppConfig.CloudflareDomains = ParseDomainList(Value("CLOUDFLARE_DOMAINS"));
            AppConfig.CloudflareDomains2 = ParseDomainList(Value("CLOUDFLARE_DOMAINS2"));
            AppConfig.CloudflareDomains3 = ParseDomainList(Value("CLOUDFLARE_DOMAINS3"));
            AppConfig.TencentCloudDomains = ParseDomainList(Value("TENCENTCLOUD_DOMAINS"));
            AppConfig.TencentCloudDomains2 = ParseDomainList(Value("TENCENTCLOUD_DOMAINS2"));
            AppConfig.TencentCloudDomains3 = ParseDomainList(Value("TENCENTCLOUD_DOMAINS3"));
            AppConfig.AlibabaCloudDomains = ParseDomainList(Value("ALIBABACLOUD_DOMAINS"));
            AppConfig.AlibabaCloudDomains2 = ParseDomainList(Value("ALIBABACLOUD_DOMAINS2"));
            AppConfig.AlibabaCloudDomains3 = ParseDomainList(Value("ALIBABACLOUD_DOMAINS3"));

            AppConfig.PingThreads = ParseNonZeroIntWithDefault(Value("PING_THREADS"), 8);
            AppConfig.MaxIps = ParseNonZeroIntWithDefault(Value("MAX_IPS"), 400);
            AppConfig.PingIntervalMs = ParseNonZeroIntWithDefault(Value("PING_INTERVAL_MS"), 150);
            var httpProbeUrl = Value("HTTP_PROBE_URL");
            AppConfig.HttpProbeUrl = string.IsNullOrWhiteSpace(httpProbeUrl) ? "https://www.visa.cn/" : httpProbeUrl;
            AppConfig.HttpProbeHeaders = ParseHeaderList(Value("HTTP_PROBE_HEADERS"), logInvalid: true);
            AppConfig.HttpProbeTimeoutMs = ParsePositiveIntWithDefault(Value("HTTP_PROBE_TIMEOUT_MS"), 4000);
            AppConfig.HttpSpeedTestTimeoutMs = ParsePositiveIntWithDefault(Value("HTTP_SPEEDTEST_TIMEOUT_MS"), 10000);
            AppConfig.HttpSpeedTestIdleTimeoutMs = ParsePositiveIntWithDefault(Value("HTTP_SPEEDTEST_IDLE_TIMEOUT_MS"), 3000);
            AppConfig.RunMinutes = ParseNonZeroIntWithDefault(Value("RUN_MINUTES"), 60);
            AppConfig.BandwidthPriority = ParseBoolWithDefault(Value("BANDWIDTH_PRIORITY"), false);
            AppConfig.UpdateIPList = ParseBoolWithDefault(Value("UPDATE_IP_LIST"), false);
            AppConfig.EnableSupplementalHttpCheck = ParseBoolWithDefault(Value("ENABLE_SUPPLEMENTAL_HTTP_CHECK"), false);
            AppConfig.ForceInitialFullSelection = ParseBoolWithDefault(Value("FORCE_INITIAL_FULL_SELECTION"), false);
            var webUsername = Value("WEB_USERNAME");
            AppConfig.WebUsername = string.IsNullOrWhiteSpace(webUsername) ? "admin" : webUsername;
            AppConfig.WebPassword = Value("WEB_PASSWORD");

            DashboardState.SetDomainGroups(BuildDomainGroups());
        }

        private static string GetEffectiveValue(SettingDefinition definition)
        {
            if (Locks.TryGetValue(definition.Key, out var lockInfo))
            {
                return lockInfo.Value;
            }

            return _saved.TryGetValue(definition.Key, out var savedValue)
                ? savedValue
                : _defaults.GetValueOrDefault(definition.Key, definition.DefaultValue);
        }

        private static List<string> SnapshotDnsSettings()
        {
            return SettingDefinitions.All
                .Where(definition => definition.AffectsDns)
                .Select(GetEffectiveValue)
                .ToList();
        }

        private static List<DomainGroupInfo> BuildDomainGroups()
        {
            var providers = new (string Name, string[][] Groups)[]
            {
                ("Cloudflare", new[] { AppConfig.CloudflareDomains, AppConfig.CloudflareDomains2, AppConfig.CloudflareDomains3 }),
                ("TencentCloud", new[] { AppConfig.TencentCloudDomains, AppConfig.TencentCloudDomains2, AppConfig.TencentCloudDomains3 }),
                ("AlibabaCloud", new[] { AppConfig.AlibabaCloudDomains, AppConfig.AlibabaCloudDomains2, AppConfig.AlibabaCloudDomains3 })
            };

            var groups = new List<DomainGroupInfo>();
            for (int rank = 0; rank < 3; rank++)
            {
                foreach (var provider in providers)
                {
                    if (provider.Groups[rank].Length > 0)
                    {
                        groups.Add(new DomainGroupInfo { Rank = rank + 1, Provider = provider.Name, Domains = provider.Groups[rank].ToList() });
                    }
                }
            }

            return groups;
        }

        private static string? TryNormalize(SettingDefinition definition, string value, out string normalized)
        {
            normalized = value;
            if (value.Length == 0)
            {
                return null;
            }

            switch (definition.Type)
            {
                case SettingType.Integer:
                    if (!int.TryParse(value, out var number))
                    {
                        return "请输入整数";
                    }

                    if (number < definition.Min || number > definition.Max)
                    {
                        return $"取值范围为 {definition.Min} ~ {definition.Max}";
                    }

                    normalized = number.ToString();
                    return null;

                case SettingType.Boolean:
                    if (!bool.TryParse(value, out var flag))
                    {
                        return "只能为 true 或 false";
                    }

                    normalized = flag ? "true" : "false";
                    return null;

                case SettingType.Url:
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        return "请输入以 http:// 或 https:// 开头的完整地址";
                    }

                    return null;

                case SettingType.DomainList:
                    var domains = value
                        .Split(new[] { ',', '\n', '\r', ';', ' ', '\t', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(domain => domain.TrimEnd('.').ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var invalidDomain = domains.FirstOrDefault(domain => !DomainRegex().IsMatch(domain));
                    if (invalidDomain != null)
                    {
                        return $"域名格式无效：{invalidDomain}";
                    }

                    normalized = string.Join(",", domains);
                    return null;

                case SettingType.Headers:
                    var headers = new List<string>();
                    foreach (var line in value.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var separatorIndex = line.IndexOf(':');
                        var name = separatorIndex > 0 ? line[..separatorIndex].Trim() : string.Empty;
                        var headerValue = separatorIndex > 0 ? line[(separatorIndex + 1)..].Trim() : string.Empty;
                        if (!HeaderNameRegex().IsMatch(name) || headerValue.Length == 0)
                        {
                            return $"请求头格式无效：{line}，应为 Header-Name: Value";
                        }

                        headers.Add($"{name}:{headerValue}");
                    }

                    normalized = string.Join(";", headers);
                    return null;

                default:
                    if (value.Contains('\n') || value.Contains('\r'))
                    {
                        return "不能包含换行";
                    }

                    return null;
            }
        }

        private static void LoadSavedSettings()
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            try
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), SettingsJsonContext.Default.SettingsFileData);
                _saved = new Dictionary<string, string>(
                    (file?.Values ?? new()).Where(item => SettingDefinitions.Find(item.Key) != null && !string.IsNullOrEmpty(item.Value)),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取设置文件 {SettingsFile} 失败，将使用默认设置：{ex.Message}");
            }
        }

        private static void WriteSavedSettings(Dictionary<string, string> values)
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = new SettingsFileData
            {
                Values = values
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value)
            };
            var tempFile = SettingsFile + ".tmp";
            File.WriteAllBytes(tempFile, JsonSerializer.SerializeToUtf8Bytes(file, SettingsJsonContext.Default.SettingsFileData));
            File.Move(tempFile, SettingsFile, overwrite: true);
        }

#if DEBUG
        private static void ApplyDebugDefault(string key, string path)
        {
            if (File.Exists(path))
            {
                _defaults[key] = File.ReadAllText(path).Trim();
            }
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

        private static string[] ParseDomainList(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value
                    .Split(',')
                    .Select(domain => domain.Trim())
                    .Where(domain => !string.IsNullOrWhiteSpace(domain))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        private static IReadOnlyDictionary<string, string> ParseHeaderList(string? value, bool logInvalid = false)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
            {
                return headers;
            }

            int itemIndex = 0;
            foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                itemIndex++;
                var separatorIndex = item.IndexOf(':');
                if (separatorIndex <= 0 || separatorIndex == item.Length - 1)
                {
                    if (logInvalid)
                    {
                        Console.WriteLine($"忽略无效 HTTP_PROBE_HEADERS 项 #{itemIndex}");
                    }
                    continue;
                }

                var name = item[..separatorIndex].Trim();
                var headerValue = item[(separatorIndex + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(headerValue))
                {
                    if (logInvalid)
                    {
                        Console.WriteLine($"忽略无效 HTTP_PROBE_HEADERS 项 #{itemIndex}");
                    }
                    continue;
                }

                headers[name] = headerValue;
            }

            return headers;
        }

        private static int ParseNonZeroIntWithDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, out var result) && result != 0
                ? result
                : defaultValue;
        }

        private static int ParsePositiveIntWithDefault(string? value, int defaultValue)
        {
            return int.TryParse(value, out var result) && result > 0
                ? result
                : defaultValue;
        }

        private static bool ParseBoolWithDefault(string? value, bool defaultValue)
        {
            return bool.TryParse(value, out var result) ? result : defaultValue;
        }

        private static string MaskSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            return $"{value[..Math.Min(4, value.Length)]}***";
        }

        private static string FormatHeaderNames(IReadOnlyDictionary<string, string> headers)
        {
            return headers.Count == 0
                ? "(empty)"
                : string.Join(",", headers.Keys);
        }

        private static bool IsSensitive(SettingDefinition definition)
        {
            return definition.Type is SettingType.Secret or SettingType.Headers;
        }

        private static string FormatType(SettingType type)
        {
            return type switch
            {
                SettingType.Secret => "secret",
                SettingType.Integer => "integer",
                SettingType.Boolean => "boolean",
                SettingType.Url => "url",
                SettingType.DomainList => "domains",
                SettingType.Headers => "headers",
                _ => "text"
            };
        }

        private static TaskCompletionSource NewChangeSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
        private static partial Regex DomainRegex();

        [GeneratedRegex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
        private static partial Regex HeaderNameRegex();

        private sealed record LockInfo(string Value, string Source, string Name);
    }
}
