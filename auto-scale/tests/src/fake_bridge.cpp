#include "common.h"

#include <sstream>

namespace {
std::filesystem::path LogPath() {
    return astest::GetEnvW(L"AUTO_SCALE_TEST_LOG");
}

std::chrono::milliseconds DelayFromMode(const std::wstring& mode) {
    constexpr std::wstring_view prefix = L"delay=";
    if (mode.rfind(std::wstring(prefix), 0) == 0) {
        return std::chrono::milliseconds(static_cast<int>(astest::ParseUnsigned(mode.substr(prefix.size()), 1200)));
    }
    return std::chrono::milliseconds(150);
}

std::string HwndText(HWND hwnd) {
    std::ostringstream out;
    out << reinterpret_cast<std::uintptr_t>(hwnd);
    return out.str();
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        const auto readyFile = std::filesystem::path(astest::ArgValue(argc, argv, L"--ready-file"));
        const auto stopEventName = astest::ArgValue(argc, argv, L"--stop-event");
        const auto sourceArg = astest::ArgValue(argc, argv, L"--source-hwnd");
        const auto captureDirArg = astest::ArgValue(argc, argv, L"--capture-dir");
        const bool freezeSourceArg = astest::HasArg(argc, argv, L"--freeze-source");
        const bool nativeResolutionArg = astest::HasArg(argc, argv, L"--native-resolution");
        const DWORD parentPid = static_cast<DWORD>(astest::ParseUnsigned(astest::ArgValue(argc, argv, L"--parent-pid"), 0));
        const auto mode = astest::GetEnvW(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
        const auto hip = astest::GetEnvW(L"HIP_VISIBLE_DEVICES");

        std::ostringstream start;
        start << "bridge_start source=" << astest::Narrow(sourceArg)
              << " ready=" << astest::Narrow(readyFile.wstring())
              << " stop=" << astest::Narrow(stopEventName)
              << " parent=" << parentPid
              << " capture_dir=" << astest::Narrow(captureDirArg)
              << " freeze_source=" << (freezeSourceArg ? 1 : 0)
              << " native_resolution=" << (nativeResolutionArg ? 1 : 0)
              << " hip=" << astest::Narrow(hip)
              << " mode=" << astest::Narrow(mode);
        astest::AppendLog(LogPath(), start.str());

        if (readyFile.empty() || stopEventName.empty() || parentPid == 0) {
            astest::AppendLog(LogPath(), "bridge_missing_required_args=1");
            return 2;
        }

        HANDLE stopEvent = OpenEventW(SYNCHRONIZE, FALSE, stopEventName.c_str());
        if (!stopEvent) {
            astest::AppendLog(LogPath(), "bridge_open_stop_failed=1");
            return 3;
        }
        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
        if (!parent) {
            CloseHandle(stopEvent);
            astest::AppendLog(LogPath(), "bridge_open_parent_failed=1");
            return 4;
        }

        HWND hwnd = astest::CreateTestWindow(L"AutoScaleFakeBridgeWindow", L"AutoScale Fake Bridge", 300, 180);
        astest::AppendLog(LogPath(), "bridge_hwnd=" + HwndText(hwnd));

        if (mode == L"exit-before-ready") {
            CloseHandle(parent);
            CloseHandle(stopEvent);
            astest::AppendLog(LogPath(), "bridge_exit_before_ready=1");
            return 5;
        }

        const auto deadline = std::chrono::steady_clock::now() + DelayFromMode(mode);
        while (std::chrono::steady_clock::now() < deadline) {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            HANDLE waits[2] = { stopEvent, parent };
            DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 10);
            if (wait == WAIT_OBJECT_0) {
                astest::AppendLog(LogPath(), "bridge_stop_event_before_ready=1");
                CloseHandle(parent);
                CloseHandle(stopEvent);
                return 0;
            }
            if (wait == WAIT_OBJECT_0 + 1) {
                astest::AppendLog(LogPath(), "bridge_parent_exit_before_ready=1");
                CloseHandle(parent);
                CloseHandle(stopEvent);
                return 0;
            }
        }

        if (mode.rfind(L"exit-after-ready", 0) == 0) {
            astest::WriteTextAtomic(readyFile, std::to_wstring(reinterpret_cast<std::uintptr_t>(hwnd)) + L"\n");
            astest::AppendLog(LogPath(), "bridge_ready_decimal=" + HwndText(hwnd));
            const auto equals = mode.find(L'=');
            const auto afterReadyDelay = equals == std::wstring::npos
                ? std::chrono::milliseconds(800)
                : std::chrono::milliseconds(static_cast<int>(astest::ParseUnsigned(mode.substr(equals + 1), 800)));
            const auto exitAt = std::chrono::steady_clock::now() + afterReadyDelay;
            while (std::chrono::steady_clock::now() < exitAt) {
                MSG msg{};
                while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                HANDLE waits[2] = { stopEvent, parent };
                DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 25);
                if (wait == WAIT_OBJECT_0) {
                    astest::AppendLog(LogPath(), "bridge_stop_event=1");
                    CloseHandle(parent);
                    CloseHandle(stopEvent);
                    return 0;
                }
                if (wait == WAIT_OBJECT_0 + 1) {
                    astest::AppendLog(LogPath(), "bridge_parent_exit=1");
                    CloseHandle(parent);
                    CloseHandle(stopEvent);
                    return 0;
                }
            }
            astest::AppendLog(LogPath(), "bridge_exit_after_ready=1");
            CloseHandle(parent);
            CloseHandle(stopEvent);
            return 6;
        }

        if (mode == L"invalid-ready") {
            astest::WriteTextAtomic(readyFile, L"not-a-window\n");
            astest::AppendLog(LogPath(), "bridge_ready_invalid=1");
        } else if (mode != L"no-ready") {
            astest::WriteTextAtomic(readyFile, std::to_wstring(reinterpret_cast<std::uintptr_t>(hwnd)) + L"\n");
            astest::AppendLog(LogPath(), "bridge_ready_decimal=" + HwndText(hwnd));
        } else {
            astest::AppendLog(LogPath(), "bridge_no_ready=1");
        }

        bool running = true;
        while (running) {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            HANDLE waits[2] = { stopEvent, parent };
            DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 25);
            if (wait == WAIT_OBJECT_0) {
                astest::AppendLog(LogPath(), "bridge_stop_event=1");
                running = false;
            } else if (wait == WAIT_OBJECT_0 + 1) {
                astest::AppendLog(LogPath(), "bridge_parent_exit=1");
                running = false;
            }
        }
        CloseHandle(parent);
        CloseHandle(stopEvent);
        return 0;
    } catch (const std::exception& ex) {
        astest::AppendLog(LogPath(), std::string("bridge_exception=") + ex.what());
        return 1;
    }
}
