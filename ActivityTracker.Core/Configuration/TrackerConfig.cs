using System.Collections.Generic;

namespace ActivityTracker.Core.Configuration;

public class TrackerConfig
{
    public List<string> IncludeProcesses { get; set; } = new();
    public List<string> ExcludeProcesses { get; set; } = new();
    public List<string> IncludeDomains { get; set; } = new();
    public List<string> ExcludeDomains { get; set; } = new();
    public int IdleTimeoutSeconds { get; set; } = 180;
    public int HttpPort { get; set; } = 14321;
}
