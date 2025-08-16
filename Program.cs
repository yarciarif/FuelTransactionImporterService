using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using FuelTransactionImporterService.Models;
using FuelTransactionImporterService.Service;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices((hostContext, services) =>
    {
        // appsettings.json'dan AppSettings nesnesini al ve DI konteynerine ekle
        services.Configure<AppSettings>(hostContext.Configuration);

        // AppSettings instance'�n� singleton olarak ekle
        services.AddSingleton(resolver =>
            resolver.GetRequiredService<IOptions<AppSettings>>().Value
        );

        // Loggers s�n�f�n� LogDirectory'yi okuyarak singleton olarak ekle
        services.AddSingleton<Loggers>(resolver =>
        {
            var settings = resolver.GetRequiredService<AppSettings>();
            string logFolder = settings.LogSettings?.LogFolderPath ?? "Logs";
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFolder);
            return new Loggers(logPath);
        });

        // Worker'� HostedService olarak ekle
        services.AddHostedService<Worker>();

        // �stersen FuelTransactionServiceProcessor da inject edilebilir
        services.AddTransient<FuelTransactionServiceProcessor>();
    })
    .Build();

await host.RunAsync();
