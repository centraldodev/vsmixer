using System.Text;

namespace VSMixer.Services;

public static class AppLogger
{
    private static readonly object SyncRoot = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VSMixer",
        "Logs");

    public static void Error(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"vsmixer-{DateTime.UtcNow:yyyy-MM-dd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] ERROR {context}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, entry);
            }
        }
        catch
        {
            // Logging must never replace the original application error.
        }
    }
}
