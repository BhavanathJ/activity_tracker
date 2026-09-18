using System;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;

namespace ActivityTracker.Service;

public static class SessionChangeNotifier
{
    public static event Action<int>? OnSessionChange;

    public static void Notify(int reason)
    {
        OnSessionChange?.Invoke(reason);
    }
}

public class TrackerServiceBase : ServiceBase
{
    private readonly IHost _host;

    public TrackerServiceBase(IHost host)
    {
        _host = host;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        _host.StartAsync().GetAwaiter().GetResult();
    }

    protected override void OnStop()
    {
        _host.StopAsync().GetAwaiter().GetResult();
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        SessionChangeNotifier.Notify((int)changeDescription.Reason);
        base.OnSessionChange(changeDescription);
    }
}
