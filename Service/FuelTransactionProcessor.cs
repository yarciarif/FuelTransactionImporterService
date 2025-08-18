using FuelTransactionImporterService.Models;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FuelTransactionImporterService.Service
{
    public class FuelTransactionServiceProcessor
    {
        private readonly AppSettings _appSettings;
        private readonly Loggers _logger;

        public FuelTransactionServiceProcessor(AppSettings appSettings, Loggers logger)
        {
            _appSettings = appSettings;
            _logger = logger;
        }

        public async Task RunAsync()
        {
            _logger.Log("===== Çalışma Başladı =====");
            var sw = Stopwatch.StartNew();

            try
            {
                await ProcessTransactionsAsync();
            }
            catch (Exception ex)
            {
                _logger.Log($"HATA: {ex.Message}");
                if (ex.InnerException != null)
                    _logger.Log($"INNER: {ex.InnerException.Message}");
                _logger.Log($"STACKTRACE:\n{ex.StackTrace}");
            }

            sw.Stop();
            _logger.Log($"===== Çalışma Bitti | Süre: {sw.ElapsedMilliseconds} ms =====\n");
        }


        private async Task ProcessTransactionsAsync()
        {
            using var httpClient = new HttpClient();

            var requestObj = new
            {
                automaticRequestInfo = new
                {
                    userId = 0,
                    clientRoleId = 0,
                    userName = _appSettings.FuelApiSettings.UserName,
                    userPassword = _appSettings.FuelApiSettings.UserPassword,
                    transactionId = Guid.NewGuid().ToString()
                },
                startDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                endDate = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                invoicePeriod = "",
                fleetList = _appSettings.FuelApiSettings.FleetList,
                viuId = "",
                invoicE_TYPE = _appSettings.FuelApiSettings.InvoiceType
            };

            var json = JsonSerializer.Serialize(requestObj);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(_appSettings.FuelApiSettings.ApiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Log($"API HATASI ({(int)response.StatusCode}): {response.ReasonPhrase}");
                _logger.Log($"Detaylı cevap: {errorContent}");
                response.EnsureSuccessStatusCode();
            }

            var responseString = await response.Content.ReadAsStringAsync();

            using var jsonDoc = JsonDocument.Parse(responseString);
            if (!jsonDoc.RootElement.TryGetProperty("jsoN_ReturnData", out JsonElement returnDataElement))
            {
                _logger.Log("API yanıtında 'jsoN_ReturnData' bulunamadı.");
                return;
            }

            string jsonReturnDataString = returnDataElement.GetString();
            if (string.IsNullOrWhiteSpace(jsonReturnDataString) || jsonReturnDataString == "[]")
            {
                _logger.Log("Yeni veri bulunamadı.");
                return;
            }

            var transactionsArray = JsonDocument.Parse(jsonReturnDataString).RootElement;

            var transactions = transactionsArray.EnumerateArray()
                .Select(item =>
                {
                    decimal totalPrice = 0;
                    if (item.TryGetProperty("AMOUNT", out var amountProp))
                    {
                        if (amountProp.ValueKind == JsonValueKind.Number)
                            totalPrice = amountProp.GetDecimal();
                        else if (amountProp.ValueKind == JsonValueKind.String)
                        {
                            var amountStr = amountProp.GetString();
                            if (!decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out totalPrice))
                            {
                                decimal.TryParse(amountStr, NumberStyles.Any, new CultureInfo("tr-TR"), out totalPrice);
                            }
                        }
                    }

                    decimal unitPrice = item.TryGetProperty("UNIT_PRICE", out var unitProp) && unitProp.ValueKind == JsonValueKind.Number
                        ? unitProp.GetDecimal()
                        : 0;

                    return new TransactionRecord
                    {
                        Plate = item.GetProperty("PLATE").GetString() ?? "",
                        Liter = decimal.Parse(item.GetProperty("QUANTITY").GetRawText(), CultureInfo.InvariantCulture),
                        FuelType = item.GetProperty("PRODUCT_NAME").GetString() ?? "",
                        TransactionDate = DateTime.Parse(item.GetProperty("PUMP_TRNX_TIME").GetString()),
                        StationTransactionId = item.GetProperty("STATION_TRNX_ID").GetString()?.Trim() ?? "",
                        ViuId = item.TryGetProperty("VIU_ID", out var viuProp) ? viuProp.GetString() ?? "" : "",
                        TotalPrice = totalPrice,
                        UnitPrice = unitPrice
                    };
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.StationTransactionId))
                .ToList();

            if (!transactions.Any())
            {
                _logger.Log("İşlenecek yeni kayıt yok.");
                return;
            }

            await using var conn = new SqlConnection(_appSettings.DatabaseSettings.ConnectionString);
            await conn.OpenAsync();

            var allIds = transactions.Select(t => t.StationTransactionId).Distinct().ToList();
            var paramNames = allIds.Select((id, index) => "@id" + index).ToList();
            var cmdText = $"SELECT StationTransactionId FROM dbo.AY_TransactionData WHERE StationTransactionId IN ({string.Join(",", paramNames)})";

            var existingIds = new HashSet<string>();
            using (var cmdCheck = new SqlCommand(cmdText, conn))
            {
                for (int i = 0; i < allIds.Count; i++)
                    cmdCheck.Parameters.AddWithValue(paramNames[i], allIds[i]);

                using var reader = await cmdCheck.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    existingIds.Add(reader.GetString(0));
            }

            var newTransactions = transactions.Where(t => !existingIds.Contains(t.StationTransactionId)).ToList();
            if (!newTransactions.Any())
            {
                _logger.Log("Yeni kayıt bulunamadı.");
                return;
            }

            // 10 dk içinde aynı plaka ve VIU_ID olanları birleştir
            var groupedTransactions = new List<TransactionRecord>();

            foreach (var plateGroup in newTransactions.GroupBy(t => new { t.Plate, t.ViuId }))
            {
                TransactionRecord currentGroup = null;

                foreach (var t in plateGroup.OrderBy(t => t.TransactionDate))
                {
                    if (currentGroup == null)
                    {
                        currentGroup = new TransactionRecord
                        {
                            Plate = t.Plate,
                            ViuId = t.ViuId,
                            TransactionDate = t.TransactionDate,
                            FuelType = t.FuelType,
                            Liter = t.Liter,
                            TotalPrice = t.TotalPrice,
                            UnitPrice = t.UnitPrice,
                            StationTransactionId = t.StationTransactionId
                        };
                    }
                    else
                    {
                        var diff = t.TransactionDate - currentGroup.TransactionDate;
                        if (diff.TotalMinutes <= 10)
                        {
                            currentGroup.Liter += t.Liter;
                            currentGroup.TotalPrice += t.TotalPrice;
                            currentGroup.StationTransactionId += $", {t.StationTransactionId}";
                        }
                        else
                        {
                            groupedTransactions.Add(currentGroup);
                            currentGroup = new TransactionRecord
                            {
                                Plate = t.Plate,
                                ViuId = t.ViuId,
                                TransactionDate = t.TransactionDate,
                                FuelType = t.FuelType,
                                Liter = t.Liter,
                                TotalPrice = t.TotalPrice,
                                UnitPrice = t.UnitPrice,
                                StationTransactionId = t.StationTransactionId
                            };
                        }
                    }
                }

                if (currentGroup != null)
                    groupedTransactions.Add(currentGroup);
            }

            // DataTable doldurma
            var dt = new DataTable();
            dt.Columns.Add("TransactionId", typeof(Guid));
            dt.Columns.Add("TransactionDate", typeof(DateTime));
            dt.Columns.Add("LicensePlate", typeof(string));
            dt.Columns.Add("Driver", typeof(string));
            dt.Columns.Add("FuelAmount", typeof(decimal));
            dt.Columns.Add("FuelType", typeof(string));
            dt.Columns.Add("StationTransactionId", typeof(string));
            dt.Columns.Add("CreatedAt", typeof(DateTime));
            dt.Columns.Add("ViuId", typeof(string));
            dt.Columns.Add("KontrolTarihi", typeof(DateTime));
            dt.Columns.Add("TotalPrice", typeof(decimal));
            dt.Columns.Add("UnitPrice", typeof(decimal));

            var sb = new StringBuilder();
            foreach (var t in groupedTransactions)
            {
                dt.Rows.Add(Guid.NewGuid(), t.TransactionDate, t.Plate, DBNull.Value, t.Liter, t.FuelType,
                    t.StationTransactionId, DateTime.Now, t.ViuId, DBNull.Value, t.TotalPrice, t.UnitPrice);

                sb.AppendLine($"Plaka: {t.Plate}, Tarih: {t.TransactionDate:yyyy-MM-dd HH:mm:ss}, Miktar: {t.Liter}, Ürün: {t.FuelType}, Tutar: {t.TotalPrice} ₺, Birim Fiyat: {t.UnitPrice} ₺");
            }

            using (var bulkCopy = new SqlBulkCopy(conn))
            {
                bulkCopy.DestinationTableName = "dbo.AY_TransactionData";
                foreach (DataColumn col in dt.Columns)
                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                await bulkCopy.WriteToServerAsync(dt);
            }

            _logger.Log($"{groupedTransactions.Count} yeni kayıt işlendi (10 dk içinde birleştirilmiş).");
            _logger.Log(sb.ToString());
        }
    }
}
