#pragma once
#include <string>

namespace shimlog {

bool Init();
void Shutdown();
void WriteRaw(const std::string& jsonObjectLine);
void Info(const char* api, const std::string& fieldsJsonObject);
std::string Esc(const std::string& s);
std::string EscW(const wchar_t* s);

}  // namespace shimlog
