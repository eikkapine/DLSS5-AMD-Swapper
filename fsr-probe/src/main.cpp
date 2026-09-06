#include <windows.h>
#include <wrl/client.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <psapi.h>

#include <dx12/ffx_api_dx12.h>
#include <ffx_upscale.h>

#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT kBufferCount = 2;
constexpr DXGI_FORMAT kColorFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
constexpr DXGI_FORMAT kDepthFormat = DXGI_FORMAT_R32_FLOAT;
constexpr DXGI_FORMAT kMotionFormat = DXGI_FORMAT_R16G16_FLOAT;
constexpr DXGI_FORMAT kExposureFormat = DXGI_FORMAT_R32_FLOAT;

struct Options {
    bool fsr = false;
    bool proxy = false;
    bool visible = false;
    bool useFsrInputs = false;
    UINT startupDelayMs = 2000;
    UINT frames = 180;
    UINT width = 640;
    UINT height = 360;
    UINT seconds = 25;
    std::filesystem::path out = ".";
};

struct DiffStats {
    uint64_t changedPixels = 0;
    uint64_t channelAbsSum = 0;
    uint8_t maxChannelDelta = 0;
};

void Check(HRESULT hr, const char* what) {
    if (FAILED(hr)) {
        std::ostringstream os;
        os << what << " failed with HRESULT 0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(os.str());
    }
}

void CheckFfx(ffxReturnCode_t code, const char* what, std::ostringstream& log) {
    log << what << "=" << code << "\n";
    if (code != FFX_API_RETURN_OK) {
        std::ostringstream os;
        os << what << " failed with FFX return " << code;
        throw std::runtime_error(os.str());
    }
}

std::string NarrowAscii(const std::wstring& value) {
    std::string out;
    out.reserve(value.size());
    for (wchar_t ch : value) {
        out.push_back(ch >= 0 && ch <= 127 ? static_cast<char>(ch) : '?');
    }
    return out;
}

void FfxMessage(uint32_t type, const wchar_t* message) {
    std::ofstream log("ffx-debug.log", std::ios::app);
    log << "type=" << type << " message=" << NarrowAscii(message ? message : L"(null)") << "\n";
}

std::string ModuleList() {
    HMODULE modules[1024]{};
    DWORD bytesNeeded = 0;
    std::ostringstream os;
    bool hasSpecialK = false;
    bool hasReShade = false;
    bool hasDlssNr = false;
    bool hasFfxLoader = false;
    bool hasFfxUpscaler = false;
    if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &bytesNeeded)) {
        os << "module_dump_error=" << GetLastError() << "\n";
        return os.str();
    }

    const DWORD count = std::min<DWORD>(bytesNeeded / sizeof(HMODULE), static_cast<DWORD>(std::size(modules)));
    os << "module_count=" << count << "\n";
    for (DWORD i = 0; i < count; ++i) {
        wchar_t path[MAX_PATH]{};
        if (GetModuleFileNameExW(GetCurrentProcess(), modules[i], path, MAX_PATH) > 0) {
            std::string module = NarrowAscii(path);
            std::string lower = module;
            std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char ch) {
                return static_cast<char>(std::tolower(ch));
            });
            hasSpecialK = hasSpecialK || lower.find("specialk") != std::string::npos;
            hasReShade = hasReShade || lower.find("reshade") != std::string::npos;
            hasDlssNr = hasDlssNr || lower.find("nvngx_dlssnr.dll") != std::string::npos;
            hasFfxLoader = hasFfxLoader || lower.find("amd_fidelityfx_loader_dx12.dll") != std::string::npos;
            hasFfxUpscaler = hasFfxUpscaler || lower.find("amd_fidelityfx_upscaler_dx12.dll") != std::string::npos;
            os << "module=" << module << "\n";
        }
    }
    os << "module_has_specialk=" << (hasSpecialK ? 1 : 0) << "\n";
    os << "module_has_reshade=" << (hasReShade ? 1 : 0) << "\n";
    os << "module_has_nvngx_dlssnr=" << (hasDlssNr ? 1 : 0) << "\n";
    os << "module_has_ffx_loader=" << (hasFfxLoader ? 1 : 0) << "\n";
    os << "module_has_ffx_upscaler=" << (hasFfxUpscaler ? 1 : 0) << "\n";
    return os.str();
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

        if (arg == L"--fsr") {
            options.fsr = true;
        } else if (arg == L"--proxy") {
            options.proxy = true;
        } else if (arg == L"--use-fsr-inputs") {
            options.useFsrInputs = true;
        } else if (arg == L"--startup-delay-ms") {
            options.startupDelayMs = std::stoul(needValue(L"--startup-delay-ms"));
        } else if (arg == L"--visible") {
            options.visible = true;
        } else if (arg == L"--frames") {
            options.frames = std::max<UINT>(1, std::stoul(needValue(L"--frames")));
        } else if (arg == L"--width") {
            options.width = std::max<UINT>(64, std::stoul(needValue(L"--width")));
        } else if (arg == L"--height") {
            options.height = std::max<UINT>(64, std::stoul(needValue(L"--height")));
        } else if (arg == L"--seconds") {
            options.seconds = std::max<UINT>(1, std::stoul(needValue(L"--seconds")));
        } else if (arg == L"--out") {
            options.out = needValue(L"--out");
        } else {
            std::wstringstream ws;
            ws << L"Unknown argument: " << arg;
            throw std::runtime_error(NarrowAscii(ws.str()));
        }
    }
    return options;
}

std::vector<uint8_t> MakePattern(UINT width, UINT height, UINT frame) {
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    const UINT shift = frame % 48;
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            const bool checker = ((((x + shift) / 8) ^ (y / 8)) & 1) != 0;
            const uint8_t r = static_cast<uint8_t>((x * 255u) / std::max<UINT>(1, width - 1));
            const uint8_t g = static_cast<uint8_t>((y * 255u) / std::max<UINT>(1, height - 1));
            const uint8_t b = checker ? 235 : 30;
            const size_t index = (static_cast<size_t>(y) * width + x) * 4;
            pixels[index + 0] = r;
            pixels[index + 1] = g;
            pixels[index + 2] = b;
            pixels[index + 3] = 255;
        }
    }
    return pixels;
}

std::vector<uint8_t> MakeDepth(UINT width, UINT height) {
    std::vector<uint8_t> bytes(static_cast<size_t>(width) * height * sizeof(float));
    auto* depth = reinterpret_cast<float*>(bytes.data());
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            depth[static_cast<size_t>(y) * width + x] = 1.0f - (static_cast<float>(y) / static_cast<float>(std::max<UINT>(1, height - 1))) * 0.5f;
        }
    }
    return bytes;
}

std::vector<uint8_t> MakeMotion(UINT width, UINT height) {
    std::vector<uint8_t> bytes(static_cast<size_t>(width) * height * sizeof(uint16_t) * 2);
    std::fill(bytes.begin(), bytes.end(), uint8_t{0});
    return bytes;
}

void SavePpm(const std::filesystem::path& path, const std::vector<uint8_t>& rgba, UINT width, UINT height) {
    std::ofstream file(path, std::ios::binary);
    if (!file) {
        throw std::runtime_error("Could not open output image");
    }
    file << "P6\n" << width << " " << height << "\n255\n";
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            const size_t index = (static_cast<size_t>(y) * width + x) * 4;
            const char rgb[3] = {
                static_cast<char>(rgba[index + 0]),
                static_cast<char>(rgba[index + 1]),
                static_cast<char>(rgba[index + 2]),
            };
            file.write(rgb, sizeof(rgb));
        }
    }
}

DiffStats Diff(const std::vector<uint8_t>& a, const std::vector<uint8_t>& b) {
    DiffStats stats;
    const size_t pixels = std::min(a.size(), b.size()) / 4;
    for (size_t i = 0; i < pixels; ++i) {
        bool changed = false;
        for (size_t c = 0; c < 3; ++c) {
            const int delta = std::abs(static_cast<int>(a[i * 4 + c]) - static_cast<int>(b[i * 4 + c]));
            stats.channelAbsSum += static_cast<uint64_t>(delta);
            stats.maxChannelDelta = std::max<uint8_t>(stats.maxChannelDelta, static_cast<uint8_t>(delta));
            changed = changed || delta != 0;
        }
        if (changed) {
            ++stats.changedPixels;
        }
    }
    return stats;
}

LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

HWND CreateProbeWindow(HINSTANCE instance, UINT width, UINT height) {
    const wchar_t* className = L"DlssNrAmdFsrProbeWindow";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = className;
    RegisterClassExW(&wc);

    RECT rect{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    HWND hwnd = CreateWindowExW(
        WS_EX_TOOLWINDOW,
        className,
        L"DLSS-NR AMD FSR probe",
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
    return hwnd;
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

D3D12_RESOURCE_BARRIER UavBarrier(ID3D12Resource* resource) {
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    barrier.UAV.pResource = resource;
    return barrier;
}

UINT64 Align(UINT64 value, UINT64 alignment) {
    return (value + alignment - 1u) & ~(alignment - 1u);
}

class FfxApi {
public:
    void Load(std::ostringstream& log) {
        module_ = LoadLibraryW(L"amd_fidelityfx_loader_dx12.dll");
        if (!module_) {
            throw std::runtime_error("LoadLibraryW(amd_fidelityfx_loader_dx12.dll) failed");
        }
        createContext_ = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(module_, "ffxCreateContext"));
        destroyContext_ = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(module_, "ffxDestroyContext"));
        query_ = reinterpret_cast<PfnFfxQuery>(GetProcAddress(module_, "ffxQuery"));
        configure_ = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(module_, "ffxConfigure"));
        dispatch_ = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(module_, "ffxDispatch"));
        if (!createContext_ || !destroyContext_ || !query_ || !configure_ || !dispatch_) {
            throw std::runtime_error("AMD FidelityFX loader is missing one or more required exports");
        }
        log << "ffx_loader_loaded=1\n";
    }

    ~FfxApi() {
        if (context_) {
            destroyContext_(&context_, nullptr);
        }
        if (module_) {
            FreeLibrary(module_);
        }
    }

    void CreateUpscaler(ID3D12Device* device, UINT renderWidth, UINT renderHeight, UINT outputWidth, UINT outputHeight, std::ostringstream& log) {
        ffxConfigureDescGlobalDebug debug{};
        debug.header.type = FFX_API_CONFIGURE_DESC_TYPE_GLOBALDEBUG;
        debug.effectId = FFX_API_EFFECT_ID_UPSCALE;
        debug.fpMessage = FfxMessage;
        debug.debugLevel = FFX_API_CONFIGURE_GLOBALDEBUG_LEVEL_VERBOSE;
        log << "ffxConfigure(global_debug)=" << configure_(nullptr, &debug.header) << "\n";

        ffxQueryDescGetVersions versions{};
        versions.header.type = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
        versions.createDescType = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        versions.device = device;
        uint64_t versionCount = 0;
        versions.outputCount = &versionCount;
        ffxReturnCode_t versionCode = query_(nullptr, &versions.header);
        log << "ffxQuery(version_count)=" << versionCode << "\n";
        log << "ffx_version_count=" << versionCount << "\n";
        if (versionCode == FFX_API_RETURN_OK && versionCount > 0) {
            versionIds_.resize(static_cast<size_t>(versionCount));
            versionNames_.resize(static_cast<size_t>(versionCount));
            versions.versionIds = versionIds_.data();
            versions.versionNames = versionNames_.data();
            CheckFfx(query_(nullptr, &versions.header), "ffxQuery(versions)", log);
            for (size_t i = 0; i < versionIds_.size(); ++i) {
                log << "ffx_version[" << i << "]=" << versionIds_[i] << ",";
                log << (versionNames_[i] ? versionNames_[i] : "(null)") << "\n";
            }
        }

        ffxCreateContextDescUpscale create{};
        create.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
        create.flags = FFX_UPSCALE_ENABLE_AUTO_EXPOSURE |
                       FFX_UPSCALE_ENABLE_DEPTH_INVERTED |
                       FFX_UPSCALE_ENABLE_NON_LINEAR_COLORSPACE |
                       FFX_UPSCALE_ENABLE_DEBUG_CHECKING;
        create.maxRenderSize = {renderWidth, renderHeight};
        create.maxUpscaleSize = {outputWidth, outputHeight};
        create.fpMessage = FfxMessage;

        ffxCreateContextDescUpscaleVersion apiVersion{};
        apiVersion.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
        apiVersion.version = FFX_UPSCALER_VERSION;

        ffxCreateBackendDX12Desc backend{};
        backend.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
        backend.device = device;

        create.header.pNext = &apiVersion.header;
        apiVersion.header.pNext = &backend.header;

        CheckFfx(createContext_(&context_, &create.header, nullptr), "ffxCreateContext(upscale)", log);

        ffxQueryGetProviderVersion provider{};
        provider.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        log << "ffxQuery(provider_version)=" << query_(&context_, &provider.header) << "\n";
        log << "ffx_provider_version_id=" << provider.versionId << "\n";
        log << "ffx_provider_version_name=" << (provider.versionName ? provider.versionName : "(null)") << "\n";

        ffxQueryDescUpscaleGetResourceRequirements req{};
        req.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GET_RESOURCE_REQUIREMENTS;
        log << "ffxQuery(resource_requirements)=" << query_(&context_, &req.header) << "\n";
        log << "ffx_required_resources=" << req.required_resources << "\n";
        log << "ffx_optional_resources=" << req.optional_resources << "\n";
    }

    void Dispatch(ID3D12GraphicsCommandList* commandList,
                  ID3D12Resource* color,
                  ID3D12Resource* depth,
                  ID3D12Resource* motion,
                  ID3D12Resource* exposure,
                  ID3D12Resource* output,
                  UINT renderWidth,
                  UINT renderHeight,
                  UINT outputWidth,
                  UINT outputHeight,
                  UINT frame,
                  std::ostringstream& log) {
        ffxDispatchDescUpscale desc{};
        desc.header.type = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
        desc.commandList = commandList;
        desc.color = ffxApiGetResourceDX12(color, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.depth = ffxApiGetResourceDX12(depth, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.motionVectors = ffxApiGetResourceDX12(motion, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.exposure = ffxApiGetResourceDX12(exposure, FFX_API_RESOURCE_STATE_COMPUTE_READ);
        desc.output = ffxApiGetResourceDX12(output, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS, FFX_API_RESOURCE_USAGE_UAV);
        desc.jitterOffset = {0.0f, 0.0f};
        desc.motionVectorScale = {static_cast<float>(renderWidth), static_cast<float>(renderHeight)};
        desc.renderSize = {renderWidth, renderHeight};
        desc.upscaleSize = {outputWidth, outputHeight};
        desc.enableSharpening = true;
        desc.sharpness = 0.7f;
        desc.frameTimeDelta = 16.6f;
        desc.preExposure = 1.0f;
        desc.reset = frame == 0;
        desc.cameraNear = 0.1f;
        desc.cameraFar = 1000.0f;
        desc.cameraFovAngleVertical = 1.0471976f;
        desc.viewSpaceToMetersFactor = 1.0f;
        desc.flags = FFX_UPSCALE_FLAG_NON_LINEAR_COLOR_SRGB;

        ffxReturnCode_t code = dispatch_(&context_, &desc.header);
        if (code == FFX_API_RETURN_OK) {
            ++dispatchOk_;
        } else {
            ++dispatchFail_;
            lastDispatchCode_ = code;
        }
        if (frame < 8 || frame % 60 == 0 || code != FFX_API_RETURN_OK) {
            log << "ffxDispatch(frame=" << frame << ")=" << code << "\n";
        }
    }

    uint64_t dispatchOk() const { return dispatchOk_; }
    uint64_t dispatchFail() const { return dispatchFail_; }
    ffxReturnCode_t lastDispatchCode() const { return lastDispatchCode_; }

private:
    HMODULE module_ = nullptr;
    PfnFfxCreateContext createContext_ = nullptr;
    PfnFfxDestroyContext destroyContext_ = nullptr;
    PfnFfxConfigure configure_ = nullptr;
    PfnFfxQuery query_ = nullptr;
    PfnFfxDispatch dispatch_ = nullptr;
    ffxContext context_ = nullptr;
    std::vector<uint64_t> versionIds_;
    std::vector<const char*> versionNames_;
    uint64_t dispatchOk_ = 0;
    uint64_t dispatchFail_ = 0;
    ffxReturnCode_t lastDispatchCode_ = FFX_API_RETURN_OK;
};

class Probe {
public:
    explicit Probe(const Options& options)
        : options_(options),
          renderWidth_(std::max<UINT>(64, options.width / 2)),
          renderHeight_(std::max<UINT>(64, options.height / 2)),
          hwnd_(CreateProbeWindow(GetModuleHandleW(nullptr), options.width, options.height)) {
        std::filesystem::create_directories(options.out);
        if (options_.visible) {
            ShowWindow(hwnd_, SW_SHOWNORMAL);
            SetForegroundWindow(hwnd_);
            UpdateWindow(hwnd_);
        }
        CreateDevice();
        CreateSwapChain();
        CreateResources();
        if (options_.fsr) {
            ffx_.Load(ffxLog_);
            ffx_.CreateUpscaler(device_.Get(), renderWidth_, renderHeight_, options_.width, options_.height, ffxLog_);
        }
    }

    ~Probe() {
        if (fenceEvent_) {
            WaitForGpu();
            CloseHandle(fenceEvent_);
        }
        if (hwnd_) {
            DestroyWindow(hwnd_);
        }
    }

    void Run() {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(options_.seconds);
        const std::vector<UINT> captureFrames = {0, 1, std::max<UINT>(1, options_.frames / 2), options_.frames - 1};
        UINT captureIndex = 0;

        for (UINT frame = 0; frame < options_.frames && std::chrono::steady_clock::now() < deadline; ++frame) {
            MSG msg{};
            while (PeekMessageW(&msg, hwnd_, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            PopulateFrame(frame);
            if (captureIndex < captureFrames.size() && frame == captureFrames[captureIndex]) {
                CaptureCurrentBeforePresent(frame);
                ++captureIndex;
            }
            PrepareCurrentForPresent();
            RecordPresentResult(swapChain_->Present(1, 0));
            ++framesRendered_;
        }

        WaitForGpu();
        for (UINT i = 0; i < kBufferCount; ++i) {
            CaptureBufferToFile(i, options_.out / ("final_buffer" + std::to_string(i) + ".ppm"));
        }
        WriteReport();
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
                adapterName_ = desc.Description;
                break;
            }
        }
        if (!device_) {
            Check(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device_)), "D3D12CreateDevice");
            adapterName_ = L"default";
        }

        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device_->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue_)), "CreateCommandQueue");
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
        desc.Width = options_.width;
        desc.Height = options_.height;
        desc.Format = kColorFormat;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = kBufferCount;
        desc.SampleDesc.Count = 1;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        ComPtr<IDXGISwapChain1> sc1;
        Check(factory_->CreateSwapChainForHwnd(queue_.Get(), hwnd_, &desc, nullptr, nullptr, &sc1), "CreateSwapChainForHwnd");
        Check(sc1.As(&swapChain_), "Query IDXGISwapChain3");
        Check(factory_->MakeWindowAssociation(hwnd_, DXGI_MWA_NO_ALT_ENTER), "MakeWindowAssociation");
        Check(swapChain_->GetDesc(&swapChainDesc_), "GetDesc");
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        for (UINT i = 0; i < kBufferCount; ++i) {
            Check(swapChain_->GetBuffer(i, IID_PPV_ARGS(&backBuffers_[i])), "GetBuffer");
            backBufferDescs_[i] = backBuffers_[i]->GetDesc();
            bufferStates_[i] = D3D12_RESOURCE_STATE_PRESENT;
        }
    }

    void CreateResources() {
        color_ = CreateTexture("color", renderWidth_, renderHeight_, kColorFormat, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST, nullptr);
        colorState_ = D3D12_RESOURCE_STATE_COPY_DEST;
        depth_ = CreateTexture("depth", renderWidth_, renderHeight_, kDepthFormat, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST, nullptr);
        depthState_ = D3D12_RESOURCE_STATE_COPY_DEST;
        motion_ = CreateTexture("motion", renderWidth_, renderHeight_, kMotionFormat, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST, nullptr);
        motionState_ = D3D12_RESOURCE_STATE_COPY_DEST;
        exposure_ = CreateTexture("exposure", 1, 1, kExposureFormat, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST, nullptr);
        exposureState_ = D3D12_RESOURCE_STATE_COPY_DEST;

        output_ = CreateTexture("output", options_.width, options_.height, kColorFormat, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, nullptr);
        outputState_ = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;

        const UINT64 maxUpload = std::max({UploadBytes(renderWidth_, renderHeight_, 4), UploadBytes(1, 1, 4)});
        D3D12_HEAP_PROPERTIES uploadHeap{};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC uploadDesc{};
        uploadDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        uploadDesc.Width = maxUpload;
        uploadDesc.Height = 1;
        uploadDesc.DepthOrArraySize = 1;
        uploadDesc.MipLevels = 1;
        uploadDesc.SampleDesc.Count = 1;
        uploadDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Check(device_->CreateCommittedResource(&uploadHeap,
                                               D3D12_HEAP_FLAG_NONE,
                                               &uploadDesc,
                                               D3D12_RESOURCE_STATE_GENERIC_READ,
                                               nullptr,
                                               IID_PPV_ARGS(&upload_)),
              "CreateCommittedResource upload");

        readbackRowPitch_ = Align(static_cast<UINT64>(options_.width) * 4, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
        D3D12_HEAP_PROPERTIES readbackHeap{};
        readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC readbackDesc = uploadDesc;
        readbackDesc.Width = readbackRowPitch_ * options_.height;
        Check(device_->CreateCommittedResource(&readbackHeap,
                                               D3D12_HEAP_FLAG_NONE,
                                               &readbackDesc,
                                               D3D12_RESOURCE_STATE_COPY_DEST,
                                               nullptr,
                                               IID_PPV_ARGS(&readback_)),
              "CreateCommittedResource readback");

        const auto depth = MakeDepth(renderWidth_, renderHeight_);
        const auto motion = MakeMotion(renderWidth_, renderHeight_);
        const float exposureValue = 1.0f;
        UploadToTexture(depth_.Get(), depthState_, depth.data(), renderWidth_, renderHeight_, 4, kDepthFormat, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        UploadToTexture(motion_.Get(), motionState_, motion.data(), renderWidth_, renderHeight_, 4, kMotionFormat, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        UploadToTexture(exposure_.Get(), exposureState_, reinterpret_cast<const uint8_t*>(&exposureValue), 1, 1, 4, kExposureFormat, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    }

    ComPtr<ID3D12Resource> CreateTexture(const char* name, UINT width, UINT height, DXGI_FORMAT format, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state, const D3D12_CLEAR_VALUE* clear) {
        D3D12_HEAP_PROPERTIES heap{};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width;
        desc.Height = height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        desc.Flags = flags;
        ComPtr<ID3D12Resource> resource;
        HRESULT hr = device_->CreateCommittedResource(&heap,
                                                      D3D12_HEAP_FLAG_NONE,
                                                      &desc,
                                                      state,
                                                      clear,
                                                      IID_PPV_ARGS(&resource));
        if (FAILED(hr)) {
            std::ostringstream os;
            os << "CreateCommittedResource texture name=" << name
               << " width=" << width
               << " height=" << height
               << " format=" << static_cast<unsigned int>(format)
               << " flags=0x" << std::hex << static_cast<unsigned int>(flags)
               << " initial_state=0x" << static_cast<unsigned int>(state)
               << " HRESULT=0x" << static_cast<unsigned long>(hr);
            throw std::runtime_error(os.str());
        }
        return resource;
    }

    static UINT64 UploadBytes(UINT width, UINT height, UINT bytesPerPixel) {
        return Align(static_cast<UINT64>(width) * bytesPerPixel, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * height;
    }

    void ResetCommands() {
        Check(allocator_->Reset(), "allocator Reset");
        Check(commandList_->Reset(allocator_.Get(), nullptr), "commandList Reset");
    }

    void ExecuteCommands() {
        Check(commandList_->Close(), "Close commandList");
        ID3D12CommandList* lists[] = {commandList_.Get()};
        queue_->ExecuteCommandLists(1, lists);
        WaitForGpu();
    }

    void TransitionResource(ID3D12Resource* resource, D3D12_RESOURCE_STATES& current, D3D12_RESOURCE_STATES next) {
        if (current == next) {
            return;
        }
        auto barrier = Transition(resource, current, next);
        commandList_->ResourceBarrier(1, &barrier);
        current = next;
    }

    void SetBackBufferState(UINT index, D3D12_RESOURCE_STATES state) {
        if (bufferStates_[index] == state) {
            return;
        }
        auto barrier = Transition(backBuffers_[index].Get(), bufferStates_[index], state);
        commandList_->ResourceBarrier(1, &barrier);
        bufferStates_[index] = state;
    }

    void UploadToTexture(ID3D12Resource* texture,
                         D3D12_RESOURCE_STATES& textureState,
                         const uint8_t* bytes,
                         UINT width,
                         UINT height,
                         UINT bytesPerPixel,
                         DXGI_FORMAT format,
                         D3D12_RESOURCE_STATES finalState) {
        ResetCommands();
        TransitionResource(texture, textureState, D3D12_RESOURCE_STATE_COPY_DEST);
        void* mapped = nullptr;
        Check(upload_->Map(0, nullptr, &mapped), "Map upload");
        const UINT64 rowPitch = Align(static_cast<UINT64>(width) * bytesPerPixel, D3D12_TEXTURE_DATA_PITCH_ALIGNMENT);
        auto* dst = static_cast<uint8_t*>(mapped);
        for (UINT y = 0; y < height; ++y) {
            std::memcpy(dst + static_cast<size_t>(y) * rowPitch,
                        bytes + static_cast<size_t>(y) * width * bytesPerPixel,
                        static_cast<size_t>(width) * bytesPerPixel);
        }
        upload_->Unmap(0, nullptr);

        D3D12_TEXTURE_COPY_LOCATION dstLoc{};
        dstLoc.pResource = texture;
        dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dstLoc.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION srcLoc{};
        srcLoc.pResource = upload_.Get();
        srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        srcLoc.PlacedFootprint.Footprint.Format = format;
        srcLoc.PlacedFootprint.Footprint.Width = width;
        srcLoc.PlacedFootprint.Footprint.Height = height;
        srcLoc.PlacedFootprint.Footprint.Depth = 1;
        srcLoc.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(rowPitch);

        commandList_->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, nullptr);
        TransitionResource(texture, textureState, finalState);
        ExecuteCommands();
    }

    void PopulateFrame(UINT frame) {
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        latestSource_ = MakePattern(renderWidth_, renderHeight_, frame);
        UploadToTexture(color_.Get(), colorState_, latestSource_.data(), renderWidth_, renderHeight_, 4, kColorFormat, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);

        ResetCommands();
        if (options_.fsr) {
            TransitionResource(color_.Get(), colorState_, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            TransitionResource(depth_.Get(), depthState_, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            TransitionResource(motion_.Get(), motionState_, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            TransitionResource(exposure_.Get(), exposureState_, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
            TransitionResource(output_.Get(), outputState_, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            ffx_.Dispatch(commandList_.Get(),
                          color_.Get(),
                          depth_.Get(),
                          motion_.Get(),
                          exposure_.Get(),
                          output_.Get(),
                          renderWidth_,
                          renderHeight_,
                          options_.width,
                          options_.height,
                          frame,
                          ffxLog_);
            auto uav = UavBarrier(output_.Get());
            commandList_->ResourceBarrier(1, &uav);
            TransitionResource(output_.Get(), outputState_, D3D12_RESOURCE_STATE_COPY_SOURCE);
        } else {
            TransitionResource(color_.Get(), colorState_, D3D12_RESOURCE_STATE_COPY_SOURCE);
        }

        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_COPY_DEST);
        if (options_.fsr) {
            commandList_->CopyResource(backBuffers_[frameIndex_].Get(), output_.Get());
        } else {
            D3D12_TEXTURE_COPY_LOCATION dst{};
            dst.pResource = backBuffers_[frameIndex_].Get();
            dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            dst.SubresourceIndex = 0;
            D3D12_TEXTURE_COPY_LOCATION src{};
            src.pResource = color_.Get();
            src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            src.SubresourceIndex = 0;
            commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        }
        ExecuteCommands();
        lastPresented_ = frameIndex_;
    }

    void PrepareCurrentForPresent() {
        ResetCommands();
        SetBackBufferState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands();
    }

    void CaptureCurrentBeforePresent(UINT frame) {
        auto pixels = CaptureBuffer(frameIndex_);
        if (baseline_.empty()) {
            baseline_ = pixels;
            SavePpm(options_.out / "baseline.ppm", baseline_, options_.width, options_.height);
        }
        auto stats = Diff(baseline_, pixels);

        std::ostringstream name;
        name << "pre_present_frame" << std::setw(3) << std::setfill('0') << frame << "_buffer" << frameIndex_ << ".ppm";
        SavePpm(options_.out / name.str(), pixels, options_.width, options_.height);

        maxChangedPixels_ = std::max(maxChangedPixels_, stats.changedPixels);
        maxAbsSum_ = std::max(maxAbsSum_, stats.channelAbsSum);
        maxChannelDelta_ = std::max(maxChannelDelta_, stats.maxChannelDelta);
        captureLines_ << "pre_present frame=" << frame
                      << " buffer=" << frameIndex_
                      << " changed_pixels_vs_baseline=" << stats.changedPixels
                      << " channel_abs_sum_vs_baseline=" << stats.channelAbsSum
                      << " max_channel_delta_vs_baseline=" << static_cast<int>(stats.maxChannelDelta)
                      << "\n";
    }

    std::vector<uint8_t> CaptureBuffer(UINT index) {
        ResetCommands();
        SetBackBufferState(index, D3D12_RESOURCE_STATE_COPY_SOURCE);

        D3D12_TEXTURE_COPY_LOCATION src{};
        src.pResource = backBuffers_[index].Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = readback_.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint.Footprint.Format = kColorFormat;
        dst.PlacedFootprint.Footprint.Width = options_.width;
        dst.PlacedFootprint.Footprint.Height = options_.height;
        dst.PlacedFootprint.Footprint.Depth = 1;
        dst.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(readbackRowPitch_);

        commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        SetBackBufferState(index, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands();

        std::vector<uint8_t> pixels(static_cast<size_t>(options_.width) * options_.height * 4);
        void* mapped = nullptr;
        Check(readback_->Map(0, nullptr, &mapped), "Map readback");
        const auto* bytes = static_cast<const uint8_t*>(mapped);
        for (UINT y = 0; y < options_.height; ++y) {
            std::memcpy(pixels.data() + static_cast<size_t>(y) * options_.width * 4,
                        bytes + static_cast<size_t>(y) * readbackRowPitch_,
                        static_cast<size_t>(options_.width) * 4);
        }
        readback_->Unmap(0, nullptr);
        return pixels;
    }

    void CaptureBufferToFile(UINT index, const std::filesystem::path& path) {
        auto pixels = CaptureBuffer(index);
        SavePpm(path, pixels, options_.width, options_.height);
    }

    void WaitForGpu() {
        const UINT64 value = ++fenceValue_;
        Check(queue_->Signal(fence_.Get(), value), "fence Signal");
        if (fence_->GetCompletedValue() < value) {
            Check(fence_->SetEventOnCompletion(value, fenceEvent_), "SetEventOnCompletion");
            WaitForSingleObject(fenceEvent_, INFINITE);
        }
    }

    void RecordPresentResult(HRESULT result) {
        if (result == S_OK) {
            ++presentOkCount_;
        } else if (result == DXGI_STATUS_OCCLUDED) {
            ++presentOccludedCount_;
        } else if (SUCCEEDED(result)) {
            ++presentOtherSuccessCount_;
            lastPresentOtherSuccess_ = result;
        } else {
            ++presentFailureCount_;
            lastPresentFailure_ = result;
        }
        lastPresentResult_ = result;
    }

    void WriteReport() {
        std::ofstream report(options_.out / "report.txt", std::ios::binary);
        if (!report) {
            throw std::runtime_error("Could not write report.txt");
        }
        report << "fsr_requested=" << (options_.fsr ? 1 : 0) << "\n";
        report << "proxy_requested=" << (options_.proxy ? 1 : 0) << "\n";
        report << "use_fsr_inputs_requested=" << (options_.useFsrInputs ? 1 : 0) << "\n";
        report << "visible=" << (options_.visible ? 1 : 0) << "\n";
        report << "adapter=" << NarrowAscii(adapterName_) << "\n";
        report << "render_width=" << renderWidth_ << "\n";
        report << "render_height=" << renderHeight_ << "\n";
        report << "output_width=" << options_.width << "\n";
        report << "output_height=" << options_.height << "\n";
        report << "swapchain_desc_width=" << swapChainDesc_.BufferDesc.Width << "\n";
        report << "swapchain_desc_height=" << swapChainDesc_.BufferDesc.Height << "\n";
        report << "swapchain_desc_format=" << static_cast<unsigned int>(swapChainDesc_.BufferDesc.Format) << "\n";
        for (UINT i = 0; i < kBufferCount; ++i) {
            report << "backbuffer" << i << "_width=" << backBufferDescs_[i].Width << "\n";
            report << "backbuffer" << i << "_height=" << backBufferDescs_[i].Height << "\n";
            report << "backbuffer" << i << "_format=" << static_cast<unsigned int>(backBufferDescs_[i].Format) << "\n";
        }
        report << "seconds_limit=" << options_.seconds << "\n";
        report << "frames_rendered=" << framesRendered_ << "\n";
        report << "ffx_dispatch_ok_count=" << ffx_.dispatchOk() << "\n";
        report << "ffx_dispatch_fail_count=" << ffx_.dispatchFail() << "\n";
        report << "ffx_last_dispatch_code=" << ffx_.lastDispatchCode() << "\n";
        report << "last_presented_buffer=" << lastPresented_ << "\n";
        report << "present_ok_count=" << presentOkCount_ << "\n";
        report << "present_occluded_count=" << presentOccludedCount_ << "\n";
        report << "present_other_success_count=" << presentOtherSuccessCount_ << "\n";
        report << "present_failure_count=" << presentFailureCount_ << "\n";
        report << "last_present_result=0x" << std::hex << static_cast<unsigned long>(lastPresentResult_) << std::dec << "\n";
        report << "last_present_other_success=0x" << std::hex << static_cast<unsigned long>(lastPresentOtherSuccess_) << std::dec << "\n";
        report << "last_present_failure=0x" << std::hex << static_cast<unsigned long>(lastPresentFailure_) << std::dec << "\n";
        report << "device_removed_reason=0x" << std::hex << static_cast<unsigned long>(device_->GetDeviceRemovedReason()) << std::dec << "\n";
        report << "max_changed_pixels_vs_baseline=" << maxChangedPixels_ << "\n";
        report << "max_channel_abs_sum_vs_baseline=" << maxAbsSum_ << "\n";
        report << "max_channel_delta_vs_baseline=" << static_cast<int>(maxChannelDelta_) << "\n";
        report << ffxLog_.str();
        report << ModuleList();
        report << captureLines_.str();
    }

    Options options_;
    UINT renderWidth_ = 0;
    UINT renderHeight_ = 0;
    HWND hwnd_ = nullptr;
    std::vector<uint8_t> latestSource_;
    std::vector<uint8_t> baseline_;
    std::wstring adapterName_;
    ComPtr<IDXGIFactory4> factory_;
    ComPtr<ID3D12Device> device_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<IDXGISwapChain3> swapChain_;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> commandList_;
    ComPtr<ID3D12Fence> fence_;
    DXGI_SWAP_CHAIN_DESC swapChainDesc_{};
    D3D12_RESOURCE_DESC backBufferDescs_[kBufferCount]{};
    HANDLE fenceEvent_ = nullptr;
    UINT64 fenceValue_ = 0;
    ComPtr<ID3D12Resource> backBuffers_[kBufferCount];
    D3D12_RESOURCE_STATES bufferStates_[kBufferCount]{};
    ComPtr<ID3D12Resource> color_;
    D3D12_RESOURCE_STATES colorState_ = D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D12Resource> depth_;
    D3D12_RESOURCE_STATES depthState_ = D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D12Resource> motion_;
    D3D12_RESOURCE_STATES motionState_ = D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D12Resource> exposure_;
    D3D12_RESOURCE_STATES exposureState_ = D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D12Resource> output_;
    D3D12_RESOURCE_STATES outputState_ = D3D12_RESOURCE_STATE_COMMON;
    ComPtr<ID3D12Resource> upload_;
    ComPtr<ID3D12Resource> readback_;
    UINT64 readbackRowPitch_ = 0;
    UINT frameIndex_ = 0;
    UINT lastPresented_ = 0;
    UINT framesRendered_ = 0;
    uint64_t presentOkCount_ = 0;
    uint64_t presentOccludedCount_ = 0;
    uint64_t presentOtherSuccessCount_ = 0;
    uint64_t presentFailureCount_ = 0;
    HRESULT lastPresentResult_ = S_OK;
    HRESULT lastPresentOtherSuccess_ = S_OK;
    HRESULT lastPresentFailure_ = S_OK;
    uint64_t maxChangedPixels_ = 0;
    uint64_t maxAbsSum_ = 0;
    uint8_t maxChannelDelta_ = 0;
    std::ostringstream captureLines_;
    std::ostringstream ffxLog_;
    FfxApi ffx_;
};

} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        auto options = ParseOptions(argc, argv);
        std::filesystem::create_directories(options.out);

        if (options.proxy) {
            HMODULE proxy = LoadLibraryW(L"version.dll");
            if (!proxy) {
                throw std::runtime_error("LoadLibraryW(version.dll) failed");
            }
            if (options.startupDelayMs > 0) {
                Sleep(options.startupDelayMs);
            }
        }

        Probe probe(options);
        probe.Run();
        return 0;
    } catch (const std::exception& ex) {
        std::ofstream error("probe-error.txt", std::ios::app);
        error << ex.what() << "\n";
        std::cerr << ex.what() << "\n";
        return 1;
    }
}
