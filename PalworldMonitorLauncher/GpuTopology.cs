using System.Runtime.InteropServices;

namespace PalworldMonitorLauncher;

internal readonly record struct GpuBinding(string AdapterName, string AdapterLuid);

/// <summary>
/// Maps GDI device names (\\.\DISPLAYn) to the DXGI adapter that owns each output.
/// Used only for hybrid-GPU guidance - does not rematch adapters.
/// </summary>
internal static class GpuTopology
{
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    /// <summary>Adapters with ≥1 desktop-attached output (software adapters excluded).</summary>
    public static int DesktopAdapterCount { get; private set; }

    public static IReadOnlyDictionary<string, GpuBinding> Map()
    {
        var result = new Dictionary<string, GpuBinding>(StringComparer.OrdinalIgnoreCase);
        DesktopAdapterCount = 0;

        var iid = IID_IDXGIFactory1;
        var hr = CreateDXGIFactory1(ref iid, out var factory);
        if (hr < 0 || factory == IntPtr.Zero)
            return result;

        try
        {
            var enumAdapters = VTable.Get<EnumAdapters1Delegate>(factory, 12);
            var seenLuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; ; i++)
            {
                var enumHr = enumAdapters(factory, i, out var adapter);
                if (enumHr == DXGI_ERROR_NOT_FOUND || enumHr < 0 || adapter == IntPtr.Zero)
                    break;

                try
                {
                    var getDesc1 = VTable.Get<GetDesc1Delegate>(adapter, 10);
                    if (getDesc1(adapter, out var desc) < 0)
                        continue;
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                        continue;

                    var name = (desc.Description ?? "").TrimEnd('\0').Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Adapter {i}";
                    var luid = FormatLuid(desc.AdapterLuid);

                    var enumOutputs = VTable.Get<EnumOutputsDelegate>(adapter, 7);
                    var desktopOutputs = 0;
                    for (uint o = 0; ; o++)
                    {
                        var outHr = enumOutputs(adapter, o, out var output);
                        if (outHr == DXGI_ERROR_NOT_FOUND || outHr < 0 || output == IntPtr.Zero)
                            break;

                        try
                        {
                            var getDesc = VTable.Get<GetDescDelegate>(output, 7);
                            if (getDesc(output, out var od) < 0)
                                continue;
                            if (!od.AttachedToDesktop)
                                continue;

                            desktopOutputs++;
                            var device = (od.DeviceName ?? "").TrimEnd('\0');
                            if (device.Length > 0)
                                result[device] = new GpuBinding(name, luid);
                        }
                        finally
                        {
                            Marshal.Release(output);
                        }
                    }

                    if (desktopOutputs > 0 && seenLuids.Add(luid))
                        DesktopAdapterCount++;
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return result;
    }

    /// <summary>Short hybrid-GPU warning for the launcher hint line, or empty.</summary>
    public static string HintFor(MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        if (DesktopAdapterCount < 2)
            return "";

        if (string.IsNullOrEmpty(target.AdapterLuid))
            return "Warning: display not mapped to a GPU (hybrid risk).";

        var primary = monitors.FirstOrDefault(m => m.Primary);
        if (primary is null || string.IsNullOrEmpty(primary.AdapterLuid))
            return "";

        if (string.Equals(primary.AdapterLuid, target.AdapterLuid, StringComparison.OrdinalIgnoreCase))
            return "";

        var a = string.IsNullOrEmpty(primary.AdapterName) ? "?" : primary.AdapterName;
        var b = string.IsNullOrEmpty(target.AdapterName) ? "?" : target.AdapterName;
        return $"Warning: primary on {a}, target on {b} (hybrid).";
    }

    /// <summary>ponytail: fails if DXGI reports desktop outputs but no monitor got a GPU name.</summary>
    public static int SelfCheck()
    {
        var map = Map();
        Console.WriteLine($"GpuTopology: {map.Count} output(s), {DesktopAdapterCount} desktop adapter(s)");
        foreach (var kv in map)
            Console.WriteLine($"  {kv.Key} -> {kv.Value.AdapterName} ({kv.Value.AdapterLuid})");

        var mons = Monitors.List();
        if (map.Count > 0 && !mons.Any(m => !string.IsNullOrEmpty(m.AdapterName)))
        {
            Console.Error.WriteLine("SELF-CHECK FAILED: DXGI outputs present but no monitor got an adapter name");
            return 11;
        }

        Console.WriteLine("GpuTopology self-check OK");
        return 0;
    }

    static string FormatLuid(LUID l) =>
        $"{unchecked((uint)l.HighPart):X8}{l.LowPart:X8}";

    static class VTable
    {
        public static TDelegate Get<TDelegate>(IntPtr com, int slot) where TDelegate : Delegate
        {
            var vtable = Marshal.ReadIntPtr(com);
            var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
        }
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int Left, Top, Right, Bottom;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumAdapters1Delegate(IntPtr self, uint adapter, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumOutputsDelegate(IntPtr self, uint output, out IntPtr ppOutput);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDesc1Delegate(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetDescDelegate(IntPtr self, out DXGI_OUTPUT_DESC desc);
}
