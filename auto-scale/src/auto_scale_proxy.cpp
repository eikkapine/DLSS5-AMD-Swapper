#include <windows.h>
#include <shellapi.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#pragma comment(linker, "/export:GetAdapterNames=Lossless_original.GetAdapterNames")
#pragma comment(linker, "/export:GetDisplayNames=Lossless_original.GetDisplayNames")
#pragma comment(linker, "/export:GetDwmRefreshRate=Lossless_original.GetDwmRefreshRate")
#pragma comment(linker, "/export:GetForegroundWindowEx=Lossless_original.GetForegroundWindowEx")
#pragma comment(linker, "/export:IsWindowsBuildAtLeast=Lossless_original.IsWindowsBuildAtLeast")
#pragma comment(linker, "/export:SetDriverSettings=Lossless_original.SetDriverSettings")
#pragma comment(linker, "/export:SetWindowsSettings=Lossless_original.SetWindowsSettings")

namespace {

using StatusCallback = void(__cdecl*)(int status,
                                      HWND hwnd,
                                      int inputWidth,
                                      int inputHeight,
                                      int outputWidth,
                                      int outputHeight,
                                      std::uint8_t resized,
                                      float factor,
                                      int error);

using InitFn = bool(__cdecl*)(StatusCallback callback);
using ActivateFn = bool(__cdecl*)(HWND hwnd);
using UnInitFn = void(__cdecl*)();
using GetForegroundWindowExFn = HWND(__cdecl*)();
using ApplySettingsFn = void(__fastcall*)(
    int scalingMode, int scalingFitMode, int scalingType, int scalingSubtype,
    float scaleFactor, std::uint8_t resizeBeforeScale, int sharpness, std::uint8_t vrs,
    int frameGenType, int frameGenSize, int frameGenMode, float frameGenMultiplier, float frameGenTarget,
    int frameGenFlowScale, std::uint8_t clipCursor, std::uint8_t adjustCursorSpeed, std::uint8_t hideCursor, std::uint8_t scaleCursor,
    int syncMode, int maxFrameLatency, std::uint8_t gsyncSupport, std::uint8_t hdrSupport,
    int captureApi, int queueTarget, std::uint8_t drawFps, int gpuId, int displayId,
    int cropLeft, int cropTop, int cropRight, int cropBottom, std::uint8_t multiDisplayMode,
    int setupPhase);

struct SettingsSnapshot {
    int scalingMode = 0;
    int scalingFitMode = 0;
    int scalingType = 0;
    int scalingSubtype = 0;
    float scaleFactor = 1.0f;
    std::uint8_t resizeBeforeScale = 0;
    int sharpness = 0;
    std::uint8_t vrs = 0;
    int frameGenType = 0;
    int frameGenSize = 0;
    int frameGenMode = 0;
    float frameGenMultiplier = 0.0f;
    float frameGenTarget = 0.0f;
    int frameGenFlowScale = 0;
    std::uint8_t clipCursor = 0;
    std::uint8_t adjustCursorSpeed = 0;
    std::uint8_t hideCursor = 0;
    std::uint8_t scaleCursor = 0;
    int syncMode = 0;
    int maxFrameLatency = 0;
    std::uint8_t gsyncSupport = 0;
    std::uint8_t hdrSupport = 0;
    int captureApi = 0;
    int queueTarget = 0;
    std::uint8_t drawFps = 0;
    int gpuId = 0;
    int displayId = 0;
    int cropLeft = 0;
    int cropTop = 0;
    int cropRight = 0;
    int cropBottom = 0;
    std::uint8_t multiDisplayMode = 0;
    int setupPhase = 0;
    bool valid = false;
};

struct Config {
    bool enabled = true;
    std::filesystem::path bridgeExe;
    std::filesystem::path runtimeDirectory;
    std::filesystem::path captureDirectory;
    std::wstring hipVisibleDevices = L"1";
    bool freezeSource = false;
    bool nativeResolution = false;
    // Missing key keeps older installations on their prior behavior. New
    // installs write the current performance preset explicitly.
    float workingScale = 0.0f;
    int neuralMaxHeight = 0;
    int width = 1280;
    int height = 720;
    int startupDelayMs = 2000;
    int warmupFrames = 320;
    int readyTimeoutMs = 180000;
    int defaultScalingTypeIfOff = 1;
    int forceCaptureApi = 1;
};

struct ChildState {
    PROCESS_INFORMATION process{};
    HANDLE stopEvent = nullptr;
    std::wstring stopEventName;
    std::filesystem::path readyFile;
    HWND sourceHwnd = nullptr;
    HWND bridgeHwnd = nullptr;
    std::uint64_t generation = 0;
    bool pending = false;
    bool active = false;
    bool nativeActivateCalled = false;
};

constexpr UINT kBridgeReadyMessage = WM_APP + 0x520;
constexpr UINT kBridgeStoppedMessage = WM_APP + 0x521;
constexpr wchar_t kClassName[] = L"NrAutoScaleControlWindow";

HMODULE g_module = nullptr;
HMODULE g_original = nullptr;
InitFn g_originalInit = nullptr;
ActivateFn g_originalActivate = nullptr;
UnInitFn g_originalUnInit = nullptr;
GetForegroundWindowExFn g_originalGetForegroundWindowEx = nullptr;
ApplySettingsFn g_originalApplySettings = nullptr;
StatusCallback g_userStatusCallback = nullptr;
HWND g_controlWindow = nullptr;
DWORD g_uiThreadId = 0;
std::mutex g_mutex;
std::mutex g_startMutex;
std::thread g_watcherThread;
Config g_config;
SettingsSnapshot g_settings;
ChildState g_child;
std::atomic<std::uint64_t> g_generation{0};

std::wstring LastErrorText(DWORD error = GetLastError()) {
    wchar_t* buffer = nullptr;
    DWORD chars = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                                 nullptr,
                                 error,
                                 MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
                                 reinterpret_cast<LPWSTR>(&buffer),
                                 0,
                                 nullptr);
    std::wstring text = chars && buffer ? std::wstring(buffer, chars) : L"unknown error";
    if (buffer) {
        LocalFree(buffer);
    }
    while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n' || text.back() == L' ')) {
        text.pop_back();
    }
    return text;
}

std::filesystem::path ModulePath() {
    wchar_t path[MAX_PATH]{};
    DWORD length = GetModuleFileNameW(g_module, path, static_cast<DWORD>(std::size(path)));
    return std::filesystem::path(std::wstring(path, length));
}

std::filesystem::path ModuleDirectory() {
    return ModulePath().parent_path();
}

void Log(const std::wstring& message) {
    const auto path = ModuleDirectory() / L"NrAutoScale.log";
    std::wofstream out(path, std::ios::app);
    if (!out) {
        return;
    }
    SYSTEMTIME now{};
    GetLocalTime(&now);
    out << L"[" << now.wYear << L"-" << now.wMonth << L"-" << now.wDay << L" "
        << now.wHour << L":" << now.wMinute << L":" << now.wSecond << L"] "
        << message << L"\n";
}

std::wstring Trim(std::wstring value) {
    const auto first = value.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) {
        return L"";
    }
    const auto last = value.find_last_not_of(L" \t\r\n");
    return value.substr(first, last - first + 1);
}

bool ParseBool(const std::wstring& value, bool fallback) {
    std::wstring lower = value;
    std::transform(lower.begin(), lower.end(), lower.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
    if (lower == L"1" || lower == L"true" || lower == L"yes" || lower == L"on") {
        return true;
    }
    if (lower == L"0" || lower == L"false" || lower == L"no" || lower == L"off") {
        return false;
    }
    return fallback;
}

int ParseInt(const std::wstring& value, int fallback) {
    try {
        return std::stoi(value);
    } catch (...) {
        return fallback;
    }
}

float ParseFloat(const std::wstring& value, float fallback) {
    try {
        return std::stof(value);
    } catch (...) {
        return fallback;
    }
}

bool UsesReducedWorkingResolution(const Config& config) {
    return config.workingScale >= 0.25f && config.workingScale < 0.999f;
}

bool UsesNativeResidualComposite(const Config& config) {
    return config.neuralMaxHeight >= 64;
}

std::filesystem::path ResolvePath(const std::wstring& value, const std::filesystem::path& base) {
    std::filesystem::path path(value);
    if (path.is_relative()) {
        path = base / path;
    }
    std::error_code ec;
    auto absolute = std::filesystem::weakly_canonical(path, ec);
    if (ec) {
        absolute = std::filesystem::absolute(path, ec);
    }
    return ec ? path : absolute;
}

Config LoadConfig() {
    Config config;
    const auto base = ModuleDirectory();
    config.bridgeExe = ResolvePath(L"nr-bridge\\runtime\\DlssNrBridge.exe", base);
    config.runtimeDirectory = ResolvePath(L"nr-bridge\\runtime", base);

    const auto configPath = base / L"NrAutoScale.ini";
    std::wifstream input(configPath);
    if (!input) {
        Log(L"NrAutoScale.ini not found; using defaults");
        return config;
    }

    std::wstring line;
    while (std::getline(input, line)) {
        line = Trim(line);
        if (line.empty() || line[0] == L';' || line[0] == L'#' || line[0] == L'[') {
            continue;
        }
        const auto equals = line.find(L'=');
        if (equals == std::wstring::npos) {
            continue;
        }
        auto key = Trim(line.substr(0, equals));
        auto value = Trim(line.substr(equals + 1));
        std::transform(key.begin(), key.end(), key.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });

        if (key == L"enabled") {
            config.enabled = ParseBool(value, config.enabled);
        } else if (key == L"bridgeexe") {
            config.bridgeExe = ResolvePath(value, base);
        } else if (key == L"runtimedirectory") {
            config.runtimeDirectory = ResolvePath(value, base);
        } else if (key == L"capturedirectory") {
            config.captureDirectory = ResolvePath(value, base);
        } else if (key == L"hipvisibledevices") {
            config.hipVisibleDevices = value;
        } else if (key == L"freezesource") {
            config.freezeSource = ParseBool(value, config.freezeSource);
        } else if (key == L"nativeresolution") {
            config.nativeResolution = ParseBool(value, config.nativeResolution);
        } else if (key == L"workingscale") {
            const float parsed = ParseFloat(value, config.workingScale);
            config.workingScale = parsed <= 0.0f ? 0.0f : std::clamp(parsed, 0.25f, 1.0f);
        } else if (key == L"neuralmaxheight") {
            const int parsed = ParseInt(value, config.neuralMaxHeight);
            config.neuralMaxHeight = parsed <= 0 ? 0 : std::clamp(parsed, 64, 2160);
        } else if (key == L"width") {
            config.width = ParseInt(value, config.width);
        } else if (key == L"height") {
            config.height = ParseInt(value, config.height);
        } else if (key == L"startupdelayms") {
            config.startupDelayMs = ParseInt(value, config.startupDelayMs);
        } else if (key == L"warmupframes") {
            config.warmupFrames = ParseInt(value, config.warmupFrames);
        } else if (key == L"readytimeoutms") {
            config.readyTimeoutMs = ParseInt(value, config.readyTimeoutMs);
        } else if (key == L"defaultscalingtypeifoff") {
            config.defaultScalingTypeIfOff = ParseInt(value, config.defaultScalingTypeIfOff);
        } else if (key == L"forcecaptureapi") {
            config.forceCaptureApi = ParseInt(value, config.forceCaptureApi);
        }
    }
    return config;
}

bool EnsureOriginal() {
    if (g_original) {
        return true;
    }

    const auto originalPath = ModuleDirectory() / L"Lossless_original.dll";
    g_original = LoadLibraryW(originalPath.c_str());
    if (!g_original) {
        Log(L"Failed to load Lossless_original.dll: " + LastErrorText());
        return false;
    }

    g_originalInit = reinterpret_cast<InitFn>(GetProcAddress(g_original, "Init"));
    g_originalActivate = reinterpret_cast<ActivateFn>(GetProcAddress(g_original, "Activate"));
    g_originalUnInit = reinterpret_cast<UnInitFn>(GetProcAddress(g_original, "UnInit"));
    g_originalGetForegroundWindowEx = reinterpret_cast<GetForegroundWindowExFn>(GetProcAddress(g_original, "GetForegroundWindowEx"));
    g_originalApplySettings = reinterpret_cast<ApplySettingsFn>(GetProcAddress(g_original, "ApplySettings"));

    if (!g_originalInit || !g_originalActivate || !g_originalUnInit || !g_originalApplySettings) {
        Log(L"Lossless_original.dll is missing one or more required exports");
        return false;
    }
    return true;
}

void NotifyStatus(int status, HWND hwnd, int error) {
    auto callback = g_userStatusCallback;
    if (callback) {
        callback(status, hwnd, 0, 0, 0, 0, 0, 1.0f, error);
    }
}

void __cdecl StatusProxy(int status,
                         HWND hwnd,
                         int inputWidth,
                         int inputHeight,
                         int outputWidth,
                         int outputHeight,
                         std::uint8_t resized,
                         float factor,
                         int error) {
    StatusCallback callback = nullptr;
    HWND mapped = hwnd;
    {
        std::lock_guard lock(g_mutex);
        if (hwnd && hwnd == g_child.bridgeHwnd && g_child.sourceHwnd) {
            mapped = g_child.sourceHwnd;
        }
        if (g_child.nativeActivateCalled) {
            g_child.active = status == 2 && error == 0;
            if (status == 0 || error != 0) {
                g_child.active = false;
            }
        }
        callback = g_userStatusCallback;
    }

    if (callback) {
        callback(status, mapped, inputWidth, inputHeight, outputWidth, outputHeight, resized, factor, error);
    }
}

std::wstring QuoteArg(const std::wstring& arg) {
    std::wstring quoted = L"\"";
    unsigned backslashes = 0;
    for (wchar_t ch : arg) {
        if (ch == L'\\') {
            ++backslashes;
        } else if (ch == L'"') {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(ch);
            backslashes = 0;
        } else {
            quoted.append(backslashes, L'\\');
            backslashes = 0;
            quoted.push_back(ch);
        }
    }
    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'"');
    return quoted;
}

std::wstring BuildCommandLine(const Config& config,
                              HWND source,
                              const std::filesystem::path& readyFile,
                              const std::wstring& stopEventName) {
    std::wstringstream handle;
    handle << L"0x" << std::hex << reinterpret_cast<std::uintptr_t>(source);

    std::wstringstream cmd;
    cmd << QuoteArg(config.bridgeExe.wstring())
        << L" --source-hwnd " << handle.str()
        << L" --width " << config.width
        << L" --height " << config.height
        << L" --startup-delay-ms " << config.startupDelayMs
        << L" --warmup-frames " << config.warmupFrames
        << L" --ready-file " << QuoteArg(readyFile.wstring())
        << L" --stop-event " << QuoteArg(stopEventName)
        << L" --parent-pid " << GetCurrentProcessId();
    if (UsesNativeResidualComposite(config)) {
        cmd << L" --neural-max-height " << config.neuralMaxHeight;
    } else if (UsesReducedWorkingResolution(config)) {
        cmd << L" --working-scale " << std::fixed << std::setprecision(3) << config.workingScale;
    } else if (config.nativeResolution) {
        cmd << L" --native-resolution";
    }
    if (!config.captureDirectory.empty()) {
        cmd << L" --capture-dir " << QuoteArg(config.captureDirectory.wstring());
    }
    if (config.freezeSource) {
        cmd << L" --freeze-source";
    }
    return cmd.str();
}

std::vector<wchar_t> BuildEnvironmentBlock(const std::wstring& hipVisibleDevices) {
    std::vector<std::wstring> entries;
    LPWCH current = GetEnvironmentStringsW();
    if (current) {
        for (LPWCH p = current; *p != L'\0'; p += wcslen(p) + 1) {
            std::wstring entry(p);
            auto equals = entry.find(L'=');
            std::wstring name = equals == std::wstring::npos ? entry : entry.substr(0, equals);
            std::wstring lower = name;
            std::transform(lower.begin(), lower.end(), lower.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
            if (lower != L"hip_visible_devices") {
                entries.push_back(entry);
            }
        }
        FreeEnvironmentStringsW(current);
    }

    entries.push_back(L"HIP_VISIBLE_DEVICES=" + hipVisibleDevices);
    std::sort(entries.begin(), entries.end(), [](const std::wstring& a, const std::wstring& b) {
        return _wcsicmp(a.c_str(), b.c_str()) < 0;
    });

    std::vector<wchar_t> block;
    for (const auto& entry : entries) {
        block.insert(block.end(), entry.begin(), entry.end());
        block.push_back(L'\0');
    }
    block.push_back(L'\0');
    return block;
}

void CloseProcessInfo(PROCESS_INFORMATION& process) {
    if (process.hProcess) {
        CloseHandle(process.hProcess);
    }
    if (process.hThread) {
        CloseHandle(process.hThread);
    }
    process = {};
}

void StopChildAndJoin(bool waitForExit) {
    PROCESS_INFORMATION process{};
    HANDLE stopEvent = nullptr;
    std::filesystem::path readyFile;

    {
        std::lock_guard lock(g_mutex);
        process = g_child.process;
        stopEvent = g_child.stopEvent;
        readyFile = g_child.readyFile;
        g_child.process = {};
        g_child.stopEvent = nullptr;
        g_child = {};
    }

    if (stopEvent) {
        SetEvent(stopEvent);
    }
    if (waitForExit && process.hProcess) {
        DWORD wait = WaitForSingleObject(process.hProcess, 2000);
        if (wait == WAIT_TIMEOUT) {
            Log(L"Bridge child did not exit after stop event; terminating owned child process");
            TerminateProcess(process.hProcess, 1);
            WaitForSingleObject(process.hProcess, 1000);
        }
    }
    if (stopEvent) {
        CloseHandle(stopEvent);
    }
    CloseProcessInfo(process);
    if (!readyFile.empty()) {
        std::error_code ec;
        std::filesystem::remove(readyFile, ec);
    }

    if (g_watcherThread.joinable()) {
        if (g_watcherThread.get_id() != std::this_thread::get_id()) {
            g_watcherThread.join();
        }
    }
}

bool ReadBridgeHwnd(const std::filesystem::path& readyFile, HWND& hwnd) {
    std::wifstream input(readyFile);
    if (!input) {
        return false;
    }
    std::wstring text;
    input >> text;
    if (text.empty()) {
        return false;
    }
    try {
        int base = 10;
        if (text.rfind(L"0x", 0) == 0 || text.rfind(L"0X", 0) == 0) {
            base = 16;
            text = text.substr(2);
        }
        const auto value = std::stoull(text, nullptr, base);
        hwnd = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(value));
        return hwnd != nullptr;
    } catch (...) {
        return false;
    }
}

bool ValidateBridgeWindow(HWND hwnd, DWORD childPid) {
    if (!hwnd || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) {
        return false;
    }
    DWORD ownerPid = 0;
    GetWindowThreadProcessId(hwnd, &ownerPid);
    return ownerPid == childPid;
}

void WatchReadiness(std::uint64_t generation,
                    PROCESS_INFORMATION process,
                    std::filesystem::path readyFile,
                    HWND sourceHwnd,
                    int timeoutMs) {
    auto closeWatcherHandle = [&]() {
        if (process.hProcess) {
            CloseHandle(process.hProcess);
            process.hProcess = nullptr;
        }
    };

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (std::chrono::steady_clock::now() < deadline) {
        {
            std::lock_guard lock(g_mutex);
            if (generation != g_child.generation) {
                closeWatcherHandle();
                return;
            }
        }

        if (!IsWindow(sourceHwnd)) {
            Log(L"Source window closed before bridge became ready");
            PostMessageW(g_controlWindow, kBridgeStoppedMessage, static_cast<WPARAM>(generation), 1002);
            closeWatcherHandle();
            return;
        }

        DWORD exitCode = STILL_ACTIVE;
        if (GetExitCodeProcess(process.hProcess, &exitCode) && exitCode != STILL_ACTIVE) {
            Log(L"Bridge child exited before publishing ready file");
            PostMessageW(g_controlWindow, kBridgeStoppedMessage, static_cast<WPARAM>(generation), 1003);
            closeWatcherHandle();
            return;
        }

        HWND bridgeHwnd = nullptr;
        if (ReadBridgeHwnd(readyFile, bridgeHwnd) && ValidateBridgeWindow(bridgeHwnd, process.dwProcessId)) {
            {
                std::lock_guard lock(g_mutex);
                if (generation != g_child.generation) {
                    closeWatcherHandle();
                    return;
                }
                g_child.bridgeHwnd = bridgeHwnd;
                g_child.pending = false;
            }
            PostMessageW(g_controlWindow, kBridgeReadyMessage, static_cast<WPARAM>(generation), 0);

            for (;;) {
                {
                    std::lock_guard lock(g_mutex);
                    if (generation != g_child.generation) {
                        closeWatcherHandle();
                        return;
                    }
                }

                DWORD monitorExitCode = STILL_ACTIVE;
                const bool childExited = GetExitCodeProcess(process.hProcess, &monitorExitCode) && monitorExitCode != STILL_ACTIVE;
                const bool sourceClosed = !IsWindow(sourceHwnd);
                if (childExited || sourceClosed) {
                    Log(childExited ? L"Bridge child exited after activation" : L"Source window closed after activation");
                    PostMessageW(g_controlWindow, kBridgeStoppedMessage, static_cast<WPARAM>(generation), sourceClosed ? 1008 : 1007);
                    closeWatcherHandle();
                    return;
                }

                std::this_thread::sleep_for(std::chrono::milliseconds(250));
            }
            return;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    Log(L"Timed out waiting for bridge ready file/window");
    PostMessageW(g_controlWindow, kBridgeStoppedMessage, static_cast<WPARAM>(generation), 1004);
    closeWatcherHandle();
}

void ApplyBridgeSettings(SettingsSnapshot settings) {
    if (!settings.valid || !g_originalApplySettings) {
        return;
    }

    const bool nativeResidualComposite = UsesNativeResidualComposite(g_config);
    const bool reducedWorkingResolution = UsesReducedWorkingResolution(g_config) && !nativeResidualComposite;
    if (nativeResidualComposite) {
        std::wstringstream message;
        message << L"Applying native-resolution source output with neural max height "
                << g_config.neuralMaxHeight << L"; preserving the selected Lossless Scaling scaler state";
        Log(message.str());
    } else if (g_config.nativeResolution && !reducedWorkingResolution) {
        settings.scalingMode = 1;
        settings.scalingType = 0;
        settings.scaleFactor = 1.0f;
        Log(L"Applying native-resolution 1:1 Lossless Scaling target settings");
    } else if (settings.scalingType == 0 && g_config.defaultScalingTypeIfOff > 0) {
        settings.scalingType = g_config.defaultScalingTypeIfOff;
    }
    if (reducedWorkingResolution) {
        std::wstringstream message;
        message << L"Applying reduced neural working scale " << std::fixed << std::setprecision(2)
                << g_config.workingScale << L" before the selected Lossless Scaling upscaler";
        Log(message.str());
    }
    settings.resizeBeforeScale = 0;
    settings.clipCursor = 0;
    settings.multiDisplayMode = 1;
    if (g_config.forceCaptureApi >= 0) {
        settings.captureApi = g_config.forceCaptureApi;
    }

    std::wstringstream handoff;
    handoff << L"Neural bridge -> Lossless Scaling: captureApi=" << settings.captureApi
            << L" frameGenType=" << settings.frameGenType
            << L" multiplier=" << settings.frameGenMultiplier
            << L" target=" << settings.frameGenTarget;
    Log(handoff.str());

    g_originalApplySettings(settings.scalingMode, settings.scalingFitMode, settings.scalingType, settings.scalingSubtype,
                            settings.scaleFactor, settings.resizeBeforeScale, settings.sharpness, settings.vrs,
                            settings.frameGenType, settings.frameGenSize, settings.frameGenMode, settings.frameGenMultiplier, settings.frameGenTarget,
                            settings.frameGenFlowScale, settings.clipCursor, settings.adjustCursorSpeed, settings.hideCursor, settings.scaleCursor,
                            settings.syncMode, settings.maxFrameLatency, settings.gsyncSupport, settings.hdrSupport,
                            settings.captureApi, settings.queueTarget, settings.drawFps, settings.gpuId, settings.displayId,
                            settings.cropLeft, settings.cropTop, settings.cropRight, settings.cropBottom, settings.multiDisplayMode,
                            settings.setupPhase);
}

void ApplyBridgeSettings() {
    SettingsSnapshot settings;
    {
        std::lock_guard lock(g_mutex);
        settings = g_settings;
    }
    ApplyBridgeSettings(settings);
}

LRESULT CALLBACK ControlWndProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == kBridgeReadyMessage) {
        (void)lparam;
        const auto generation = static_cast<std::uint64_t>(wparam);
        HWND bridge = nullptr;
        HWND source = nullptr;
        {
            std::lock_guard lock(g_mutex);
            if (generation != g_child.generation) {
                return 0;
            }
            bridge = g_child.bridgeHwnd;
            source = g_child.sourceHwnd;
        }

        if (!bridge || !IsWindow(bridge) || !g_originalActivate) {
            NotifyStatus(0, source, 1005);
            return 0;
        }

        Log(L"Bridge ready; activating Lossless Scaling on bridge window");
        ApplyBridgeSettings();
        bool ok = g_originalActivate(bridge);
        {
            std::lock_guard lock(g_mutex);
            if (generation == g_child.generation) {
                g_child.nativeActivateCalled = ok;
                g_child.active = ok;
            }
        }
        if (source && IsWindow(source)) {
            SetForegroundWindow(source);
        }
        if (!ok) {
            NotifyStatus(0, source, 1006);
            Log(L"Original Activate returned false for bridge window");
        }
        return 0;
    }
    if (message == kBridgeStoppedMessage) {
        const auto generation = static_cast<std::uint64_t>(wparam);
        const auto error = static_cast<int>(lparam);
        HWND source = nullptr;
        bool nativeActivateCalled = false;
        {
            std::lock_guard lock(g_mutex);
            if (generation != g_child.generation) {
                return 0;
            }
            source = g_child.sourceHwnd;
            nativeActivateCalled = g_child.nativeActivateCalled;
        }

        Log(L"Bridge/source stopped; deactivating original Lossless Scaling");
        if (nativeActivateCalled && g_originalActivate) {
            g_originalActivate(nullptr);
        }
        StopChildAndJoin(false);
        NotifyStatus(0, source, error);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

bool EnsureControlWindow() {
    if (g_controlWindow && IsWindow(g_controlWindow)) {
        return true;
    }

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = ControlWndProc;
    wc.hInstance = g_module;
    wc.lpszClassName = kClassName;
    RegisterClassExW(&wc);

    g_controlWindow = CreateWindowExW(0, kClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!g_controlWindow) {
        Log(L"Failed to create control window: " + LastErrorText());
        return false;
    }
    g_uiThreadId = GetCurrentThreadId();
    return true;
}

bool StartBridgeAsync(HWND sourceHwnd) {
    std::lock_guard startLock(g_startMutex);

    if (!IsWindow(sourceHwnd)) {
        Log(L"Cannot start bridge: source HWND is not valid");
        NotifyStatus(0, sourceHwnd, 1001);
        return false;
    }
    if (!std::filesystem::exists(g_config.bridgeExe)) {
        Log(L"Cannot start bridge: BridgeExe does not exist: " + g_config.bridgeExe.wstring());
        NotifyStatus(0, sourceHwnd, 1010);
        return false;
    }
    if (!std::filesystem::exists(g_config.runtimeDirectory)) {
        Log(L"Cannot start bridge: RuntimeDirectory does not exist: " + g_config.runtimeDirectory.wstring());
        NotifyStatus(0, sourceHwnd, 1011);
        return false;
    }

    StopChildAndJoin(true);

    const auto generation = ++g_generation;
    const auto readyFile = g_config.runtimeDirectory / (L"auto-ready-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(generation) + L".txt");
    const auto stopEventName = L"Local\\DlssNrStop-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(generation);
    std::error_code ec;
    std::filesystem::remove(readyFile, ec);

    HANDLE stopEvent = CreateEventW(nullptr, TRUE, FALSE, stopEventName.c_str());
    if (!stopEvent) {
        Log(L"CreateEventW failed for stop event: " + LastErrorText());
        NotifyStatus(0, sourceHwnd, 1012);
        return false;
    }

    auto cmd = BuildCommandLine(g_config, sourceHwnd, readyFile, stopEventName);
    auto env = BuildEnvironmentBlock(g_config.hipVisibleDevices);
    std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
    mutableCmd.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    BOOL created = CreateProcessW(g_config.bridgeExe.c_str(),
                                  mutableCmd.data(),
                                  nullptr,
                                  nullptr,
                                  FALSE,
                                  CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                                  env.data(),
                                  g_config.runtimeDirectory.c_str(),
                                  &startup,
                                  &process);
    if (!created) {
        const auto error = LastErrorText();
        CloseHandle(stopEvent);
        Log(L"CreateProcessW failed for bridge: " + error);
        NotifyStatus(0, sourceHwnd, 1013);
        return false;
    }

    {
        std::lock_guard lock(g_mutex);
        g_child.process = process;
        g_child.stopEvent = stopEvent;
        g_child.stopEventName = stopEventName;
        g_child.readyFile = readyFile;
        g_child.sourceHwnd = sourceHwnd;
        g_child.generation = generation;
        g_child.pending = true;
        g_child.active = false;
    }

    PROCESS_INFORMATION processForThread{};
    if (!DuplicateHandle(GetCurrentProcess(), process.hProcess, GetCurrentProcess(), &processForThread.hProcess, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        Log(L"DuplicateHandle failed for bridge watcher: " + LastErrorText());
        NotifyStatus(0, sourceHwnd, 1014);
        StopChildAndJoin(true);
        return false;
    }
    processForThread.dwProcessId = process.dwProcessId;

    if (g_watcherThread.joinable()) {
        g_watcherThread.join();
    }
    g_watcherThread = std::thread(WatchReadiness, generation, processForThread, readyFile, sourceHwnd, g_config.readyTimeoutMs);
    Log(L"Bridge child started, waiting for ready file: " + readyFile.wstring());
    NotifyStatus(1, sourceHwnd, 0);
    return true;
}

bool IsOurWindow(HWND hwnd) {
    if (!hwnd) {
        return false;
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    return pid == GetCurrentProcessId();
}

HWND ResolveSourceWindow(HWND requested) {
    if (requested && IsWindow(requested)) {
        return requested;
    }

    HWND foreground = g_originalGetForegroundWindowEx ? g_originalGetForegroundWindowEx() : GetForegroundWindow();
    if (foreground && IsWindow(foreground) && !IsOurWindow(foreground)) {
        return foreground;
    }
    return nullptr;
}

} // namespace

extern "C" __declspec(dllexport) bool __cdecl Init(StatusCallback callback) {
    if (!EnsureOriginal()) {
        return false;
    }
    g_userStatusCallback = callback;
    g_config = LoadConfig();
    EnsureControlWindow();
    Log(L"Init called; auto-scale " + std::wstring(g_config.enabled ? L"enabled" : L"disabled"));
    return g_originalInit(StatusProxy);
}

extern "C" __declspec(dllexport) bool __cdecl Activate(HWND hwnd) {
    if (!EnsureOriginal()) {
        return false;
    }

    bool stopOnly = false;
    bool nativeActivateCalled = false;
    HWND source = nullptr;
    {
        std::lock_guard lock(g_mutex);
        stopOnly = hwnd == nullptr && (g_child.pending || g_child.process.hProcess != nullptr || g_child.nativeActivateCalled);
        nativeActivateCalled = g_child.nativeActivateCalled;
        source = g_child.sourceHwnd;
    }

    if (stopOnly) {
        Log(L"Activate(nullptr) while active/pending; stopping original scaling and bridge child");
        bool ok = true;
        if (nativeActivateCalled) {
            ok = g_originalActivate(nullptr);
        }
        StopChildAndJoin(true);
        NotifyStatus(0, source ? source : hwnd, 0);
        return ok;
    }

    if (!g_config.enabled) {
        return g_originalActivate(hwnd);
    }

    HWND resolvedSource = ResolveSourceWindow(hwnd);
    if (!resolvedSource) {
        Log(L"Activate could not resolve a source window");
        NotifyStatus(0, hwnd, 1020);
        return false;
    }

    return StartBridgeAsync(resolvedSource);
}

extern "C" __declspec(dllexport) void __cdecl UnInit() {
    if (!EnsureOriginal()) {
        return;
    }
    Log(L"UnInit called; stopping bridge child");
    StopChildAndJoin(true);
    if (g_controlWindow && IsWindow(g_controlWindow) && GetCurrentThreadId() == g_uiThreadId) {
        DestroyWindow(g_controlWindow);
        g_controlWindow = nullptr;
    }
    g_originalUnInit();
}

extern "C" __declspec(dllexport) void __fastcall ApplySettings(
    int scalingMode, int scalingFitMode, int scalingType, int scalingSubtype,
    float scaleFactor, std::uint8_t resizeBeforeScale, int sharpness, std::uint8_t vrs,
    int frameGenType, int frameGenSize, int frameGenMode, float frameGenMultiplier, float frameGenTarget,
    int frameGenFlowScale, std::uint8_t clipCursor, std::uint8_t adjustCursorSpeed, std::uint8_t hideCursor, std::uint8_t scaleCursor,
    int syncMode, int maxFrameLatency, std::uint8_t gsyncSupport, std::uint8_t hdrSupport,
    int captureApi, int queueTarget, std::uint8_t drawFps, int gpuId, int displayId,
    int cropLeft, int cropTop, int cropRight, int cropBottom, std::uint8_t multiDisplayMode,
    int setupPhase) {
    if (!EnsureOriginal()) {
        return;
    }

    SettingsSnapshot incoming;
    bool bridgeTarget = false;
    {
        std::lock_guard lock(g_mutex);
        g_settings = SettingsSnapshot{scalingMode, scalingFitMode, scalingType, scalingSubtype,
                                      scaleFactor, resizeBeforeScale, sharpness, vrs,
                                      frameGenType, frameGenSize, frameGenMode, frameGenMultiplier, frameGenTarget,
                                      frameGenFlowScale, clipCursor, adjustCursorSpeed, hideCursor, scaleCursor,
                                      syncMode, maxFrameLatency, gsyncSupport, hdrSupport,
                                      captureApi, queueTarget, drawFps, gpuId, displayId,
                                      cropLeft, cropTop, cropRight, cropBottom, multiDisplayMode,
                                      setupPhase, true};
        incoming = g_settings;
        bridgeTarget = g_config.enabled && g_child.bridgeHwnd != nullptr && g_child.process.hProcess != nullptr;
    }

    // Keep this call's frame-generation choices, but retain the capture and
    // geometry required by the ready/active bridge. Forward outside the lock:
    // the original implementation may call back into the proxy.
    if (bridgeTarget) {
        ApplyBridgeSettings(incoming);
        return;
    }

    g_originalApplySettings(scalingMode, scalingFitMode, scalingType, scalingSubtype,
                            scaleFactor, resizeBeforeScale, sharpness, vrs,
                            frameGenType, frameGenSize, frameGenMode, frameGenMultiplier, frameGenTarget,
                            frameGenFlowScale, clipCursor, adjustCursorSpeed, hideCursor, scaleCursor,
                            syncMode, maxFrameLatency, gsyncSupport, hdrSupport,
                            captureApi, queueTarget, drawFps, gpuId, displayId,
                            cropLeft, cropTop, cropRight, cropBottom, multiDisplayMode,
                            setupPhase);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
