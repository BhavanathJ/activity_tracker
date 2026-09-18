using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ActivityTracker.Service;
using ActivityTracker.Core.Data;

var builder = Host.CreateApplicationBuilder(args);
// AddWindowsService removed to manually handle session change notifications

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
