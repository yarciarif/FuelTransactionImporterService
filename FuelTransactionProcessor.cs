using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class FuelTransactionProcessor
{
    private readonly ILogger<FuelTransactionProcessor> _logger;
    private readonly HttpClient _httpClient;
    private readonly FuelApiSettings _apiSettings;
    private readonly string _connectionString;

    public FuelTransactionProcessor(
        ILogger<FuelTransactionProcessor> logger,
        HttpClient httpClient,
        IOptions<FuelApiSettings> apiOptions,
        IOptions<DatabaseSettings> dbOptions)
    {
        _logger = logger;
        _httpClient = httpClient;
        _apiSettings = apiOptions.Value;
        _connectionString = dbOptions.Value.ConnectionString;
    }

    public async Task RunAsync()
    {
        _logger.LogInformation("Başladı: {time}", DateTimeOffset.Now);

        var transactionId = Guid.NewGuid().ToString();
        var startDate = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var requestObj = new
        {
            automaticRequestInfo = new
            {
                userId = 0,
                clientRoleId = 0,
                userName = _apiSettings.UserName,
                userPassword = _apiSettings.UserPassword,
                transactionId = transactionId
            },
            startDate,
            endDate,
            invoicePeriod = "",
            fleetList = _apiSettings.FleetList,
            viuId = "",
            invoicE_TYPE = _apiSettings.InvoiceType
        };

        try
        {
            var swApi = System.Diagnostics.Stopwatch.StartNew();

            var content = new StringContent(JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_apiSettings.ApiUrl, content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadAsStringAsync();

            swApi.Stop();
            _logger.LogInformation("API çağrısı süresi: {ms} ms", swApi.ElapsedMilliseconds);

            using var jsonDoc = JsonDocument.Parse(result);
            if (!jsonDoc.RootElement.TryGetProperty("jsoN_ReturnData", out var returnDataElement))
            {
                _logger.LogWarning("API yanıtında 'jsoN_ReturnData' bulunamadı.");
                return;
            }

            var jsonReturnDataString = returnDataElement.GetString();
            if (string.IsNullOrWhiteSpace(jsonReturnDataString) || jsonReturnDataString == "[]")
            {
                _logger.LogInformation("Yeni veri bulunamadı.");
                return;
            }

            var swParse = System.Diagnostics.Stopwatch.StartNew();

            var transactionsArray = JsonDocument.Parse(jsonReturnDataString).RootElement;

            var transactions = transactionsArray.EnumerateArray()
                .Select(item => new FuelTransaction
                {
                    Plate = item.GetProperty("PLATE").GetString() ?? "",
                    FuelAmount = decimal.Parse(item.GetProperty("QUANTITY").GetRawText(), CultureInfo.InvariantCulture),
                    FuelType = item.GetProperty("PRODUCT_NAME").GetString() ?? "",
                    TransactionDateUtc = DateTime.Parse(item.GetProperty("PUMP_TRNX_TIME").GetString()!, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    StationTransactionId = item.GetProperty("STATION_TRNX_ID").GetString() ?? "",
                    ViuId = item.TryGetProperty("VIU_ID", out var viuProp) ? viuProp.GetString() ?? "" : ""
                })
                .Where(t => !string.IsNullOrEmpty(t.StationTransactionId))
                .ToList();

            swParse.Stop();
            _logger.LogInformation("JSON parse ve veri işleme süresi: {ms} ms", swParse.ElapsedMilliseconds);

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var allIds = transactions.Select(t => t.StationTransactionId).Distinct().ToList();
            if (allIds.Count == 0)
            {
                _logger.LogInformation("İşlenecek yeni kayıt yok.");
                return;
            }

            var idListString = string.Join(",", allIds.Select(id => $"'{id.Replace("'", "''")}'"));
            var existingIds = new HashSet<string>();

            await using (var cmd = new SqlCommand($"SELECT StationTransactionId FROM dbo.AY_ProcessedTransactions WHERE StationTransactionId IN ({idListString})", conn))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    existingIds.Add(reader.GetString(0));
            }

            var newTransactions = transactions.Where(t => !existingIds.Contains(t.StationTransactionId)).ToList();
            if (newTransactions.Count == 0)
            {
                _logger.LogInformation("Yeni kayıt bulunamadı.");
                return;
            }

            var grouped = newTransactions
                .GroupBy(t => new
                {
                    t.Plate,
                    TimeBlock = new DateTime((t.TransactionDateUtc.Ticks / TimeSpan.FromMinutes(5).Ticks) * TimeSpan.FromMinutes(5).Ticks)
                })
                .Select(g => new
                {
                    Plate = g.Key.Plate,
                    FuelAmountSum = g.Sum(x => x.FuelAmount),
                    FuelType = g.First().FuelType,
                    TransactionDateUtc = g.Max(x => x.TransactionDateUtc),
                    StationTransactionIds = g.Select(x => x.StationTransactionId).ToList(),
                    ViuId = g.First().ViuId
                })
                .ToList();

            var dataTable = CreateDataTable(grouped);

            using (var bulkCopy = new SqlBulkCopy(conn))
            {
                bulkCopy.DestinationTableName = "dbo.AY_TransactionData";
                foreach (DataColumn col in dataTable.Columns)
                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);

                await bulkCopy.WriteToServerAsync(dataTable);
            }

            var processedTable = new DataTable();
            processedTable.Columns.Add("StationTransactionId", typeof(string));
            foreach (var trx in newTransactions)
                processedTable.Rows.Add(trx.StationTransactionId);

            using (var bulkCopy = new SqlBulkCopy(conn))
            {
                bulkCopy.DestinationTableName = "dbo.AY_ProcessedTransactions";
                bulkCopy.ColumnMappings.Add("StationTransactionId", "StationTransactionId");
                await bulkCopy.WriteToServerAsync(processedTable);
            }

            _logger.LogInformation("{groupCount} grup eklendi, {transactionCount} işlem işlendi.", grouped.Count, newTransactions.Count);

            if (grouped.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Yeni eklenen kayıtlar:");

                foreach (var group in grouped)
                {
                    string combinedId = string.Join('|', group.StationTransactionIds);
                    DateTime trxDateLocal = DateTime.SpecifyKind(group.TransactionDateUtc, DateTimeKind.Utc).ToLocalTime();

                    sb.AppendLine($"- Plaka: {group.Plate}, Tarih: {trxDateLocal:yyyy-MM-dd HH:mm:ss}, Miktar: {group.FuelAmountSum}, Ürün: {group.FuelType}, TransactionIds: {combinedId}");
                }

                _logger.LogInformation(sb.ToString());
            }
            else
            {
                _logger.LogInformation("Yeni kayıt bulunamadı.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "İşlem sırasında hata oluştu.");
            throw;
        }
    }

    private static DataTable CreateDataTable(IEnumerable<dynamic> grouped)
    {
        var dataTable = new DataTable();
        dataTable.Columns.Add("TransactionId", typeof(Guid));
        dataTable.Columns.Add("TransactionDate", typeof(DateTime));
        dataTable.Columns.Add("LicensePlate", typeof(string));
        dataTable.Columns.Add("FuelAmount", typeof(decimal));
        dataTable.Columns.Add("FuelType", typeof(string));
        dataTable.Columns.Add("StationTransactionId", typeof(string));
        dataTable.Columns.Add("CreatedAt", typeof(DateTime));
        dataTable.Columns.Add("ViuId", typeof(string));

        foreach (var group in grouped)
        {
            string combinedId = string.Join('|', group.StationTransactionIds);
            DateTime trxDateLocal = DateTime.SpecifyKind(group.TransactionDateUtc, DateTimeKind.Utc).ToLocalTime();
            dataTable.Rows.Add(Guid.NewGuid(), trxDateLocal, group.Plate, group.FuelAmountSum, group.FuelType, combinedId, DateTime.Now, group.ViuId);
        }

        return dataTable;
    }
}

public class FuelTransaction
{
    public string Plate { get; set; } = "";
    public decimal FuelAmount { get; set; }
    public string FuelType { get; set; } = "";
    public DateTime TransactionDateUtc { get; set; }
    public string StationTransactionId { get; set; } = "";
    public string ViuId { get; set; } = "";
}