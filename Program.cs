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

        // AppSettings instance'ýný singleton olarak ekle
        services.AddSingleton(resolver =>
            resolver.GetRequiredService<IOptions<AppSettings>>().Value
        );

        // Loggers sýnýfýný LogDirectory'yi okuyarak singleton olarak ekle
        services.AddSingleton<Loggers>(resolver =>
        {
            var settings = resolver.GetRequiredService<AppSettings>();
            string logFolder = settings.LogSettings?.LogFolderPath ?? "Logs";
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logFolder);
            return new Loggers(logPath);
        });

        // Worker'ý HostedService olarak ekle
        services.AddHostedService<Worker>();

        // Ýstersen FuelTransactionServiceProcessor da inject edilebilir
        services.AddTransient<FuelTransactionServiceProcessor>();
    })
    .Build();

await host.RunAsync();
