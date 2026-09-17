using System;

namespace ActivityTracker.Core.Models;

public record EventRecord
{
    public long Id { get; init; }
    public required string Type { get; init; } // "window", "browser", "audio", "lock"
    public required string ProcessOrDomain { get; init; }
    public string? Browser { get; init; } // null for window/lock, "chrome"|"brave"|"edge" for browser/audio
    public required string Title { get; init; }
    public long StartTime { get; init; } // Unix epoch seconds
    public long? EndTime { get; init; } // Nullable until session ends
}
