using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly FuelTransactionProcessor _processor;

    public Worker(ILogger<Worker> logger, FuelTransactionProcessor processor)
    {
        _logger = logger;
        _processor = processor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker ba�lad�: {time}", DateTimeOffset.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _processor.RunAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "��lem s�ras�nda hata olu�tu.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("Worker duruyor: {time}", DateTimeOffset.Now);
    }
}
