#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace astest {

std::wstring ArgValue(int argc, wchar_t** argv, const wchar_t* name, const std::wstring& fallback = L"");
bool HasArg(int argc, wchar_t** argv, const wchar_t* name);
std::wstring GetEnvW(const wchar_t* name);
void AppendLog(const std::filesystem::path& path, const std::string& line);
std::string Narrow(const std::wstring& value);
std::wstring Widen(const std::string& value);
uint64_t ParseUnsigned(const std::wstring& value, uint64_t fallback = 0);
HWND CreateTestWindow(const wchar_t* className, const wchar_t* title, int width = 320, int height = 180);
bool PumpUntil(std::chrono::milliseconds timeout, const std::function<bool()>& done);
void WriteTextAtomic(const std::filesystem::path& path, const std::wstring& text);

} // namespace astest
