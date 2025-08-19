using FuelTransactionImporterService.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly FuelTransactionServiceProcessor _serviceProcessor;

    public Worker(ILogger<Worker> logger, FuelTransactionServiceProcessor serviceProcessor)
    {
        _logger = logger;
        _serviceProcessor = serviceProcessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Servis baþlatýldý.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _serviceProcessor.RunAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Servis baþlatýlýrken hata ile karþýlaþtýnýz.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
