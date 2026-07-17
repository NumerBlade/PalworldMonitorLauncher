using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PalworldMonitorLauncher;

internal readonly record struct GpuProcessHit(string AdapterLuid, double Score);

/// <summary>
/// Detects which GPU Palworld-Win64-Shipping is using via pid-scoped
/// GPU Process Memory counters (same family as Task Manager).
/// Uses PDH wildcards - full GetInstanceNames enumeration is too slow (~40s).
/// </summary>
internal static class GpuProcessProbe
{
    static readonly Regex InstanceRx = new(
        @"^pid_(?<pid>\d+)_luid_0x(?<hi>[0-9A-Fa-f]+)_0x(?<lo>[0-9A-Fa-f]+)(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex InstanceRxSingle = new(
        @"^pid_(?<pid>\d+)_luid_0x(?<luid>[0-9A-Fa-f]+)(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex PathInstanceRx = new(
        @"\\GPU Process Memory\((?<inst>[^)]+)\)\\(?<counter>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_NO_DATA = 0x800007D5;

    public static string NormalizeLuid(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        s = s.TrimStart('0');
        return s.Length == 0 ? "0" : s.ToUpperInvariant();
    }

    public static string FromHighLow(string highHex, string lowHex)
    {
        var hi = highHex.Trim();
        var lo = lowHex.Trim();
        if (hi.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hi = hi[2..];
        if (lo.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) lo = lo[2..];
        hi = hi.PadLeft(8, '0');
        lo = lo.PadLeft(8, '0');
        if (hi.Length > 8) hi = hi[^8..];
        if (lo.Length > 8) lo = lo[^8..];
        return NormalizeLuid(hi + lo);
    }

    public static bool LuidsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(NormalizeLuid(a), NormalizeLuid(b), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseInstance(string instanceName, out int pid, out string luid, out string rest)
    {
        pid = 0;
        luid = "";
        rest = "";
        var m = InstanceRx.Match(instanceName);
        if (m.Success)
        {
            if (!int.TryParse(m.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                return false;
            luid = FromHighLow(m.Groups["hi"].Value, m.Groups["lo"].Value);
            rest = m.Groups["rest"].Value;
            return true;
        }

        m = InstanceRxSingle.Match(instanceName);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
            return false;
        luid = NormalizeLuid(m.Groups["luid"].Value);
        rest = m.Groups["rest"].Value;
        return true;
    }

    /// <summary>Prefer Palworld-Win64-Shipping.exe; fall back to hinted pid.</summary>
    public static int ResolveShippingPid(int hintedPid, TimeSpan waitForShipping)
    {
        var name = Path.GetFileNameWithoutExtension(Paths.ShippingExeName);
        var deadline = DateTime.UtcNow + waitForShipping;
        while (true)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                var id = procs[0].Id;
                foreach (var p in procs) p.Dispose();
                return id;
            }
            foreach (var p in procs) p.Dispose();

            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(250);
        }

        try
        {
            using var p = Process.GetProcessById(hintedPid);
            if (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return hintedPid;
        }
        catch { /* gone */ }

        return hintedPid;
    }

    /// <summary>
    /// Wait settleDelay, resolve shipping PID, then sample until a dedicated-VRAM hit or timeout.
    /// </summary>
    public static GpuProcessHit? WaitForActiveGpu(int hintedPid, TimeSpan settleDelay, TimeSpan timeoutAfterSettle)
    {
        if (settleDelay > TimeSpan.Zero)
            Thread.Sleep(settleDelay);

        var pid = ResolveShippingPid(hintedPid, TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + timeoutAfterSettle;
        GpuProcessHit? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = SamplePidMemory(pid);
            if (last is { } h && h.Score > 1_000_000) // > ~1 MB dedicated/shared
                return h;
            Thread.Sleep(1500);
        }

        return last ?? SamplePidMemory(pid);
    }

    public static GpuProcessHit? SamplePidMemory(int pid)
    {
        // Dedicated first (real VRAM) - matches Task Manager "GPU memory".
        var hit = SampleWildcard(pid, "Dedicated Usage");
        if (hit is { } d && d.Score > 1_000_000)
            return d;

        var shared = SampleWildcard(pid, "Shared Usage");
        if (shared is { } s && s.Score > 1_000_000)
            return s;

        return hit ?? shared;
    }

    static GpuProcessHit? SampleWildcard(int pid, string counter)
    {
        var wild = $@"\GPU Process Memory(pid_{pid}*)\{counter}";
        if (!TryExpand(wild, out var paths) || paths.Count == 0)
            return null;

        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var m = PathInstanceRx.Match(path);
            if (!m.Success) continue;
            var instance = m.Groups["inst"].Value;
            if (!TryParseInstance(instance, out var p, out var luid, out _) || p != pid)
                continue;

            if (!TryReadCounter("GPU Process Memory", counter, instance, out var value))
                continue;
            if (value <= 0) continue;
            sums[luid] = sums.TryGetValue(luid, out var prev) ? prev + value : value;
        }

        if (sums.Count == 0) return null;
        var best = sums.OrderByDescending(kv => kv.Value).First();
        return new GpuProcessHit(best.Key, best.Value);
    }

    static bool TryReadCounter(string category, string counter, string instance, out double value)
    {
        value = 0;
        try
        {
            using var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            value = c.NextValue();
            // Memory counters are usually valid on the first read (unlike % util).
            if (value <= 0)
                value = c.NextValue();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryExpand(string wildCardPath, out List<string> paths)
    {
        paths = [];
        try
        {
            uint chars = 0;
            var st = PdhExpandWildCardPathW(null, wildCardPath, IntPtr.Zero, ref chars, 0);
            if (st != PDH_MORE_DATA && st != 0)
                return false;
            if (chars == 0) return false;

            var buf = Marshal.AllocHGlobal((int)chars * sizeof(char));
            try
            {
                st = PdhExpandWildCardPathW(null, wildCardPath, buf, ref chars, 0);
                if (st != 0 && st != PDH_NO_DATA)
                    return false;

                var p = buf;
                while (true)
                {
                    var s = Marshal.PtrToStringUni(p);
                    if (string.IsNullOrEmpty(s)) break;
                    paths.Add(s);
                    p = IntPtr.Add(p, (s.Length + 1) * sizeof(char));
                }
                return paths.Count > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveName(string luid, IEnumerable<MonitorInfo> monitors)
    {
        foreach (var m in monitors)
        {
            if (LuidsEqual(m.AdapterLuid, luid) && !string.IsNullOrEmpty(m.AdapterName))
                return m.AdapterName;
        }

        foreach (var b in GpuTopology.Map().Values)
        {
            if (LuidsEqual(b.AdapterLuid, luid) && !string.IsNullOrEmpty(b.AdapterName))
                return b.AdapterName;
        }

        return $"GPU LUID {NormalizeLuid(luid)}";
    }

    public static int SelfCheck()
    {
        const string split = "pid_4242_luid_0x00000000_0x0001A42D_phys_0";
        if (!TryParseInstance(split, out var pid, out var luid, out _) ||
            pid != 4242 || !LuidsEqual(luid, "000000000001A42D"))
        {
            Console.Error.WriteLine($"SELF-CHECK FAILED: LUID parse (pid={pid} luid={luid})");
            return 12;
        }

        // Expand against this process - may be empty; must not throw / hang.
        _ = TryExpand($@"\GPU Process Memory(pid_{Environment.ProcessId}*)\Dedicated Usage", out var paths);
        Console.WriteLine($"GpuProcessProbe: PDH expand returned {paths.Count} path(s) for self pid");
        Console.WriteLine("GpuProcessProbe self-check OK");
        return 0;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhExpandWildCardPathW(
        string? szDataSource,
        string szWildCardPath,
        IntPtr mszExpandedPathList,
        ref uint pcchPathListLength,
        uint dwFlags);
}
