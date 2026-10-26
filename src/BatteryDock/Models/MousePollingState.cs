namespace BatteryDock.Models;

internal sealed record MousePollingState(
    int Hz,
    IReadOnlyList<int> SupportedRates,
    string Backend);
