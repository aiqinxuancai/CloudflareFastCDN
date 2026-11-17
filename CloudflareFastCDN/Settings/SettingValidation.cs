using System.Net;
using System.Text.RegularExpressions;

namespace CloudflareFastCDN.Settings
{
    /// <summary>
    /// 设置值的校验与规范化，Web 页面保存、配置文件读取和旧版配置迁移共用同一套规则。
    /// </summary>
    internal static partial class SettingValidation
    {
        private static readonly char[] DomainSeparators = { ',', '\n', '\r', ';', ' ', '\t', '，' };

        /// <summary>
        /// 返回错误信息，校验通过时返回 null；空字符串表示恢复默认值，总是有效。
        /// </summary>
        public static string? TryNormalize(SettingDefinition definition, string value, out string normalized)
        {
            normalized = value.Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            switch (definition.Type)
            {
                case SettingType.Integer:
                    if (!int.TryParse(normalized, out var number))
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
                    if (!bool.TryParse(normalized, out var flag))
                    {
                        return "只能为 true 或 false";
                    }

                    normalized = flag ? "true" : "false";
                    return null;

                case SettingType.Url:
                    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        return "请输入以 http:// 或 https:// 开头的完整地址";
                    }

                    return null;

                case SettingType.Host:
                    if (normalized is "*" or "+" || normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = normalized.ToLowerInvariant();
                        return null;
                    }

                    return IPAddress.TryParse(normalized, out _)
                        ? null
                        : "请输入 IP 地址、localhost 或留空自动选择";

                case SettingType.Headers:
                    var headers = new List<string>();
                    foreach (var line in normalized.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                    if (normalized.Contains('\n') || normalized.Contains('\r'))
                    {
                        return "不能包含换行";
                    }

                    return null;
            }
        }

        /// <summary>
        /// 写入已规范化的值，空字符串表示恢复默认值。
        /// </summary>
        public static void Apply(SettingDefinition definition, AppSettings settings, string normalized)
        {
            definition.Set(settings, normalized.Length == 0 ? definition.DefaultValue : normalized);
        }

        public static bool TryNormalizeDomain(string? value, out string domain)
        {
            domain = (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            return domain.Length is > 0 and <= 253 && DomainRegex().IsMatch(domain);
        }

        /// <summary>拆分旧版以逗号等分隔的域名列表。</summary>
        public static IEnumerable<string> SplitDomainList(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(DomainSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public static string MaskSecret(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "(empty)"
                : $"{value[..Math.Min(4, value.Length)]}***";
        }

        [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
        private static partial Regex DomainRegex();

        [GeneratedRegex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
        private static partial Regex HeaderNameRegex();
    }
}
