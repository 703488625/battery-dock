namespace BatteryDock.Models;

internal sealed record MouseDpiState(
    int DpiX,
    int DpiY,
    int MinDpi,
    int MaxDpi,
    int Step,
    string Backend);
