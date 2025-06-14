using System;
using AnkietySystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Azure.Functions.Worker;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        var sqlConnectionString = Environment.GetEnvironmentVariable("SqlConnectionString");
        
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));
        
    })
    .Build();

host.Run();