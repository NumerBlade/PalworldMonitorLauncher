
# Palworld Monitor Launcher

Make [Palworld](https://store.steampowered.com/app/1623730/) treat a monitor you pick as the primary one, with resolution and placement respected, all that **without** changing Windows' real primary display.

> Works process-locally via a small injected DLL. 
> 
> **Single-player / offline recommended.** Injection can conflict with anti-cheat, overlays, or security software. Use at your own risk.

---

## Use (from Releases)

### 1. Download

Grab the latest build from **Releases** and extract it somewhere permanent (not inside the Palworld install folder).

You need these two files in the same folder:

- `PalworldMonitorLauncher.exe`
- `PalworldMonitorShim.dll`

Release builds are **self-contained** - you do **not** need to install the .NET Desktop Runtime.

### 2. Run

**Option A - Steam (recommended)**

1. Steam -> Palworld -> Properties -> Launch Options
2. Set:

```text
"C:\Path\To\PalworldMonitorLauncher.exe" %command%
```

3. Replace the path with the full path to **your** extracted `PalworldMonitorLauncher.exe`
4. Launch Palworld from Steam as usual

**Option B - double-click**

Run `PalworldMonitorLauncher.exe`, then start the game from the launcher UI.

### 3. First launch

1. Pick the target display.
2. Click **LAUNCH**.
3. Later runs remember that display; you can still change it in the list.
4. Optional: **Don't hide** keeps the launcher window open after start.

Settings are stored at `%TEMP%\PalworldMonitor\config.json`.

---

## Build from source

For contributors, or if you prefer not to use a prebuilt release.

### What's in this repo

| Folder | Role |
|---|---|
| `PalworldMonitorShim/` | Injected x64 DLL (display/window hooks) |
| `PalworldMonitorLauncher/` | WinForms launcher + Steam `%command%` wrapper |
| `third_party/minhook/` | Vendored [MinHook](https://github.com/TsudaKageyu/minhook) |

### Requirements

- Windows x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [CMake](https://cmake.org/download/) (3.20+)
- Visual Studio 2022 with **Desktop development with C++**
  (Build Tools-only is fine)

Open **Developer PowerShell for VS 2022** (or any shell with `cmake`, `dotnet`, and MSVC on `PATH`), then `cd` to this repo root.

### 1. Build the shim DLL

```powershell
cmake -S PalworldMonitorShim -B PalworldMonitorShim/build -G "Visual Studio 17 2022" -A x64
cmake --build PalworldMonitorShim/build --config Release
```

Output: `PalworldMonitorShim/build/bin/PalworldMonitorShim.dll`

After changing shim sources, prefer a clean rebuild so stale object files are not linked:

```powershell
Remove-Item PalworldMonitorShim\build -Recurse -Force -ErrorAction SilentlyContinue
cmake -S PalworldMonitorShim -B PalworldMonitorShim/build -G "Visual Studio 17 2022" -A x64
cmake --build PalworldMonitorShim/build --config Release
```

### 2. Build the launcher

```powershell
dotnet build PalworldMonitorLauncher/PalworldMonitorLauncher.csproj -c Release
```

Output: `PalworldMonitorLauncher/bin/Release/net8.0-windows/`

### 3. Copy the DLL next to the launcher

```powershell
Copy-Item PalworldMonitorShim/build/bin/PalworldMonitorShim.dll `
  PalworldMonitorLauncher/bin/Release/net8.0-windows/ -Force
```

Then use that folder the same way as a Release download (Steam launch options or double-click). Framework-dependent builds need the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.

### Self-contained release zip (optional)

Produces a single-file exe (trimmed + compressed) that does **not** need the .NET Desktop Runtime:

```powershell
dotnet publish PalworldMonitorLauncher/PalworldMonitorLauncher.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -o .\publish\PalworldMonitor

Copy-Item PalworldMonitorShim\build\bin\PalworldMonitorShim.dll `
  .\publish\PalworldMonitor\ -Force

Compress-Archive .\publish\PalworldMonitor\* .\publish\PalworldMonitor-win-x64.zip -Force
```

Ship the zip, or the two files inside `publish\PalworldMonitor\`.

### Optional checks

Self-test (no game). Framework-dependent build:

```powershell
.\PalworldMonitorLauncher\bin\Release\net8.0-windows\PalworldMonitorLauncher.exe --mode selftest
```

Self-contained publish folder:

```powershell
.\publish\PalworldMonitor\PalworldMonitorLauncher.exe --mode selftest
```

---

## How it works (short)

Inside the Palworld process only, the shim:

- Makes your chosen monitor look like the primary
- Spoofs screen size to that monitor's physical mode
- Moves/sizes the game window onto that monitor

It does **not** change the system primary display.

**Hybrid GPUs (laptops):** if integrated and discrete GPUs drive different monitors, the launcher shows which GPU owns each display and can warn when Palworld ends up on a different GPU than your chosen monitor. You can dismiss or silence that dialog; status still shows process GPU vs display GPU. (Note that this feature can be slow sometimes during testing, I've found it difficult to predict what causes this.)

---

## License

This project's original code is MIT - see [LICENSE](LICENSE)
(`Copyright (c) 2026 Contributors`).

Vendored MinHook keeps its own BSD-style license under
`third_party/minhook/LICENSE.txt`.
