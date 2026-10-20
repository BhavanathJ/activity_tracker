using System.Text.Json.Serialization;
using ActivityTracker.Core.Configuration;

namespace ActivityTracker.Service.Http;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExtensionEventPayload))]
[JsonSerializable(typeof(IdleEventPayload))]
[JsonSerializable(typeof(WindowEventPayload))]
[JsonSerializable(typeof(BlurEventPayload))]
[JsonSerializable(typeof(AudioSyncPayload))]
[JsonSerializable(typeof(TrackerConfig))]
[JsonSerializable(typeof(MediaEventPayload))]
internal partial class ServiceJsonContext : JsonSerializerContext
{
}
