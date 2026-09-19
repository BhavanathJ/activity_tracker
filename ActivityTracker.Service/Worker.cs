using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ActivityTracker.Core.Data;
using ActivityTracker.Service.Tracking;
using ActivityTracker.Service.Http;

namespace ActivityTracker.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly DatabaseManager _dbManager;
    private WindowTracker? _windowTracker;
    private ExtensionListener? _httpListener;
    private SystemStateTracker? _systemStateTracker;

    public Worker(ILogger<Worker> logger, DatabaseManager dbManager)
    {
        _logger = logger;
        _dbManager = dbManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Activity Tracker Service starting.");

        _windowTracker = new WindowTracker(_logger, _dbManager);
        _httpListener = new ExtensionListener(_logger, _dbManager, _windowTracker);
        
        // SystemStateTracker needs to hook into power/session events. 
        // In a true Windows Service, we'd override OnSessionChange/OnPowerEvent in ServiceBase.
        // For BackgroundService we might need to use SystemEvents (which requires a message pump) 
        // or WTSRegisterSessionNotification. We'll implement this carefully in SystemStateTracker.
        _systemStateTracker = new SystemStateTracker(_logger, _windowTracker, _dbManager);

        _windowTracker.Start();
        _httpListener.Start();
        _systemStateTracker.Start();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            // Expected
        }
        finally
        {
            _systemStateTracker.Stop();
            _httpListener.Stop();
            _windowTracker.Stop();
            _logger.LogInformation("Activity Tracker Service stopped.");
        }
    }
}
