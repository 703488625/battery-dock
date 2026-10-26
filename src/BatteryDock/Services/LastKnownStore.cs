using System.Text.Json;
using BatteryDock.Models;

namespace BatteryDock.Services;

internal sealed class LastKnownStore
{
    private readonly object _sync = new();
    private readonly LastKnownState _state;

    public LastKnownStore()
    {
        _state = Load();
    }

    private static LastKnownState Load()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            if (!File.Exists(AppPaths.LastKnownFile)) return new LastKnownState();
            var json = File.ReadAllText(AppPaths.LastKnownFile);
            var state = JsonSerializer.Deserialize<LastKnownState>(json, JsonDefaults.Options) ?? new LastKnownState();
            state.Devices = new Dictionary<string, LastKnownDevice>(state.Devices, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            Logger.Error("读取上次电量", ex);
            return new LastKnownState();
        }
    }

    public LastKnownDevice? Get(string key)
    {
        lock (_sync)
        {
            return _state.Devices.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Remember(
        string key,
        string name,
        int battery,
        DeviceKind kind,
        DateTimeOffset observedAt,
        bool estimated = false)
    {
        var changed = false;
        lock (_sync)
        {
            if (!_state.Devices.TryGetValue(key, out var existing) ||
                existing.Battery != battery ||
                existing.Name != name ||
                existing.Kind != kind ||
                existing.Estimated != estimated)
            {
                _state.Devices[key] = new LastKnownDevice
                {
                    Name = name,
                    Battery = battery,
                    Estimated = estimated,
                    Kind = kind,
                    ObservedAt = observedAt
                };
                changed = true;

                if (_state.Devices.Count > 100)
                {
                    foreach (var stale in _state.Devices
                                 .OrderByDescending(pair => pair.Value.ObservedAt)
                                 .Skip(100)
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        _state.Devices.Remove(stale);
                    }
                }
            }
            else if (observedAt - existing.ObservedAt > TimeSpan.FromMinutes(10))
            {
                existing.ObservedAt = observedAt;
                changed = true;
            }
        }

        if (changed) Save();
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                AppPaths.EnsureDataDirectory();
                var json = JsonSerializer.Serialize(_state, JsonDefaults.Options);
                var temp = AppPaths.LastKnownFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.LastKnownFile, true);
            }
            catch (Exception ex)
            {
                Logger.Error("保存上次电量", ex);
            }
        }
    }
}
