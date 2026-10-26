using System.Text;

namespace BatteryDock.Services;

internal static class Logger
{
    private static readonly object Sync = new();
    private static bool _verbose;

    public static void Initialize(bool verbose)
    {
        _verbose = verbose;
        try
        {
            AppPaths.EnsureDataDirectory();
            var file = new FileInfo(AppPaths.LogFile);
            if (file.Exists && file.Length > 512 * 1024)
            {
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", true);
            }
        }
        catch
        {
            // Logging must never stop the application.
        }
    }

    public static void Info(string message)
    {
        if (_verbose) Write("INFO", message);
    }

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string operation, Exception exception)
    {
        Write("ERROR", $"{operation}: {exception.GetType().Name} (0x{exception.HResult:X8}) {exception.Message}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            lock (Sync)
            {
                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}
