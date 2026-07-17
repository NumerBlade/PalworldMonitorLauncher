using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PalworldMonitorLauncher;

internal static class Native
{
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint PAGE_READWRITE = 0x04;
    public const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);
}

internal static class Injector
{
    public static void Inject(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("Shim DLL not found", dllPath);

        var hProcess = Native.OpenProcess(Native.PROCESS_ALL_ACCESS, false, pid);
        if (hProcess == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess failed: {Marshal.GetLastWin32Error()}");

        try
        {
            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            var remote = Native.VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)bytes.Length,
                Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remote == IntPtr.Zero)
                throw new InvalidOperationException($"VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");

            if (!Native.WriteProcessMemory(hProcess, remote, bytes, (UIntPtr)bytes.Length, out _))
                throw new InvalidOperationException($"WriteProcessMemory failed: {Marshal.GetLastWin32Error()}");

            var k32 = Native.GetModuleHandleW("kernel32.dll");
            var loadLibrary = Native.GetProcAddress(k32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new InvalidOperationException("GetProcAddress(LoadLibraryW) failed");

            var thread = Native.CreateRemoteThread(hProcess, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
                throw new InvalidOperationException($"CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");

            Native.WaitForSingleObject(thread, Native.INFINITE);
            Native.CloseHandle(thread);
        }
        finally
        {
            Native.CloseHandle(hProcess);
        }
    }
}

internal static class Paths
{
    public const int SteamAppId = 1623730;
    public const string ShippingExeName = "Palworld-Win64-Shipping.exe";

    public static string FindSteamExe()
    {
        foreach (var root in CandidateSteamRoots())
        {
            var exe = Path.Combine(root, "steam.exe");
            if (File.Exists(exe)) return exe;
        }
        throw new FileNotFoundException("steam.exe not found");
    }

    public static string FindShippingExe()
    {
        foreach (var root in CandidateSteamRoots())
        {
            var exe = Path.Combine(root, "steamapps", "common", "Palworld", "Pal", "Binaries", "Win64", ShippingExeName);
            if (File.Exists(exe)) return exe;
        }
        throw new FileNotFoundException($"{ShippingExeName} not found under Steam libraries");
    }

    static IEnumerable<string> CandidateSteamRoots()
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = p.Trim().TrimEnd('\\');
            if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                roots.Add(p);
        }

        Add(@"C:\Program Files (x86)\Steam");
        Add(@"C:\Program Files\Steam");
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        foreach (var steam in roots.ToArray())
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadLines(vdf))
            {
                if (line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i + 1 < parts.Length; i++)
                {
                    if (parts[i].Equals("path", StringComparison.OrdinalIgnoreCase))
                        Add(parts[i + 1].Replace(@"\\", @"\"));
                }
            }
        }
        return roots;
    }

    public static string DefaultDllPath()
    {
        // Relative-only lookup - no machine-specific absolute paths.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PalworldMonitorShim.dll"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "PalworldMonitorShim", "build", "bin", "PalworldMonitorShim.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "PalworldMonitorShim", "build", "bin", "Release", "PalworldMonitorShim.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "PalworldMonitorShim", "build", "Release", "PalworldMonitorShim.dll")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException("PalworldMonitorShim.dll not found; build the shim first");
    }
}

internal static class PipeWait
{
    public static bool WaitReady(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "PalworldMonitorShim", PipeDirection.In);
                client.Connect(500);
                using var reader = new StreamReader(client, Encoding.ASCII, false, 64, leaveOpen: true);
                var line = reader.ReadLine();
                if (line != null && line.StartsWith("ready", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }
}

internal static class Program
{
    /// <summary>Set while the launcher is tearing down so UI callbacks no-op instead of flashing dialogs.</summary>
    internal static volatile bool Exiting;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var dll = GetArg(args, "--dll") ?? Paths.DefaultDllPath();
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PalworldMonitor"));
            LogDir.PurgePreviousRuns();

            // Headless self-test keeps a console.
            if ((GetArg(args, "--mode") ?? "").Equals("selftest", StringComparison.OrdinalIgnoreCase))
            {
                AllocConsole();
                Console.WriteLine($"dll={dll}");
                return RunSelfTest(dll);
            }

            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                // Known PublishTrimmed WinForms gap: UIA provider types missing on WM_DESTROY.
                if (IsUiaDestroyTrimGap(e.Exception)) return;
                CrashLog.Write("ThreadException", e.Exception);
                if (Exiting || IsBenignUiShutdown(e.Exception)) return;
                var text = string.IsNullOrWhiteSpace(e.Exception.Message)
                    ? e.Exception.ToString()
                    : e.Exception.Message;
                MessageBox.Show(text, "Palworld Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                CrashLog.Write("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashLog.Write("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            Application.Run(new LauncherForm(args, dll));
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Main", ex);
            try { AllocConsole(); } catch { /* ignore */ }
            Console.Error.WriteLine(ex);
            MessageBox.Show(ex.ToString(), "Palworld Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static bool IsBenignUiShutdown(Exception ex) =>
        ex is ObjectDisposedException
        || IsUiaDestroyTrimGap(ex)
        || ex is InvalidOperationException ioe &&
           (ioe.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase)
            || ioe.Message.Contains("handle", StringComparison.OrdinalIgnoreCase)
            || ioe.Message.Contains("Invoke", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Trimmed WinForms often can't load UIA COM types when releasing providers on destroy.
    /// Harmless (control is going away); Message is usually empty → blank error dialogs.
    /// </summary>
    static bool IsUiaDestroyTrimGap(Exception ex)
    {
        if (ex is not TypeLoadException) return false;
        var stack = ex.StackTrace ?? "";
        return stack.Contains("ReleaseUiaProvider", StringComparison.Ordinal)
            || stack.Contains("UiaReturnRawElementProvider", StringComparison.Ordinal);
    }

    internal static class CrashLog
    {
        internal static string Path =>
            System.IO.Path.Combine(LogDir.Root, "launcher-exceptions.log");

        internal static void Write(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir.Root);
                File.AppendAllText(Path,
                    $"---- {DateTime.UtcNow:o} [{source}] ----\n{ex}\n\n");
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>%TEMP%\PalworldMonitor - wipe prior-run logs; never touch config.json.</summary>
    internal static class LogDir
    {
        internal static string Root =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PalworldMonitor");

        internal static void PurgePreviousRuns()
        {
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var f in Directory.EnumerateFiles(Root))
                {
                    var name = System.IO.Path.GetFileName(f);
                    if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Keep last crash log across restarts so flash-errors remain diagnosable.
                    if (name.Equals("launcher-exceptions.log", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!name.StartsWith("shim-", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("observe-", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("launcher-", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { File.Delete(f); } catch { /* locked */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    /// <summary>Entry used by the GUI - same Steam/%command%/direct routing as before.</summary>
    internal static int RunLaunch(string[] args, string dll, Action<string>? status, Action<int>? onReady)
    {
        if (TryParseSteamCommand(args, out var launchExe, out var cmdLine))
        {
            status?.Invoke("Steam %command%…");
            return RunSteamCommand(dll, launchExe, cmdLine, status, onReady);
        }

        var mode = GetArg(args, "--mode") ?? "steam";
        return mode.ToLowerInvariant() switch
        {
            "steam" => RunSteam(dll, status, onReady),
            "direct" => RunDirect(dll, status, onReady),
            _ => throw new ArgumentException($"Unknown --mode {mode}"),
        };
    }

    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>
    /// Detect Steam's %command% payload. For Palworld this is usually
    /// <c>...\Palworld\Palworld.exe</c> (bootstrap), sometimes the shipping exe.
    /// </summary>
    static bool TryParseSteamCommand(string[] args, out string launchExe, out string commandLine)
    {
        launchExe = "";
        commandLine = "";
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i].Trim().Trim('"');
            if (raw.Equals("--mode", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("--dll", StringComparison.OrdinalIgnoreCase))
            {
                i++; // skip value
                continue;
            }

            if (!LooksLikePalworldLaunchExe(raw) || !File.Exists(raw))
                continue;

            launchExe = Path.GetFullPath(raw);
            var sb = new StringBuilder();
            sb.Append('"').Append(launchExe).Append('"');
            for (int j = i + 1; j < args.Length; j++)
            {
                if (args[j].Equals("--mode", StringComparison.OrdinalIgnoreCase) ||
                    args[j].Equals("--dll", StringComparison.OrdinalIgnoreCase))
                {
                    j++;
                    continue;
                }
                sb.Append(' ');
                var a = args[j];
                if (a.Length == 0) continue;
                if (a.Contains(' ') || a.Contains('\t'))
                    sb.Append('"').Append(a).Append('"');
                else
                    sb.Append(a);
            }
            commandLine = sb.ToString();
            return true;
        }
        return false;
    }

    static bool LooksLikePalworldLaunchExe(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals(Paths.ShippingExeName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.Equals("Palworld.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        // Any exe under the Palworld game folder Steam gave us.
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
               path.Contains($"{Path.DirectorySeparatorChar}Palworld{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Steam Play via launch options wrapper: we are the process Steam started; we must
    /// start Steam's %command% (usually Palworld.exe), inject the shipping child, wait.
    /// </summary>
    static int RunSteamCommand(string dll, string launchExe, string commandLine,
        Action<string>? status = null, Action<int>? onReady = null)
    {
        var dir = Path.GetDirectoryName(launchExe)!;
        var launchName = Path.GetFileName(launchExe);
        var isShipping = launchName.Equals(Paths.ShippingExeName, StringComparison.OrdinalIgnoreCase);
        status?.Invoke(isShipping ? "Starting shipping…" : "Starting bootstrap…");

        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.ShippingExeName));
        if (existing.Length > 0)
        {
            status?.Invoke("Already running");
            return 2;
        }

        if (isShipping)
            return RunSteamCommandShipping(dll, launchExe, commandLine, dir, status, onReady);

        var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>() };
        if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                0, IntPtr.Zero, dir, ref si, out var pi))
        {
            status?.Invoke($"CreateProcess failed ({Marshal.GetLastWin32Error()})");
            return 3;
        }
        Native.CloseHandle(pi.hThread);

        status?.Invoke("Waiting for shipping…");
        var detectSw = Stopwatch.StartNew();
        Process? proc = null;
        while (detectSw.Elapsed < TimeSpan.FromSeconds(120))
        {
            if (Native.WaitForSingleObject(pi.hProcess, 0) == 0)
            {
                Native.CloseHandle(pi.hProcess);
                status?.Invoke("Bootstrap exited early");
                return 3;
            }
            proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.ShippingExeName)).FirstOrDefault();
            if (proc != null) break;
            Thread.Sleep(25);
        }
        if (proc == null)
        {
            Native.CloseHandle(pi.hProcess);
            status?.Invoke("Timed out (shipping)");
            return 3;
        }

        status?.Invoke("Injecting…");
        try { Injector.Inject(proc.Id, dll); }
        catch (Exception ex)
        {
            status?.Invoke($"Inject failed: {ex.Message}");
            Native.CloseHandle(pi.hProcess);
            return 4;
        }

        WriteLauncherMeta("steam-command", proc.Id, dll, detectSw.Elapsed.TotalMilliseconds, detectSw.Elapsed.TotalMilliseconds);
        var ready = PipeWait.WaitReady(TimeSpan.FromSeconds(15));
        status?.Invoke(ready ? "Shim ready" : "Shim timeout");
        if (ready) onReady?.Invoke(proc.Id);
        PrintLogHint(proc.Id);

        WaitForPidExit(proc.Id);
        Native.CloseHandle(pi.hProcess);
        return 0;
    }

    static int RunSteamCommandShipping(string dll, string shipping, string commandLine, string dir,
        Action<string>? status = null, Action<int>? onReady = null)
    {
        var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>() };
        if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                Native.CREATE_SUSPENDED, IntPtr.Zero, dir, ref si, out var pi))
        {
            status?.Invoke($"CreateProcess failed ({Marshal.GetLastWin32Error()})");
            return 3;
        }

        status?.Invoke("Injecting…");
        try { Injector.Inject(pi.dwProcessId, dll); }
        catch (Exception ex)
        {
            status?.Invoke($"Inject failed: {ex.Message}");
            Native.TerminateProcess(pi.hProcess, 1);
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return 4;
        }

        WriteLauncherMeta("steam-command", pi.dwProcessId, dll, 0, 0);
        Native.ResumeThread(pi.hThread);
        var ready = PipeWait.WaitReady(TimeSpan.FromSeconds(15));
        status?.Invoke(ready ? "Shim ready" : "Shim timeout");
        if (ready) onReady?.Invoke(pi.dwProcessId);
        PrintLogHint(pi.dwProcessId);

        Native.WaitForSingleObject(pi.hProcess, Native.INFINITE);
        Native.CloseHandle(pi.hThread);
        Native.CloseHandle(pi.hProcess);
        return 0;
    }

    static int RunSteam(string dll, Action<string>? status = null, Action<int>? onReady = null)
    {
        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.ShippingExeName));
        if (existing.Length > 0)
        {
            status?.Invoke("Already running");
            return 2;
        }

        var steam = Paths.FindSteamExe();
        status?.Invoke("Starting via Steam…");
        Process.Start(new ProcessStartInfo
        {
            FileName = steam,
            Arguments = $"-applaunch {Paths.SteamAppId}",
            UseShellExecute = false,
        });

        status?.Invoke("Waiting for shipping…");
        var detectSw = Stopwatch.StartNew();
        Process? proc = null;
        while (detectSw.Elapsed < TimeSpan.FromSeconds(120))
        {
            proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.ShippingExeName)).FirstOrDefault();
            if (proc != null) break;
            Thread.Sleep(25);
        }
        if (proc == null)
        {
            status?.Invoke("Timed out (shipping)");
            return 3;
        }

        status?.Invoke("Injecting…");
        try { Injector.Inject(proc.Id, dll); }
        catch (Exception ex)
        {
            status?.Invoke($"Inject failed: {ex.Message}");
            return 4;
        }

        WriteLauncherMeta("steam", proc.Id, dll, detectSw.Elapsed.TotalMilliseconds, detectSw.Elapsed.TotalMilliseconds);
        var ready = PipeWait.WaitReady(TimeSpan.FromSeconds(15));
        status?.Invoke(ready ? "Shim ready" : "Shim timeout");
        if (ready) onReady?.Invoke(proc.Id);
        PrintLogHint(proc.Id);

        WaitForPidExit(proc.Id);
        return 0;
    }

    static int RunDirect(string dll, Action<string>? status = null, Action<int>? onReady = null)
    {
        var shipping = Paths.FindShippingExe();
        var dir = Path.GetDirectoryName(shipping)!;
        EnsureAppId(dir);
        EnsureAppId(Path.GetFullPath(Path.Combine(dir, "..", "..", "..")));

        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.ShippingExeName));
        if (existing.Length > 0)
        {
            status?.Invoke("Already running");
            return 2;
        }

        status?.Invoke("Starting suspended…");
        var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>() };
        var cmd = $"\"{shipping}\"";
        if (!Native.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                Native.CREATE_SUSPENDED, IntPtr.Zero, dir, ref si, out var pi))
        {
            status?.Invoke($"CreateProcess failed ({Marshal.GetLastWin32Error()})");
            return 3;
        }

        status?.Invoke("Injecting…");
        try { Injector.Inject(pi.dwProcessId, dll); }
        catch (Exception ex)
        {
            status?.Invoke($"Inject failed: {ex.Message}");
            Native.TerminateProcess(pi.hProcess, 1);
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return 4;
        }

        WriteLauncherMeta("direct", pi.dwProcessId, dll, 0, 0);
        Native.ResumeThread(pi.hThread);
        var ready = PipeWait.WaitReady(TimeSpan.FromSeconds(15));
        status?.Invoke(ready ? "Shim ready" : "Shim timeout");
        if (ready) onReady?.Invoke(pi.dwProcessId);
        PrintLogHint(pi.dwProcessId);

        Native.WaitForSingleObject(pi.hProcess, Native.INFINITE);
        Native.CloseHandle(pi.hThread);
        Native.CloseHandle(pi.hProcess);
        return 0;
    }

    /// <summary>
    /// Wait for a process we did not CreateProcess ourselves (e.g. Steam-launched).
    /// Do not use Process.ExitCode - that throws for attached processes.
    /// </summary>
    static void WaitForPidExit(int pid)
    {
        while (true)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.WaitForExit(500);
                if (p.HasExited) return;
            }
            catch (ArgumentException)
            {
                // Process already gone.
                return;
            }
        }
    }

    static int RunSelfTest(string dll)
    {
        Console.WriteLine("Self-test: LoadLibrary shim in this process and poke display APIs.");
        var h = Native.LoadLibraryW(dll);
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"LoadLibrary failed: {Marshal.GetLastWin32Error()}");
            return 5;
        }

        var ready = PipeWait.WaitReady(TimeSpan.FromSeconds(5));
        Console.WriteLine(ready ? "Shim pipe: ready" : "Shim pipe: timeout");

        var cx = Native.GetSystemMetrics(0); // SM_CXSCREEN (spoofed when fakePrimary)
        var cy = Native.GetSystemMetrics(1); // SM_CYSCREEN
        Console.WriteLine($"GetSystemMetrics SM_CX/CY={cx}x{cy}");
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (_, _, _, _) => true, IntPtr.Zero);

        PrintLogHint(Environment.ProcessId);
        var dir = Path.Combine(Path.GetTempPath(), "PalworldMonitor");
        var logs = Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"shim-{Environment.ProcessId}-*.jsonl")
            : Array.Empty<string>();
        if (logs.Length == 0)
        {
            Console.Error.WriteLine("SELF-CHECK FAILED: no shim JSONL for this pid");
            return 6;
        }

        string text;
        using (var fs = new FileStream(logs[^1], FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
            text = sr.ReadToEnd();
        Console.WriteLine($"Log: {logs[^1]} ({text.Length} bytes)");
        if (!text.Contains("GetSystemMetrics", StringComparison.Ordinal) &&
            !text.Contains("EnumDisplayMonitors", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("SELF-CHECK FAILED: expected hooked API entries missing");
            return 7;
        }
        if (!text.Contains("fakePrimary\":true", StringComparison.Ordinal))
            Console.WriteLine("Note: fakePrimary not enabled (no target in config) - OK for bare selftest");
        if (!text.Contains("\"spoofed\":true", StringComparison.Ordinal))
            Console.WriteLine("Note: no spoofed metrics (no target configured) - OK for bare selftest");

        var gpuCode = GpuTopology.SelfCheck();
        if (gpuCode != 0) return gpuCode;

        var probeCode = GpuProcessProbe.SelfCheck();
        if (probeCode != 0) return probeCode;

        Console.WriteLine("SELF-CHECK OK");
        return 0;
    }

    static void EnsureAppId(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var path = Path.Combine(directory, "steam_appid.txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, Paths.SteamAppId.ToString());
                Console.WriteLine($"Wrote {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note: could not write steam_appid.txt in {directory}: {ex.Message}");
        }
    }

    static void WriteLauncherMeta(string mode, int pid, string dll, double detectMs, double totalMs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PalworldMonitor");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"launcher-{mode}-{pid}.json");
        File.WriteAllText(path,
            "{\n" +
            $"  \"mode\": \"{mode}\",\n" +
            $"  \"pid\": {pid},\n" +
            $"  \"dll\": \"{dll.Replace("\\", "\\\\")}\",\n" +
            $"  \"detectLatencyMs\": {detectMs.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
            $"  \"totalFromLaunchMs\": {totalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
            $"  \"utc\": \"{DateTime.UtcNow:o}\"\n" +
            "}\n");
        Console.WriteLine($"Launcher meta: {path}");
    }

    static void PrintLogHint(int pid)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PalworldMonitor");
        Console.WriteLine($"Logs: {dir}\\shim-{pid}-*.jsonl");
    }
}
