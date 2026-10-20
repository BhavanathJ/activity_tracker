using System.Text.Json.Serialization;

namespace ActivityTracker.SessionAgent;

public class MediaEventPayload
{
    [JsonPropertyName("Source")]
    public string Source { get; set; } = "agent";

    [JsonPropertyName("Playing")]
    public bool Playing { get; set; }

    [JsonPropertyName("Process")]
    public string Process { get; set; } = "";
}

[JsonSerializable(typeof(MediaEventPayload))]
internal partial class MediaEventPayloadContext : JsonSerializerContext
{
}
