using System.ComponentModel;
using BatteryDock.Models;

namespace BatteryDock.ViewModels;

internal sealed class DeviceItemViewModel : INotifyPropertyChanged
{
    public DeviceItemViewModel(DeviceReading reading)
    {
        Source = reading;
        Key = reading.Key;
        Name = reading.Name;
        Address = reading.Address;
        Kind = reading.Kind;
        Transport = reading.Transport;
        IsConnected = reading.IsConnected;
        Battery = reading.Battery;
        IsEstimated = reading.IsEstimated;
        IsCharging = reading.IsCharging;
        IsFull = reading.IsFull;
        Origin = reading.Origin;
        ObservedAt = reading.ObservedAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeviceReading Source { get; private set; }
    public string Key { get; }
    public string Name { get; private set; }
    public string Address { get; private set; }
    public DeviceKind Kind { get; private set; }
    public DeviceTransport Transport { get; private set; }
    public bool IsConnected { get; private set; }
    public int? Battery { get; private set; }
    public bool IsEstimated { get; private set; }
    public bool IsCharging { get; private set; }
    public bool IsFull { get; private set; }
    public bool HasChargingState => IsCharging || IsFull;
    public string ChargingText => IsFull ? "已充满" : "充电中";
    public BatteryOrigin Origin { get; private set; }
    public DateTimeOffset? ObservedAt { get; private set; }

    public void Update(DeviceReading reading)
    {
        if (!string.Equals(Key, reading.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Source = reading;
        Name = reading.Name;
        Address = reading.Address;
        Kind = reading.Kind;
        Transport = reading.Transport;
        IsConnected = reading.IsConnected;
        Battery = reading.Battery;
        IsEstimated = reading.IsEstimated;
        IsCharging = reading.IsCharging;
        IsFull = reading.IsFull;
        Origin = reading.Origin;
        ObservedAt = reading.ObservedAt;

        // All displayed fields are derived from this small set; an empty
        // property name refreshes every binding without rebuilding the card.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string PercentText => Battery.HasValue
        ? $"{(IsEstimated ? "≈" : string.Empty)}{Battery.Value}%"
        : "—";

    public double BatteryValue => Battery ?? 0;

    public bool HasBattery => Battery.HasValue;

    public string TransportLabel => Transport == DeviceTransport.Wireless24G ? "2.4G" : "蓝牙";

    public string KindLabel => Kind switch
    {
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Gamepad => "手柄",
        DeviceKind.Headset => "耳机",
        DeviceKind.Phone => "手机",
        _ => Transport == DeviceTransport.Wireless24G ? "无线设备" : "蓝牙设备"
    };

    public string KindGlyph => Kind switch
    {
        DeviceKind.Keyboard => "",
        DeviceKind.Mouse => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Headset => "",
        DeviceKind.Phone => "",
        _ => ""
    };

    public string BatteryTone
    {
        get
        {
            if (!IsConnected || !Battery.HasValue) return "Muted";
            if (Battery.Value <= 10) return "Danger";
            if (Battery.Value <= 20) return "Warning";
            return "Good";
        }
    }

    public string StatusText
    {
        get
        {
            if (IsConnected)
            {
                if (Origin == BatteryOrigin.Current && Battery.HasValue)
                {
                    return $"{KindLabel} · {TransportLabel} · 已连接 · {RelativeTime(ObservedAt)}";
                }

                if (Battery.HasValue)
                {
                    return $"{KindLabel} · {TransportLabel} · 已连接 · 上次 {Battery.Value}% · {RelativeTime(ObservedAt)}";
                }

                if (Transport == DeviceTransport.Wireless24G)
                {
                    var hint = Kind switch
                    {
                        DeviceKind.Mouse => "移动鼠标后读取电量",
                        DeviceKind.Keyboard => "按一下按键后读取电量",
                        DeviceKind.Gamepad => "按一下手柄后读取电量",
                        _ => "使用设备后读取电量"
                    };
                    return $"{KindLabel} · {TransportLabel} · {hint}";
                }

                return $"{KindLabel} · {TransportLabel} · 已连接 · 暂未提供电量";
            }

            if (Battery.HasValue)
            {
                return $"{KindLabel} · {TransportLabel} · 未连接 · 上次 {Battery.Value}% · {RelativeTime(ObservedAt)}";
            }

            return $"{KindLabel} · {TransportLabel} · 未连接";
        }
    }

    public string ToolTipText
    {
        get
        {
            var address = string.IsNullOrWhiteSpace(Address) ? "地址未知" : Address;
            var estimateNote = IsEstimated ? "（推算值）" : string.Empty;
            var action = Kind switch
            {
                DeviceKind.Keyboard => "点击打开键盘按键测试",
                DeviceKind.Mouse => "点击打开鼠标 DPI 设置",
                DeviceKind.Headset => "耳机暂不支持打开设置",
                _ => "点击打开 Windows 蓝牙设置"
            };
            return $"{Name}\n{StatusText}{estimateNote}\n{address}\n\n{action}";
        }
    }

    private static string RelativeTime(DateTimeOffset? observedAt)
    {
        if (!observedAt.HasValue) return "时间未知";
        var delta = DateTimeOffset.Now - observedAt.Value;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45) return "刚刚更新";
        if (delta.TotalMinutes < 60) return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        if (delta.TotalHours < 24) return $"{Math.Max(1, (int)delta.TotalHours)} 小时前";
        if (delta.TotalDays < 30) return $"{Math.Max(1, (int)delta.TotalDays)} 天前";
        return observedAt.Value.LocalDateTime.ToString("MM-dd HH:mm");
    }
}
