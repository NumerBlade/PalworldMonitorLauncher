using System.Runtime.InteropServices;
using System.Text;

namespace PalworldMonitorLauncher;

internal sealed class MonitorInfo
{
    public required string Device { get; init; }
    public required string Label { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Hz { get; init; }
    public bool Primary { get; init; }
    public string AdapterName { get; init; } = "";
    public string AdapterLuid { get; init; } = "";
    public override string ToString() => Label;
}

internal static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion;
        public ushort dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    const uint MONITORINFOF_PRIMARY = 1;
    const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsExW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public static List<MonitorInfo> List()
    {
        var gpu = GpuTopology.Map();
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(hMon, ref mi)) return true;

            int w = 0, h = 0, hz = 0;
            var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsExW(mi.szDevice, ENUM_CURRENT_SETTINGS, ref dm, 0))
            {
                w = (int)dm.dmPelsWidth;
                h = (int)dm.dmPelsHeight;
                hz = (int)dm.dmDisplayFrequency;
            }

            var friendly = mi.szDevice;
            var dd = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (EnumDisplayDevicesW(mi.szDevice, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
                friendly = dd.DeviceString;

            gpu.TryGetValue(mi.szDevice, out var binding);
            var primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
            var label = new StringBuilder();
            label.Append(friendly);
            if (w > 0) label.Append($"  ·  {w}×{h}");
            if (hz > 0) label.Append($" @{hz}");
            if (!string.IsNullOrEmpty(binding.AdapterName))
                label.Append($"  ·  {binding.AdapterName}");
            if (primary) label.Append("  ·  Windows primary");
            label.Append($"  ({mi.szDevice})");

            list.Add(new MonitorInfo
            {
                Device = mi.szDevice,
                Label = label.ToString(),
                Width = w,
                Height = h,
                Hz = hz,
                Primary = primary,
                AdapterName = binding.AdapterName ?? "",
                AdapterLuid = binding.AdapterLuid ?? "",
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
