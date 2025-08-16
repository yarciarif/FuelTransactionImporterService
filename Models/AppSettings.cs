public class AppSettings
{
    public FuelApiSettings FuelApiSettings { get; set; }
    public DatabaseSettings DatabaseSettings { get; set; }
    public LogSettings LogSettings { get; set; }
}

public class FuelApiSettings
{
    public string ApiUrl { get; set; }
    public string UserName { get; set; }
    public string UserPassword { get; set; }
    public string FleetList { get; set; }
    public string InvoiceType { get; set; }
}

public class DatabaseSettings
{
    public string ConnectionString { get; set; }
}

public class LogSettings
{
    public string LogFolderPath { get; set; }
}
