using System.Text.Json.Serialization;

namespace ActivityTracker.Core.Configuration;

[JsonSerializable(typeof(TrackerConfig))]
internal partial class TrackerConfigJsonContext : JsonSerializerContext
{
}
