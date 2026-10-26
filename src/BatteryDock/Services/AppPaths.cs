namespace BatteryDock.Services;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatteryDock");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LastKnownFile => Path.Combine(DataDirectory, "last-known.json");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }
}
