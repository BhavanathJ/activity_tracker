namespace ActivityTracker.Core.Models;

public record MonthlySummary
{
    public required string Month { get; init; } // Format: YYYY-MM
    public required string Category { get; init; } // "window", "browser", "audio", "lock"
    public required string Key { get; init; } // Process name, Domain, or "Locked"
    public string? Browser { get; init; }
    public long TotalSeconds { get; init; }
}
