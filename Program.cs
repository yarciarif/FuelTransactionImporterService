using System;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

internal class Program
{
    public static int Main(string[] args)
    {
        var logRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        var logDir = Path.Combine(logRoot, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(logDir);

        var logFilePath = Path.Combine(logDir, "log.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logFilePath,
                outputTemplate: "{Timestamp:HH:mm:ss} | {Level:u3} | {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Infinite,
                shared: true)
            .CreateLogger();

        try
        {
            Log.Information("Uygulama baþladý.");

            CreateHostBuilder(args).Build().Run();

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Uygulama beklenmedik þekilde durdu.");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .UseSerilog()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                // appsettings.json, environment variables vb. eklenebilir
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables();
                if (args != null)
                    config.AddCommandLine(args);
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.Configure<FuelApiSettings>(hostContext.Configuration.GetSection("FuelApiSettings"));
                services.Configure<DatabaseSettings>(hostContext.Configuration.GetSection("DatabaseSettings"));

                services.AddHttpClient();

                services.AddSingleton<FuelTransactionProcessor>();
                services.AddHostedService<Worker>();
            });
}
