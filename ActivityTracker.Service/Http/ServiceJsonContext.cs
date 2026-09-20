using System.Text.Json.Serialization;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.Service.Http;

[JsonSerializable(typeof(ExtensionEventPayload))]
[JsonSerializable(typeof(IdleEventPayload))]
[JsonSerializable(typeof(WindowEventPayload))]
[JsonSerializable(typeof(TrackerConfig))]
internal partial class ServiceJsonContext : JsonSerializerContext
{
}
