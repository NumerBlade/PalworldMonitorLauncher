#include "log.h"

#include <Windows.h>

#include <chrono>
#include <cstdio>
#include <fcntl.h>
#include <io.h>
#include <mutex>
#include <sstream>

namespace shimlog {
namespace {

std::mutex g_mu;
FILE* g_fp = nullptr;
wchar_t g_path[MAX_PATH] = {};

std::string NowIso() {
  SYSTEMTIME st{};
  GetSystemTime(&st);
  char buf[64];
  snprintf(buf, sizeof(buf), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
           st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
           st.wMilliseconds);
  return buf;
}

void EnsureDir() {
  wchar_t temp[MAX_PATH];
  GetTempPathW(MAX_PATH, temp);
  wchar_t dir[MAX_PATH];
  swprintf(dir, MAX_PATH, L"%sPalworldMonitor", temp);
  CreateDirectoryW(dir, nullptr);
}

}  // namespace

bool Init() {
  std::lock_guard<std::mutex> lock(g_mu);
  if (g_fp) return true;
  EnsureDir();
  wchar_t temp[MAX_PATH];
  GetTempPathW(MAX_PATH, temp);
  SYSTEMTIME st{};
  GetLocalTime(&st);
  swprintf(g_path, MAX_PATH,
           L"%sPalworldMonitor\\shim-%lu-%04u%02u%02u-%02u%02u%02u.jsonl",
           temp, GetCurrentProcessId(), st.wYear, st.wMonth, st.wDay, st.wHour,
           st.wMinute, st.wSecond);

  HANDLE h = CreateFileW(g_path, FILE_APPEND_DATA | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                         nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (h == INVALID_HANDLE_VALUE) return false;
  int fd = _open_osfhandle(reinterpret_cast<intptr_t>(h), 0);
  if (fd == -1) {
    CloseHandle(h);
    return false;
  }
  g_fp = _fdopen(fd, "ab");
  if (!g_fp) {
    _close(fd);
    return false;
  }

  std::ostringstream oss;
  oss << "{\"ts\":\"" << NowIso() << "\",\"api\":\"shim.init\",\"pid\":"
      << GetCurrentProcessId() << ",\"logPath\":\"" << EscW(g_path) << "\"}\n";
  fputs(oss.str().c_str(), g_fp);
  fflush(g_fp);
  return true;
}

void Shutdown() {
  std::lock_guard<std::mutex> lock(g_mu);
  if (!g_fp) return;
  fclose(g_fp);
  g_fp = nullptr;
}

void WriteRaw(const std::string& jsonObjectLine) {
  std::lock_guard<std::mutex> lock(g_mu);
  if (!g_fp) return;
  fputs(jsonObjectLine.c_str(), g_fp);
  if (jsonObjectLine.empty() || jsonObjectLine.back() != '\n') fputc('\n', g_fp);
  fflush(g_fp);
}

void Info(const char* api, const std::string& fieldsJsonObject) {
  std::ostringstream oss;
  oss << "{\"ts\":\"" << NowIso() << "\",\"api\":\"" << api << "\"";
  if (!fieldsJsonObject.empty()) {
    // fieldsJsonObject is expected like: ,"k":"v" or full object body without braces
    if (fieldsJsonObject.front() == ',')
      oss << fieldsJsonObject;
    else
      oss << "," << fieldsJsonObject;
  }
  oss << "}\n";
  WriteRaw(oss.str());
}

std::string Esc(const std::string& s) {
  std::string o;
  o.reserve(s.size() + 8);
  for (unsigned char c : s) {
    switch (c) {
      case '\\': o += "\\\\"; break;
      case '"': o += "\\\""; break;
      case '\n': o += "\\n"; break;
      case '\r': o += "\\r"; break;
      case '\t': o += "\\t"; break;
      default:
        if (c < 0x20) {
          char buf[8];
          snprintf(buf, sizeof(buf), "\\u%04x", c);
          o += buf;
        } else {
          o.push_back(static_cast<char>(c));
        }
    }
  }
  return o;
}

std::string EscW(const wchar_t* s) {
  if (!s) return {};
  // CreateWindowExW / similar often pass an ATOM as LPCWSTR (HIWORD==0).
  // WideCharToMultiByte then reads that small address → AV (seen at 0xC03C).
  if (IS_INTRESOURCE(s)) {
    char buf[32];
    snprintf(buf, sizeof(buf), "atom:0x%04X", static_cast<unsigned>(reinterpret_cast<uintptr_t>(s) & 0xFFFF));
    return buf;
  }
  int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
  if (n <= 1) return {};
  std::string utf8(static_cast<size_t>(n - 1), '\0');
  WideCharToMultiByte(CP_UTF8, 0, s, -1, utf8.data(), n, nullptr, nullptr);
  return Esc(utf8);
}

}  // namespace shimlog
