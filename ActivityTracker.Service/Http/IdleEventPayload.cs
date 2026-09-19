namespace ActivityTracker.Service.Http;

public class IdleEventPayload
{
    public string State { get; set; } = "";
    public double IdleSeconds { get; set; }
}
