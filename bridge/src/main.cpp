#include <windows.h>
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
#include <mutex>
#include <optional>
#include <regex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <immintrin.h>

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

    std::optional<FramePixels> TryCapture() {
        if (!IsWindow(source_) || IsIconic(source_)) {
            closed_.store(true);
            return std::nullopt;
        }
        auto frame = framePool_.TryGetNextFrame();
        if (!frame) {
            return std::nullopt;
        }
        // The capture pool has two slots. Prefer the newest complete frame rather
        // than displaying a queued older frame after a slow neural evaluation.
        if (auto newer = framePool_.TryGetNextFrame()) {
            frame.Close();
            frame = std::move(newer);
        }
        const auto size = frame.ContentSize();
        if (size.Width <= 0 || size.Height <= 0) {
            return std::nullopt;
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

        d3dContext_->CopyResource(staging_.Get(), texture.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Check(d3dContext_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map staging");
        FramePixels result;
        result.width = desc.Width;
        result.height = desc.Height;
        result.rgba.resize(static_cast<size_t>(result.width) * result.height * 4);
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
        return result;
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
                // Destruction must not terminate the process while unwinding a
                // GPU error; the original exception is reported by wmain.
                std::cerr << "D3D12 cleanup: " << ex.what() << "\n";
            }
            CloseHandle(fenceEvent_);
        }
        if (upload_ && uploadMapped_) {
            D3D12_RANGE writeRange{0, 0};
            upload_->Unmap(0, &writeRange);
            uploadMapped_ = nullptr;
        }
    }

    void Present(const FramePixels& frame) {
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        Upload(frame);
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
        Check(allocator_->Reset(), "allocator Reset");
        Check(commandList_->Reset(allocator_.Get(), nullptr), "commandList Reset");
    }

    void ExecuteCommands(DWORD timeoutMs) {
        Check(commandList_->Close(), "Close commandList");
        ID3D12CommandList* lists[] = {commandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        WaitForGpu(timeoutMs);
    }

    void WaitForGpu(DWORD timeoutMs) {
        const UINT64 value = ++fenceValue_;
        Check(queue_->Signal(fence_.Get(), value), "fence Signal");
        if (fence_->GetCompletedValue() < value) {
            Check(fence_->SetEventOnCompletion(value, fenceEvent_), "SetEventOnCompletion");
            DWORD wait = WaitForSingleObject(fenceEvent_, timeoutMs);
            if (wait != WAIT_OBJECT_0) {
                throw std::runtime_error("Timed out waiting for D3D12 fence");
            }
        }
    }

    void SetBackBufferState(UINT index, D3D12_RESOURCE_STATES state) {
        if (bufferStates_[index] == state) {
            return;
        }
        auto barrier = Transition(backBuffers_[index].Get(), bufferStates_[index], state);
        commandList_->ResourceBarrier(1, &barrier);
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
    ComPtr<ID3D12Fence> fence_;
    HANDLE fenceEvent_ = nullptr;
    UINT64 fenceValue_ = 0;
    ComPtr<ID3D12Resource> backBuffers_[kBufferCount];
    D3D12_RESOURCE_STATES bufferStates_[kBufferCount]{};
    ComPtr<ID3D12Resource> upload_;
    uint8_t* uploadMapped_ = nullptr;
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

class D3D11Presenter {
public:
    D3D11Presenter(HWND hwnd, UINT width, UINT height) : hwnd_(hwnd), width_(width), height_(height) {
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
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        Check(D3D11CreateDeviceAndSwapChain(nullptr,
                                           D3D_DRIVER_TYPE_HARDWARE,
                                           nullptr,
                                           flags,
                                           levels,
                                           static_cast<UINT>(std::size(levels)),
                                           D3D11_SDK_VERSION,
                                           &desc,
                                           &swapChain_,
                                           &device_,
                                           nullptr,
                                           &context_),
              "D3D11CreateDeviceAndSwapChain");

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
    }

    void Present(const FramePixels& frame) {
        if (frame.width != width_ || frame.height != height_ || frame.rgba.size() != static_cast<size_t>(width_) * height_ * 4) {
            throw std::runtime_error("Visible presenter received an unexpected frame size");
        }
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
        Check(swapChain_->Present(1, 0), "D3D11 Present");
        ++presentCount_;
    }

    uint64_t presentCount() const {
        return presentCount_;
    }

private:
    HWND hwnd_ = nullptr;
    UINT width_ = 0;
    UINT height_ = 0;
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGISwapChain> swapChain_;
    ComPtr<ID3D11Texture2D> upload_;
    uint64_t presentCount_ = 0;
};

void UpdateBridgeTitle(HWND hwnd, bool enabled, float strength, bool frozenSource) {
    std::wstringstream title;
    title << L"DLSS NR Bridge [" << (enabled ? L"on" : L"off") << L" "
          << std::fixed << std::setprecision(1) << strength;
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
    report << "output_width=" << options.width << "\n";
    report << "output_height=" << options.height << "\n";
    report << "native_resolution=" << (options.nativeResolution ? 1 : 0) << "\n";
    report << "source_actual_width=" << sourceActualWidth << "\n";
    report << "source_actual_height=" << sourceActualHeight << "\n";
    report << "frames_captured=" << captured << "\n";
    report << "nr_frames_presented=" << nrPresented << "\n";
    report << "visible_frames_presented=" << visiblePresented << "\n";
    report << std::fixed << std::setprecision(4);
    report << "visible_elapsed_seconds=" << visibleSeconds << "\n";
    report << "bridge_present_fps=" << (visibleSeconds > 0 ? visiblePresented / visibleSeconds : 0) << "\n";
    report << "max_fps=" << options.maxFps << "\n";
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

        [[maybe_unused]] HMODULE proxy = nullptr;
        if (!options.noProxy) {
            ResetRuntimeLogForCurrentLaunch();
            proxy = LoadLibraryW(L"version.dll");
            if (!proxy) {
                throw std::runtime_error("LoadLibraryW(version.dll) failed");
            }
            if (options.startupDelayMs > 0) {
                Sleep(options.startupDelayMs);
            }
        }

        HINSTANCE instance = GetModuleHandleW(nullptr);
        WindowCapture capture(source);
        const auto start = std::chrono::steady_clock::now();
        // A limit is a start-to-start budget, never a delay added after work.
        // Default pacing comes from the visible swapchain's single vblank wait.
        const auto frameInterval = options.maxFps > 0
            ? std::chrono::nanoseconds(1000000000ULL / options.maxFps)
            : std::chrono::nanoseconds::zero();
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
        std::optional<FramePixels> lastOriginalFrame;
        std::optional<FramePixels> lastNrFrame;
        std::optional<FramePixels> lastDisplayFrame;

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

        if (options.nativeResolution) {
            const auto firstFrameDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
            while (!lastInputFrame) {
                if (shouldStop()) {
                    return 0;
                }
                if (!pumpMessages()) {
                    return 0;
                }
                if (capture.closed()) {
                    throw std::runtime_error("Source window closed before native-resolution dimensions were captured");
                }
                auto maybeFrame = capture.TryCapture();
                if (maybeFrame) {
                    sourceActualWidth = maybeFrame->width;
                    sourceActualHeight = maybeFrame->height;
                    lastInputFrame = std::move(*maybeFrame);
                    ++capturedFrames;
                    break;
                }
                if (std::chrono::steady_clock::now() >= firstFrameDeadline) {
                    throw std::runtime_error("Timed out waiting for first WGC frame in native-resolution mode");
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
            }
            options.width = lastInputFrame->width;
            options.height = lastInputFrame->height;
            if (options.width == 0 || options.height == 0 || options.width > 3840 || options.height > 2160) {
                std::ostringstream os;
                os << "Native-resolution source size " << options.width << "x" << options.height
                   << " is outside supported bounds 1..3840 x 1..2160";
                throw std::runtime_error(os.str());
            }
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
                std::this_thread::sleep_for(std::min(std::chrono::duration_cast<std::chrono::milliseconds>(nextFrameAt - now), std::chrono::milliseconds(5)));
                continue;
            }
            auto maybeFrame = capture.TryCapture();
            if (maybeFrame) {
                if (options.nativeResolution) {
                    ValidateNativeDimensions(*maybeFrame, options.width, options.height);
                }
                sourceActualWidth = maybeFrame->width;
                sourceActualHeight = maybeFrame->height;
                if (!options.freezeSource || !lastInputFrame) {
                    lastInputFrame = std::move(*maybeFrame);
                }
                ++capturedFrames;
            }
            if (!lastInputFrame) {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }
            FramePixels original = PrepareInputFrame(*lastInputFrame, options);
            lastOriginalFrame = original;
            nrPresenter.Present(original);
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
                auto maybeFrame = capture.TryCapture();
                if (maybeFrame) {
                    if (options.nativeResolution) {
                        ValidateNativeDimensions(*maybeFrame, options.width, options.height);
                    }
                    sourceActualWidth = maybeFrame->width;
                    sourceActualHeight = maybeFrame->height;
                    if (!options.freezeSource || !lastInputFrame) {
                        lastInputFrame = std::move(*maybeFrame);
                    }
                    ++capturedFrames;
                }
                if (!lastInputFrame) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(33));
                    continue;
                }
                FramePixels original = PrepareInputFrame(*lastInputFrame, options);
                lastOriginalFrame = original;
                nrPresenter.Present(original);
                lastNrFrame = nrPresenter.CaptureBackBuffer();
                RuntimeHealth health = ReadRuntimeHealth();
                lastHealthDetail = health.detail;
                if (health.fatal) {
                    throw std::runtime_error("DLSS-NR runtime is not healthy: " + health.detail);
                }
                healthy = health.logPresent && health.completedJob && (!SourceMeaningfullyNonBlack(original) || FrameHasNonBlackPixels(*lastNrFrame));
                if (!healthy) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(33));
                }
            }
            if (!healthy) {
                throw std::runtime_error("DLSS-NR runtime did not become healthy before visible output: " + lastHealthDetail);
            }
        }

        ShowWindow(nrWindow, SW_HIDE);
        HWND bridge = CreateRenderWindow(instance, L"DLSS NR Bridge", options.width, options.height, true, true);
        D3D11Presenter visiblePresenter(bridge, options.width, options.height);
        Hotkeys hotkeys;
        bool effectEnabled = true;
        float strength = 1.0f;
        UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);

        auto saveSnapshot = [&](const std::string& prefix, uint64_t index) {
            if (!options.saveCaptures || !lastInputFrame || !lastOriginalFrame || !lastNrFrame || !lastDisplayFrame) {
                return;
            }
            std::ostringstream name;
            name << prefix << "_" << std::setw(3) << std::setfill('0') << index
                 << "_" << (effectEnabled ? "on" : "off") << "_" << std::fixed << std::setprecision(1) << strength;
            const std::string stem = name.str();
            SavePpm(options.captureDir / (stem + "_input.ppm"), *lastInputFrame);
            SavePpm(options.captureDir / (stem + "_original.ppm"), *lastOriginalFrame);
            SavePpm(options.captureDir / (stem + "_nr.ppm"), *lastNrFrame);
            SavePpm(options.captureDir / (stem + "_display.ppm"), *lastDisplayFrame);
        };

        auto nextRuntimeLogCheck = std::chrono::steady_clock::now() + std::chrono::seconds(1);

        bool readyWritten = false;
        bool running = true;
        FramePixels reusableNrFrame;
        PerformanceStats performance;
        const auto visibleStart = std::chrono::steady_clock::now();
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
                        strength = std::max(0.0f, strength - 0.1f);
                    } else if (msg.wParam == Hotkeys::IncreaseId) {
                        strength = std::min(1.0f, strength + 0.1f);
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
            }

            const auto now = std::chrono::steady_clock::now();
            if (now < nextFrameAt) {
                std::this_thread::sleep_for(std::min(std::chrono::duration_cast<std::chrono::milliseconds>(nextFrameAt - now), std::chrono::milliseconds(5)));
                continue;
            }
            std::array<PerformanceStats::Clock::time_point, 7> stageTimes;
            stageTimes[0] = PerformanceStats::Clock::now();
            auto maybeFrame = capture.TryCapture();
            if (maybeFrame) {
                if (options.nativeResolution) {
                    ValidateNativeDimensions(*maybeFrame, options.width, options.height);
                }
                sourceActualWidth = maybeFrame->width;
                sourceActualHeight = maybeFrame->height;
                if (!options.freezeSource || !lastInputFrame) {
                    lastInputFrame = std::move(*maybeFrame);
                }
                ++capturedFrames;
            }
            if (!lastInputFrame) {
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                continue;
            }
            stageTimes[1] = PerformanceStats::Clock::now();
            if (!lastOriginalFrame || (maybeFrame && !options.freezeSource)) {
                lastOriginalFrame = PrepareInputFrame(*lastInputFrame, options);
            }
            const FramePixels& original = *lastOriginalFrame;
            stageTimes[2] = PerformanceStats::Clock::now();
            nrPresenter.Present(original);
            stageTimes[3] = PerformanceStats::Clock::now();
            if (!options.noProxy) {
                nrPresenter.CaptureBackBuffer(reusableNrFrame);
            }
            const FramePixels& nrFrame = options.noProxy ? original : reusableNrFrame;
            stageTimes[4] = PerformanceStats::Clock::now();
            if (options.saveCaptures) {
                lastNrFrame = nrFrame;
            }
            if (!options.noProxy && SourceMeaningfullyNonBlack(original) && !FrameHasNonBlackPixels(nrFrame)) {
                ++consecutiveBrokenNrFrames;
                effectEnabled = false;
                UpdateBridgeTitle(bridge, effectEnabled, strength, options.freezeSource);
                if (consecutiveBrokenNrFrames >= 30) {
                    throw std::runtime_error("DLSS-NR output stayed black for 30 presented frames while source was nonblack");
                }
            } else if (FrameHasNonBlackPixels(nrFrame) || !SourceMeaningfullyNonBlack(original)) {
                consecutiveBrokenNrFrames = 0;
            }
            // Full-strength NR and bypass need no extra full-frame copy. The
            // visible upload still forces opaque alpha, exactly as before.
            FramePixels blended;
            const FramePixels* display = &original;
            if (effectEnabled && strength >= 1.0f) {
                display = &nrFrame;
            } else if (effectEnabled && strength > 0.0f) {
                blended = BlendFrames(original, nrFrame, strength, true);
                display = &blended;
            }
            if (options.saveCaptures) {
                lastDisplayFrame = *display;
            }
            stageTimes[5] = PerformanceStats::Clock::now();
            visiblePresenter.Present(*display);
            stageTimes[6] = PerformanceStats::Clock::now();
            performance.Record(stageTimes);
            if (!readyWritten) {
                WriteReadyFileAtomic(options.readyFile, bridge);
                readyWritten = true;
            }
            nextFrameAt = now + frameInterval;

            if (pendingHotkeySnapshot) {
                saveSnapshot("hotkey", transitionSnapshots++);
                pendingHotkeySnapshot = false;
            }
            if (options.saveCaptures && savedFrames < 3) {
                saveSnapshot("frame", savedFrames);
                ++savedFrames;
            }
        }

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
