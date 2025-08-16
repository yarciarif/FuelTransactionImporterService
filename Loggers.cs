using System;
using System.Globalization;
using System.IO;

public class Loggers
{
    private readonly string _logFolderPath;

    public Loggers(string logFolderPath)
    {
        // Eğer boş veya null ise default Logs klasörü kullan
        _logFolderPath = string.IsNullOrWhiteSpace(logFolderPath)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
            : logFolderPath;

        // Klasörü başta oluşturmak iyi olur
        Directory.CreateDirectory(_logFolderPath);
    }

    public void Log(string message)
    {
        try
        {
            string logFilePath = Path.Combine(_logFolderPath, $"{DateTime.Now:yyyy-MM-dd}.txt");

            // Tarih saat kısmı sadece saat:dakika:saniye formatında olsun istersen:
            string logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

            File.AppendAllText(logFilePath, logMessage);
        }
        catch
        {
            // Loglama sırasında hata oluşursa uygulamayı etkilemesin diye boş bırakıyoruz.
        }
    }
}
