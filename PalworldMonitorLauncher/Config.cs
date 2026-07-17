using System.Text.Json;

namespace PalworldMonitorLauncher;

internal sealed class AppConfig
{
    public bool FakePrimary { get; set; } = true;
    public string TargetDevice { get; set; } = "";
    public bool Configured { get; set; }
    public bool SuppressGpuMismatchWarn { get; set; }

    public static string Path =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PalworldMonitor", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppConfig();
            var json = File.ReadAllText(Path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var c = new AppConfig();
            if (root.TryGetProperty("fakePrimary", out var fp))
                c.FakePrimary = fp.ValueKind != JsonValueKind.False && fp.GetRawText() != "0";
            if (root.TryGetProperty("targetDevice", out var td) && td.ValueKind == JsonValueKind.String)
                c.TargetDevice = td.GetString() ?? "";
            if (root.TryGetProperty("configured", out var cfg))
                c.Configured = cfg.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("suppressGpuMismatchWarn", out var sg))
                c.SuppressGpuMismatchWarn = sg.ValueKind == JsonValueKind.True;
            // Pre-GUI configs: device set ⇒ treat as configured.
            if (!c.Configured && !string.IsNullOrWhiteSpace(c.TargetDevice))
                c.Configured = true;
            return c;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // Manual JSON (no JsonSerializer) so trimmed publishes stay reflection-free
        // and the shim's naive parser still sees "\\\\.\\DISPLAYn".
        File.WriteAllText(Path,
            "{\n" +
            $"  \"fakePrimary\": {(FakePrimary ? "true" : "false")},\n" +
            $"  \"configured\": true,\n" +
            $"  \"targetDevice\": \"{EscapeJson(TargetDevice)}\",\n" +
            $"  \"suppressGpuMismatchWarn\": {(SuppressGpuMismatchWarn ? "true" : "false")}\n" +
            "}\n");
        Configured = true;
    }

    static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal);
}
