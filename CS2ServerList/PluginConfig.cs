using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace Plugin;

public class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("server-api-key")]
    public string ServerApiKey { get; set; } = "Server API Key";
}

