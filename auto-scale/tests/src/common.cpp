#include "common.h"

#include <codecvt>
#include <functional>
#include <locale>

namespace astest {

std::wstring ArgValue(int argc, wchar_t** argv, const wchar_t* name, const std::wstring& fallback) {
    for (int i = 1; i + 1 < argc; ++i) {
        if (_wcsicmp(argv[i], name) == 0) {
            return argv[i + 1];
        }
    }
    return fallback;
}

bool HasArg(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 1; i < argc; ++i) {
        if (_wcsicmp(argv[i], name) == 0) {
            return true;
        }
    }
    return false;
}

std::wstring GetEnvW(const wchar_t* name) {
    DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
    if (needed == 0) {
        return L"";
    }
    std::wstring value(needed, L'\0');
    DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
    if (written == 0) {
        return L"";
    }
    value.resize(written);
    return value;
}

std::string Narrow(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) {
        return {};
    }
    std::string out(static_cast<size_t>(bytes), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), out.data(), bytes, nullptr, nullptr);
    return out;
}

std::wstring Widen(const std::string& value) {
    if (value.empty()) {
        return {};
    }
    int chars = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
    if (chars <= 0) {
        return {};
    }
    std::wstring out(static_cast<size_t>(chars), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), out.data(), chars);
    return out;
}

void AppendLog(const std::filesystem::path& path, const std::string& line) {
    if (!path.empty()) {
        std::filesystem::create_directories(path.parent_path());
        std::ofstream log(path, std::ios::app | std::ios::binary);
        log << line << "\n";
    }
}

uint64_t ParseUnsigned(const std::wstring& value, uint64_t fallback) {
    if (value.empty()) {
        return fallback;
    }
    try {
        size_t consumed = 0;
        uint64_t parsed = std::stoull(value, &consumed, 0);
        return consumed == value.size() ? parsed : fallback;
    } catch (...) {
        return fallback;
    }
}

namespace {
LRESULT CALLBACK TestWndProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_CLOSE) {
        DestroyWindow(hwnd);
        return 0;
    }
    if (message == WM_DESTROY) {
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}
}

HWND CreateTestWindow(const wchar_t* className, const wchar_t* title, int width, int height) {
    HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = TestWndProc;
    wc.hInstance = instance;
    wc.lpszClassName = className;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&wc);

    HWND hwnd = CreateWindowExW(0, className, title, WS_OVERLAPPEDWINDOW,
                                CW_USEDEFAULT, CW_USEDEFAULT, width, height,
                                nullptr, nullptr, instance, nullptr);
    if (!hwnd) {
        throw std::runtime_error("CreateWindowExW failed");
    }
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(hwnd);
    return hwnd;
}

bool PumpUntil(std::chrono::milliseconds timeout, const std::function<bool()>& done) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (done()) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    return done();
}

void WriteTextAtomic(const std::filesystem::path& path, const std::wstring& text) {
    std::filesystem::create_directories(path.parent_path());
    auto tmp = path;
    tmp += L".tmp";
    {
        std::wofstream out(tmp, std::ios::binary | std::ios::trunc);
        out << text;
    }
    std::error_code ec;
    std::filesystem::rename(tmp, path, ec);
    if (ec) {
        std::filesystem::remove(path, ec);
        std::filesystem::rename(tmp, path);
    }
}

} // namespace astest
