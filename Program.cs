using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using OpenAI.Chat;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ComplianceCheck;

class Program
{
    static async Task Main(string[] args)
    {
        var stopwatch = Stopwatch.StartNew();

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Resolve the log file path from configuration (defaults to "log.txt").
        var logFilePath = configuration["Logging:File"] ?? "log.txt";

        // Set up dependency injection
        await using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(builder =>
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddConsole();
                builder.AddDebug();
                builder.AddProvider(new FileLoggerProvider(logFilePath));
            })
            .AddScoped<ComplianceChecker>()
            .BuildServiceProvider();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();

        var program = new Program();
        await program.CheckCompliance(args, services);
        stopwatch.Stop();
        logger.LogInformation($"Execution Time: {stopwatch.Elapsed.TotalMinutes} minutes");
    }
    public async Task CheckCompliance(string[] args, IServiceProvider serviceProvider)
    {
        var complianceChecker = serviceProvider.GetRequiredService<ComplianceChecker>();
        await complianceChecker.CheckCompliance();

        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();
        logger.LogInformation("Compliance check completed. Press any key to exit.");
    }
}
