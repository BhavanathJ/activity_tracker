using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ActivityTracker.Service;
using ActivityTracker.Core.Data;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ActivityTrackerService";
});

builder.Services.AddSingleton<DatabaseManager>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ActivityTracker.Service.Jobs.RetentionJob>();

var host = builder.Build();
host.Run();
