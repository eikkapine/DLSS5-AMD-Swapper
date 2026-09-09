#include <windows.h>
#include <timeapi.h>
#include <wrl/client.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <psapi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <optional>
#include <regex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>
#include <immintrin.h>

#include "gpu_transport.h"
#include "hip_host_timing.h"

using Microsoft::WRL::ComPtr;
namespace wg = winrt::Windows::Graphics;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgd = winrt::Windows::Graphics::DirectX;
namespace wgd11 = winrt::Windows::Graphics::DirectX::Direct3D11;

extern "C" HRESULT __stdcall CreateDirect3D11DeviceFromDXGIDevice(::IDXGIDevice* dxgiDevice, ::IInspectable** graphicsDevice);

namespace {

constexpr UINT kBufferCount = 2;
constexpr DXGI_FORMAT kFormat = DXGI_FORMAT_R8G8B8A8_UNORM;

struct Options {
    bool listWindows = false;
    HWND sourceHwnd = nullptr;
    std::wstring sourceTitle;
    UINT width = 1280;
    UINT height = 720;
    UINT seconds = 0;
    UINT startupDelayMs = 2000;
    std::filesystem::path captureDir;
    std::filesystem::path readyFile;
    bool saveCaptures = false;
    bool noProxy = false;
    bool freezeSource = false;
    bool nativeResolution = false;
    float workingScale = 0.0f;
    UINT neuralMaxHeight = 0;
    UINT displayWidth = 0;
    UINT displayHeight = 0;
    bool preciseScheduling = true;
    bool repeatPresentations = false;
    bool completionPacing = true;
    bool cpuTransport = false;
    bool hipHostTiming = true;
    bool hipKernelSampling = false;
    std::wstring stopEventName;
    DWORD parentPid = 0;
    UINT warmupFrames = 301;
    UINT maxFps = 0;
};

struct FramePixels {
    UINT width = 0;
    UINT height = 0;
    std::vector<uint8_t> rgba;
};

struct PerformanceStats {
    using Clock = std::chrono::steady_clock;
    std::array<double, 6> stageMs{};
    uint64_t frames = 0;

    void Record(const std::array<Clock::time_point, 7>& boundaries) {
        for (size_t i = 0; i < stageMs.size(); ++i) {
            stageMs[i] += std::chrono::duration<double, std::milli>(boundaries[i + 1] - boundaries[i]).count();
        }
        ++frames;
    }
};

// The neural DLL polls for work with Sleep(1). Request precise wake-ups in
// this process, including while its output is covered by Lossless Scaling.
// This changes scheduling only; it does not skip work or alter GPU fences.
class ScopedRenderScheduling {
public:
    explicit ScopedRenderScheduling(bool enabled) {
        if (!enabled) {
            return;
        }
        previous_.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
        if (GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                  &previous_, sizeof(previous_))) {
            auto requested = previous_;
            requested.ControlMask |= kControlledFlags;
            requested.StateMask &= ~kControlledFlags;
            policyApplied_ = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                                   &requested, sizeof(requested)) != FALSE;
            if (!policyApplied_) {
                policyError_ = GetLastError();
            }
        } else {
            policyError_ = GetLastError();
        }
        TIMECAPS caps{};
        MMRESULT timerResult = timeGetDevCaps(&caps, sizeof(caps));
        if (timerResult == TIMERR_NOERROR) {
            const UINT period = std::max<UINT>(1, caps.wPeriodMin);
            if (period <= caps.wPeriodMax) {
                timerResult = timeBeginPeriod(period);
                if (timerResult == TIMERR_NOERROR) {
                    period_ = period;
                }
            } else {
                timerResult = TIMERR_NOCANDO;
            }
        }
        SYSTEMTIME now{};
        GetLocalTime(&now);
        std::ofstream log("bridge-scheduling.log", std::ios::app);
        log << now.wYear << '-' << now.wMonth << '-' << now.wDay << ' '
            << now.wHour << ':' << now.wMinute << ':' << now.wSecond
            << " pid=" << GetCurrentProcessId()
            << " timer_period_ms=" << period_
            << " timer_result=" << timerResult
            << " foreground_qos_and_occlusion_policy=" << policyApplied_
            << " policy_error=" << policyError_ << '\n';
    }

    ~ScopedRenderScheduling() {
        if (period_ != 0) {
            timeEndPeriod(period_);
        }
        if (policyApplied_) {
            // Preserve any unrelated policy changed since this scope began.
            PROCESS_POWER_THROTTLING_STATE current{};
            current.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
            if (GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                      &current, sizeof(current))) {
                current.ControlMask = (current.ControlMask & ~kControlledFlags)
                                    | (previous_.ControlMask & kControlledFlags);
                current.StateMask = (current.StateMask & ~kControlledFlags)
                                  | (previous_.StateMask & kControlledFlags);
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                      &current, sizeof(current));
            } else {
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                                      &previous_, sizeof(previous_));
            }
        }
    }

    ScopedRenderScheduling(const ScopedRenderScheduling&) = delete;
    ScopedRenderScheduling& operator=(const ScopedRenderScheduling&) = delete;

private:
    static constexpr ULONG kControlledFlags = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                                           | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
    PROCESS_POWER_THROTTLING_STATE previous_{};
    UINT period_ = 0;
    DWORD policyError_ = ERROR_SUCCESS;
    bool policyApplied_ = false;
};

std::string NarrowAscii(const std::wstring& value) {
    std::string out;
    out.reserve(value.size());
    for (wchar_t ch : value) {
        out.push_back(ch >= 0 && ch <= 127 ? static_cast<char>(ch) : '?');
    }
    return out;
}

void Check(HRESULT hr, const char* what) {
    if (FAILED(hr)) {
        std::ostringstream os;
        os << what << " failed with HRESULT 0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(os.str());
    }
}

UINT64 Align(UINT64 value, UINT64 alignment) {
    return (value + alignment - 1u) & ~(alignment - 1u);
}


D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource;
    barrier.Transition.StateBefore = before;
    barrier.Transition.StateAfter = after;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    return barrier;
}

void SavePpm(const std::filesystem::path& path, const FramePixels& frame) {
    if (frame.rgba.empty()) {
        return;
    }
    std::ofstream file(path, std::ios::binary);
    if (!file) {
        throw std::runtime_error("Could not open PPM output");
    }
    file << "P6\n" << frame.width << " " << frame.height << "\n255\n";
    for (UINT y = 0; y < frame.height; ++y) {
        for (UINT x = 0; x < frame.width; ++x) {
            const size_t index = (static_cast<size_t>(y) * frame.width + x) * 4;
            const char rgb[3] = {
                static_cast<char>(frame.rgba[index + 0]),
                static_cast<char>(frame.rgba[index + 1]),
                static_cast<char>(frame.rgba[index + 2]),
            };
            file.write(rgb, sizeof(rgb));
        }
    }
}

std::wstring WindowTitle(HWND hwnd) {
    const int len = GetWindowTextLengthW(hwnd);
    if (len <= 0) {
        return L"";
    }
    std::wstring title(static_cast<size_t>(len) + 1, L'\0');
    GetWindowTextW(hwnd, title.data(), len + 1);
    title.resize(static_cast<size_t>(len));
    return title;
}

bool IsInterestingWindow(HWND hwnd) {
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) {
        return false;
    }
    RECT rect{};
    if (!GetWindowRect(hwnd, &rect) || rect.right <= rect.left || rect.bottom <= rect.top) {
        return false;
    }
    return GetWindowTextLengthW(hwnd) > 0;
}

BOOL CALLBACK ListWindowsProc(HWND hwnd, LPARAM) {
    if (!IsInterestingWindow(hwnd)) {
        return TRUE;
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    RECT rect{};
    GetWindowRect(hwnd, &rect);
    std::wcout << L"hwnd=0x" << std::hex << reinterpret_cast<uintptr_t>(hwnd) << std::dec
               << L" pid=" << pid
               << L" size=" << (rect.right - rect.left) << L"x" << (rect.bottom - rect.top)
               << L" title=\"" << WindowTitle(hwnd) << L"\"\n";
    return TRUE;
}

struct FindByTitleState {
    std::wstring needle;
    HWND hwnd = nullptr;
};

std::wstring Lower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    return value;
}

BOOL CALLBACK FindByTitleProc(HWND hwnd, LPARAM param) {
    auto* state = reinterpret_cast<FindByTitleState*>(param);
    if (!IsInterestingWindow(hwnd)) {
        return TRUE;
    }
    std::wstring title = Lower(WindowTitle(hwnd));
    if (title.find(state->needle) != std::wstring::npos) {
        state->hwnd = hwnd;
        return FALSE;
    }
    return TRUE;
}

HWND FindWindowByTitle(std::wstring title) {
    FindByTitleState state{Lower(std::move(title)), nullptr};
    EnumWindows(FindByTitleProc, reinterpret_cast<LPARAM>(&state));
    return state.hwnd;
}

uint64_t ParseHandle(const std::wstring& text) {
    size_t index = 0;
    int base = 10;
    if (text.rfind(L"0x", 0) == 0 || text.rfind(L"0X", 0) == 0) {
        index = 2;
        base = 16;
    }
    return std::stoull(text.substr(index), nullptr, base);
}

class UniqueHandle {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(HANDLE handle) : handle_(handle) {}
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    UniqueHandle(UniqueHandle&& other) noexcept : handle_(other.handle_) {
        other.handle_ = nullptr;
    }
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) {
            reset(other.handle_);
            other.handle_ = nullptr;
        }
        return *this;
    }
    ~UniqueHandle() {
        reset();
    }
    HANDLE get() const { return handle_; }
    explicit operator bool() const { return handle_ != nullptr; }
    void reset(HANDLE handle = nullptr) noexcept {
        if (handle_) {
            CloseHandle(handle_);
        }
        handle_ = handle;
    }
private:
    HANDLE handle_ = nullptr;
};

void WriteReadyFileAtomic(const std::filesystem::path& path, HWND hwnd) {
    if (path.empty()) {
        return;
    }
    if (path.has_parent_path()) {
        std::filesystem::create_directories(path.parent_path());
    }
    std::filesystem::path temp = path;
    temp += L".tmp";
    {
        std::ofstream file(temp, std::ios::binary | std::ios::trunc);
        if (!file) {
            throw std::runtime_error("Could not open ready-file temp path");
        }
        file << reinterpret_cast<uintptr_t>(hwnd) << "\n";
    }
    std::error_code ec;
    std::filesystem::remove(path, ec);
    ec.clear();
    std::filesystem::rename(temp, path, ec);
    if (ec) {
        std::filesystem::remove(temp);
        throw std::runtime_error("Could not atomically publish ready-file: " + ec.message());
    }
}

Options ParseOptions(int argc, wchar_t** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        auto needValue = [&](const wchar_t* name) -> std::wstring {
            if (i + 1 >= argc) {
                std::wstringstream ws;
                ws << L"Missing value for " << name;
                throw std::runtime_error(NarrowAscii(ws.str()));
            }
            return argv[++i];
        };

        if (arg == L"--list-windows") {
            options.listWindows = true;
        } else if (arg == L"--source-hwnd") {
            options.sourceHwnd = reinterpret_cast<HWND>(static_cast<uintptr_t>(ParseHandle(needValue(L"--source-hwnd"))));
        } else if (arg == L"--source-title") {
            options.sourceTitle = needValue(L"--source-title");
        } else if (arg == L"--width") {
            options.width = std::clamp<UINT>(std::stoul(needValue(L"--width")), 64, 3840);
        } else if (arg == L"--height") {
            options.height = std::clamp<UINT>(std::stoul(needValue(L"--height")), 64, 2160);
        } else if (arg == L"--seconds") {
            options.seconds = std::stoul(needValue(L"--seconds"));
        } else if (arg == L"--startup-delay-ms") {
            options.startupDelayMs = std::stoul(needValue(L"--startup-delay-ms"));
        } else if (arg == L"--capture-dir") {
            options.captureDir = needValue(L"--capture-dir");
            options.saveCaptures = true;
        } else if (arg == L"--ready-file") {
            options.readyFile = needValue(L"--ready-file");
        } else if (arg == L"--stop-event") {
            options.stopEventName = needValue(L"--stop-event");
        } else if (arg == L"--parent-pid") {
            options.parentPid = std::stoul(needValue(L"--parent-pid"));
        } else if (arg == L"--no-proxy") {
            options.noProxy = true;
        } else if (arg == L"--freeze-source") {
            options.freezeSource = true;
        } else if (arg == L"--native-resolution") {
            options.nativeResolution = true;
        } else if (arg == L"--working-scale") {
            const float value = std::stof(needValue(L"--working-scale"));
            if (!std::isfinite(value) || value < 0.25f || value > 1.0f) {
                throw std::runtime_error("--working-scale must be between 0.25 and 1.0");
            }
            options.workingScale = value;
        } else if (arg == L"--neural-max-height") {
            const unsigned long value = std::stoul(needValue(L"--neural-max-height"));
            if (value < 64 || value > 2160) {
                throw std::runtime_error("--neural-max-height must be between 64 and 2160");
            }
            options.neuralMaxHeight = static_cast<UINT>(value);
        } else if (arg == L"--default-scheduling") {
            options.preciseScheduling = false;
        } else if (arg == L"--repeat-presentations") {
            options.repeatPresentations = true;
        } else if (arg == L"--fixed-neural-feed") {
            options.completionPacing = false;
        } else if (arg == L"--cpu-transport") {
            options.cpuTransport = true;
        } else if (arg == L"--no-hip-timing") {
            options.hipHostTiming = false;
        } else if (arg == L"--no-hip-kernel-timing") {
            options.hipKernelSampling = false;
        } else if (arg == L"--hip-kernel-timing") {
            options.hipKernelSampling = true;
        } else if (arg == L"--warmup-frames") {
            options.warmupFrames = std::stoul(needValue(L"--warmup-frames"));
        } else if (arg == L"--max-fps") {
            const auto value = std::stoul(needValue(L"--max-fps"));
            if (value > 1000) {
                throw std::runtime_error("--max-fps must be between 0 (display-paced) and 1000");
            }
            options.maxFps = static_cast<UINT>(value);
        } else {
            std::wstringstream ws;
            ws << L"Unknown argument: " << arg;
            throw std::runtime_error(NarrowAscii(ws.str()));
        }
    }
    if (options.neuralMaxHeight > 0) {
        // Native-detail mode owns the source-relative resolution choice. Keep
        // the legacy controls available as explicit rollback modes.
        options.nativeResolution = false;
        options.workingScale = 0.0f;
    } else if (options.workingScale > 0.0f && options.workingScale < 0.999f) {
        // Reduced NR must leave the final scaler active. It intentionally
        // takes precedence if both switches were supplied.
        options.nativeResolution = false;
    }
    return options;
}

LRESULT CALLBACK BridgeWindowProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    if (msg == WM_KEYDOWN && wparam == VK_ESCAPE) {
        DestroyWindow(hwnd);
        return 0;
    }
    if (msg == WM_CLOSE) {
        DestroyWindow(hwnd);
        return 0;
    }
    if (msg == WM_DESTROY) {
        if (GetWindowLongPtrW(hwnd, GWLP_USERDATA) == 1) {
            PostQuitMessage(0);
        }
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

HWND CreateRenderWindow(HINSTANCE instance, const wchar_t* title, UINT width, UINT height, bool visible, bool postsQuit) {
    const wchar_t* className = L"DlssNrBridgeWindowClass";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = BridgeWindowProc;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = className;
    RegisterClassExW(&wc);

    RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    HWND hwnd = CreateWindowExW(visible ? WS_EX_NOACTIVATE : 0,
                                className,
                                title,
                                WS_OVERLAPPEDWINDOW,
                                CW_USEDEFAULT,
                                CW_USEDEFAULT,
                                rect.right - rect.left,
                                rect.bottom - rect.top,
                                nullptr,
                                nullptr,
                                instance,
                                nullptr);
    if (!hwnd) {
        throw std::runtime_error("CreateWindowExW failed");
    }
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, postsQuit ? 1 : 0);
    if (visible) {
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);
    }
    return hwnd;
}

inline void SwizzleBgraToRgba(const uint8_t* src, uint8_t* dst, size_t pixelCount) {
    const size_t totalBytes = pixelCount * 4;
    size_t i = 0;
    const __m256i mask256 = _mm256_setr_epi8(
        2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15,
        2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15
    );
    for (; i + 32 <= totalBytes; i += 32) {
        __m256i v = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(src + i));
        v = _mm256_shuffle_epi8(v, mask256);
        _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + i), v);
    }
    if (i + 16 <= totalBytes) {
        const __m128i mask128 = _mm_setr_epi8(
            2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15
        );
        __m128i v = _mm_loadu_si128(reinterpret_cast<const __m128i*>(src + i));
        v = _mm_shuffle_epi8(v, mask128);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(dst + i), v);
        i += 16;
    }
    for (; i < totalBytes; i += 4) {
        dst[i + 0] = src[i + 2];
        dst[i + 1] = src[i + 1];
        dst[i + 2] = src[i + 0];
        dst[i + 3] = src[i + 3];
    }
}

inline void SwizzleRgbaToBgraOpaque(const uint8_t* src, uint8_t* dst, size_t pixelCount) {
    const size_t totalBytes = pixelCount * 4;
    size_t i = 0;
    const __m256i mask256 = _mm256_setr_epi8(
        2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15,
        2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15
    );
    const __m256i alphaMask256 = _mm256_set1_epi32(static_cast<int>(0xFF000000));
    for (; i + 32 <= totalBytes; i += 32) {
        __m256i v = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(src + i));
        v = _mm256_shuffle_epi8(v, mask256);
        v = _mm256_or_si256(v, alphaMask256);
        _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + i), v);
    }
    if (i + 16 <= totalBytes) {
        const __m128i mask128 = _mm_setr_epi8(
            2, 1, 0, 3,  6, 5, 4, 7,  10, 9, 8, 11,  14, 13, 12, 15
        );
        const __m128i alphaMask128 = _mm_set1_epi32(static_cast<int>(0xFF000000));
        __m128i v = _mm_loadu_si128(reinterpret_cast<const __m128i*>(src + i));
        v = _mm_shuffle_epi8(v, mask128);
        v = _mm_or_si128(v, alphaMask128);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(dst + i), v);
        i += 16;
    }
    for (; i < totalBytes; i += 4) {
        dst[i + 0] = src[i + 2];
        dst[i + 1] = src[i + 1];
        dst[i + 2] = src[i + 0];
        dst[i + 3] = 255;
    }
}

class WindowCapture {
public:
    explicit WindowCapture(HWND source) : source_(source) {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        if (!wgc::GraphicsCaptureSession::IsSupported()) {
            throw std::runtime_error("Windows Graphics Capture is not supported on this OS");
        }
        CreateDevice();
        CreateItem();
        const auto size = item_.Size();
        lastSize_ = size;
        framePool_ = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            device_,
            wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            size);
        session_ = framePool_.CreateCaptureSession(item_);
        session_.IsCursorCaptureEnabled(false);
        closedRevoker_ = item_.Closed(winrt::auto_revoke, [this](auto const&, auto const&) {
            closed_.store(true);
        });
        session_.StartCapture();
    }

    bool closed() const {
        return closed_.load();
    }

    ID3D11Device* device() const { return d3dDevice_.Get(); }
    ID3D11DeviceContext* context() const { return d3dContext_.Get(); }

    // The caller retains the WGC frame until the input-ready fence and the
    // neural feed copy have completed. The capture pool cannot recycle a
    // surface while our GPU is still reading it.
    wgc::Direct3D11CaptureFrame TryCaptureTexture(ComPtr<ID3D11Texture2D>& texture) {
        texture.Reset();
        if (!IsWindow(source_) || IsIconic(source_)) {
            closed_.store(true);
            return nullptr;
        }
        auto frame = framePool_.TryGetNextFrame();
        if (!frame) return nullptr;
        if (auto newer = framePool_.TryGetNextFrame()) {
            frame.Close();
            frame = std::move(newer);
        }
        const auto size = frame.ContentSize();
        if (size.Width <= 0 || size.Height <= 0) {
            frame.Close();
            return nullptr;
        }
        if (size.Width != lastSize_.Width || size.Height != lastSize_.Height) {
            frame.Close();
            throw std::runtime_error("Native source dimensions changed; restart scaling at the new resolution");
        }
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        Check(access->GetInterface(IID_PPV_ARGS(&texture)), "Get GPU capture texture");
        return frame;
    }

    bool TryCapture(FramePixels& result) {
        if (!IsWindow(source_) || IsIconic(source_)) {
            closed_.store(true);
            return false;
        }
        auto frame = framePool_.TryGetNextFrame();
        if (!frame) {
            return false;
        }
        // The capture pool has two slots. Prefer the newest complete frame rather
        // than displaying a queued older frame after a slow neural evaluation.
        if (auto newer = framePool_.TryGetNextFrame()) {
            frame.Close();
            frame = std::move(newer);
        }
        const auto size = frame.ContentSize();
        if (size.Width <= 0 || size.Height <= 0) {
            return false;
        }
        if (size.Width != lastSize_.Width || size.Height != lastSize_.Height) {
            lastSize_ = size;
            framePool_.Recreate(device_, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        }

        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> texture;
        Check(access->GetInterface(IID_PPV_ARGS(&texture)), "IDirect3DDxgiInterfaceAccess::GetInterface");

        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        if (!staging_ || stagingDesc_.Width != desc.Width || stagingDesc_.Height != desc.Height || stagingDesc_.Format != desc.Format) {
            stagingDesc_ = desc;
            stagingDesc_.BindFlags = 0;
            stagingDesc_.MiscFlags = 0;
            stagingDesc_.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc_.Usage = D3D11_USAGE_STAGING;
            stagingDesc_.MipLevels = 1;
            stagingDesc_.ArraySize = 1;
            staging_.Reset();
            Check(d3dDevice_->CreateTexture2D(&stagingDesc_, nullptr, &staging_), "CreateTexture2D staging");
        }

        result.width = desc.Width;
        result.height = desc.Height;
        // Reuse caller-owned storage; allocate before mapping so an allocation
        // failure cannot leave the staging texture mapped.
        result.rgba.resize(static_cast<size_t>(result.width) * result.height * 4);
        d3dContext_->CopyResource(staging_.Get(), texture.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(d3dContext_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map staging");
        const size_t rowBytes = static_cast<size_t>(result.width) * 4;
        if (mapped.RowPitch == rowBytes) {
            SwizzleBgraToRgba(static_cast<const uint8_t*>(mapped.pData), result.rgba.data(), static_cast<size_t>(result.width) * result.height);
        } else {
            for (UINT y = 0; y < result.height; ++y) {
                const auto* src = static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                auto* dst = result.rgba.data() + static_cast<size_t>(y) * rowBytes;
                SwizzleBgraToRgba(src, dst, result.width);
            }
        }
        d3dContext_->Unmap(staging_.Get(), 0);
        return true;
    }

private:
    void CreateDevice() {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        D3D_FEATURE_LEVEL chosen{};
        Check(D3D11CreateDevice(nullptr,
                                D3D_DRIVER_TYPE_HARDWARE,
                                nullptr,
                                flags,
                                levels,
                                static_cast<UINT>(std::size(levels)),
                                D3D11_SDK_VERSION,
                                &d3dDevice_,
                                &chosen,
                                &d3dContext_),
              "D3D11CreateDevice");
        ComPtr<IDXGIDevice> dxgiDevice;
        Check(d3dDevice_.As(&dxgiDevice), "Query IDXGIDevice");
        winrt::com_ptr<IInspectable> inspectable;
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()), "CreateDirect3D11DeviceFromDXGIDevice");
        device_ = inspectable.as<wgd11::IDirect3DDevice>();
    }

    void CreateItem() {
        auto factory = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        wgc::GraphicsCaptureItem item{nullptr};
        Check(factory->CreateForWindow(source_, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(item)), "CreateForWindow");
        item_ = item;
    }

    HWND source_ = nullptr;
    ComPtr<ID3D11Device> d3dDevice_;
    ComPtr<ID3D11DeviceContext> d3dContext_;
    wgd11::IDirect3DDevice device_{nullptr};
    wgc::GraphicsCaptureItem item_{nullptr};
    wgc::Direct3D11CaptureFramePool framePool_{nullptr};
    wgc::GraphicsCaptureSession session_{nullptr};
    wgc::GraphicsCaptureItem::Closed_revoker closedRevoker_;
    wg::SizeInt32 lastSize_{};
    std::atomic<bool> closed_{false};
    ComPtr<ID3D11Texture2D> staging_;
    D3D11_TEXTURE2D_DESC stagingDesc_{};
};

FramePixels ScaleToFit(const FramePixels& src, UINT outWidth, UINT outHeight) {
    if (src.width == outWidth && src.height == outHeight) {
        return src;
    }
    FramePixels out;
    out.width = outWidth;
    out.height = outHeight;
    out.rgba.assign(static_cast<size_t>(outWidth) * outHeight * 4, 0);
    if (src.rgba.empty() || src.width == 0 || src.height == 0) {
        return out;
    }
    const double scale = std::min(static_cast<double>(outWidth) / src.width, static_cast<double>(outHeight) / src.height);
    const UINT drawWidth = std::max<UINT>(1, static_cast<UINT>(src.width * scale));
    const UINT drawHeight = std::max<UINT>(1, static_cast<UINT>(src.height * scale));
    const UINT offsetX = (outWidth - drawWidth) / 2;
    const UINT offsetY = (outHeight - drawHeight) / 2;
    for (UINT y = 0; y < drawHeight; ++y) {
        const UINT sy = std::min<UINT>(src.height - 1, static_cast<UINT>(static_cast<uint64_t>(y) * src.height / drawHeight));
        for (UINT x = 0; x < drawWidth; ++x) {
            const UINT sx = std::min<UINT>(src.width - 1, static_cast<UINT>(static_cast<uint64_t>(x) * src.width / drawWidth));
            const size_t srcIndex = (static_cast<size_t>(sy) * src.width + sx) * 4;
            const size_t dstIndex = (static_cast<size_t>(y + offsetY) * outWidth + (x + offsetX)) * 4;
            std::memcpy(out.rgba.data() + dstIndex, src.rgba.data() + srcIndex, 4);
        }
    }
    return out;
}

void ValidateNativeDimensions(const FramePixels& frame, UINT width, UINT height) {
    if (frame.width != width || frame.height != height) {
        std::ostringstream os;
        os << "Native-resolution source dimensions changed from " << width << "x" << height
           << " to " << frame.width << "x" << frame.height;
        throw std::runtime_error(os.str());
    }
}

FramePixels PrepareInputFrame(const FramePixels& src, const Options& options) {
    if (options.nativeResolution) {
        ValidateNativeDimensions(src, options.width, options.height);
        return src;
    }
    return ScaleToFit(src, options.width, options.height);
}

UINT ScaledEvenDimension(UINT source, float scale) {
    UINT result = static_cast<UINT>(std::floor(static_cast<double>(source) * scale));
    result = std::max<UINT>(64, result);
    result &= ~1u;
    return std::max<UINT>(64, result);
}

std::pair<UINT, UINT> FitNeuralDimensions(UINT sourceWidth, UINT sourceHeight, UINT maxHeight) {
    if (sourceWidth == 0 || sourceHeight == 0 || maxHeight < 64) {
        throw std::runtime_error("Cannot derive neural dimensions from an empty source");
    }
    const double scale = std::min({1.0,
                                   static_cast<double>(maxHeight) / sourceHeight,
                                   3840.0 / sourceWidth});
    if (scale >= 0.999999) {
        return {sourceWidth, sourceHeight};
    }
    auto nearestEven = [](double value) {
        UINT result = static_cast<UINT>(std::lround(value / 2.0) * 2.0);
        return std::max<UINT>(64, result);
    };
    UINT width = nearestEven(sourceWidth * scale);
    UINT height = nearestEven(sourceHeight * scale);
    while (height > maxHeight && height > 64) height -= 2;
    while (width > 3840 && width > 64) width -= 2;
    return {width, height};
}

FramePixels ComposeNativeDetailCpu(const FramePixels& nativeSource,
                                   const FramePixels& neuralInput,
                                   const FramePixels& neuralOutput,
                                   float strength,
                                   bool enabled) {
    if (!enabled || strength <= 0.0f) {
        return nativeSource;
    }
    if (neuralOutput.width == 0 || neuralOutput.height == 0 || neuralOutput.rgba.empty()) {
        throw std::runtime_error("Neural output is empty during native-detail fallback");
    }
    if (neuralInput.width != neuralOutput.width || neuralInput.height != neuralOutput.height ||
        neuralInput.rgba.size() != neuralOutput.rgba.size()) {
        throw std::runtime_error("Neural input/output dimensions differ during native-detail fallback");
    }
    FramePixels result = nativeSource;
    const float amount = std::clamp(strength, 0.0f, 4.0f);
    auto sampleAt = [](const FramePixels& frame, UINT x, UINT y, size_t channel) -> int {
        x = std::min(x, frame.width - 1);
        y = std::min(y, frame.height - 1);
        return frame.rgba[(static_cast<size_t>(y) * frame.width + x) * 4 + channel];
    };
    auto neuralAt = [&](UINT x, UINT y, size_t channel) -> int {
        x = std::min(x, neuralOutput.width - 1);
        y = std::min(y, neuralOutput.height - 1);
        return sampleAt(neuralOutput, x, y, channel);
    };
    for (UINT y = 0; y < nativeSource.height; ++y) {
        const UINT ny = std::min<UINT>(neuralOutput.height - 1,
            static_cast<UINT>(static_cast<uint64_t>(y) * neuralOutput.height / nativeSource.height));
        for (UINT x = 0; x < nativeSource.width; ++x) {
            const UINT nx = std::min<UINT>(neuralOutput.width - 1,
                static_cast<UINT>(static_cast<uint64_t>(x) * neuralOutput.width / nativeSource.width));
            const size_t offset = (static_cast<size_t>(y) * nativeSource.width + x) * 4;
            for (size_t channel = 0; channel < 3; ++channel) {
                const int neural = neuralAt(nx, ny, channel);
                const int input = sampleAt(neuralInput, nx, ny, channel);
                const int value = static_cast<int>(nativeSource.rgba[offset + channel]) +
                                  static_cast<int>(std::lround((neural - input) * amount));
                result.rgba[offset + channel] = static_cast<uint8_t>(std::clamp(value, 0, 255));
            }
        }
    }
    return result;
}

const FramePixels& SelectInputFrame(const FramePixels& src,
                                   const Options& options,
                                   std::optional<FramePixels>& prepared,
                                   bool sourceChanged) {
    if (options.nativeResolution) {
        ValidateNativeDimensions(src, options.width, options.height);
        return src;
    }
    if (src.width == options.width && src.height == options.height) {
        return src;
    }
    if (!prepared || sourceChanged) {
        prepared = PrepareInputFrame(src, options);
    }
    return *prepared;
}

class D3D12Presenter {
public:
    D3D12Presenter(HWND hwnd, UINT width, UINT height) : hwnd_(hwnd), width_(width), height_(height) {
        CreateDevice();
        CreateSwapChain();
        CreateResources();
    }

    ~D3D12Presenter() {
        if (fenceEvent_) {
            try {
                WaitForGpu(5000);
            } catch (const std::exception& ex) {
                bridge_gpu::StopAfterUnconfirmedGpuCompletion(ex.what());
            }
            CloseHandle(fenceEvent_);
        }
        if (upload_ && uploadMapped_) {
            D3D12_RANGE writeRange{0, 0};
            upload_->Unmap(0, &writeRange);
            uploadMapped_ = nullptr;
        }
    }

    void Present(const FramePixels& frame, bool inputChanged = true) {
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        // The upload heap retains its bytes. Only rewrite it for a newly
        // captured/prepared input; every neural feed Present still happens.
        if (inputChanged || !uploadInitialized_) {
            Upload(frame);
            uploadInitialized_ = true;
        }
        ResetCommands();
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_COPY_DEST);
        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = backBuffers_[frameIndex_].Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION src{};
        src.pResource = upload_.Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint.Footprint.Format = kFormat;
        src.PlacedFootprint.Footprint.Width = width_;
        src.PlacedFootprint.Footprint.Height = height_;
        src.PlacedFootprint.Footprint.Depth = 1;
        src.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(rowPitch_);
        commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands(5000);
        // This private feed is hidden after warmup. Only the visible presenter
        // should pace to the monitor; a second vblank wait adds latency.
        HRESULT present = swapChain_->Present(0, 0);
        if (FAILED(present)) {
            Check(present, "Present");
        }
        ++presentCount_;
    }

    ID3D12Device* device() const { return device_.Get(); }

    void DrainForShutdown() { WaitForGpu(5000); }

    void PresentGpu(bridge_gpu::FrameTransport& transport) {
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        // The D3D11 signal has already been flushed before this queue wait.
        Check(queue_->Wait(transport.inputReadyFence(), transport.inputReadyValue()),
              "Wait for GPU capture texture");
        // Feed and output must not reset the same in-flight allocator. The
        // previous Compose normally completed this fence transitively; retain
        // a bounded check before reuse rather than relying on call ordering.
        WaitForFenceValue(gpuFeedFenceValue_, 5000);
        Check(gpuFeedAllocator_->Reset(), "GPU feed allocator Reset");
        Check(gpuFeedCommandList_->Reset(gpuFeedAllocator_.Get(), nullptr), "GPU feed commandList Reset");
        auto acquire = Transition(transport.inputResource(), D3D12_RESOURCE_STATE_COMMON,
                                  D3D12_RESOURCE_STATE_COPY_SOURCE);
        gpuFeedCommandList_->ResourceBarrier(1, &acquire);
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_COPY_DEST, gpuFeedCommandList_.Get());
        gpuFeedCommandList_->CopyResource(backBuffers_[frameIndex_].Get(), transport.inputResource());
        auto release = Transition(transport.inputResource(), D3D12_RESOURCE_STATE_COPY_SOURCE,
                                  D3D12_RESOURCE_STATE_COMMON);
        gpuFeedCommandList_->ResourceBarrier(1, &release);
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT, gpuFeedCommandList_.Get());
        Check(gpuFeedCommandList_->Close(), "Close GPU feed commandList");
        ID3D12CommandList* lists[] = {gpuFeedCommandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        gpuFeedFenceValue_ = SignalGpu();
        // Present and the following output copy use this same direct queue.
        // Queue ordering supplies the dependency without blocking the CPU
        // here. The caller retains its WGC frame through Compose's completed
        // consumer fence (or drains both devices before releasing on failure).
        Check(swapChain_->Present(0, 0), "Present GPU neural feed");
        uploadInitialized_ = false;
        ++presentCount_;
    }

    void CopyOutputGpu(bridge_gpu::FrameTransport& transport) {
        if (transport.consumerDoneValue() != 0) {
            Check(queue_->Wait(transport.consumerDoneFence(), transport.consumerDoneValue()),
                  "Wait for prior GPU output consumer");
        }
        transport.BeginOutputWrite();
        ResetCommands();
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_COPY_SOURCE);
        auto acquire = Transition(transport.outputResource(), D3D12_RESOURCE_STATE_COMMON,
                                  D3D12_RESOURCE_STATE_COPY_DEST);
        commandList_->ResourceBarrier(1, &acquire);
        commandList_->CopyResource(transport.outputResource(), backBuffers_[frameIndex_].Get());
        auto release = Transition(transport.outputResource(), D3D12_RESOURCE_STATE_COPY_DEST,
                                  D3D12_RESOURCE_STATE_COMMON);
        commandList_->ResourceBarrier(1, &release);
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT);
        // Compose waits on outputReady on D3D11, then completes its bounded
        // consumer fence before returning. That transitively completes this
        // copy, the preceding feed and both allocators. The caller must Compose
        // (or DrainForShutdown on error) before releasing the capture/shared
        // resources. ResetCommands additionally checks its own reuse fence.
        SubmitCommands();
        const UINT64 outputValue = transport.NextOutputValue();
        Check(queue_->Signal(transport.outputReadyFence(), outputValue), "Signal GPU neural output copy");
        transport.WaitForOutput(outputValue);
    }

    FramePixels CaptureBackBuffer() {
        FramePixels out;
        CaptureBackBuffer(out);
        return out;
    }

    void CaptureBackBuffer(FramePixels& out) {
        out.width = width_;
        out.height = height_;
        out.rgba.resize(static_cast<size_t>(width_) * height_ * 4);
        ResetCommands();
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_COPY_SOURCE);
        D3D12_TEXTURE_COPY_LOCATION src{};
        src.pResource = backBuffers_[frameIndex_].Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = readback_.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint.Footprint.Format = kFormat;
        dst.PlacedFootprint.Footprint.Width = width_;
        dst.PlacedFootprint.Footprint.Height = height_;
        dst.PlacedFootprint.Footprint.Depth = 1;
        dst.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(rowPitch_);
        commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands(5000);
        D3D12_RANGE readRange{0, rowPitch_ * height_};
        void* mapped = nullptr;
        Check(readback_->Map(0, &readRange, &mapped), "Map readback");
        const auto* bytes = static_cast<const uint8_t*>(mapped);
        const size_t rowBytes = static_cast<size_t>(width_) * 4;
        if (rowPitch_ == rowBytes) {
            std::memcpy(out.rgba.data(), bytes, rowBytes * height_);
        } else {
            for (UINT y = 0; y < height_; ++y) {
                std::memcpy(out.rgba.data() + static_cast<size_t>(y) * rowBytes,
                            bytes + static_cast<size_t>(y) * rowPitch_,
                            rowBytes);
            }
        }
        D3D12_RANGE writeRange{0, 0};
        readback_->Unmap(0, &writeRange);
    }

    uint64_t presentCount() const {
        return presentCount_;
    }

private:
    void CreateDevice() {
        Check(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory_)), "CreateDXGIFactory2");
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0; factory_->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i) {
            DXGI_ADAPTER_DESC1 desc{};
            adapter->GetDesc1(&desc);
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) {
                continue;
            }
            if (SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device_)))) {
                break;
            }
        }
        if (!device_) {
            Check(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device_)), "D3D12CreateDevice");
        }
        D3D12_COMMAND_QUEUE_DESC q{};
        q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device_->CreateCommandQueue(&q, IID_PPV_ARGS(&queue_)), "CreateCommandQueue");
        Check(device_->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator_)), "CreateCommandAllocator");
        Check(device_->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator_.Get(), nullptr, IID_PPV_ARGS(&commandList_)), "CreateCommandList");
        Check(commandList_->Close(), "initial Close");
        Check(device_->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&gpuFeedAllocator_)), "Create GPU feed allocator");
        Check(device_->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, gpuFeedAllocator_.Get(), nullptr, IID_PPV_ARGS(&gpuFeedCommandList_)), "Create GPU feed commandList");
        Check(gpuFeedCommandList_->Close(), "initial GPU feed Close");
        Check(device_->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence_)), "CreateFence");
        fenceEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!fenceEvent_) {
            throw std::runtime_error("CreateEventW failed");
        }
    }

    void CreateSwapChain() {
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = width_;
        desc.Height = height_;
        desc.Format = kFormat;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = kBufferCount;
        desc.SampleDesc.Count = 1;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> sc1;
        Check(factory_->CreateSwapChainForHwnd(queue_.Get(), hwnd_, &desc, nullptr, nullptr, &sc1), "CreateSwapChainForHwnd");
        Check(sc1.As(&swapChain_), "Query IDXGISwapChain3");
        for (UINT i = 0; i < kBufferCount; ++i) {
            Check(swapChain_->GetBuffer(i, IID_PPV_ARGS(&backBuffers_[i])), "GetBuffer");
            bufferStates_[i] = D3D12_RESOURCE_STATE_PRESENT;
        }
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
    }

    void CreateResources() {
        rowPitch_ = Align(static_cast<UINT64>(width_) * 4, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = rowPitch_ * height_;
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        D3D12_HEAP_PROPERTIES uploadHeap{};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
        Check(device_->CreateCommittedResource(&uploadHeap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&upload_)), "Create upload");
        D3D12_HEAP_PROPERTIES readbackHeap{};
        readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
        Check(device_->CreateCommittedResource(&readbackHeap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&readback_)), "Create readback");

        D3D12_RANGE readRange{0, 0};
        Check(upload_->Map(0, &readRange, reinterpret_cast<void**>(&uploadMapped_)), "Map persistent upload");
    }

    void Upload(const FramePixels& frame) {
        if (!uploadMapped_) {
            throw std::runtime_error("Upload buffer is not mapped");
        }
        if (frame.width != width_ || frame.height != height_ ||
            frame.rgba.size() != static_cast<size_t>(width_) * height_ * 4) {
            throw std::runtime_error("Neural presenter received an unexpected frame size");
        }
        const auto* src = frame.rgba.data();
        auto* dst = uploadMapped_;
        const size_t rowBytes = static_cast<size_t>(width_) * 4;
        if (rowPitch_ == rowBytes) {
            std::memcpy(dst, src, rowBytes * height_);
        } else {
            for (UINT y = 0; y < height_; ++y) {
                std::memcpy(dst + static_cast<size_t>(y) * rowPitch_,
                            src + static_cast<size_t>(y) * rowBytes,
                            rowBytes);
            }
        }
    }

    void ResetCommands() {
        WaitForFenceValue(commandsFenceValue_, 5000);
        Check(allocator_->Reset(), "allocator Reset");
        Check(commandList_->Reset(allocator_.Get(), nullptr), "commandList Reset");
    }

    void SubmitCommands() {
        Check(commandList_->Close(), "Close commandList");
        ID3D12CommandList* lists[] = {commandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        commandsFenceValue_ = SignalGpu();
    }

    void ExecuteCommands(DWORD timeoutMs) {
        SubmitCommands();
        WaitForFenceValue(commandsFenceValue_, timeoutMs);
    }

    UINT64 SignalGpu() {
        const UINT64 value = ++fenceValue_;
        Check(queue_->Signal(fence_.Get(), value), "fence Signal");
        return value;
    }

    void WaitForGpu(DWORD timeoutMs) {
        WaitForFenceValue(SignalGpu(), timeoutMs);
    }

    void WaitForFenceValue(UINT64 value, DWORD timeoutMs) {
        if (value == 0) return;
        auto completedValue = [&]() {
            const UINT64 completed = fence_->GetCompletedValue();
            if (completed == UINT64_MAX) {
                Check(device_->GetDeviceRemovedReason(), "D3D12 device removed during fence wait");
                throw std::runtime_error("D3D12 fence reported device removal");
            }
            return completed;
        };
        if (completedValue() < value) {
            Check(fence_->SetEventOnCompletion(value, fenceEvent_), "SetEventOnCompletion");
            DWORD wait = WaitForSingleObject(fenceEvent_, timeoutMs);
            if (wait != WAIT_OBJECT_0) {
                throw std::runtime_error("Timed out waiting for D3D12 fence");
            }
            if (completedValue() < value) {
                throw std::runtime_error("D3D12 fence signaled before submitted work completed");
            }
        }
    }

    void SetBackBufferState(UINT index, D3D12_RESOURCE_STATES state,
                           ID3D12GraphicsCommandList* commands = nullptr) {
        if (bufferStates_[index] == state) {
            return;
        }
        auto barrier = Transition(backBuffers_[index].Get(), bufferStates_[index], state);
        (commands ? commands : commandList_.Get())->ResourceBarrier(1, &barrier);
        bufferStates_[index] = state;
    }

    HWND hwnd_ = nullptr;
    UINT width_ = 0;
    UINT height_ = 0;
    ComPtr<IDXGIFactory4> factory_;
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<IDXGISwapChain3> swapChain_;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> commandList_;
    ComPtr<ID3D12CommandAllocator> gpuFeedAllocator_;
    ComPtr<ID3D12GraphicsCommandList> gpuFeedCommandList_;
    ComPtr<ID3D12Fence> fence_;
    HANDLE fenceEvent_ = nullptr;
    UINT64 fenceValue_ = 0;
    UINT64 commandsFenceValue_ = 0;
    UINT64 gpuFeedFenceValue_ = 0;
    ComPtr<ID3D12Resource> backBuffers_[kBufferCount];
    D3D12_RESOURCE_STATES bufferStates_[kBufferCount]{};
    ComPtr<ID3D12Resource> upload_;
    uint8_t* uploadMapped_ = nullptr;
    bool uploadInitialized_ = false;
    ComPtr<ID3D12Resource> readback_;
    UINT64 rowPitch_ = 0;
    UINT frameIndex_ = 0;
    uint64_t presentCount_ = 0;
};




struct FrameStats {
    bool nonblack = false;
    double meanLuma = 0.0;
};

FrameStats AnalyzeFrame(const FramePixels& frame) {
    FrameStats stats;
    if (frame.rgba.empty() || frame.width == 0 || frame.height == 0) {
        return stats;
    }
    uint64_t sum = 0;
    for (size_t i = 0; i + 2 < frame.rgba.size(); i += 4) {
        const uint8_t r = frame.rgba[i];
        const uint8_t g = frame.rgba[i + 1];
        const uint8_t b = frame.rgba[i + 2];
        if (r != 0 || g != 0 || b != 0) {
            stats.nonblack = true;
        }
        sum += static_cast<uint64_t>(r) + g + b;
    }
    const size_t pixelCount = frame.rgba.size() / 4;
    stats.meanLuma = pixelCount == 0 ? 0.0 : static_cast<double>(sum) / (static_cast<double>(pixelCount) * 3.0);
    return stats;
}

bool SourceMeaningfullyNonBlack(const FramePixels& frame) {
    if (frame.rgba.empty() || frame.width == 0 || frame.height == 0) {
        return false;
    }
    // mean RGB >= 2 is exactly sum RGB >= 6 * pixel count. Since all
    // contributions are nonnegative, normal bright frames can return early
    // without scanning every pixel or changing the black-frame threshold.
    const uint64_t threshold = 6ULL * (frame.rgba.size() / 4);
    uint64_t sum = 0;
    for (size_t i = 0; i + 2 < frame.rgba.size(); i += 4) {
        sum += static_cast<uint64_t>(frame.rgba[i]) + frame.rgba[i + 1] + frame.rgba[i + 2];
        if (sum >= threshold) {
            return true;
        }
    }
    return false;
}

bool FrameHasNonBlackPixels(const FramePixels& frame) {
    for (size_t i = 0; i + 2 < frame.rgba.size(); i += 4) {
        if (frame.rgba[i] != 0 || frame.rgba[i + 1] != 0 || frame.rgba[i + 2] != 0) {
            return true;
        }
    }
    return false;
}

std::string LowerAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

struct RuntimeHealth {
    bool logPresent = false;
    bool completedJob = false;
    bool fatal = false;
    std::string detail;
};

bool UsesAsyncBackbufferRuntime() {
    const auto ini = std::filesystem::absolute("dlssnr_on_amd.ini");
    auto setting = [&](const wchar_t* name) {
        return GetPrivateProfileIntW(L"DlssNrOnAmd", name, -1, ini.c_str());
    };
    // Missing or changed settings fail closed to the original feed cadence.
    // In particular, never defer a producer needed by inline/FSR work.
    return setting(L"Enabled") == 1 && setting(L"Inline") == 0 &&
           setting(L"UseFsrInputs") == 0 && setting(L"Interop") == 1;
}


void ResetRuntimeLogForCurrentLaunch() {
    const std::filesystem::path logPath = "dlssnr_on_amd.log";
    std::error_code ec;
    if (!std::filesystem::exists(logPath, ec)) {
        return;
    }
    ec.clear();
    std::filesystem::path archived = logPath;
    archived += L".previous." + std::to_wstring(GetCurrentProcessId());
    std::filesystem::remove(archived, ec);
    ec.clear();
    std::filesystem::rename(logPath, archived, ec);
    if (ec) {
        ec.clear();
        std::filesystem::remove(logPath, ec);
        if (ec || std::filesystem::exists(logPath)) {
            throw std::runtime_error("Could not clear stale dlssnr_on_amd.log before proxy startup");
        }
    }
}

RuntimeHealth ReadRuntimeHealth() {
    RuntimeHealth health;
    std::ifstream file("dlssnr_on_amd.log", std::ios::binary);
    if (!file) {
        health.detail = "dlssnr_on_amd.log is not present yet";
        return health;
    }
    health.logPresent = true;
    std::ostringstream buffer;
    buffer << file.rdbuf();
    const std::string lower = LowerAscii(buffer.str());
    health.completedJob = std::regex_search(lower, std::regex(R"(network[ \t]+job[ \t]+[0-9]+[ \t]+done)"));
    health.fatal = lower.find("gpu errors") != std::string::npos ||
                   lower.find("invalid kernel file") != std::string::npos ||
                   lower.find("fault") != std::string::npos ||
                   lower.find("crash") != std::string::npos ||
                   (lower.find("100.00%") != std::string::npos && lower.find("zero") != std::string::npos);
    if (health.fatal) {
        health.detail = "runtime log contains a fatal GPU/kernel/fault/crash/zero-output marker";
    } else if (!health.completedJob) {
        health.detail = "runtime log has no completed network job yet";
    } else {
        health.detail = "runtime log has completed network jobs and no fatal markers";
    }
    return health;
}

void RequireHealthyRuntimeOrThrow(const FramePixels& nrFrame) {
    const RuntimeHealth health = ReadRuntimeHealth();
    if (!health.logPresent || !health.completedJob || health.fatal) {
        throw std::runtime_error("DLSS-NR runtime is not healthy: " + health.detail);
    }
    if (!FrameHasNonBlackPixels(nrFrame)) {
        throw std::runtime_error("DLSS-NR readback is black/all-zero after completed warmup");
    }
}

FramePixels BlendFrames(const FramePixels& original, const FramePixels& nr, float strength, bool enabled) {
    if (!enabled || strength <= 0.0f || nr.rgba.size() != original.rgba.size() || nr.width != original.width || nr.height != original.height) {
        return original;
    }
    const float amount = std::clamp(strength, 0.0f, 1.0f);
    if (amount <= 0.0f) {
        return original;
    }
    const uint32_t alpha = static_cast<uint32_t>(std::lround(amount * 256.0f));
    if (alpha == 0) {
        return original;
    }

    FramePixels out;
    out.width = original.width;
    out.height = original.height;
    const size_t totalBytes = original.rgba.size();
    out.rgba.resize(totalBytes);

    const uint8_t* pOrig = original.rgba.data();
    const uint8_t* pNr = nr.rgba.data();
    uint8_t* pOut = out.rgba.data();

    if (alpha >= 256) {
        const __m256i alphaMask256 = _mm256_set1_epi32(static_cast<int>(0xFF000000));
        size_t i = 0;
        for (; i + 32 <= totalBytes; i += 32) {
            __m256i v = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pNr + i));
            v = _mm256_or_si256(v, alphaMask256);
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(pOut + i), v);
        }
        if (i + 16 <= totalBytes) {
            const __m128i alphaMask128 = _mm_set1_epi32(static_cast<int>(0xFF000000));
            __m128i v = _mm_loadu_si128(reinterpret_cast<const __m128i*>(pNr + i));
            v = _mm_or_si128(v, alphaMask128);
            _mm_storeu_si128(reinterpret_cast<__m128i*>(pOut + i), v);
            i += 16;
        }
        for (; i < totalBytes; i += 4) {
            pOut[i + 0] = pNr[i + 0];
            pOut[i + 1] = pNr[i + 1];
            pOut[i + 2] = pNr[i + 2];
            pOut[i + 3] = 255;
        }
        return out;
    }

    const uint32_t invAlpha = 256 - alpha;
    const __m256i vAlpha = _mm256_set1_epi16(static_cast<short>(alpha));
    const __m256i vInvAlpha = _mm256_set1_epi16(static_cast<short>(invAlpha));
    const __m256i vRound = _mm256_set1_epi16(128);
    const __m256i zero256 = _mm256_setzero_si256();
    const __m256i alphaMask256 = _mm256_set1_epi32(static_cast<int>(0xFF000000));

    size_t i = 0;
    for (; i + 32 <= totalBytes; i += 32) {
        const __m256i vOrig = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pOrig + i));
        const __m256i vNrVal = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pNr + i));

        const __m256i orig_lo = _mm256_unpacklo_epi8(vOrig, zero256);
        const __m256i orig_hi = _mm256_unpackhi_epi8(vOrig, zero256);
        const __m256i nr_lo = _mm256_unpacklo_epi8(vNrVal, zero256);
        const __m256i nr_hi = _mm256_unpackhi_epi8(vNrVal, zero256);

        __m256i res_lo = _mm256_mullo_epi16(orig_lo, vInvAlpha);
        res_lo = _mm256_add_epi16(res_lo, _mm256_mullo_epi16(nr_lo, vAlpha));
        res_lo = _mm256_add_epi16(res_lo, vRound);
        res_lo = _mm256_srli_epi16(res_lo, 8);

        __m256i res_hi = _mm256_mullo_epi16(orig_hi, vInvAlpha);
        res_hi = _mm256_add_epi16(res_hi, _mm256_mullo_epi16(nr_hi, vAlpha));
        res_hi = _mm256_add_epi16(res_hi, vRound);
        res_hi = _mm256_srli_epi16(res_hi, 8);

        __m256i packed = _mm256_packus_epi16(res_lo, res_hi);
        packed = _mm256_or_si256(packed, alphaMask256);

        _mm256_storeu_si256(reinterpret_cast<__m256i*>(pOut + i), packed);
    }

    if (i + 16 <= totalBytes) {
        const __m128i vAlpha128 = _mm_set1_epi16(static_cast<short>(alpha));
        const __m128i vInvAlpha128 = _mm_set1_epi16(static_cast<short>(invAlpha));
        const __m128i vRound128 = _mm_set1_epi16(128);
        const __m128i zero128 = _mm_setzero_si128();
        const __m128i alphaMask128 = _mm_set1_epi32(static_cast<int>(0xFF000000));

        const __m128i vOrig = _mm_loadu_si128(reinterpret_cast<const __m128i*>(pOrig + i));
        const __m128i vNrVal = _mm_loadu_si128(reinterpret_cast<const __m128i*>(pNr + i));

        const __m128i orig_lo = _mm_unpacklo_epi8(vOrig, zero128);
        const __m128i orig_hi = _mm_unpackhi_epi8(vOrig, zero128);
        const __m128i nr_lo = _mm_unpacklo_epi8(vNrVal, zero128);
        const __m128i nr_hi = _mm_unpackhi_epi8(vNrVal, zero128);

        __m128i res_lo = _mm_mullo_epi16(orig_lo, vInvAlpha128);
        res_lo = _mm_add_epi16(res_lo, _mm_mullo_epi16(nr_lo, vAlpha128));
        res_lo = _mm_add_epi16(res_lo, vRound128);
        res_lo = _mm_srli_epi16(res_lo, 8);

        __m128i res_hi = _mm_mullo_epi16(orig_hi, vInvAlpha128);
        res_hi = _mm_add_epi16(res_hi, _mm_mullo_epi16(nr_hi, vAlpha128));
        res_hi = _mm_add_epi16(res_hi, vRound128);
        res_hi = _mm_srli_epi16(res_hi, 8);

        __m128i packed = _mm_packus_epi16(res_lo, res_hi);
        packed = _mm_or_si128(packed, alphaMask128);

        _mm_storeu_si128(reinterpret_cast<__m128i*>(pOut + i), packed);
        i += 16;
    }

    for (; i < totalBytes; i += 4) {
        for (size_t ch = 0; ch < 3; ++ch) {
            const uint32_t a = pOrig[i + ch];
            const uint32_t b = pNr[i + ch];
            pOut[i + ch] = static_cast<uint8_t>((a * invAlpha + b * alpha + 128) >> 8);
        }
        pOut[i + 3] = 255;
    }
    return out;
}

class Hotkeys {
public:
    static constexpr int ToggleId = 0x4e51;
    static constexpr int DecreaseId = 0x4e52;
    static constexpr int IncreaseId = 0x4e53;
    static constexpr UINT Modifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;

    Hotkeys() {
        try {
            Register(ToggleId, VK_F6, "RegisterHotKey Ctrl+Alt+F6");
            Register(DecreaseId, VK_F7, "RegisterHotKey Ctrl+Alt+F7");
            Register(IncreaseId, VK_F8, "RegisterHotKey Ctrl+Alt+F8");
        } catch (...) {
            Cleanup();
            throw;
        }
    }

    ~Hotkeys() {
        Cleanup();
    }

private:
    void Register(int id, UINT key, const char* what) {
        if (!RegisterHotKey(nullptr, id, Modifiers, key)) {
            std::ostringstream os;
            os << what << " failed with Win32 error " << GetLastError();
            throw std::runtime_error(os.str());
        }
        registered_.push_back(id);
    }

    void Cleanup() noexcept {
        for (int id : registered_) {
            UnregisterHotKey(nullptr, id);
        }
        registered_.clear();
    }

    std::vector<int> registered_;
};

// Compare every displayed RGB byte, without a threshold or hash collisions.
// Alpha is excluded because visible upload always replaces it with 255.
bool SameVisibleRgb(const FramePixels& left, const FramePixels& right) {
    if (left.width != right.width || left.height != right.height ||
        left.rgba.size() != right.rgba.size()) {
        return false;
    }
    const __m256i rgbMask = _mm256_set1_epi32(0x00ffffff);
    size_t offset = 0;
    for (; offset + 32 <= left.rgba.size(); offset += 32) {
        const __m256i a = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(left.rgba.data() + offset));
        const __m256i b = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(right.rgba.data() + offset));
        const __m256i difference = _mm256_and_si256(_mm256_xor_si256(a, b), rgbMask);
        if (!_mm256_testz_si256(difference, difference)) {
            return false;
        }
    }
    for (; offset + 3 < left.rgba.size(); offset += 4) {
        if (left.rgba[offset] != right.rgba[offset] ||
            left.rgba[offset + 1] != right.rgba[offset + 1] ||
            left.rgba[offset + 2] != right.rgba[offset + 2]) {
            return false;
        }
    }
    return true;
}

class D3D11Presenter {
public:
    D3D11Presenter(HWND hwnd, UINT width, UINT height, bool repeatPresentations,
                   ID3D11Device* sharedDevice, ID3D11DeviceContext* sharedContext)
        : hwnd_(hwnd), width_(width), height_(height), device_(sharedDevice), context_(sharedContext),
          repeatPresentations_(repeatPresentations) {
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = width_;
        desc.BufferDesc.Height = height_;
        desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = kBufferCount;
        desc.OutputWindow = hwnd_;
        desc.Windowed = TRUE;
        desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory> factory;
        Check(device_.As(&dxgiDevice), "Query visible DXGI device");
        Check(dxgiDevice->GetAdapter(&adapter), "Get visible adapter");
        Check(adapter->GetParent(IID_PPV_ARGS(&factory)), "Get visible DXGI factory");
        Check(factory->CreateSwapChain(device_.Get(), &desc, &swapChain_), "Create visible D3D11 swapchain");

        D3D11_TEXTURE2D_DESC uploadDesc{};
        uploadDesc.Width = width_;
        uploadDesc.Height = height_;
        uploadDesc.MipLevels = 1;
        uploadDesc.ArraySize = 1;
        uploadDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        uploadDesc.SampleDesc.Count = 1;
        uploadDesc.Usage = D3D11_USAGE_DYNAMIC;
        uploadDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        uploadDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        Check(device_->CreateTexture2D(&uploadDesc, nullptr, &upload_), "CreateTexture2D visible upload");

        ComPtr<ID3D11Texture2D> backBuffer;
        Check(swapChain_->GetBuffer(0, IID_PPV_ARGS(&backBuffer)), "Get visible GPU render target");
        Check(device_->CreateRenderTargetView(backBuffer.Get(), nullptr, &gpuTarget_), "Create visible GPU render target");
    }

    bool Present(const FramePixels& frame, bool forcePresent = false) {
        if (frame.width != width_ || frame.height != height_ || frame.rgba.size() != static_cast<size_t>(width_) * height_ * 4) {
            throw std::runtime_error("Visible presenter received an unexpected frame size");
        }
        const bool changed = !hasPresentedFrame_ || !SameVisibleRgb(frame, lastPresentedFrame_);
        if (!repeatPresentations_ && !forcePresent && !repaintRequired_ && !changed) {
            ++duplicatesSkipped_;
            return false;
        }
        // Allocate before mapping GPU memory. Retain this allocation for later
        // comparisons; copy pixels only after an accepted visible submission.
        lastPresentedFrame_.rgba.resize(frame.rgba.size());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(context_->Map(upload_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map visible upload");
        const size_t rowBytes = static_cast<size_t>(width_) * 4;
        if (mapped.RowPitch == rowBytes) {
            SwizzleRgbaToBgraOpaque(frame.rgba.data(), static_cast<uint8_t*>(mapped.pData), static_cast<size_t>(width_) * height_);
        } else {
            for (UINT y = 0; y < height_; ++y) {
                auto* dst = static_cast<uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                const auto* src = frame.rgba.data() + static_cast<size_t>(y) * rowBytes;
                SwizzleRgbaToBgraOpaque(src, dst, width_);
            }
        }
        context_->Unmap(upload_.Get(), 0);

        ComPtr<ID3D11Texture2D> backBuffer;
        Check(swapChain_->GetBuffer(0, IID_PPV_ARGS(&backBuffer)), "D3D11 GetBuffer");
        context_->CopyResource(backBuffer.Get(), upload_.Get());
        const HRESULT result = swapChain_->Present(1, 0);
        Check(result, "D3D11 Present");
        ++presentCount_;
        if (result == DXGI_STATUS_OCCLUDED) {
            // Keep retrying while covered. Do not let a cached identical image
            // prevent a successful repaint after the window becomes visible.
            repaintRequired_ = true;
            ++occludedSubmissions_;
        } else {
            if (changed) {
                ++changedRgbUpdates_;
                lastPresentedFrame_.width = frame.width;
                lastPresentedFrame_.height = frame.height;
                std::memcpy(lastPresentedFrame_.rgba.data(), frame.rgba.data(), frame.rgba.size());
            }
            hasPresentedFrame_ = true;
            repaintRequired_ = false;
        }
        return true;
    }

    bool PresentGpu(bridge_gpu::FrameTransport& transport, const bridge_gpu::FrameStatistics& stats,
                    bool forcePresent = false) {
        if (!repeatPresentations_ && !forcePresent && !repaintRequired_ && !stats.changed) {
            ++duplicatesSkipped_;
            return false;
        }
        transport.DrawVisible(gpuTarget_.Get());
        const HRESULT result = swapChain_->Present(1, 0);
        Check(result, "Present visible GPU image");
        ++presentCount_;
        if (result == DXGI_STATUS_OCCLUDED) {
            repaintRequired_ = true;
            ++occludedSubmissions_;
        } else {
            if (stats.changed) {
                ++changedRgbUpdates_;
                // Duplicate forced presents are cadence only. Do not rewrite
                // history for the same pixels; that keeps the high-rate source
                // stream cheap and leaves correction history tied to a real
                // image change.
                transport.AcceptPresentation();
            }
            repaintRequired_ = false;
            hasPresentedFrame_ = false; // A later CPU fallback must refresh its own history.
        }
        transport.FinishPresentation();
        return true;
    }

    uint64_t presentCount() const {
        return presentCount_;
    }

    uint64_t changedRgbUpdates() const { return changedRgbUpdates_; }
    uint64_t duplicatesSkipped() const { return duplicatesSkipped_; }
    uint64_t occludedSubmissions() const { return occludedSubmissions_; }

private:
    HWND hwnd_ = nullptr;
    UINT width_ = 0;
    UINT height_ = 0;
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGISwapChain> swapChain_;
    ComPtr<ID3D11Texture2D> upload_;
    ComPtr<ID3D11RenderTargetView> gpuTarget_;
    uint64_t presentCount_ = 0;
    uint64_t changedRgbUpdates_ = 0;
    uint64_t duplicatesSkipped_ = 0;
    uint64_t occludedSubmissions_ = 0;
    FramePixels lastPresentedFrame_;
    bool hasPresentedFrame_ = false;
    bool repaintRequired_ = false;
    bool repeatPresentations_ = false;
};

void UpdateBridgeTitle(HWND hwnd, bool enabled, float strength, bool frozenSource) {
    std::wstringstream title;
    title << L"DLSS NR Bridge [" << (enabled ? L"on" : L"off") << L" "
          << std::fixed << std::setprecision(2) << strength;
    if (frozenSource) {
        title << L" frozen-source diagnostic";
    }
    title << L"]";
    SetWindowTextW(hwnd, title.str().c_str());
}

void WriteReport(const std::filesystem::path& path,
                 HWND source,
                 const Options& options,
                 uint64_t captured,
                 uint64_t nrPresented,
                 uint64_t visiblePresented,
                 bool effectEnabled,
                 float strength,
                 uint64_t warmupPresented,
                 UINT sourceActualWidth,
                 UINT sourceActualHeight,
                 double visibleSeconds,
                 const PerformanceStats& performance) {
    std::ofstream report(path, std::ios::binary);
    report << "source_hwnd=0x" << std::hex << reinterpret_cast<uintptr_t>(source) << std::dec << "\n";
    report << "source_title=" << NarrowAscii(WindowTitle(source)) << "\n";
    report << "output_width=" << options.displayWidth << "\n";
    report << "output_height=" << options.displayHeight << "\n";
    report << "neural_width=" << options.width << "\n";
    report << "neural_height=" << options.height << "\n";
    report << "native_resolution=" << (options.nativeResolution ? 1 : 0) << "\n";
    report << "working_scale=" << options.workingScale << "\n";
    report << "neural_max_height=" << options.neuralMaxHeight << "\n";
    report << "source_actual_width=" << sourceActualWidth << "\n";
    report << "source_actual_height=" << sourceActualHeight << "\n";
    report << "frames_captured=" << captured << "\n";
    report << "nr_frames_presented=" << nrPresented << "\n";
    report << "visible_frames_presented=" << visiblePresented << "\n";
    report << std::fixed << std::setprecision(4);
    report << "visible_elapsed_seconds=" << visibleSeconds << "\n";
    report << "bridge_present_fps=" << (visibleSeconds > 0 ? visiblePresented / visibleSeconds : 0) << "\n";
    report << "max_fps=" << options.maxFps << "\n";
    report << "precise_scheduling_requested=" << (options.preciseScheduling && !options.noProxy) << "\n";
    report << "suppress_identical_rgb=" << (!options.repeatPresentations && !options.noProxy) << "\n";
    report << "fps_note=bridge presents are not unique neural jobs or game FPS\n";
    constexpr const char* stageNames[] = {"capture", "prepare", "nr_present", "readback", "guard_blend", "visible_present"};
    for (size_t i = 0; i < performance.stageMs.size(); ++i) {
        report << "mean_" << stageNames[i] << "_ms="
               << (performance.frames ? performance.stageMs[i] / performance.frames : 0) << "\n";
    }
    report << "warmup_frames_requested=" << options.warmupFrames << "\n";
    report << "warmup_frames_presented=" << warmupPresented << "\n";
    report << "freeze_source=" << (options.freezeSource ? 1 : 0) << "\n";
    report << "freeze_source_note=" << (options.freezeSource ? "diagnostic mode; first valid captured frame reused for deterministic comparison" : "live capture") << "\n";
    report << "effect_enabled_final=" << (effectEnabled ? 1 : 0) << "\n";
    report << "strength_final=" << std::fixed << std::setprecision(2) << strength << "\n";
    report << "hotkeys=Ctrl+Alt+F6 toggle,Ctrl+Alt+F7 decrease,Ctrl+Alt+F8 increase\n";
    report << "black_guard=effect disabled after any black NR frame from a nonblack source; process fails after 30 consecutive frames\n";
    report << "proxy_loaded=" << (options.noProxy ? 0 : 1) << "\n";
    report << "pipeline=WindowCapture_D3D11_to_private_D3D12_NR_swapchain_to_visible_D3D11_blend\n";
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        Options options = ParseOptions(argc, argv);
        if (options.listWindows) {
            EnumWindows(ListWindowsProc, 0);
            return 0;
        }
        HWND source = options.sourceHwnd;
        if (!source && !options.sourceTitle.empty()) {
            source = FindWindowByTitle(options.sourceTitle);
        }
        if (!source || !IsWindow(source)) {
            throw std::runtime_error("A valid --source-hwnd or --source-title is required");
        }
        if (IsIconic(source)) {
            throw std::runtime_error("Source window is minimized");
        }
        if (options.saveCaptures) {
            std::filesystem::create_directories(options.captureDir);
        }
        if (!options.readyFile.empty()) {
            std::error_code ec;
            std::filesystem::remove(options.readyFile, ec);
            std::filesystem::remove(std::filesystem::path(options.readyFile).concat(L".tmp"), ec);
        }

        UniqueHandle stopEvent;
        if (!options.stopEventName.empty()) {
            stopEvent.reset(OpenEventW(SYNCHRONIZE, FALSE, options.stopEventName.c_str()));
            if (!stopEvent) {
                std::wstringstream ws;
                ws << L"--stop-event was provided but OpenEventW failed for '" << options.stopEventName << L"' with Win32 error " << GetLastError();
                throw std::runtime_error(NarrowAscii(ws.str()));
            }
        }
        UniqueHandle parentProcess;
        if (options.parentPid != 0) {
            parentProcess.reset(OpenProcess(SYNCHRONIZE, FALSE, options.parentPid));
            if (!parentProcess) {
                std::wstringstream ws;
                ws << L"--parent-pid was provided but OpenProcess failed for pid " << options.parentPid << L" with Win32 error " << GetLastError();
                throw std::runtime_error(NarrowAscii(ws.str()));
            }
        }
        auto shouldStop = [&]() -> bool {
            if (stopEvent && WaitForSingleObject(stopEvent.get(), 0) == WAIT_OBJECT_0) {
                return true;
            }
            if (parentProcess && WaitForSingleObject(parentProcess.get(), 0) == WAIT_OBJECT_0) {
                return true;
            }
            return false;
        };

        auto waitForActivity = [&](std::chrono::milliseconds duration, bool includeHipProgress) {
            std::array<HANDLE, 3> handles{};
            DWORD count = 0;
            if (stopEvent) handles[count++] = stopEvent.get();
            if (parentProcess) handles[count++] = parentProcess.get();
            if (includeHipProgress) {
                if (HANDLE event = HipWorkProgressEvent()) handles[count++] = event;
            }
            const DWORD timeout = static_cast<DWORD>(std::clamp<int64_t>(duration.count(), 1, 5));
            if (MsgWaitForMultipleObjectsEx(count, count ? handles.data() : nullptr,
                    timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_FAILED) {
                throw std::runtime_error("Bridge activity wait failed: " + std::to_string(GetLastError()));
            }
        };

        ScopedRenderScheduling scheduling(options.preciseScheduling && !options.noProxy);
        [[maybe_unused]] HMODULE proxy = nullptr;
        const char* hipTimingStatus = "no_proxy";
        if (!options.noProxy) {
            ResetRuntimeLogForCurrentLaunch();
            proxy = LoadLibraryW(L"version.dll");
            if (!proxy) {
                throw std::runtime_error("LoadLibraryW(version.dll) failed");
            }
            hipTimingStatus = StartHipHostTiming(proxy, options.hipHostTiming, options.hipKernelSampling);
            if (options.startupDelayMs > 0) {
                Sleep(options.startupDelayMs);
            }
        }

        HINSTANCE instance = GetModuleHandleW(nullptr);
        WindowCapture capture(source);
        const auto start = std::chrono::steady_clock::now();
        // The producer must keep feeding the asynchronous runtime even when
        // visible output is identical. Bound polling independently of visible
        // Present so suppressing an upload cannot create an uncapped busy loop.
        // This is a start-to-start budget, not extra sleep after GPU work.
        const bool suppressIdenticalRgb = !options.repeatPresentations && !options.noProxy;
        const auto requestedInterval = options.maxFps > 0
            ? std::chrono::nanoseconds(1000000000ULL / options.maxFps)
            : std::chrono::nanoseconds::zero();
        const auto minimumFeedInterval = suppressIdenticalRgb
            ? std::chrono::nanoseconds(1000000000ULL / 120)
            : std::chrono::nanoseconds::zero();
        const auto frameInterval = std::max(requestedInterval, minimumFeedInterval);
        auto nextFrameAt = std::chrono::steady_clock::now();
        uint64_t capturedFrames = 0;
        uint64_t warmupFrames = 0;
        uint64_t savedFrames = 0;
        uint64_t transitionSnapshots = 0;
        UINT sourceActualWidth = 0;
        UINT sourceActualHeight = 0;
        bool pendingHotkeySnapshot = false;
        UINT consecutiveBrokenNrFrames = 0;
        std::optional<FramePixels> lastInputFrame;
        std::optional<FramePixels> preparedInputFrame;
        std::optional<FramePixels> lastNrFrame;
        std::optional<FramePixels> lastDisplayFrame;
        FramePixels captureFrame;
        std::optional<bool> sourceNonBlack;

        auto captureLatestInput = [&]() -> bool {
            if (!capture.TryCapture(captureFrame)) {
                return false;
            }
            // Source-relative modes lock to the first captured dimensions so a
            // resolution change cannot silently mismatch neural/native history.
            if ((options.nativeResolution || options.neuralMaxHeight > 0 ||
                 (options.workingScale > 0.0f && options.workingScale < 0.999f)) &&
                lastInputFrame && sourceActualWidth != 0 && sourceActualHeight != 0) {
                ValidateNativeDimensions(captureFrame, sourceActualWidth, sourceActualHeight);
            }
            sourceActualWidth = captureFrame.width;
            sourceActualHeight = captureFrame.height;
            ++capturedFrames;
            if (options.freezeSource && lastInputFrame) {
                return false;
            }
            if (!lastInputFrame) {
                lastInputFrame.emplace();
            }
            // Keep both allocations alive across captures. No pixel data is
            // copied or discarded when promoting the newest captured image.
            std::swap(*lastInputFrame, captureFrame);
            sourceNonBlack.reset();
            return true;
        };

        auto pumpMessages = [&]() -> bool {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) {
                    return false;
                }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            return true;
        };

        const bool sourceRelativeDimensions = options.neuralMaxHeight > 0 || options.nativeResolution ||
            (options.workingScale > 0.0f && options.workingScale < 0.999f);
        if (sourceRelativeDimensions) {
            const auto firstFrameDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            while (!lastInputFrame) {
                if (shouldStop()) {
                    return 0;
                }
                if (!pumpMessages()) {
                    return 0;
                }
                if (capture.closed()) {
                    throw std::runtime_error("Source window closed before source dimensions were captured");
                }
                if (captureLatestInput()) {
                    break;
                }
                if (std::chrono::steady_clock::now() >= firstFrameDeadline) {
                    throw std::runtime_error("Timed out waiting for first WGC frame for source-relative processing");
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
            }
            if (options.neuralMaxHeight > 0) {
                options.displayWidth = lastInputFrame->width;
                options.displayHeight = lastInputFrame->height;
                const auto neural = FitNeuralDimensions(
                    lastInputFrame->width, lastInputFrame->height, options.neuralMaxHeight);
                options.width = neural.first;
                options.height = neural.second;
            } else if (options.nativeResolution) {
                options.width = lastInputFrame->width;
                options.height = lastInputFrame->height;
            } else {
                options.width = ScaledEvenDimension(lastInputFrame->width, options.workingScale);
                options.height = ScaledEvenDimension(lastInputFrame->height, options.workingScale);
            }
            if (options.width == 0 || options.height == 0 || options.width > 3840 || options.height > 2160) {
                std::ostringstream os;
                os << "Neural working size " << options.width << "x" << options.height
                   << " is outside supported bounds 64..3840 x 64..2160";
                throw std::runtime_error(os.str());
            }
        }
        if (options.displayWidth == 0 || options.displayHeight == 0) {
            options.displayWidth = options.width;
            options.displayHeight = options.height;
        }

        HWND nrWindow = CreateRenderWindow(instance, L"DLSS NR Bridge NR Feed", options.width, options.height, true, false);
        D3D12Presenter nrPresenter(nrWindow, options.width, options.height);

        while (warmupFrames < options.warmupFrames) {
            if (shouldStop()) {
                return 0;
            }
            if (!pumpMessages()) {
                break;
            }
            if (capture.closed()) {
                throw std::runtime_error("Source window closed or became unavailable during NR warmup");
            }
            if (options.seconds > 0 &&
                std::chrono::steady_clock::now() - start >= std::chrono::seconds(options.seconds)) {
                throw std::runtime_error("Timed out before NR warmup completed");
            }
            const auto now = std::chrono::steady_clock::now();
            if (now < nextFrameAt) {
                std::this_thread::sleep_for(std::min(std::chrono::ceil<std::chrono::milliseconds>(nextFrameAt - now), std::chrono::milliseconds(5)));
                continue;
            }
            const bool inputChanged = captureLatestInput();
            if (!lastInputFrame) {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }
            const FramePixels& original = SelectInputFrame(*lastInputFrame, options, preparedInputFrame, inputChanged);
            nrPresenter.Present(original, inputChanged);
            ++warmupFrames;
            nextFrameAt = now + frameInterval;
        }

        if (!options.noProxy) {
            bool healthy = false;
            std::string lastHealthDetail;
            const auto healthDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(60);
            while (!healthy && std::chrono::steady_clock::now() < healthDeadline) {
                if (shouldStop()) {
                    return 0;
                }
                if (!pumpMessages()) {
                    throw std::runtime_error("Window closed before DLSS-NR runtime health was verified");
                }
                if (capture.closed()) {
                    throw std::runtime_error("Source window closed before DLSS-NR runtime health was verified");
                }
                if (options.seconds > 0 && std::chrono::steady_clock::now() - start >= std::chrono::seconds(options.seconds)) {
                    throw std::runtime_error("Timed out before DLSS-NR runtime health was verified");
                }
                const bool inputChanged = captureLatestInput();
                if (!lastInputFrame) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(33));
                    continue;
                }
                const FramePixels& original = SelectInputFrame(*lastInputFrame, options, preparedInputFrame, inputChanged);
                nrPresenter.Present(original, inputChanged);
                if (!lastNrFrame) {
                    lastNrFrame.emplace();
                }
                nrPresenter.CaptureBackBuffer(*lastNrFrame);
                RuntimeHealth health = ReadRuntimeHealth();
                lastHealthDetail = health.detail;
                if (health.fatal) {
                    throw std::runtime_error("DLSS-NR runtime is not healthy: " + health.detail);
                }
                if (!sourceNonBlack.has_value()) {
                    sourceNonBlack = SourceMeaningfullyNonBlack(original);
                }
                healthy = health.logPresent && health.completedJob && (!*sourceNonBlack || FrameHasNonBlackPixels(*lastNrFrame));
                if (!healthy) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(33));
                }
            }
            if (!healthy) {
                throw std::runtime_error("DLSS-NR runtime did not become healthy before visible output: " + lastHealthDetail);
            }
        }

        ShowWindow(nrWindow, SW_HIDE);
        HWND bridge = CreateRenderWindow(instance, L"DLSS NR Bridge", options.displayWidth, options.displayHeight, true, true);
        D3D11Presenter visiblePresenter(bridge, options.displayWidth, options.displayHeight, !suppressIdenticalRgb,
                                       capture.device(), capture.context());
        std::unique_ptr<bridge_gpu::FrameTransport> gpuTransport;
        std::string transportDetail = "cpu: diagnostic, bypass, or explicit compatibility mode";
        if (!options.noProxy && !options.cpuTransport &&
            !options.saveCaptures && !options.freezeSource) {
            try {
                gpuTransport = std::make_unique<bridge_gpu::FrameTransport>(
                    capture.device(), capture.context(), nrPresenter.device(), options.width, options.height,
                    options.displayWidth, options.displayHeight, options.neuralMaxHeight > 0);
                const FramePixels& initial = SelectInputFrame(*lastInputFrame, options, preparedInputFrame, false);
                gpuTransport->SeedInput(initial.rgba.data(), options.width * 4);
                if (options.neuralMaxHeight > 0) {
                    gpuTransport->SeedNativeSource(lastInputFrame->rgba.data(), options.displayWidth * 4);
                }
                std::ostringstream detail;
                detail << "gpu_shared: d3d11_nt textures imported into D3D12; WGC "
                       << sourceActualWidth << 'x' << sourceActualHeight << " -> neural "
                       << options.width << 'x' << options.height << " -> visible "
                       << options.displayWidth << 'x' << options.displayHeight
                       << (options.neuralMaxHeight > 0
                           ? "; native source retained + low-res neural delta composite"
                           : "; GPU bilinear resize only when dimensions differ")
                       << "; 16-byte status readback per compose";
                transportDetail = detail.str();
            } catch (const std::exception& error) {
                gpuTransport.reset();
                // Unsupported sharing may use the existing CPU path. Device
                // loss must still stop the session instead of being concealed.
                Check(capture.device()->GetDeviceRemovedReason(), "Capture device removed during transport setup");
                Check(nrPresenter.device()->GetDeviceRemovedReason(), "Neural device removed during transport setup");
                transportDetail = std::string("cpu_fallback: ") + error.what();
            }
        }
        Hotkeys hotkeys;
        bool effectEnabled = true;
        float strength = 1.0f;
        const float maxStrength = options.neuralMaxHeight > 0 ? 4.0f : 1.0f;
        UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);

        auto guardBlackOutput = [&](bool sourceIsNonblack, bool neuralIsNonblack) {
            if (!options.noProxy && sourceIsNonblack && !neuralIsNonblack) {
                ++consecutiveBrokenNrFrames;
                effectEnabled = false;
                UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);
                if (consecutiveBrokenNrFrames >= 30) {
                    throw std::runtime_error("DLSS-NR output stayed black for 30 presented frames while source was nonblack");
                }
            } else if (neuralIsNonblack || !sourceIsNonblack) {
                consecutiveBrokenNrFrames = 0;
            }
        };

        auto saveSnapshot = [&](const std::string& prefix, uint64_t index) {
            if (!options.saveCaptures || !lastInputFrame || !lastNrFrame || !lastDisplayFrame) {
                return;
            }
            const FramePixels& original = SelectInputFrame(*lastInputFrame, options, preparedInputFrame, false);
            std::ostringstream name;
            name << prefix << "_" << std::setw(3) << std::setfill('0') << index
                 << "_" << (effectEnabled ? "on" : "off") << "_" << std::fixed << std::setprecision(1) << strength;
            const std::string stem = name.str();
            SavePpm(options.captureDir / (stem + "_input.ppm"), *lastInputFrame);
            SavePpm(options.captureDir / (stem + "_original.ppm"), original);
            SavePpm(options.captureDir / (stem + "_nr.ppm"), *lastNrFrame);
            SavePpm(options.captureDir / (stem + "_display.ppm"), *lastDisplayFrame);
        };

        auto nextRuntimeLogCheck = std::chrono::steady_clock::now() + std::chrono::seconds(1);

        const bool completionPacingAvailable = options.completionPacing && gpuTransport &&
            suppressIdenticalRgb && ReadHipWorkProgress().available;
        bool asyncOutputUsesPreviousInput = gpuTransport && UsesAsyncBackbufferRuntime();
        bool asyncBackbufferRuntime = completionPacingAvailable && asyncOutputUsesPreviousInput;
        uint64_t seenReturnedWaits = ReadHipWorkProgress().returnedWaits;
        uint64_t busyFeedDeferrals = 0;
        uint64_t completionHintFeeds = 0;
        uint64_t publicationRechecks = 0;
        uint64_t keepaliveFeeds = 0;
        uint64_t prefetchedCaptures = 0;
        auto lastGpuFeedAt = std::chrono::steady_clock::now();
        constexpr auto feedKeepalive = std::chrono::milliseconds(100);

        // While neural work is busy, keep just the latest *unsubmitted* capture.
        // This releases older pool slots without GPU copies or pixel readback.
        // Once submitted, ownership moves into the existing fenced try/catch.
        wgc::Direct3D11CaptureFrame prefetchedFrame{nullptr};
        ComPtr<ID3D11Texture2D> prefetchedTexture;
        auto prefetchLatestCapture = [&]() {
            ComPtr<ID3D11Texture2D> texture;
            auto frame = capture.TryCaptureTexture(texture);
            if (!frame) return;
            if (prefetchedFrame) prefetchedFrame.Close();
            prefetchedFrame = std::move(frame);
            prefetchedTexture = std::move(texture);
            ++capturedFrames;
            ++prefetchedCaptures;
        };

        bool readyWritten = false;
        bool running = true;
        FramePixels reusableNrFrame;
        PerformanceStats performance;
        const auto visibleStart = std::chrono::steady_clock::now();
        auto cadenceAt = visibleStart;
        uint64_t cadenceFeed = nrPresenter.presentCount();
        uint64_t cadenceChanged = 0;
        uint64_t cadencePresented = 0;
        uint64_t cadenceSkipped = 0;
        uint64_t cadenceOccluded = 0;
        uint64_t gpuTransportFrames = 0;
        uint64_t cadenceGpuFrames = 0;
        uint64_t cadenceCaptured = capturedFrames;
        uint64_t cadenceIterations = 0;
        uint32_t cadenceKernelAttempts = ReadHipKernelTimingProgress().attempts;
        std::array<double, 6> cadenceStages{};
        std::ofstream cadenceLog("bridge-cadence-" + std::to_string(GetCurrentProcessId()) + ".log");
        cadenceLog << "width=" << options.width << " height=" << options.height
                   << " source_width=" << sourceActualWidth << " source_height=" << sourceActualHeight
                   << " display_width=" << options.displayWidth << " display_height=" << options.displayHeight
                   << " working_scale=" << options.workingScale
                   << " neural_max_height=" << options.neuralMaxHeight
                   << " suppress_identical_rgb=" << suppressIdenticalRgb
                   << " minimum_feed_interval_ms=" << std::chrono::duration<double, std::milli>(frameInterval).count()
                   << " transport=" << (gpuTransport ? "gpu_shared" : "cpu")
                   << " runtime_scheduling=unchanged\n"
                   << "transport_detail=" << transportDetail << '\n'
                   << "bridge_version=" << BRIDGE_BUILD_VERSION << '\n'
                   << "gpu_transport_revision=async_residual_spatial_filter_motion_rejection\n"
                   << "effect_state_sample=end_of_interval\n"
                   << "hip_host_timing=" << hipTimingStatus << '\n'
                   << "completion_pacing=" << (asyncBackbufferRuntime ? "worker_wait_hints" : "fixed_feed_fallback")
                   << " keepalive_ms=" << feedKeepalive.count()
                   << " explicit_max_fps=" << options.maxFps << '\n'
                   << "completion_pacing_note=returned HIP waits are scheduling hints, not ready images; normal output fences and RGB comparison remain required\n"
                   << "note=changed_rgb_fps counts changed RGB submissions, not neural jobs, displayed/game FPS or LSFG output\n";
        cadenceLog.flush();
        auto logCadence = [&](bool final) {
            const auto at = std::chrono::steady_clock::now();
            const double seconds = std::chrono::duration<double>(at - cadenceAt).count();
            if ((!final && seconds < 1.0) || seconds <= 0.0) {
                return;
            }
            FlushHipHostTiming();
            const uint64_t feed = nrPresenter.presentCount();
            const uint64_t changed = visiblePresenter.changedRgbUpdates();
            const uint64_t presented = visiblePresenter.presentCount();
            const uint64_t skipped = visiblePresenter.duplicatesSkipped();
            const uint64_t occluded = visiblePresenter.occludedSubmissions();
            const auto kernelTiming = ReadHipKernelTimingProgress();
            cadenceLog << std::fixed << std::setprecision(3)
                       << "elapsed_s=" << std::chrono::duration<double>(at - visibleStart).count()
                       << " interval_s=" << seconds
                       << " feed_fps=" << (feed - cadenceFeed) / seconds
                       << " changed_rgb_fps=" << (changed - cadenceChanged) / seconds
                       << " visible_submit_fps=" << (presented - cadencePresented) / seconds
                       << " capture_fps=" << (capturedFrames - cadenceCaptured) / seconds
                       << " gpu_transport_frames=" << (gpuTransportFrames - cadenceGpuFrames)
                       << " identical_skipped=" << (skipped - cadenceSkipped)
                       << " occluded_submissions=" << (occluded - cadenceOccluded)
                       << " total_changed=" << changed << " total_skipped=" << skipped
                       << " busy_feed_deferrals=" << busyFeedDeferrals
                       << " completion_hint_feeds=" << completionHintFeeds
                       << " publication_rechecks=" << publicationRechecks
                       << " keepalive_feeds=" << keepaliveFeeds
                       << " prefetched_captures=" << prefetchedCaptures
                       << " kernel_sampling_available=" << kernelTiming.available
                       << " kernel_sampling_attempts=" << kernelTiming.attempts
                       << " kernel_sampling_attempts_in_interval=" << (kernelTiming.attempts - cadenceKernelAttempts)
                       << " kernel_sampling_dropped=" << kernelTiming.dropped
                       << " effect_enabled=" << effectEnabled
                       << " strength=" << strength
                       << " final=" << final;
            if (gpuTransport) {
                // Cumulative work counters, not presentation or inference FPS.
                cadenceLog << " total_direct_image_checks=" << gpuTransport->directImageCount()
                           << " total_blended_images=" << gpuTransport->blendedImageCount()
                           << " total_source_analyses=" << gpuTransport->sourceAnalysisCount()
                           << " total_direct_captures=" << gpuTransport->directCaptureCount()
                           << " total_copied_captures=" << gpuTransport->copiedCaptureCount()
                           << " total_scaled_captures=" << gpuTransport->scaledCaptureCount()
                           << " total_history_copies_avoided=" << gpuTransport->historyCopiesAvoided()
                           << " total_history_copies=" << gpuTransport->historyCopies()
                           << " total_source_history_copies=" << gpuTransport->sourceHistoryCopies();
            }
            constexpr const char* stageNames[] = {"capture", "prepare", "nr_present", "output_handoff", "guard_blend", "visible_present"};
            const uint64_t iterations = performance.frames - cadenceIterations;
            for (size_t index = 0; index < cadenceStages.size(); ++index) {
                cadenceLog << ' ' << stageNames[index] << "_ms="
                           << (iterations ? (performance.stageMs[index] - cadenceStages[index]) / iterations : 0.0);
            }
            cadenceLog << '\n';
            cadenceLog.flush();
            cadenceAt = at;
            cadenceFeed = feed;
            cadenceChanged = changed;
            cadencePresented = presented;
            cadenceSkipped = skipped;
            cadenceOccluded = occluded;
            cadenceCaptured = capturedFrames;
            cadenceGpuFrames = gpuTransportFrames;
            cadenceIterations = performance.frames;
            cadenceKernelAttempts = kernelTiming.attempts;
            cadenceStages = performance.stageMs;
        };
        while (running) {
            if (shouldStop()) {
                break;
            }
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) {
                    running = false;
                    break;
                }
                if (msg.message == WM_HOTKEY) {
                    if (msg.wParam == Hotkeys::ToggleId) {
                        effectEnabled = !effectEnabled;
                    } else if (msg.wParam == Hotkeys::DecreaseId) {
                        const float step = strength > 1.0f ? 0.25f : 0.1f;
                        strength = std::max(0.0f, strength - step);
                        if (strength < 1.0f && strength > 0.95f) strength = 1.0f;
                    } else if (msg.wParam == Hotkeys::IncreaseId) {
                        const float step = strength >= 1.0f && maxStrength > 1.0f ? 0.25f : 0.1f;
                        strength = std::min(maxStrength, strength + step);
                        if (strength > 0.95f && strength < 1.0f) strength = 1.0f;
                    }
                    UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);
                    pendingHotkeySnapshot = true;
                    continue;
                }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (!running) {
                break;
            }
            if (capture.closed()) {
                throw std::runtime_error("Source window closed or became unavailable");
            }
            if (options.seconds > 0 &&
                std::chrono::steady_clock::now() - start >= std::chrono::seconds(options.seconds)) {
                break;
            }

            if (!options.noProxy && std::chrono::steady_clock::now() >= nextRuntimeLogCheck) {
                const RuntimeHealth health = ReadRuntimeHealth();
                if (!health.logPresent || !health.completedJob || health.fatal) {
                    effectEnabled = false;
                    UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);
                    throw std::runtime_error("DLSS-NR runtime became unhealthy: " + health.detail);
                }
                nextRuntimeLogCheck = std::chrono::steady_clock::now() + std::chrono::seconds(1);
                asyncOutputUsesPreviousInput = gpuTransport && UsesAsyncBackbufferRuntime();
                asyncBackbufferRuntime = completionPacingAvailable && asyncOutputUsesPreviousInput;
            }

            const auto now = std::chrono::steady_clock::now();
            const bool paceForInference = asyncBackbufferRuntime && readyWritten &&
                effectEnabled && strength >= 1.0f && !pendingHotkeySnapshot;
            const auto progress = paceForInference ? ReadHipWorkProgress() : HipWorkProgress{};
            const bool completionHint = progress.available && progress.returnedWaits != seenReturnedWaits;
            const bool keepaliveDue = now >= lastGpuFeedAt + feedKeepalive;
            if (progress.available && progress.activeWaits && !completionHint && !keepaliveDue) {
                // The previous iteration's output handoff has already finished.
                // Avoid interrupting inference with another full GPU feed/copy/
                // compare just to rediscover the same output. A keepalive bounds
                // this optimization even if a future runtime needs more feeds.
                prefetchLatestCapture();
                ++busyFeedDeferrals;
                waitForActivity(std::chrono::ceil<std::chrono::milliseconds>(
                    lastGpuFeedAt + feedKeepalive - now), true);
                logCadence(false);
                continue;
            }
            // A returned wait can wake a feed immediately instead of waiting for
            // the next 120 Hz poll. The user's explicit --max-fps still wins.
            const auto feedAt = completionHint ? lastGpuFeedAt + requestedInterval : nextFrameAt;
            if (now < feedAt) {
                waitForActivity(std::chrono::ceil<std::chrono::milliseconds>(feedAt - now), paceForInference);
                continue;
            }
            std::array<PerformanceStats::Clock::time_point, 7> stageTimes;
            stageTimes[0] = PerformanceStats::Clock::now();
            if (gpuTransport) {
                ComPtr<ID3D11Texture2D> capturedTexture;
                wgc::Direct3D11CaptureFrame capturedFrame{nullptr};
                try {
                    capturedFrame = capture.TryCaptureTexture(capturedTexture);
                    if (capturedFrame) {
                        ++capturedFrames;
                        if (prefetchedFrame) prefetchedFrame.Close();
                        prefetchedFrame = nullptr;
                        prefetchedTexture.Reset();
                    } else if (prefetchedFrame) {
                        capturedFrame = std::move(prefetchedFrame);
                        prefetchedFrame = nullptr;
                        capturedTexture = std::move(prefetchedTexture);
                    }
                    stageTimes[1] = PerformanceStats::Clock::now();
                    if (capturedFrame) {
                        gpuTransport->UpdateInput(capturedTexture.Get());
                    }
                    stageTimes[2] = PerformanceStats::Clock::now();
                    nrPresenter.PresentGpu(*gpuTransport);
                    stageTimes[3] = PerformanceStats::Clock::now();
                    nrPresenter.CopyOutputGpu(*gpuTransport);
                    if (progress.available) seenReturnedWaits = progress.returnedWaits;
                    if (completionHint) ++completionHintFeeds;
                    if (progress.available && progress.activeWaits && keepaliveDue) ++keepaliveFeeds;
                    lastGpuFeedAt = now;
                    const float gpuStrength = options.neuralMaxHeight > 0
                        ? std::clamp(strength, 0.0f, 4.0f)
                        : std::clamp(strength, 0.0f, 1.0f);
                    const UINT alpha = effectEnabled
                        ? static_cast<UINT>(std::lround(gpuStrength * 256.0f)) : 0;
                    auto stats = gpuTransport->Compose(alpha);
                    // Compose's bounded D3D11 consumer fence follows its wait
                    // on the D3D12 output copy. That copy follows the feed and
                    // input-ready wait, so all reads of this WGC surface have
                    // now completed. Do not return it to the pool any earlier.
                    if (capturedFrame) {
                        capturedFrame.Close();
                        capturedFrame = nullptr;
                        capturedTexture.Reset();
                    }
                    stageTimes[4] = PerformanceStats::Clock::now();
                    const bool wasEnabled = effectEnabled;
                    guardBlackOutput(stats.sourceMeaningfullyNonblack, stats.neuralNonblack);
                    if (wasEnabled && !effectEnabled) {
                        stats = gpuTransport->Compose(0);
                    }
                    stageTimes[5] = PerformanceStats::Clock::now();
                    // Lossless Scaling observes this bridge window as its source.
                    // Keep presenting at the neural/feed cadence even when the
                    // pixels are identical; suppressing those presents made the
                    // source stream collapse to WGC's ~60 changed frames/s.
                    const bool cadencePresent = asyncBackbufferRuntime && effectEnabled;
                    const bool visibleSubmitted = visiblePresenter.PresentGpu(
                        *gpuTransport, stats, !readyWritten || pendingHotkeySnapshot || cadencePresent);
                    stageTimes[6] = PerformanceStats::Clock::now();
                    performance.Record(stageTimes);
                    ++gpuTransportFrames;
                    if (visibleSubmitted && !readyWritten) {
                        WriteReadyFileAtomic(options.readyFile, bridge);
                        readyWritten = true;
                    }
                    if (visibleSubmitted) pendingHotkeySnapshot = false;
                    nextFrameAt = now + frameInterval;
                    if (completionHint && !ReadHipWorkProgress().activeWaits) {
                        // HIP returns before the runtime publishes its ready
                        // flag. If this feed raced publication, give it one
                        // near-term follow-up instead of another full poll
                        // interval. The follow-up cannot recursively rearm
                        // itself without a genuinely new returned-wait hint.
                        nextFrameAt = now + std::max(requestedInterval,
                            std::chrono::duration_cast<std::chrono::nanoseconds>(
                                std::chrono::milliseconds(1)));
                        ++publicationRechecks;
                    }
                    logCadence(false);
                } catch (...) {
                    // Keep both the WGC frame and shared allocations alive
                    // while draining the producer first, then the consumer.
                    // In particular, failed Present/Copy waits are not proof
                    // that these buffers have stopped being used by the GPU.
                    try {
                        nrPresenter.DrainForShutdown();
                        gpuTransport->DrainForShutdown();
                    } catch (const std::exception& cleanupError) {
                        bridge_gpu::StopAfterUnconfirmedGpuCompletion(cleanupError.what());
                    } catch (...) {
                        bridge_gpu::StopAfterUnconfirmedGpuCompletion("unknown GPU cleanup error");
                    }
                    throw;
                }
                continue;
            }
            const bool inputChanged = captureLatestInput();
            if (!lastInputFrame) {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }
            stageTimes[1] = PerformanceStats::Clock::now();
            const FramePixels& original = SelectInputFrame(*lastInputFrame, options, preparedInputFrame, inputChanged);
            stageTimes[2] = PerformanceStats::Clock::now();
            nrPresenter.Present(original, inputChanged);
            stageTimes[3] = PerformanceStats::Clock::now();
            if (!options.noProxy) {
                nrPresenter.CaptureBackBuffer(reusableNrFrame);
            }
            const FramePixels& nrFrame = options.noProxy ? original : reusableNrFrame;
            stageTimes[4] = PerformanceStats::Clock::now();
            if (options.saveCaptures) {
                lastNrFrame = nrFrame;
            }
            if (!sourceNonBlack.has_value()) {
                sourceNonBlack = SourceMeaningfullyNonBlack(
                    options.neuralMaxHeight > 0 ? *lastInputFrame : original);
            }
            const bool nrNonBlack = FrameHasNonBlackPixels(nrFrame);
            guardBlackOutput(*sourceNonBlack, nrNonBlack);
            // Native-detail fallback preserves the captured source as the base
            // image and applies the network's low-resolution correction to it.
            FramePixels nativeResidual;
            FramePixels blended;
            const FramePixels* display = options.neuralMaxHeight > 0 ? &*lastInputFrame : &original;
            if (options.neuralMaxHeight > 0) {
                if (effectEnabled && strength > 0.0f) {
                    nativeResidual = ComposeNativeDetailCpu(
                        *lastInputFrame, original, nrFrame, strength, true);
                    display = &nativeResidual;
                }
            } else {
                if (effectEnabled && strength >= 1.0f) {
                    display = &nrFrame;
                } else if (effectEnabled && strength > 0.0f) {
                    blended = BlendFrames(original, nrFrame, strength, true);
                    display = &blended;
                }
            }
            if (options.saveCaptures) {
                lastDisplayFrame = *display;
            }
            stageTimes[5] = PerformanceStats::Clock::now();
            const bool visibleSubmitted = visiblePresenter.Present(*display, !readyWritten || pendingHotkeySnapshot);
            stageTimes[6] = PerformanceStats::Clock::now();
            performance.Record(stageTimes);
            if (visibleSubmitted && !readyWritten) {
                WriteReadyFileAtomic(options.readyFile, bridge);
                readyWritten = true;
            }
            nextFrameAt = now + frameInterval;
            logCadence(false);

            if (visibleSubmitted && pendingHotkeySnapshot) {
                saveSnapshot("hotkey", transitionSnapshots++);
                pendingHotkeySnapshot = false;
            }
            if (visibleSubmitted && options.saveCaptures && savedFrames < 3) {
                saveSnapshot("frame", savedFrames);
                ++savedFrames;
            }
        }
        logCadence(true);

        if (options.saveCaptures) {
            WriteReport(options.captureDir / "bridge-report.txt",
                        source,
                        options,
                        capturedFrames,
                        nrPresenter.presentCount(),
                        visiblePresenter.presentCount(),
                        effectEnabled,
                        strength,
                        warmupFrames,
                        sourceActualWidth,
                        sourceActualHeight,
                        std::chrono::duration<double>(std::chrono::steady_clock::now() - visibleStart).count(),
                        performance);
            saveSnapshot("final", 0);
        }
        return 0;
    } catch (const winrt::hresult_error& ex) {
        std::wcerr << L"WinRT error 0x" << std::hex << static_cast<uint32_t>(ex.code()) << L": " << ex.message().c_str() << L"\n";
        std::ofstream error("bridge-error.txt", std::ios::app);
        error << "WinRT error 0x" << std::hex << static_cast<uint32_t>(ex.code()) << ": " << NarrowAscii(ex.message().c_str()) << "\n";
        return 1;
    } catch (const std::exception& ex) {
        std::cerr << ex.what() << "\n";
        std::ofstream error("bridge-error.txt", std::ios::app);
        error << ex.what() << "\n";
        return 1;
    }
}
