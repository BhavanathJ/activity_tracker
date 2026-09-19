using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ActivityTracker.Service;
using ActivityTracker.Service.Logging;
using ActivityTracker.Core.Data;

var builder = Host.CreateApplicationBuilder(args);
// AddWindowsService removed to manually handle session change notifications

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "ActivityTracker",
    "logs");
var logPath = Path.Combine(logDir, "service.log");
builder.Logging.AddFile(logPath, LogLevel.Information);

builder.Services.AddSingleton<DatabaseManager>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ActivityTracker.Service.Jobs.RetentionJob>();

var host = builder.Build();

if (!Environment.UserInteractive)
{
    System.ServiceProcess.ServiceBase.Run(new TrackerServiceBase(host));
}
else
{
    host.Run();
}
