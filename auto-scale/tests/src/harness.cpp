#include "common.h"

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <iostream>
#include <map>
#include <mutex>
#include <set>

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
using ApplySettingsFn = void(__fastcall*)(
    int scalingMode, int scalingFitMode, int scalingType, int scalingSubtype,
    float scaleFactor, std::uint8_t resizeBeforeScale, int sharpness, std::uint8_t vrs,
    int frameGenType, int frameGenSize, int frameGenMode, float frameGenMultiplier, float frameGenTarget,
    int frameGenFlowScale, std::uint8_t clipCursor, std::uint8_t adjustCursorSpeed, std::uint8_t hideCursor, std::uint8_t scaleCursor,
    int syncMode, int maxFrameLatency, std::uint8_t gsyncSupport, std::uint8_t hdrSupport,
    int captureApi, int queueTarget, std::uint8_t drawFps, int gpuId, int displayId,
    int cropLeft, int cropTop, int cropRight, int cropBottom, std::uint8_t multiDisplayMode,
    int setupPhase);

namespace {
struct CallbackEvent {
    int status = -1;
    HWND hwnd = nullptr;
    int inputWidth = 0;
    int inputHeight = 0;
    int outputWidth = 0;
    int outputHeight = 0;
    std::uint8_t resized = 0;
    float factor = 0.0f;
    int error = 0;
};

std::mutex g_callbackMutex;
std::vector<CallbackEvent> g_callbacks;

void __cdecl UserStatusCallback(int status,
                                HWND hwnd,
                                int inputWidth,
                                int inputHeight,
                                int outputWidth,
                                int outputHeight,
                                std::uint8_t resized,
                                float factor,
                                int error) {
    {
        std::lock_guard lock(g_callbackMutex);
        g_callbacks.push_back({status, hwnd, inputWidth, inputHeight, outputWidth, outputHeight, resized, factor, error});
    }
    std::ostringstream line;
    line << "user_callback=" << status << "," << reinterpret_cast<std::uintptr_t>(hwnd) << "," << error;
    astest::AppendLog(astest::GetEnvW(L"AUTO_SCALE_TEST_LOG"), line.str());
}

std::vector<CallbackEvent> CallbackSnapshot() {
    std::lock_guard lock(g_callbackMutex);
    return g_callbacks;
}

void ClearCallbacks() {
    std::lock_guard lock(g_callbackMutex);
    g_callbacks.clear();
}

[[noreturn]] void Fail(const std::string& message) {
    std::cerr << "FAIL: " << message << "\n";
    std::exit(1);
}

void Require(bool condition, const std::string& message) {
    if (!condition) {
        Fail(message);
    }
}

std::string ReadText(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        return {};
    }
    return std::string(std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
}

bool Contains(const std::string& haystack, const std::string& needle) {
    return haystack.find(needle) != std::string::npos;
}

std::vector<std::string> LinesWithPrefix(const std::string& text, const std::string& prefix) {
    std::vector<std::string> lines;
    std::istringstream input(text);
    std::string line;
    while (std::getline(input, line)) {
        if (line.rfind(prefix, 0) == 0) {
            lines.push_back(line);
        }
    }
    return lines;
}

std::uintptr_t ParseAfterEqualsHandle(const std::string& line) {
    const auto pos = line.find('=');
    if (pos == std::string::npos) {
        return 0;
    }
    return static_cast<std::uintptr_t>(std::stoull(line.substr(pos + 1)));
}

std::filesystem::path ExeDir() {
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    return std::filesystem::path(std::wstring(path, length)).parent_path();
}

std::filesystem::path DefaultWrapperPath() {
    auto dir = ExeDir();
    for (int i = 0; i < 5; ++i) {
        auto candidate = dir / L".." / L".." / L".." / L"build" / L"Release" / L"Lossless.dll";
        std::error_code ec;
        candidate = std::filesystem::weakly_canonical(candidate, ec);
        if (!ec && std::filesystem::exists(candidate)) {
            return candidate;
        }
        dir = dir.parent_path();
    }
    return {};
}

struct Args {
    std::filesystem::path wrapper;
    std::filesystem::path fakeOriginal;
    std::filesystem::path fakeBridge;
    std::filesystem::path runRoot;
    std::string only;
};

Args ParseArgs(int argc, wchar_t** argv) {
    Args args;
    args.wrapper = astest::ArgValue(argc, argv, L"--wrapper");
    args.fakeOriginal = astest::ArgValue(argc, argv, L"--fake-original");
    args.fakeBridge = astest::ArgValue(argc, argv, L"--fake-bridge");
    args.runRoot = astest::ArgValue(argc, argv, L"--run-root");
    args.only = astest::Narrow(astest::ArgValue(argc, argv, L"--case"));
    if (args.wrapper.empty()) {
        args.wrapper = DefaultWrapperPath();
    }
    const auto bin = ExeDir();
    if (args.fakeOriginal.empty()) {
        args.fakeOriginal = bin / L"Lossless_original.dll";
    }
    if (args.fakeBridge.empty()) {
        args.fakeBridge = bin / L"FakeBridge.exe";
    }
    if (args.runRoot.empty()) {
        args.runRoot = std::filesystem::current_path() / L"runs";
    }
    return args;
}

void RecordPass(const Args& args, const std::string& name) {
    astest::AppendLog(std::filesystem::absolute(args.runRoot) / L"harness-results.txt", "PASS " + name);
}

void SetEnvWChecked(const wchar_t* name, const std::wstring& value) {
    Require(SetEnvironmentVariableW(name, value.c_str()) != 0, "SetEnvironmentVariableW failed");
}

void ClearEnvWChecked(const wchar_t* name) {
    SetEnvironmentVariableW(name, nullptr);
}

struct LoadedProxy {
    HMODULE module = nullptr;
    InitFn Init = nullptr;
    ActivateFn Activate = nullptr;
    UnInitFn UnInit = nullptr;
    ApplySettingsFn ApplySettings = nullptr;

    ~LoadedProxy() {
        if (module) {
            FreeLibrary(module);
        }
    }
};

struct TestCaseRuntime {
    std::filesystem::path dir;
    std::filesystem::path runtimeDir;
    std::filesystem::path log;
};

TestCaseRuntime PrepareRuntime(const Args& args,
                               const std::string& name,
                               const std::wstring& bridgeExeOverride = L"",
                               int readyTimeoutMs = 5000,
                               int defaultScalingTypeIfOff = 9,
                               int forceCaptureApi = 8,
                               bool enabled = true,
                               bool nativeResolution = true,
                               bool configureDiagnostics = false) {
    TestCaseRuntime rt;
    rt.dir = std::filesystem::absolute(args.runRoot / astest::Widen(name));
    rt.runtimeDir = rt.dir / L"nr-bridge" / L"runtime";
    rt.log = rt.dir / L"events.log";
    std::error_code ec;
    std::filesystem::remove_all(rt.dir, ec);
    std::filesystem::create_directories(rt.runtimeDir);

    std::filesystem::copy_file(args.wrapper, rt.dir / L"Lossless.dll", std::filesystem::copy_options::overwrite_existing);
    std::filesystem::copy_file(args.fakeOriginal, rt.dir / L"Lossless_original.dll", std::filesystem::copy_options::overwrite_existing);
    std::filesystem::copy_file(args.fakeBridge, rt.runtimeDir / L"DlssNrBridge.exe", std::filesystem::copy_options::overwrite_existing);

    const std::wstring bridgePath = bridgeExeOverride.empty() ? (rt.runtimeDir / L"DlssNrBridge.exe").wstring() : bridgeExeOverride;
    std::wofstream config(rt.dir / L"NrAutoScale.ini", std::ios::binary | std::ios::trunc);
    const auto captureDir = rt.dir / L"captures";
    config << L"[AutoScale]\n"
           << L"Enabled=" << (enabled ? 1 : 0) << L"\n"
           << L"BridgeExe=" << bridgePath << L"\n"
           << L"RuntimeDirectory=" << rt.runtimeDir.wstring() << L"\n"
           << L"HipVisibleDevices=77\n"
           << L"Width=123\n"
           << L"Height=45\n"
           << L"StartupDelayMs=0\n"
           << L"WarmupFrames=0\n"
           << L"ReadyTimeoutMs=" << readyTimeoutMs << L"\n"
           << L"NativeResolution=" << (nativeResolution ? 1 : 0) << L"\n"
           << L"DefaultScalingTypeIfOff=" << defaultScalingTypeIfOff << L"\n"
           << L"ForceCaptureApi=" << forceCaptureApi << L"\n";
    if (configureDiagnostics) {
        config << L"CaptureDirectory=" << captureDir.wstring() << L"\n"
               << L"FreezeSource=1\n";
    }
    return rt;
}

LoadedProxy LoadProxy(const TestCaseRuntime& rt) {
    LoadedProxy proxy;
    proxy.module = LoadLibraryW((rt.dir / L"Lossless.dll").c_str());
    if (!proxy.module) {
        Fail("LoadLibraryW(Lossless.dll) failed with error " + std::to_string(GetLastError()));
    }
    proxy.Init = reinterpret_cast<InitFn>(GetProcAddress(proxy.module, "Init"));
    proxy.Activate = reinterpret_cast<ActivateFn>(GetProcAddress(proxy.module, "Activate"));
    proxy.UnInit = reinterpret_cast<UnInitFn>(GetProcAddress(proxy.module, "UnInit"));
    proxy.ApplySettings = reinterpret_cast<ApplySettingsFn>(GetProcAddress(proxy.module, "ApplySettings"));
    Require(proxy.Init && proxy.Activate && proxy.UnInit && proxy.ApplySettings, "proxy missing one or more wrapped exports");
    return proxy;
}

void ApplyKnownSettings(const LoadedProxy& proxy) {
    proxy.ApplySettings(10, 11, 0, 13,
                        1.5f, 1, 16, 1,
                        18, 19, 20, 2.5f, 3.5f,
                        24, 1, 1, 1, 1,
                        29, 30, 1, 1,
                        42, 43, 1, 45, 46,
                        47, 48, 49, 50, 0,
                        52);
}

void InitProxy(LoadedProxy& proxy) {
    ClearCallbacks();
    Require(proxy.Init(UserStatusCallback), "Init returned false");
}

bool WaitForCallback(int status, HWND hwnd, int error, std::chrono::milliseconds timeout) {
    return astest::PumpUntil(timeout, [&]() {
        const auto callbacks = CallbackSnapshot();
        return std::any_of(callbacks.begin(), callbacks.end(), [&](const CallbackEvent& event) {
            return event.status == status && event.hwnd == hwnd && event.error == error;
        });
    });
}

void CleanupProxy(LoadedProxy& proxy) {
    if (proxy.UnInit) {
        proxy.UnInit();
    }
}

void TestHappyPath(const Args& args) {
    const auto rt = PrepareRuntime(args, "happy");
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"delay=900");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceHappy", L"AutoScale Harness Source Happy");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);

    const auto before = std::chrono::steady_clock::now();
    Require(proxy.Activate(source), "Activate(source) returned false before readiness");
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - before);
    Require(elapsed < std::chrono::milliseconds(500), "Activate(source) blocked UI while bridge was pending");

    const std::string earlyLog = ReadText(rt.log);
    Require(!Contains(earlyLog, "original_activate_hwnd="), "original Activate was called before bridge readiness");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(6000)), "did not receive mapped active status for source HWND");

    const std::string log = ReadText(rt.log);
    const auto activations = LinesWithPrefix(log, "original_activate_hwnd=");
    Require(!activations.empty(), "original Activate was not called after bridge readiness");
    const auto bridgeHwnd = ParseAfterEqualsHandle(activations.front());
    Require(bridgeHwnd != 0, "original Activate did not receive a bridge HWND");
    Require(bridgeHwnd != reinterpret_cast<std::uintptr_t>(source), "original Activate received the source HWND instead of bridge HWND");
    Require(Contains(log, "bridge_ready_decimal=" + std::to_string(bridgeHwnd)), "bridge readiness HWND did not match original Activate target");
    Require(Contains(log, "hip=77"), "HIP_VISIBLE_DEVICES was not passed to child environment");
    Require(Contains(log, "source=0x"), "bridge launch log did not include source hwnd argument");
    Require(Contains(log, "capture_dir= freeze_source=0 native_resolution=1"), "default diagnostic/native flags were not forwarded as expected");

    const auto applies = LinesWithPrefix(log, "original_apply_settings=");
    Require(applies.size() >= 2, "expected initial and bridge-adjusted ApplySettings calls");
    Require(Contains(applies.front(), "10,11,0,13,1.5,1"), "initial ApplySettings did not preserve caller values");
    Require(Contains(applies.back(), "1,11,0,13,1,0"), "native-resolution ApplySettings did not force scalingMode=1 scalingType=0 scaleFactor=1 resize=0");
    Require(Contains(applies.back(), ",24,0,1,1,1,29,"), "bridge ApplySettings did not force clipCursor off while preserving nearby settings");
    Require(Contains(applies.back(), ",8,43,1,45,46,"), "bridge ApplySettings did not force configured capture API");
    Require(Contains(applies.back(), ",49,50,1,52"), "bridge ApplySettings did not force multiDisplayMode on");

    Require(proxy.Activate(nullptr), "Activate(nullptr) returned false on active unscale");
    Require(astest::PumpUntil(std::chrono::milliseconds(2000), [&]() { return Contains(ReadText(rt.log), "bridge_stop_event=1"); }), "bridge did not observe stop event after unscale");
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "happy_path");
    std::cout << "PASS happy_path\n";
}

void TestCancelPending(const Args& args) {
    const auto rt = PrepareRuntime(args, "cancel-pending", L"", 5000);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"delay=3000");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceCancel", L"AutoScale Harness Source Cancel");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);
    Require(proxy.Activate(source), "Activate(source) returned false for pending-cancel test");
    Sleep(200);
    Require(proxy.Activate(nullptr), "Activate(nullptr) returned false while pending");
    Require(astest::PumpUntil(std::chrono::milliseconds(2000), [&]() {
        const auto log = ReadText(rt.log);
        return Contains(log, "bridge_stop_event_before_ready=1") || Contains(log, "bridge_stop_event=1");
    }), "pending bridge did not observe stop event");
    const auto log = ReadText(rt.log);
    const auto activations = LinesWithPrefix(log, "original_activate_hwnd=");
    for (const auto& activation : activations) {
        Require(ParseAfterEqualsHandle(activation) == 0, "pending cancel forwarded a non-null original Activate");
    }
    Require(WaitForCallback(0, source, 0, std::chrono::milliseconds(1000)), "pending cancel did not report inactive source status");
    for (const auto& event : CallbackSnapshot()) {
        Require(!(event.status == 2 && event.hwnd == source && event.error == 0), "pending cancel produced active source callback");
    }
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "cancel_pending");
    std::cout << "PASS cancel_pending\n";
}


void TestDiagnosticsForwarded(const Args& args) {
    const auto rt = PrepareRuntime(args, "diagnostics-forwarded", L"", 5000, 9, 8, true, true, true);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"delay=150");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceDiagnostics", L"AutoScale Harness Source Diagnostics");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);

    Require(proxy.Activate(source), "diagnostics Activate(source) returned false");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(3000)), "diagnostics case did not become active");
    const auto log = ReadText(rt.log);
    Require(Contains(log, "capture_dir="), "diagnostics bridge log missing capture-dir field");
    Require(Contains(log, "diagnostics-forwarded\\captures"), "configured CaptureDirectory was not forwarded to bridge");
    Require(Contains(log, "freeze_source=1"), "configured FreezeSource was not forwarded to bridge");
    Require(Contains(log, "native_resolution=1"), "default NativeResolution was not forwarded to bridge");
    Require(Contains(log, "original_activate_hwnd="), "diagnostics case did not call original Activate after readiness");
    Require(proxy.Activate(nullptr), "diagnostics unscale returned false");
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "diagnostics_forwarded");
    std::cout << "PASS diagnostics_forwarded\n";
}


void TestFixedSizeCompatibility(const Args& args) {
    const auto rt = PrepareRuntime(args, "fixed-size-compat", L"", 5000, 9, 8, true, false, false);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"delay=150");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceFixed", L"AutoScale Harness Source Fixed");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);

    Require(proxy.Activate(source), "fixed-size Activate(source) returned false");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(3000)), "fixed-size case did not become active");
    const auto log = ReadText(rt.log);
    Require(Contains(log, "native_resolution=0"), "NativeResolution=0 still forwarded --native-resolution");
    const auto applies = LinesWithPrefix(log, "original_apply_settings=");
    Require(applies.size() >= 2, "fixed-size case expected initial and bridge-adjusted ApplySettings calls");
    Require(Contains(applies.back(), "10,11,9,13,1.5,0"), "fixed-size compatibility path did not preserve old scalingMode/scaleFactor and default scaling type");
    Require(Contains(applies.back(), ",8,43,1,45,46,"), "fixed-size compatibility path did not force configured capture API");
    Require(Contains(applies.back(), ",49,50,1,52"), "fixed-size compatibility path did not force multiDisplayMode on");
    Require(proxy.Activate(nullptr), "fixed-size unscale returned false");
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "fixed_size_compatibility");
    std::cout << "PASS fixed_size_compatibility\n";
}

void TestDisabledPassThrough(const Args& args) {
    const auto rt = PrepareRuntime(args, "disabled-pass-through", L"", 5000, 9, 8, false);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceDisabled", L"AutoScale Harness Source Disabled");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);

    Require(proxy.Activate(source), "disabled Activate(source) returned false");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(1000)), "disabled pass-through did not report active source status");
    Require(proxy.Activate(nullptr), "disabled Activate(nullptr) returned false");
    Require(WaitForCallback(0, nullptr, 0, std::chrono::milliseconds(1000)), "disabled pass-through did not report inactive null status");

    const auto log = ReadText(rt.log);
    Require(Contains(log, "original_activate_hwnd=" + std::to_string(reinterpret_cast<std::uintptr_t>(source))), "disabled mode did not pass source HWND to original Activate");
    Require(Contains(log, "original_activate_hwnd=0"), "disabled mode did not pass null HWND to original Activate on unscale");
    Require(!Contains(log, "bridge_start"), "disabled mode started the bridge");
    CleanupProxy(proxy);
    DestroyWindow(source);
    RecordPass(args, "disabled_pass_through");
    std::cout << "PASS disabled_pass_through\n";
}
void TestSourceCloseAfterActive(const Args& args) {
    const auto rt = PrepareRuntime(args, "source-close", L"", 5000);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"delay=150");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceClose", L"AutoScale Harness Source Close");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);
    Require(proxy.Activate(source), "Activate(source) returned false for source-close test");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(3000)), "source-close test did not become active");
    DestroyWindow(source);
    Require(WaitForCallback(0, source, 1008, std::chrono::milliseconds(4000)), "source closure did not report status 0/error 1008");
    const auto log = ReadText(rt.log);
    Require(Contains(log, "original_activate_hwnd=0"), "source closure did not deactivate original scaling");
    Require(astest::PumpUntil(std::chrono::milliseconds(2000), [&]() { return Contains(ReadText(rt.log), "bridge_stop_event=1"); }), "source closure did not stop bridge child");
    CleanupProxy(proxy);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "source_close_after_active");
    std::cout << "PASS source_close_after_active\n";
}

void TestChildExitAfterActive(const Args& args) {
    const auto rt = PrepareRuntime(args, "child-exit", L"", 5000);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"exit-after-ready=900");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceChildExit", L"AutoScale Harness Source Child Exit");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);
    Require(proxy.Activate(source), "Activate(source) returned false for child-exit test");
    Require(WaitForCallback(2, source, 0, std::chrono::milliseconds(3000)), "child-exit test did not become active before bridge exited");
    Require(WaitForCallback(0, source, 1007, std::chrono::milliseconds(4000)), "child exit did not report status 0/error 1007");
    const auto log = ReadText(rt.log);
    Require(Contains(log, "bridge_exit_after_ready=1"), "fake bridge did not record exit-after-ready");
    Require(Contains(log, "original_activate_hwnd=0"), "child exit did not deactivate original scaling");
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "child_exit_after_active");
    std::cout << "PASS child_exit_after_active\n";
}
void TestMissingBridge(const Args& args) {
    auto rt = PrepareRuntime(args, "missing-bridge", L"Z:\\definitely-missing\\DlssNrBridge.exe", 800);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceMissing", L"AutoScale Harness Source Missing");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);
    Require(!proxy.Activate(source), "Activate(source) unexpectedly succeeded with missing bridge");
    Require(WaitForCallback(0, source, 1010, std::chrono::milliseconds(1000)), "missing bridge did not report status 0/error 1010 for source");
    const auto log = ReadText(rt.log);
    Require(!Contains(log, "original_activate_hwnd="), "missing bridge still called original Activate");
    CleanupProxy(proxy);
    DestroyWindow(source);
    RecordPass(args, "missing_bridge");
    std::cout << "PASS missing_bridge\n";
}

void TestInvalidReadiness(const Args& args) {
    const auto rt = PrepareRuntime(args, "invalid-readiness", L"", 800);
    SetEnvWChecked(L"AUTO_SCALE_TEST_LOG", rt.log.wstring());
    SetEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE", L"invalid-ready");
    HWND source = astest::CreateTestWindow(L"AutoScaleHarnessSourceInvalid", L"AutoScale Harness Source Invalid");
    LoadedProxy proxy = LoadProxy(rt);
    InitProxy(proxy);
    ApplyKnownSettings(proxy);
    Require(proxy.Activate(source), "Activate(source) returned false for invalid-readiness test");
    Require(WaitForCallback(0, source, 1004, std::chrono::milliseconds(3000)), "invalid ready file did not time out with status 0/error 1004");
    const auto log = ReadText(rt.log);
    Require(Contains(log, "bridge_ready_invalid=1"), "fake bridge did not write invalid ready evidence");
    Require(!Contains(log, "original_activate_hwnd="), "invalid ready file still called original Activate");
    CleanupProxy(proxy);
    DestroyWindow(source);
    ClearEnvWChecked(L"AUTO_SCALE_FAKE_BRIDGE_MODE");
    RecordPass(args, "invalid_readiness");
    std::cout << "PASS invalid_readiness\n";
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        const auto args = ParseArgs(argc, argv);
        Require(!args.wrapper.empty() && std::filesystem::exists(args.wrapper), "wrapper DLL not found; pass --wrapper <Lossless.dll>");
        Require(std::filesystem::exists(args.fakeOriginal), "fake original DLL not found; build tests first or pass --fake-original");
        Require(std::filesystem::exists(args.fakeBridge), "fake bridge exe not found; build tests first or pass --fake-bridge");
        std::filesystem::create_directories(args.runRoot);

        const std::set<std::string> selected = args.only.empty()
            ? std::set<std::string>{"happy", "cancel", "diagnostics", "fixed-size", "disabled", "source-close", "child-exit", "missing", "invalid"}
            : std::set<std::string>{args.only};
        if (selected.count("happy")) TestHappyPath(args);
        if (selected.count("cancel")) TestCancelPending(args);
        if (selected.count("diagnostics")) TestDiagnosticsForwarded(args);
        if (selected.count("fixed-size")) TestFixedSizeCompatibility(args);
        if (selected.count("disabled")) TestDisabledPassThrough(args);
        if (selected.count("source-close")) TestSourceCloseAfterActive(args);
        if (selected.count("child-exit")) TestChildExitAfterActive(args);
        if (selected.count("missing")) TestMissingBridge(args);
        if (selected.count("invalid")) TestInvalidReadiness(args);
        RecordPass(args, "auto_scale_harness");
        std::cout << "PASS auto_scale_harness\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << "FAIL exception: " << ex.what() << "\n";
        return 1;
    }
}
