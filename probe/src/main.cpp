#include <windows.h>
#include <wrl/client.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <psapi.h>

#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstring>
#include <cstdint>
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

struct Options {
    bool proxy = false;
    bool visible = false;
    UINT frames = 700;
    UINT width = 640;
    UINT height = 360;
    UINT seconds = 25;
    UINT startupDelayMs = 2000;
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

std::string NarrowAscii(const std::wstring& value) {
    std::string out;
    out.reserve(value.size());
    for (wchar_t ch : value) {
        out.push_back(ch >= 0 && ch <= 127 ? static_cast<char>(ch) : '?');
    }
    return out;
}

std::string ModuleList() {
    HMODULE modules[1024]{};
    DWORD bytesNeeded = 0;
    std::ostringstream os;
    bool hasSpecialK = false;
    bool hasReShade = false;
    bool hasDlssNr = false;
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
            os << "module=" << module << "\n";
        }
    }
    os << "module_has_specialk=" << (hasSpecialK ? 1 : 0) << "\n";
    os << "module_has_reshade=" << (hasReShade ? 1 : 0) << "\n";
    os << "module_has_nvngx_dlssnr=" << (hasDlssNr ? 1 : 0) << "\n";
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

        if (arg == L"--proxy") {
            options.proxy = true;
        } else if (arg == L"--visible") {
            options.visible = true;
        } else if (arg == L"--frames") {
            options.frames = std::max<UINT>(1, std::stoul(needValue(L"--frames")));
        } else if (arg == L"--width") {
            options.width = std::max<UINT>(16, std::stoul(needValue(L"--width")));
        } else if (arg == L"--height") {
            options.height = std::max<UINT>(16, std::stoul(needValue(L"--height")));
        } else if (arg == L"--seconds") {
            options.seconds = std::max<UINT>(1, std::stoul(needValue(L"--seconds")));
        } else if (arg == L"--startup-delay-ms") {
            options.startupDelayMs = std::stoul(needValue(L"--startup-delay-ms"));
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

std::vector<uint8_t> MakePattern(UINT width, UINT height) {
    std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            const bool checker = (((x / 16) ^ (y / 16)) & 1) != 0;
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

HWND CreateProbeWindow(HINSTANCE instance, UINT width, UINT height, bool visible) {
    const wchar_t* className = L"DlssNrAmdD3D12ProbeWindow";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = className;
    RegisterClassExW(&wc);

    HWND hwnd = CreateWindowExW(
        visible ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW,
        className,
        L"DLSS-NR AMD D3D12 probe",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        static_cast<int>(width),
        static_cast<int>(height),
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

class Probe {
public:
    explicit Probe(const Options& options)
        : options_(options),
          hwnd_(CreateProbeWindow(GetModuleHandleW(nullptr), options.width, options.height, options.visible)),
          expected_(MakePattern(options.width, options.height)) {
        std::filesystem::create_directories(options.out);
        SavePpm(options.out / "expected.ppm", expected_, options.width, options.height);
        if (options_.visible) {
            ShowWindow(hwnd_, SW_SHOWNORMAL);
            SetForegroundWindow(hwnd_);
            UpdateWindow(hwnd_);
        }
        CreateDevice();
        CreateSwapChain();
        CreateResources();
    }

    ~Probe() {
        if (fenceEvent_) {
            CloseHandle(fenceEvent_);
        }
        if (hwnd_) {
            DestroyWindow(hwnd_);
        }
    }

    void Run() {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(options_.seconds);
        UINT capturedFrame = 0;
        for (UINT frame = 0; frame < options_.frames && std::chrono::steady_clock::now() < deadline; ++frame) {
            PumpMessages();
            PopulateFrame(frame);
            if (frame == 0 || frame == 1 || frame == 300 || frame + 1 == options_.frames) {
                CaptureCurrentBeforePresent(frame);
            } else {
                PrepareCurrentForPresent();
            }
            const HRESULT presentResult = swapChain_->Present(0, 0);
            RecordPresentResult(presentResult);
            if (FAILED(presentResult)) {
                Check(presentResult, "Present");
            }
            WaitForGpu();

            if (frame == 0 || frame == 1 || frame + 1 == options_.frames || frame == 300 || frame == 450 || frame == 650) {
                CaptureAllBuffers(frame, capturedFrame++);
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(16));
        }
        WriteReport();
    }

private:
    void PumpMessages() {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    void CreateDevice() {
        UINT flags = 0;
        Check(CreateDXGIFactory2(flags, IID_PPV_ARGS(&factory_)), "CreateDXGIFactory2");

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
            adapterName_ = L"default adapter";
        }
        Check(device_->QueryInterface(IID_PPV_ARGS(&deviceIdentity_)), "device QueryInterface IUnknown");

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
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = kBufferCount;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

        ComPtr<IDXGISwapChain1> sc1;
        Check(factory_->CreateSwapChainForHwnd(queue_.Get(), hwnd_, &desc, nullptr, nullptr, &sc1), "CreateSwapChainForHwnd");
        Check(sc1.As(&swapChain_), "Query IDXGISwapChain3");
        Check(swapChain_->QueryInterface(IID_PPV_ARGS(&swapChainIdentity_)), "swapchain QueryInterface IUnknown");
        swapChainDeviceHr_ = swapChain_->GetDevice(IID_PPV_ARGS(&swapChainDevice_));
        if (SUCCEEDED(swapChainDeviceHr_)) {
            swapChainDeviceIdentityHr_ = swapChainDevice_->QueryInterface(IID_PPV_ARGS(&swapChainDeviceIdentity_));
        } else {
            swapChainDeviceIdentityHr_ = swapChainDeviceHr_;
        }
        Check(swapChain_->GetDesc(&swapChainDesc_), "GetDesc");
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();

        for (UINT i = 0; i < kBufferCount; ++i) {
            Check(swapChain_->GetBuffer(i, IID_PPV_ARGS(&backBuffers_[i])), "GetBuffer");
            backBufferDescs_[i] = backBuffers_[i]->GetDesc();
            bufferStates_[i] = D3D12_RESOURCE_STATE_PRESENT;
        }
    }

    void CreateResources() {
        const UINT64 uploadRowPitch = Align256(static_cast<UINT64>(options_.width) * 4);
        const UINT64 uploadSize = uploadRowPitch * options_.height;

        D3D12_HEAP_PROPERTIES uploadHeap{};
        uploadHeap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC uploadDesc{};
        uploadDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        uploadDesc.Width = uploadSize;
        uploadDesc.Height = 1;
        uploadDesc.DepthOrArraySize = 1;
        uploadDesc.MipLevels = 1;
        uploadDesc.SampleDesc.Count = 1;
        uploadDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        Check(device_->CreateCommittedResource(
                  &uploadHeap,
                  D3D12_HEAP_FLAG_NONE,
                  &uploadDesc,
                  D3D12_RESOURCE_STATE_GENERIC_READ,
                  nullptr,
                  IID_PPV_ARGS(&upload_)),
              "CreateCommittedResource upload");

        void* mapped = nullptr;
        Check(upload_->Map(0, nullptr, &mapped), "Map upload");
        auto* uploadBytes = static_cast<uint8_t*>(mapped);
        for (UINT y = 0; y < options_.height; ++y) {
            std::memcpy(
                uploadBytes + static_cast<size_t>(y) * uploadRowPitch,
                expected_.data() + static_cast<size_t>(y) * options_.width * 4,
                static_cast<size_t>(options_.width) * 4);
        }
        upload_->Unmap(0, nullptr);

        D3D12_HEAP_PROPERTIES readbackHeap{};
        readbackHeap.Type = D3D12_HEAP_TYPE_READBACK;
        D3D12_RESOURCE_DESC readbackDesc = uploadDesc;
        const UINT64 rowPitch = Align256(static_cast<UINT64>(options_.width) * 4);
        readbackDesc.Width = rowPitch * options_.height;
        readbackRowPitch_ = rowPitch;
        Check(device_->CreateCommittedResource(
                  &readbackHeap,
                  D3D12_HEAP_FLAG_NONE,
                  &readbackDesc,
                  D3D12_RESOURCE_STATE_COPY_DEST,
                  nullptr,
                  IID_PPV_ARGS(&readback_)),
              "CreateCommittedResource readback");
    }

    static UINT64 Align256(UINT64 value) {
        return (value + 255u) & ~255ull;
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

    void SetState(UINT index, D3D12_RESOURCE_STATES state) {
        if (bufferStates_[index] == state) {
            return;
        }
        auto barrier = Transition(backBuffers_[index].Get(), bufferStates_[index], state);
        commandList_->ResourceBarrier(1, &barrier);
        bufferStates_[index] = state;
    }

    void PopulateFrame(UINT frame) {
        frameIndex_ = swapChain_->GetCurrentBackBufferIndex();
        ResetCommands();
        SetState(frameIndex_, D3D12_RESOURCE_STATE_COPY_DEST);

        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = backBuffers_[frameIndex_].Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        dst.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION src{};
        src.pResource = upload_.Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        src.PlacedFootprint.Footprint.Width = options_.width;
        src.PlacedFootprint.Footprint.Height = options_.height;
        src.PlacedFootprint.Footprint.Depth = 1;
        src.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(Align256(static_cast<UINT64>(options_.width) * 4));

        commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        ExecuteCommands();
        lastPresented_ = frameIndex_;
        framesRendered_ = frame + 1;
    }

    void PrepareCurrentForPresent() {
        ResetCommands();
        SetState(frameIndex_, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands();
    }

    void CaptureCurrentBeforePresent(UINT frame) {
        auto pixels = CaptureBuffer(frameIndex_);
        auto stats = Diff(expected_, pixels);

        std::ostringstream name;
        name << "pre_present_frame" << std::setw(3) << std::setfill('0') << frame << "_buffer" << frameIndex_ << ".ppm";
        SavePpm(options_.out / name.str(), pixels, options_.width, options_.height);

        captureLines_ << "pre_present frame=" << frame
                      << " buffer=" << frameIndex_
                      << " changed_pixels=" << stats.changedPixels
                      << " channel_abs_sum=" << stats.channelAbsSum
                      << " max_channel_delta=" << static_cast<int>(stats.maxChannelDelta)
                      << "\n";
    }

    std::vector<uint8_t> CaptureBuffer(UINT index) {
        ResetCommands();
        SetState(index, D3D12_RESOURCE_STATE_COPY_SOURCE);

        D3D12_TEXTURE_COPY_LOCATION src{};
        src.pResource = backBuffers_[index].Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        src.SubresourceIndex = 0;

        D3D12_TEXTURE_COPY_LOCATION dst{};
        dst.pResource = readback_.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        dst.PlacedFootprint.Footprint.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        dst.PlacedFootprint.Footprint.Width = options_.width;
        dst.PlacedFootprint.Footprint.Height = options_.height;
        dst.PlacedFootprint.Footprint.Depth = 1;
        dst.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(readbackRowPitch_);

        commandList_->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        SetState(index, D3D12_RESOURCE_STATE_PRESENT);
        ExecuteCommands();

        std::vector<uint8_t> pixels(static_cast<size_t>(options_.width) * options_.height * 4);
        void* mapped = nullptr;
        Check(readback_->Map(0, nullptr, &mapped), "Map readback");
        const auto* bytes = static_cast<const uint8_t*>(mapped);
        for (UINT y = 0; y < options_.height; ++y) {
            std::memcpy(
                pixels.data() + static_cast<size_t>(y) * options_.width * 4,
                bytes + static_cast<size_t>(y) * readbackRowPitch_,
                static_cast<size_t>(options_.width) * 4);
        }
        readback_->Unmap(0, nullptr);
        return pixels;
    }

    void CaptureAllBuffers(UINT frame, UINT captureIndex) {
        for (UINT i = 0; i < kBufferCount; ++i) {
            auto pixels = CaptureBuffer(i);
            auto stats = Diff(expected_, pixels);
            maxAllChangedPixels_ = std::max(maxAllChangedPixels_, stats.changedPixels);
            maxAllAbsSum_ = std::max(maxAllAbsSum_, stats.channelAbsSum);
            maxAllChannelDelta_ = std::max(maxAllChannelDelta_, stats.maxChannelDelta);
            if (!(frame == 0 && i != lastPresented_)) {
                maxReadiedChangedPixels_ = std::max(maxReadiedChangedPixels_, stats.changedPixels);
                maxReadiedAbsSum_ = std::max(maxReadiedAbsSum_, stats.channelAbsSum);
                maxReadiedChannelDelta_ = std::max(maxReadiedChannelDelta_, stats.maxChannelDelta);
            }

            std::ostringstream name;
            name << "capture_frame" << std::setw(3) << std::setfill('0') << frame << "_buffer" << i << ".ppm";
            SavePpm(options_.out / name.str(), pixels, options_.width, options_.height);

            captureLines_ << "capture=" << captureIndex
                          << " frame=" << frame
                          << " buffer=" << i
                          << " changed_pixels=" << stats.changedPixels
                          << " channel_abs_sum=" << stats.channelAbsSum
                          << " max_channel_delta=" << static_cast<int>(stats.maxChannelDelta)
                          << "\n";
        }
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
        std::string adapter = NarrowAscii(adapterName_);
        report << "proxy_requested=" << (options_.proxy ? 1 : 0) << "\n";
        report << "visible=" << (options_.visible ? 1 : 0) << "\n";
        report << "startup_delay_ms=" << options_.startupDelayMs << "\n";
        report << "adapter=" << adapter << "\n";
        report << "host_device_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(device_.Get()) << std::dec << "\n";
        report << "host_device_iunknown_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(deviceIdentity_.Get()) << std::dec << "\n";
        report << "swapchain_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(swapChain_.Get()) << std::dec << "\n";
        report << "swapchain_iunknown_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(swapChainIdentity_.Get()) << std::dec << "\n";
        report << "swapchain_get_device_hr=0x" << std::hex << static_cast<unsigned long>(swapChainDeviceHr_) << std::dec << "\n";
        report << "swapchain_device_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(swapChainDevice_.Get()) << std::dec << "\n";
        report << "swapchain_device_iunknown_hr=0x" << std::hex << static_cast<unsigned long>(swapChainDeviceIdentityHr_) << std::dec << "\n";
        report << "swapchain_device_iunknown_ptr=0x" << std::hex << reinterpret_cast<uintptr_t>(swapChainDeviceIdentity_.Get()) << std::dec << "\n";
        report << "width=" << options_.width << "\n";
        report << "height=" << options_.height << "\n";
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
        report << "last_presented_buffer=" << lastPresented_ << "\n";
        report << "present_ok_count=" << presentOkCount_ << "\n";
        report << "present_occluded_count=" << presentOccludedCount_ << "\n";
        report << "present_other_success_count=" << presentOtherSuccessCount_ << "\n";
        report << "present_failure_count=" << presentFailureCount_ << "\n";
        report << "last_present_result=0x" << std::hex << static_cast<unsigned long>(lastPresentResult_) << std::dec << "\n";
        report << "last_present_other_success=0x" << std::hex << static_cast<unsigned long>(lastPresentOtherSuccess_) << std::dec << "\n";
        report << "last_present_failure=0x" << std::hex << static_cast<unsigned long>(lastPresentFailure_) << std::dec << "\n";
        report << "device_removed_reason=0x" << std::hex << static_cast<unsigned long>(device_->GetDeviceRemovedReason()) << std::dec << "\n";
        report << "max_all_changed_pixels_vs_expected=" << maxAllChangedPixels_ << "\n";
        report << "max_all_channel_abs_sum_vs_expected=" << maxAllAbsSum_ << "\n";
        report << "max_all_channel_delta_vs_expected=" << static_cast<int>(maxAllChannelDelta_) << "\n";
        report << "max_readied_changed_pixels_vs_expected=" << maxReadiedChangedPixels_ << "\n";
        report << "max_readied_channel_abs_sum_vs_expected=" << maxReadiedAbsSum_ << "\n";
        report << "max_readied_channel_delta_vs_expected=" << static_cast<int>(maxReadiedChannelDelta_) << "\n";
        report << ModuleList();
        report << captureLines_.str();
    }

    Options options_;
    HWND hwnd_ = nullptr;
    std::vector<uint8_t> expected_;
    std::wstring adapterName_;
    ComPtr<IDXGIFactory4> factory_;
    ComPtr<ID3D12Device> device_;
    ComPtr<IUnknown> deviceIdentity_;
    ComPtr<ID3D12CommandQueue> queue_;
    ComPtr<IDXGISwapChain3> swapChain_;
    ComPtr<IUnknown> swapChainIdentity_;
    ComPtr<ID3D12Device> swapChainDevice_;
    ComPtr<IUnknown> swapChainDeviceIdentity_;
    HRESULT swapChainDeviceHr_ = S_OK;
    HRESULT swapChainDeviceIdentityHr_ = S_OK;
    ComPtr<ID3D12CommandAllocator> allocator_;
    ComPtr<ID3D12GraphicsCommandList> commandList_;
    ComPtr<ID3D12Fence> fence_;
    DXGI_SWAP_CHAIN_DESC swapChainDesc_{};
    D3D12_RESOURCE_DESC backBufferDescs_[kBufferCount]{};
    HANDLE fenceEvent_ = nullptr;
    UINT64 fenceValue_ = 0;
    ComPtr<ID3D12Resource> backBuffers_[kBufferCount];
    D3D12_RESOURCE_STATES bufferStates_[kBufferCount]{};
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
    uint64_t maxAllChangedPixels_ = 0;
    uint64_t maxAllAbsSum_ = 0;
    uint8_t maxAllChannelDelta_ = 0;
    uint64_t maxReadiedChangedPixels_ = 0;
    uint64_t maxReadiedAbsSum_ = 0;
    uint8_t maxReadiedChannelDelta_ = 0;
    std::ostringstream captureLines_;
};

} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        auto options = ParseOptions(argc, argv);
        std::filesystem::create_directories(options.out);

        HMODULE proxy = nullptr;
        if (options.proxy) {
            proxy = LoadLibraryW(L"version.dll");
            if (!proxy) {
                throw std::runtime_error("LoadLibraryW(version.dll) failed");
            }
        }
        if (options.startupDelayMs > 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(options.startupDelayMs));
        }

        Probe probe(options);
        probe.Run();
        return 0;
    } catch (const std::exception& ex) {
        std::ofstream error("probe-error.txt", std::ios::app);
        error << ex.what() << "\n";
        MessageBoxA(nullptr, ex.what(), "dlssnr_d3d12_probe", MB_ICONERROR | MB_OK);
        return 1;
    }
}
