#include "hooks.h"
#include "log.h"

#include <MinHook.h>

#include <Windows.h>

#include <atomic>
#include <cctype>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>

namespace {

std::atomic<int> g_windowLogs{0};
HWND g_gameHwnd = nullptr;

// Process-local fake primary: spoof metrics/flags and place UnrealWindow.
bool g_fakePrimary = false;
wchar_t g_targetDevice[32] = {};
HMONITOR g_targetMon = nullptr;
int g_targetCx = 0;
int g_targetCy = 0;
RECT g_targetRect{};

using Fn_EnumDisplayMonitors = BOOL(WINAPI*)(HDC, LPCRECT, MONITORENUMPROC, LPARAM);
using Fn_GetMonitorInfoW = BOOL(WINAPI*)(HMONITOR, LPMONITORINFO);
using Fn_GetMonitorInfoA = BOOL(WINAPI*)(HMONITOR, LPMONITORINFO);
using Fn_EnumDisplayDevicesW = BOOL(WINAPI*)(LPCWSTR, DWORD, PDISPLAY_DEVICEW, DWORD);
using Fn_EnumDisplaySettingsExW = BOOL(WINAPI*)(LPCWSTR, DWORD, DEVMODEW*, DWORD);
using Fn_GetSystemMetrics = int(WINAPI*)(int);
using Fn_GetSystemMetricsForDpi = int(WINAPI*)(int, UINT);
using Fn_CreateWindowExW = HWND(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, DWORD, int, int, int, int, HWND, HMENU, HINSTANCE, LPVOID);
using Fn_ShowWindow = BOOL(WINAPI*)(HWND, int);

Fn_EnumDisplayMonitors Real_EnumDisplayMonitors = nullptr;
Fn_GetMonitorInfoW Real_GetMonitorInfoW = nullptr;
Fn_GetMonitorInfoA Real_GetMonitorInfoA = nullptr;
Fn_EnumDisplayDevicesW Real_EnumDisplayDevicesW = nullptr;
Fn_EnumDisplaySettingsExW Real_EnumDisplaySettingsExW = nullptr;
Fn_GetSystemMetrics Real_GetSystemMetrics = nullptr;
Fn_GetSystemMetricsForDpi Real_GetSystemMetricsForDpi = nullptr;
Fn_CreateWindowExW Real_CreateWindowExW = nullptr;
Fn_ShowWindow Real_ShowWindow = nullptr;

std::string HexPtr(const void* p) {
  char buf[32];
  snprintf(buf, sizeof(buf), "0x%llX", static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(p)));
  return buf;
}

bool DeviceEq(const wchar_t* a, const wchar_t* b) {
  if (!a || !b) return false;
  return _wcsicmp(a, b) == 0;
}

void LoadConfig() {
  // No machine-specific defaults - config/env must supply the target GDI device.
  g_fakePrimary = false;
  g_targetDevice[0] = L'\0';

  if (const wchar_t* env = _wgetenv(L"PALWORLD_MONITOR_TARGET")) {
    if (env[0]) wcscpy_s(g_targetDevice, env);
  }
  if (const wchar_t* env = _wgetenv(L"PALWORLD_MONITOR_FAKE_PRIMARY")) {
    if (_wcsicmp(env, L"0") == 0 || _wcsicmp(env, L"false") == 0) g_fakePrimary = false;
    if (_wcsicmp(env, L"1") == 0 || _wcsicmp(env, L"true") == 0) g_fakePrimary = true;
  }

  wchar_t path[MAX_PATH] = {};
  GetTempPathW(MAX_PATH, path);
  wcscat_s(path, L"PalworldMonitor\\config.json");

  std::ifstream in(path);
  if (!in) {
    if (g_targetDevice[0]) g_fakePrimary = true;
    return;
  }
  std::string json((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

  auto grab = [&](const char* key) -> std::string {
    std::string k = std::string("\"") + key + "\"";
    auto p = json.find(k);
    if (p == std::string::npos) return {};
    p = json.find(':', p);
    if (p == std::string::npos) return {};
    while (p < json.size() && (json[p] == ':' || json[p] == ' ' || json[p] == '\t')) ++p;
    if (p >= json.size()) return {};
    if (json[p] == '"') {
      auto end = json.find('"', p + 1);
      if (end == std::string::npos) return {};
      return json.substr(p + 1, end - p - 1);
    }
    auto end = p;
    while (end < json.size() && (isalnum((unsigned char)json[end]) || json[end] == '.' || json[end] == '-'))
      ++end;
    return json.substr(p, end - p);
  };

  std::string device = grab("targetDevice");
  if (!device.empty()) {
    // Normalize doubled backslashes from naive JSON files (e.g. "\\\\.\\DISPLAYn").
    std::string norm;
    for (size_t i = 0; i < device.size(); ++i) {
      if (device[i] == '\\' && i + 1 < device.size() && device[i + 1] == '\\') {
        norm.push_back('\\');
        ++i;
      } else {
        norm.push_back(device[i]);
      }
    }
    MultiByteToWideChar(CP_UTF8, 0, norm.c_str(), -1, g_targetDevice, 32);
  }
  std::string fp = grab("fakePrimary");
  if (fp == "false" || fp == "0") g_fakePrimary = false;
  else if (fp == "true" || fp == "1") g_fakePrimary = true;
  else if (g_targetDevice[0]) g_fakePrimary = true;

  if (!g_targetDevice[0]) g_fakePrimary = false;
}

struct ResolveCtx {
  HMONITOR mon = nullptr;
  int cx = 0, cy = 0;
  RECT rect{};
  bool found = false;
};

BOOL CALLBACK ResolveEnum(HMONITOR mon, HDC, LPRECT, LPARAM data) {
  auto* ctx = reinterpret_cast<ResolveCtx*>(data);
  MONITORINFOEXW mi{};
  mi.cbSize = sizeof(mi);
  if (!Real_GetMonitorInfoW(mon, reinterpret_cast<MONITORINFO*>(&mi))) return TRUE;
  if (!DeviceEq(mi.szDevice, g_targetDevice)) return TRUE;
  ctx->mon = mon;
  ctx->rect = mi.rcMonitor;
  ctx->cx = mi.rcMonitor.right - mi.rcMonitor.left;
  ctx->cy = mi.rcMonitor.bottom - mi.rcMonitor.top;
  ctx->found = true;
  return FALSE;
}

bool ResolveTarget() {
  if (!Real_EnumDisplayMonitors || !Real_GetMonitorInfoW) return false;
  ResolveCtx ctx{};
  Real_EnumDisplayMonitors(nullptr, nullptr, ResolveEnum, reinterpret_cast<LPARAM>(&ctx));
  if (!ctx.found) {
    shimlog::Info("fakePrimary.error",
                  std::string(",\"msg\":\"target not found\",\"device\":\"") + shimlog::EscW(g_targetDevice) + "\"");
    g_fakePrimary = false;
    return false;
  }
  g_targetMon = ctx.mon;
  g_targetRect = ctx.rect;

  // GetMonitorInfo bounds are DPI-virtualized for unaware callers (e.g. 1280x720 at 200%).
  // SM_CXSCREEN spoof must use physical pixels - EnumDisplaySettings is physical.
  int cx = ctx.cx;
  int cy = ctx.cy;
  if (Real_EnumDisplaySettingsExW) {
    DEVMODEW dm{};
    dm.dmSize = sizeof(dm);
    if (Real_EnumDisplaySettingsExW(g_targetDevice, ENUM_CURRENT_SETTINGS, &dm, 0) &&
        dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) {
      cx = static_cast<int>(dm.dmPelsWidth);
      cy = static_cast<int>(dm.dmPelsHeight);
    } else {
      // Fall back to largest enumerated mode.
      for (DWORD mode = 0;; ++mode) {
        DEVMODEW m{};
        m.dmSize = sizeof(m);
        if (!Real_EnumDisplaySettingsExW(g_targetDevice, mode, &m, 0)) break;
        if (static_cast<int>(m.dmPelsWidth) > cx) {
          cx = static_cast<int>(m.dmPelsWidth);
          cy = static_cast<int>(m.dmPelsHeight);
        }
      }
    }
  }
  if (cx <= 0 || cy <= 0) {
    shimlog::Info("fakePrimary.error", ",\"msg\":\"could not resolve physical mode\"");
    g_fakePrimary = false;
    return false;
  }
  g_targetCx = cx;
  g_targetCy = cy;

  std::ostringstream oss;
  oss << ",\"enabled\":true,\"device\":\"" << shimlog::EscW(g_targetDevice) << "\""
      << ",\"hmonitor\":\"" << HexPtr(g_targetMon) << "\""
      << ",\"cx\":" << g_targetCx << ",\"cy\":" << g_targetCy
      << ",\"infoBounds\":{\"l\":" << g_targetRect.left << ",\"t\":" << g_targetRect.top
      << ",\"r\":" << g_targetRect.right << ",\"b\":" << g_targetRect.bottom
      << ",\"w\":" << ctx.cx << ",\"h\":" << ctx.cy << "}";
  shimlog::Info("fakePrimary.resolved", oss.str());
  return true;
}

void SpoofMonitorInfoFlags(LPMONITORINFO lpmi) {
  if (!g_fakePrimary || !lpmi) return;
  bool isTarget = false;
  if (lpmi->cbSize >= sizeof(MONITORINFOEXW)) {
    isTarget = DeviceEq(reinterpret_cast<MONITORINFOEXW*>(lpmi)->szDevice, g_targetDevice);
  } else {
    isTarget = EqualRect(&lpmi->rcMonitor, &g_targetRect) != 0;
  }
  if (isTarget)
    lpmi->dwFlags |= MONITORINFOF_PRIMARY;
  else
    lpmi->dwFlags &= ~MONITORINFOF_PRIMARY;
}

void SpoofMonitorInfoFlagsA(LPMONITORINFO lpmi) {
  if (!g_fakePrimary || !lpmi) return;
  bool isTarget = false;
  if (lpmi->cbSize >= sizeof(MONITORINFOEXA)) {
    char targetA[32];
    WideCharToMultiByte(CP_ACP, 0, g_targetDevice, -1, targetA, 32, nullptr, nullptr);
    isTarget = _stricmp(reinterpret_cast<MONITORINFOEXA*>(lpmi)->szDevice, targetA) == 0;
  } else {
    isTarget = EqualRect(&lpmi->rcMonitor, &g_targetRect) != 0;
  }
  if (isTarget)
    lpmi->dwFlags |= MONITORINFOF_PRIMARY;
  else
    lpmi->dwFlags &= ~MONITORINFOF_PRIMARY;
}

void LogMonitorInfo(const char* api, HMONITOR mon, BOOL ok, const MONITORINFOEXW* mi) {
  std::ostringstream oss;
  oss << ",\"hmonitor\":\"" << HexPtr(mon) << "\",\"ok\":" << (ok ? "true" : "false");
  if (ok && mi) {
    oss << ",\"device\":\"" << shimlog::EscW(mi->szDevice) << "\""
        << ",\"primary\":" << ((mi->dwFlags & MONITORINFOF_PRIMARY) ? "true" : "false")
        << ",\"bounds\":{\"l\":" << mi->rcMonitor.left << ",\"t\":" << mi->rcMonitor.top
        << ",\"r\":" << mi->rcMonitor.right << ",\"b\":" << mi->rcMonitor.bottom
        << ",\"w\":" << (mi->rcMonitor.right - mi->rcMonitor.left)
        << ",\"h\":" << (mi->rcMonitor.bottom - mi->rcMonitor.top) << "}";
  }
  shimlog::Info(api, oss.str());
}

BOOL WINAPI Hook_GetMonitorInfoW(HMONITOR mon, LPMONITORINFO lpmi) {
  BOOL ok = Real_GetMonitorInfoW(mon, lpmi);
  if (ok) SpoofMonitorInfoFlags(lpmi);
  if (lpmi && lpmi->cbSize >= sizeof(MONITORINFOEXW))
    LogMonitorInfo("GetMonitorInfoW", mon, ok, reinterpret_cast<MONITORINFOEXW*>(lpmi));
  else {
    std::ostringstream oss;
    oss << ",\"hmonitor\":\"" << HexPtr(mon) << "\",\"ok\":" << (ok ? "true" : "false")
        << ",\"cbSize\":" << (lpmi ? lpmi->cbSize : 0);
    if (ok && lpmi) {
      oss << ",\"primary\":" << ((lpmi->dwFlags & MONITORINFOF_PRIMARY) ? "true" : "false")
          << ",\"bounds\":{\"l\":" << lpmi->rcMonitor.left << ",\"t\":" << lpmi->rcMonitor.top
          << ",\"r\":" << lpmi->rcMonitor.right << ",\"b\":" << lpmi->rcMonitor.bottom
          << ",\"w\":" << (lpmi->rcMonitor.right - lpmi->rcMonitor.left)
          << ",\"h\":" << (lpmi->rcMonitor.bottom - lpmi->rcMonitor.top) << "}";
    }
    shimlog::Info("GetMonitorInfoW", oss.str());
  }
  return ok;
}

BOOL WINAPI Hook_GetMonitorInfoA(HMONITOR mon, LPMONITORINFO lpmi) {
  BOOL ok = Real_GetMonitorInfoA(mon, lpmi);
  if (ok) SpoofMonitorInfoFlagsA(lpmi);
  std::ostringstream oss;
  oss << ",\"hmonitor\":\"" << HexPtr(mon) << "\",\"ok\":" << (ok ? "true" : "false");
  if (ok && lpmi) {
    oss << ",\"primary\":" << ((lpmi->dwFlags & MONITORINFOF_PRIMARY) ? "true" : "false")
        << ",\"bounds\":{\"w\":" << (lpmi->rcMonitor.right - lpmi->rcMonitor.left)
        << ",\"h\":" << (lpmi->rcMonitor.bottom - lpmi->rcMonitor.top) << "}";
  }
  shimlog::Info("GetMonitorInfoA", oss.str());
  return ok;
}

struct EnumCtx {
  MONITORENUMPROC userProc;
  LPARAM userData;
  int count;
};

BOOL CALLBACK EnumThunk(HMONITOR mon, HDC hdc, LPRECT rc, LPARAM data) {
  auto* ctx = reinterpret_cast<EnumCtx*>(data);
  ctx->count++;
  MONITORINFOEXW mi{};
  mi.cbSize = sizeof(mi);
  // Log what the game will see (spoofed flags) via the hooked path when available.
  BOOL ok = Real_GetMonitorInfoW(mon, reinterpret_cast<MONITORINFO*>(&mi));
  if (ok) SpoofMonitorInfoFlags(reinterpret_cast<MONITORINFO*>(&mi));
  LogMonitorInfo("EnumDisplayMonitors.callback", mon, ok, ok ? &mi : nullptr);
  return ctx->userProc ? ctx->userProc(mon, hdc, rc, ctx->userData) : TRUE;
}

BOOL WINAPI Hook_EnumDisplayMonitors(HDC hdc, LPCRECT clip, MONITORENUMPROC proc, LPARAM data) {
  shimlog::Info("EnumDisplayMonitors", ",\"phase\":\"enter\"");
  EnumCtx ctx{proc, data, 0};
  BOOL ok = Real_EnumDisplayMonitors(hdc, clip, EnumThunk, reinterpret_cast<LPARAM>(&ctx));
  std::ostringstream oss;
  oss << ",\"phase\":\"leave\",\"ok\":" << (ok ? "true" : "false") << ",\"count\":" << ctx.count;
  shimlog::Info("EnumDisplayMonitors", oss.str());
  return ok;
}

BOOL WINAPI Hook_EnumDisplayDevicesW(LPCWSTR device, DWORD devNum, PDISPLAY_DEVICEW dd, DWORD flags) {
  BOOL ok = Real_EnumDisplayDevicesW(device, devNum, dd, flags);
  if (ok && dd && g_fakePrimary && (device == nullptr || device[0] == L'\0')) {
    if (DeviceEq(dd->DeviceName, g_targetDevice))
      dd->StateFlags |= DISPLAY_DEVICE_PRIMARY_DEVICE;
    else
      dd->StateFlags &= ~DISPLAY_DEVICE_PRIMARY_DEVICE;
  }
  std::ostringstream oss;
  oss << ",\"device\":\"" << shimlog::EscW(device) << "\",\"devNum\":" << devNum
      << ",\"ok\":" << (ok ? "true" : "false");
  if (ok && dd) {
    oss << ",\"name\":\"" << shimlog::EscW(dd->DeviceName) << "\""
        << ",\"string\":\"" << shimlog::EscW(dd->DeviceString) << "\""
        << ",\"stateFlags\":" << dd->StateFlags;
  }
  shimlog::Info("EnumDisplayDevicesW", oss.str());
  return ok;
}

BOOL WINAPI Hook_EnumDisplaySettingsExW(LPCWSTR device, DWORD mode, DEVMODEW* dm, DWORD flags) {
  LPCWSTR use = device;
  bool redirected = false;
  if (g_fakePrimary && (device == nullptr || device[0] == L'\0')) {
    use = g_targetDevice;
    redirected = true;
  }
  BOOL ok = Real_EnumDisplaySettingsExW(use, mode, dm, flags);
  bool interesting = redirected || (mode == ENUM_CURRENT_SETTINGS) || (mode <= 5) ||
                     (ok && dm && dm->dmPelsWidth >= 1920 && mode < 30);
  if (interesting) {
    std::ostringstream oss;
    oss << ",\"device\":\"" << shimlog::EscW(device) << "\",\"mode\":" << static_cast<int>(mode)
        << ",\"ok\":" << (ok ? "true" : "false");
    if (redirected) oss << ",\"redirected\":\"" << shimlog::EscW(use) << "\"";
    if (ok && dm) {
      oss << ",\"width\":" << dm->dmPelsWidth << ",\"height\":" << dm->dmPelsHeight
          << ",\"hz\":" << dm->dmDisplayFrequency << ",\"bpp\":" << dm->dmBitsPerPel
          << ",\"posX\":" << dm->dmPosition.x << ",\"posY\":" << dm->dmPosition.y;
    }
    shimlog::Info("EnumDisplaySettingsExW", oss.str());
  }
  return ok;
}

int WINAPI Hook_GetSystemMetrics(int index) {
  int v = Real_GetSystemMetrics(index);
  if (g_fakePrimary) {
    if (index == SM_CXSCREEN) v = g_targetCx;
    else if (index == SM_CYSCREEN) v = g_targetCy;
  }
  if (index == SM_CXSCREEN || index == SM_CYSCREEN || index == SM_CXVIRTUALSCREEN ||
      index == SM_CYVIRTUALSCREEN || index == SM_XVIRTUALSCREEN || index == SM_YVIRTUALSCREEN ||
      index == SM_CMONITORS) {
    std::ostringstream oss;
    oss << ",\"index\":" << index << ",\"value\":" << v;
    if (g_fakePrimary && (index == SM_CXSCREEN || index == SM_CYSCREEN))
      oss << ",\"spoofed\":true";
    shimlog::Info("GetSystemMetrics", oss.str());
  }
  return v;
}

int WINAPI Hook_GetSystemMetricsForDpi(int index, UINT dpi) {
  int v = Real_GetSystemMetricsForDpi(index, dpi);
  if (g_fakePrimary) {
    if (index == SM_CXSCREEN) v = g_targetCx;
    else if (index == SM_CYSCREEN) v = g_targetCy;
  }
  if (index == SM_CXSCREEN || index == SM_CYSCREEN) {
    std::ostringstream oss;
    oss << ",\"index\":" << index << ",\"dpi\":" << dpi << ",\"value\":" << v;
    if (g_fakePrimary) oss << ",\"spoofed\":true";
    shimlog::Info("GetSystemMetricsForDpi", oss.str());
  }
  return v;
}

bool IsGameWindowClass(LPCWSTR cls) {
  if (!cls || IS_INTRESOURCE(cls)) return false;
  return _wcsicmp(cls, L"UnrealWindow") == 0;
}

bool TargetMonitorRect(RECT* out) {
  if (!out) return false;
  if (g_targetMon && Real_GetMonitorInfoW) {
    MONITORINFOEXW mi{};
    mi.cbSize = sizeof(mi);
    if (Real_GetMonitorInfoW(g_targetMon, reinterpret_cast<MONITORINFO*>(&mi))) {
      *out = mi.rcMonitor;
      return true;
    }
  }
  if (g_targetRect.right > g_targetRect.left) {
    *out = g_targetRect;
    return true;
  }
  return false;
}

// Fill the target monitor (game-style "maximized" / borderless on that display).
bool PlaceGameWindow(HWND hwnd, const char* reason) {
  if (!hwnd || !g_fakePrimary) return false;
  RECT r{};
  if (!TargetMonitorRect(&r)) return false;
  const int w = r.right - r.left;
  const int h = r.bottom - r.top;
  if (w <= 0 || h <= 0) return false;
  // TOPMOST briefly avoided - just TOP so it lands on the right display.
  BOOL ok = SetWindowPos(hwnd, HWND_TOP, r.left, r.top, w, h,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW);
  std::ostringstream oss;
  oss << ",\"hwnd\":\"" << HexPtr(hwnd) << "\",\"ok\":" << (ok ? "true" : "false")
      << ",\"reason\":\"" << reason << "\""
      << ",\"rect\":{\"l\":" << r.left << ",\"t\":" << r.top << ",\"w\":" << w << ",\"h\":" << h << "}";
  shimlog::Info("PlaceGameWindow", oss.str());
  return ok == TRUE;
}

HWND WINAPI Hook_CreateWindowExW(DWORD ex, LPCWSTR cls, LPCWSTR name, DWORD style, int x, int y,
                                 int w, int h, HWND parent, HMENU menu, HINSTANCE inst, LPVOID param) {
  bool relocate = g_fakePrimary && IsGameWindowClass(cls) && parent == nullptr;
  int ox = x, oy = y, ow = w, oh = h;
  if (relocate) {
    RECT r{};
    if (TargetMonitorRect(&r)) {
      x = r.left;
      y = r.top;
      w = r.right - r.left;
      h = r.bottom - r.top;
    } else {
      relocate = false;
    }
  }

  HWND hwnd = Real_CreateWindowExW(ex, cls, name, style, x, y, w, h, parent, menu, inst, param);
  if (relocate && hwnd) {
    g_gameHwnd = hwnd;
    PlaceGameWindow(hwnd, "CreateWindowExW");
  }

  int n = g_windowLogs.fetch_add(1);
  if (n < 12) {
    std::ostringstream oss;
    oss << ",\"n\":" << n << ",\"hwnd\":\"" << HexPtr(hwnd) << "\",\"class\":\"" << shimlog::EscW(cls)
        << "\",\"title\":\"" << shimlog::EscW(name) << "\",\"x\":" << x << ",\"y\":" << y
        << ",\"w\":" << w << ",\"h\":" << h;
    if (relocate) {
      oss << ",\"relocated\":true,\"orig\":{\"x\":" << ox << ",\"y\":" << oy << ",\"w\":" << ow
          << ",\"h\":" << oh << "}";
    }
    shimlog::Info("CreateWindowExW", oss.str());
  }
  return hwnd;
}

BOOL WINAPI Hook_ShowWindow(HWND hwnd, int cmd) {
  BOOL ok = Real_ShowWindow(hwnd, cmd);
  if (g_fakePrimary && hwnd && hwnd == g_gameHwnd &&
      (cmd == SW_SHOW || cmd == SW_SHOWNORMAL || cmd == SW_SHOWDEFAULT || cmd == SW_RESTORE ||
       cmd == SW_MAXIMIZE || cmd == SW_SHOWMAXIMIZED)) {
    PlaceGameWindow(hwnd, "ShowWindow");
  }
  int n = g_windowLogs.load();
  if (n < 16) {
    std::ostringstream oss;
    oss << ",\"hwnd\":\"" << HexPtr(hwnd) << "\",\"cmd\":" << cmd << ",\"ok\":" << (ok ? "true" : "false");
    shimlog::Info("ShowWindow", oss.str());
  }
  return ok;
}

bool HookApi(const wchar_t* module, const char* name, void* detour, void** original) {
  HMODULE mod = GetModuleHandleW(module);
  if (!mod) mod = LoadLibraryW(module);
  if (!mod) return false;
  void* target = reinterpret_cast<void*>(GetProcAddress(mod, name));
  if (!target) return false;
  if (MH_CreateHook(target, detour, original) != MH_OK) return false;
  if (MH_EnableHook(target) != MH_OK) return false;
  return true;
}

}  // namespace

namespace hooks {

bool Install() {
  LoadConfig();

  if (MH_Initialize() != MH_OK) {
    shimlog::Info("hooks.error", ",\"msg\":\"MH_Initialize failed\"");
    return false;
  }

  HookApi(L"user32.dll", "EnumDisplayMonitors", reinterpret_cast<void*>(&Hook_EnumDisplayMonitors),
          reinterpret_cast<void**>(&Real_EnumDisplayMonitors));
  HookApi(L"user32.dll", "GetMonitorInfoW", reinterpret_cast<void*>(&Hook_GetMonitorInfoW),
          reinterpret_cast<void**>(&Real_GetMonitorInfoW));
  HookApi(L"user32.dll", "GetMonitorInfoA", reinterpret_cast<void*>(&Hook_GetMonitorInfoA),
          reinterpret_cast<void**>(&Real_GetMonitorInfoA));
  HookApi(L"user32.dll", "EnumDisplayDevicesW", reinterpret_cast<void*>(&Hook_EnumDisplayDevicesW),
          reinterpret_cast<void**>(&Real_EnumDisplayDevicesW));
  HookApi(L"user32.dll", "EnumDisplaySettingsExW", reinterpret_cast<void*>(&Hook_EnumDisplaySettingsExW),
          reinterpret_cast<void**>(&Real_EnumDisplaySettingsExW));
  HookApi(L"user32.dll", "GetSystemMetrics", reinterpret_cast<void*>(&Hook_GetSystemMetrics),
          reinterpret_cast<void**>(&Real_GetSystemMetrics));
  HookApi(L"user32.dll", "GetSystemMetricsForDpi", reinterpret_cast<void*>(&Hook_GetSystemMetricsForDpi),
          reinterpret_cast<void**>(&Real_GetSystemMetricsForDpi));
  HookApi(L"user32.dll", "CreateWindowExW", reinterpret_cast<void*>(&Hook_CreateWindowExW),
          reinterpret_cast<void**>(&Real_CreateWindowExW));
  HookApi(L"user32.dll", "ShowWindow", reinterpret_cast<void*>(&Hook_ShowWindow),
          reinterpret_cast<void**>(&Real_ShowWindow));

  if (g_fakePrimary) ResolveTarget();

  std::ostringstream oss;
  oss << ",\"win32Only\":true,\"fakePrimary\":" << (g_fakePrimary ? "true" : "false")
      << ",\"target\":\"" << shimlog::EscW(g_targetDevice) << "\""
      << ",\"cx\":" << g_targetCx << ",\"cy\":" << g_targetCy;
  shimlog::Info("hooks.installed", oss.str());

  if (g_fakePrimary) {
    // Hits our own hooks - fails loud if spoof didn't stick.
    int cx = GetSystemMetrics(SM_CXSCREEN);
    int cy = GetSystemMetrics(SM_CYSCREEN);
    if (cx != g_targetCx || cy != g_targetCy) {
      std::ostringstream e;
      e << ",\"msg\":\"metrics self-check failed\",\"gotCx\":" << cx << ",\"gotCy\":" << cy
        << ",\"wantCx\":" << g_targetCx << ",\"wantCy\":" << g_targetCy;
      shimlog::Info("shim.fatal", e.str());
      return false;
    }
  }
  return true;
}

void Uninstall() {
  MH_DisableHook(MH_ALL_HOOKS);
  MH_Uninitialize();
}

}  // namespace hooks
