namespace IoBuild.Api.Devices.Domain.Model.Catalog;

public sealed record DeviceCatalogEntry(string Code, string DisplayName, string Scope);
public static class DeviceCatalog
{
    private static readonly IReadOnlyDictionary<string, DeviceCatalogEntry> Entries = new Dictionary<string, DeviceCatalogEntry>(StringComparer.Ordinal)
    {
        ["SmartMeter"] = new("SmartMeter", "Smart Meter", "floor"),
        ["WaterSensor"] = new("WaterSensor", "Water Sensor", "floor"),
        ["SmokeDetector"] = new("SmokeDetector", "Smoke Detector", "floor"),
        ["AirConditioner"] = new("AirConditioner", "Air Conditioner", "unit"),
        ["SmartLight"] = new("SmartLight", "Smart Light", "unit"),
        ["EdgeNode"] = new("EdgeNode", "Edge Node (ESP32)", "unit")
    };
    public static DeviceCatalogEntry? Find(string code) => Entries.GetValueOrDefault(code);
}

/// <summary>A numeric threshold the ESP32 edge node accepts in commands/{id}. Defaults mirror the firmware.</summary>
public sealed record NumericSetting(string Name, double Min, double Max, double Step, double Default, string Unit);

public static class EdgeNodeSettings
{
    public static readonly IReadOnlyList<NumericSetting> Numeric =
    [
        new("lightHoldSeconds", 5, 600, 5, 10, "s"),   // light stays on after last motion
        new("climateDelta", 0.5, 5, 0.5, 1.5, "C"),    // climate turns on at ambient + delta
        new("alarmDelta", 1, 10, 0.5, 3, "C"),         // buzzer at ambient + delta
        new("blindStep", 0.5, 5, 0.5, 1, "C"),         // blind opens/closes per this change
        new("blindAngle", 0, 180, 5, 0, "deg"),        // servo target while blindAuto is false
    ];
    /// <summary>On/off settings and their firmware defaults.</summary>
    public static readonly IReadOnlyList<(string Name, bool Default)> Booleans =
    [
        ("alarmEnabled", true),   // buzzer + fast LED on over-temperature
        ("motionEnabled", true),  // PIR sensor drives the hallway light
        ("blindAuto", true),      // true: blind follows temperature; false: servo goes to blindAngle
    ];
    public static NumericSetting? Find(string name) => Numeric.FirstOrDefault(setting => setting.Name == name);
    public static bool IsBoolean(string name) => Booleans.Any(setting => setting.Name == name);
}

internal static class DeviceStateLocks
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> Gates = new();
    public static async Task<IDisposable> EnterAsync(int deviceId, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }
    private sealed class Releaser(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
