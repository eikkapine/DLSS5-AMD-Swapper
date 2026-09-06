#include "common.h"

#include <atomic>

using StatusCallback = void(__cdecl*)(int status,
                                      HWND hwnd,
                                      int inputWidth,
                                      int inputHeight,
                                      int outputWidth,
                                      int outputHeight,
                                      std::uint8_t resized,
                                      float factor,
                                      int error);

namespace {
StatusCallback g_callback = nullptr;
std::filesystem::path LogPath() {
    return astest::GetEnvW(L"AUTO_SCALE_TEST_LOG");
}
std::string HwndText(HWND hwnd) {
    std::ostringstream out;
    out << reinterpret_cast<std::uintptr_t>(hwnd);
    return out.str();
}
}

extern "C" __declspec(dllexport) bool __cdecl Init(StatusCallback callback) {
    g_callback = callback;
    astest::AppendLog(LogPath(), "original_init=1");
    return true;
}

extern "C" __declspec(dllexport) bool __cdecl Activate(HWND hwnd) {
    astest::AppendLog(LogPath(), "original_activate_hwnd=" + HwndText(hwnd));
    if (g_callback) {
        g_callback(hwnd ? 2 : 0, hwnd, 111, 222, 333, 444, 1, 1.25f, 0);
    }
    return true;
}

extern "C" __declspec(dllexport) void __cdecl UnInit() {
    astest::AppendLog(LogPath(), "original_uninit=1");
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
    std::ostringstream line;
    line << "original_apply_settings="
         << scalingMode << ',' << scalingFitMode << ',' << scalingType << ',' << scalingSubtype << ','
         << scaleFactor << ',' << static_cast<int>(resizeBeforeScale) << ',' << sharpness << ',' << static_cast<int>(vrs) << ','
         << frameGenType << ',' << frameGenSize << ',' << frameGenMode << ',' << frameGenMultiplier << ',' << frameGenTarget << ','
         << frameGenFlowScale << ',' << static_cast<int>(clipCursor) << ',' << static_cast<int>(adjustCursorSpeed) << ','
         << static_cast<int>(hideCursor) << ',' << static_cast<int>(scaleCursor) << ',' << syncMode << ',' << maxFrameLatency << ','
         << static_cast<int>(gsyncSupport) << ',' << static_cast<int>(hdrSupport) << ',' << captureApi << ',' << queueTarget << ','
         << static_cast<int>(drawFps) << ',' << gpuId << ',' << displayId << ',' << cropLeft << ',' << cropTop << ','
         << cropRight << ',' << cropBottom << ',' << static_cast<int>(multiDisplayMode) << ',' << setupPhase;
    astest::AppendLog(LogPath(), line.str());
}

extern "C" __declspec(dllexport) void __cdecl GetAdapterNames() {}
extern "C" __declspec(dllexport) void __cdecl GetDisplayNames() {}
extern "C" __declspec(dllexport) void __cdecl GetDwmRefreshRate() {}
extern "C" __declspec(dllexport) HWND __cdecl GetForegroundWindowEx() { return GetForegroundWindow(); }
extern "C" __declspec(dllexport) void __cdecl IsWindowsBuildAtLeast() {}
extern "C" __declspec(dllexport) void __cdecl SetDriverSettings() {}
extern "C" __declspec(dllexport) void __cdecl SetWindowsSettings() {}
