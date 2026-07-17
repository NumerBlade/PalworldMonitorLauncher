#include "hooks.h"
#include "log.h"

#include <Windows.h>

#include <string>

namespace {

// Install hooks + named-pipe ready signal off the loader lock.
// MinHook / CRT / CreateNamedPipe are unsafe inside DllMain.
DWORD WINAPI InitThread(LPVOID) {
  if (!shimlog::Init()) return 1;
  // Self-check: atom-as-LPCWSTR must not AV (regression for 0xC03C crash).
  {
    auto atom = shimlog::EscW(reinterpret_cast<const wchar_t*>(static_cast<uintptr_t>(0xC03C)));
    if (atom.find("atom:0xC03C") == std::string::npos) {
      shimlog::Info("shim.fatal", ",\"msg\":\"EscW atom self-check failed\"");
      return 2;
    }
  }
  if (!hooks::Install()) {
    shimlog::Info("shim.fatal", ",\"msg\":\"hook install failed\"");
  }

  const wchar_t* name = L"\\\\.\\pipe\\PalworldMonitorShim";
  for (int i = 0; i < 32; ++i) {
    HANDLE pipe = CreateNamedPipeW(
        name, PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 256, 256, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
      Sleep(100);
      continue;
    }
    BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
    if (connected) {
      const char msg[] = "ready\n";
      DWORD written = 0;
      WriteFile(pipe, msg, sizeof(msg) - 1, &written, nullptr);
      FlushFileBuffers(pipe);
    }
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
  }
  return 0;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(module);
    // ponytail: CreateThread under loader lock is imperfect but the usual escape;
    // upgrade path = exported Init() called by launcher after LoadLibrary returns.
    HANDLE t = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
    if (t) CloseHandle(t);
  } else if (reason == DLL_PROCESS_DETACH) {
    hooks::Uninstall();
    shimlog::Shutdown();
  }
  return TRUE;
}
