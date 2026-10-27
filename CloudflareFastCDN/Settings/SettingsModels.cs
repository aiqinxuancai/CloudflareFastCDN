using System.Text.Json.Serialization;

namespace CloudflareFastCDN.Settings
{
    internal sealed class SettingsFileData
    {
        public Dictionary<string, string> Values { get; set; } = new();
    }

    internal sealed class SettingsResponse
    {
        public string SettingsFile { get; set; } = string.Empty;
        public List<StartupItem> Startup { get; set; } = new();
        public List<SettingsGroup> Groups { get; set; } = new();
    }

    internal sealed class StartupItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    internal sealed class SettingsGroup
    {
        public string Name { get; set; } = string.Empty;
        public List<SettingItem> Items { get; set; } = new();
    }

    internal sealed class SettingItem
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? DefaultValue { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public bool RestartRequired { get; set; }
        /// <summary>当前生效值；敏感项不返回。</summary>
        public string? Value { get; set; }
        public bool IsSet { get; set; }
        /// <summary>敏感项的脱敏提示，例如密钥前缀或请求头名称。</summary>
        public string? Hint { get; set; }
        /// <summary>locked / saved / default</summary>
        public string Source { get; set; } = string.Empty;
        public string? LockedBy { get; set; }
    }

    internal sealed class SettingsUpdateRequest
    {
        public Dictionary<string, string?> Values { get; set; } = new();
    }

    internal sealed class SettingsUpdateResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool DnsChanged { get; set; }
        public bool SelectionQueued { get; set; }
        public Dictionary<string, string> Errors { get; set; } = new();
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(SettingsFileData))]
    [JsonSerializable(typeof(SettingsResponse))]
    [JsonSerializable(typeof(SettingsUpdateRequest))]
    [JsonSerializable(typeof(SettingsUpdateResponse))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
