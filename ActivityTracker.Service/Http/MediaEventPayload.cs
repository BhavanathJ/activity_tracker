using System.Text.Json.Serialization;

namespace ActivityTracker.Service.Http;

public class MediaEventPayload
{
    [JsonPropertyName("Source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("Playing")]
    public bool Playing { get; set; }

    [JsonPropertyName("Process")]
    public string Process { get; set; } = "";
}
