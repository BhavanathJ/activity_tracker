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

public static class PowerChangeNotifier
{
    public static event Action<int>? OnPowerChange;

    public static void Notify(int reason)
    {
        OnPowerChange?.Invoke(reason);
    }
}

public class TrackerServiceBase : ServiceBase
{
    private readonly IHost _host;

    public TrackerServiceBase(IHost host)
    {
        _host = host;
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
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

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus == PowerBroadcastStatus.Suspend)
        {
            PowerChangeNotifier.Notify(0x0004); // PBT_APMSUSPEND
        }
        else if (powerStatus == PowerBroadcastStatus.ResumeAutomatic)
        {
            PowerChangeNotifier.Notify(0x0012); // PBT_APMRESUMEAUTOMATIC
        }

        return base.OnPowerEvent(powerStatus);
    }
}
