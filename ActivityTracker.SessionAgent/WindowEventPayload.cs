using System.Text.Json.Serialization;

namespace ActivityTracker.SessionAgent;

public class WindowEventPayload
{
    [JsonPropertyName("ProcessOrDomain")]
    public string ProcessOrDomain { get; set; } = "";
    
    [JsonPropertyName("Title")]
    public string Title { get; set; } = "";
}

[JsonSerializable(typeof(WindowEventPayload))]
internal partial class WindowEventPayloadContext : JsonSerializerContext
{
}
