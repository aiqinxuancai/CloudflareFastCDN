using System.Text.Json.Serialization;

namespace CloudflareFastCDN.Web
{
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(StatusResponse))]
    [JsonSerializable(typeof(HistoryData))]
    [JsonSerializable(typeof(LogPage))]
    [JsonSerializable(typeof(ActionResponse))]
    internal partial class WebJsonContext : JsonSerializerContext
    {
    }
}
