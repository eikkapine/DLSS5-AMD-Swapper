#pragma once

#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <sstream>
#include <stdexcept>

#include "kBridgeFullscreenVS.h"
#include "kBridgeCopyPS.h"
#include "kBridgeOpaquePS.h"
#include "kBridgeComposeCS.h"

namespace bridge_gpu {

using Microsoft::WRL::ComPtr;

inline void Require(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        std::ostringstream message;
        message << operation << " failed with HRESULT 0x" << std::hex
                << static_cast<unsigned long>(result);
        throw std::runtime_error(message.str());
    }
}

[[noreturn]] inline void StopAfterUnconfirmedGpuCompletion(const char* detail) noexcept {
    // This executable is an isolated child. If a queue cannot be drained,
    // leave its allocations owned until OS process teardown instead of
    // unwinding COM resources that an active D3D12 queue may still reference.
    // Never terminate Lossless Scaling or the captured application.
    constexpr char prefix[] = "GPU completion could not be established; stopping bridge before resource destruction: ";
    HANDLE file = CreateFileW(L"bridge-error.txt", FILE_APPEND_DATA,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                              OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(file, prefix, static_cast<DWORD>(sizeof(prefix) - 1), &written, nullptr);
        WriteFile(file, detail, static_cast<DWORD>(std::strlen(detail)), &written, nullptr);
        WriteFile(file, "\r\n", 2, &written, nullptr);
        FlushFileBuffers(file);
        CloseHandle(file);
    }
    TerminateProcess(GetCurrentProcess(), 1);
    ExitProcess(1);
}

class Handle {
public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { if (value_) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    HANDLE get() const { return value_; }
private:
    HANDLE value_ = nullptr;
};

struct FrameStatistics {
    bool changed = false;
    bool neuralNonblack = false;
    bool sourceMeaningfullyNonblack = false;
};

// Same-adapter GPU handoff. The D3D12 neural presenter owns all neural work;
// this class only converts existing 8-bit pixels and moves their storage.
class FrameTransport {
public:
    FrameTransport(ID3D11Device* device11, ID3D11DeviceContext* context11,
                   ID3D12Device* device12, UINT width, UINT height)
        : device12_(device12), width_(width), height_(height),
          completionEvent_(CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
        if (!completionEvent_.get()) {
            throw std::runtime_error("Could not create GPU transport fence event");
        }
        Require(device11->QueryInterface(IID_PPV_ARGS(&device_)), "Query D3D11.4 device");
        Require(context11->QueryInterface(IID_PPV_ARGS(&context_)), "Query D3D11.4 context");
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        Require(device_.As(&dxgi), "Query capture DXGI device");
        Require(dxgi->GetAdapter(&adapter), "Get capture adapter");
        DXGI_ADAPTER_DESC description{};
        Require(adapter->GetDesc(&description), "Get capture adapter description");
        const LUID neuralAdapter = device12_->GetAdapterLuid();
        if (description.AdapterLuid.LowPart != neuralAdapter.LowPart ||
            description.AdapterLuid.HighPart != neuralAdapter.HighPart) {
            throw std::runtime_error("Capture and neural devices use different adapters");
        }

        CreateSharedTexture(input_, D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET, "input");
        CreateSharedTexture(output_, D3D11_BIND_SHADER_RESOURCE, "output");
        Require(device_->CreateRenderTargetView(input_.texture.Get(), nullptr, &inputTarget_),
                "Create shared input render target");
        CreateFence(inputReady12_, inputReady11_);
        CreateFence(outputReady12_, outputReady11_);
        CreateFence(consumerDone12_, consumerDone11_);

        CreateLocalTexture(DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_SHADER_RESOURCE,
                           capturedTexture_, capturedView_);
        CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM,
                           D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS,
                           composedTexture_, composedView_);
        Require(device_->CreateUnorderedAccessView(composedTexture_.Get(), nullptr, &composedUav_),
                "Create composed image UAV");
        CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_SHADER_RESOURCE,
                           previousTexture_, previousView_);

        Require(device_->CreateVertexShader(kBridgeFullscreenVS, sizeof(kBridgeFullscreenVS),
                                            nullptr, &vertexShader_), "Create transport vertex shader");
        Require(device_->CreatePixelShader(kBridgeCopyPS, sizeof(kBridgeCopyPS),
                                           nullptr, &copyShader_), "Create transport copy shader");
        Require(device_->CreatePixelShader(kBridgeOpaquePS, sizeof(kBridgeOpaquePS),
                                           nullptr, &opaqueShader_), "Create transport opaque shader");
        Require(device_->CreateComputeShader(kBridgeComposeCS, sizeof(kBridgeComposeCS),
                                             nullptr, &composeShader_), "Create transport composition shader");

        D3D11_RASTERIZER_DESC rasterizer{};
        rasterizer.FillMode = D3D11_FILL_SOLID;
        rasterizer.CullMode = D3D11_CULL_NONE;
        rasterizer.DepthClipEnable = TRUE;
        Require(device_->CreateRasterizerState(&rasterizer, &rasterizer_), "Create transport rasterizer");
        D3D11_DEPTH_STENCIL_DESC depth{};
        depth.DepthEnable = FALSE;
        depth.StencilEnable = FALSE;
        Require(device_->CreateDepthStencilState(&depth, &depthState_), "Create transport depth state");

        D3D11_BUFFER_DESC parameters{};
        parameters.ByteWidth = 16;
        parameters.Usage = D3D11_USAGE_DEFAULT;
        parameters.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        Require(device_->CreateBuffer(&parameters, nullptr, &parameters_), "Create transport parameters");

        D3D11_BUFFER_DESC counters{};
        counters.ByteWidth = 16;
        counters.Usage = D3D11_USAGE_DEFAULT;
        counters.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
        counters.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
        Require(device_->CreateBuffer(&counters, nullptr, &counters_), "Create transport counters");
        D3D11_UNORDERED_ACCESS_VIEW_DESC counterView{};
        counterView.Format = DXGI_FORMAT_R32_TYPELESS;
        counterView.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        counterView.Buffer.NumElements = 4;
        counterView.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
        Require(device_->CreateUnorderedAccessView(counters_.Get(), &counterView, &counterUav_),
                "Create transport counter UAV");
        counters.Usage = D3D11_USAGE_STAGING;
        counters.BindFlags = 0;
        counters.MiscFlags = 0;
        counters.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Require(device_->CreateBuffer(&counters, nullptr, &counterReadback_), "Create 16-byte counter readback");
    }

    ~FrameTransport() {
        // Drain our D3D11 copies before releasing shared allocation owners.
        if (context_ && consumerDone11_) {
            try {
                SignalConsumerAndWait();
            } catch (const std::exception& error) {
                StopAfterUnconfirmedGpuCompletion(error.what());
            }
        }
    }

    FrameTransport(const FrameTransport&) = delete;
    FrameTransport& operator=(const FrameTransport&) = delete;

    void DrainForShutdown() { SignalConsumerAndWait(); }

    void SeedInput(const uint8_t* rgba, UINT rowPitch) {
        context_->UpdateSubresource(input_.texture.Get(), 0, nullptr, rgba, rowPitch, 0);
        SignalInput();
    }

    void UpdateInput(ID3D11Texture2D* texture) {
        D3D11_TEXTURE2D_DESC description{};
        texture->GetDesc(&description);
        if (description.Width != width_ || description.Height != height_ ||
            description.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
            description.SampleDesc.Count != 1 || description.MipLevels != 1 || description.ArraySize != 1) {
            throw std::runtime_error("GPU capture dimensions/format changed; restart scaling at the new native resolution");
        }
        // The WGC texture need not support shader views. This local copy does.
        context_->CopyResource(capturedTexture_.Get(), texture);
        Draw(capturedView_.Get(), inputTarget_.Get(), copyShader_.Get());
        SignalInput();
    }

    ID3D12Resource* inputResource() const { return input_.resource.Get(); }
    ID3D12Resource* outputResource() const { return output_.resource.Get(); }
    ID3D12Fence* inputReadyFence() const { return inputReady12_.Get(); }
    ID3D12Fence* outputReadyFence() const { return outputReady12_.Get(); }
    ID3D12Fence* consumerDoneFence() const { return consumerDone12_.Get(); }
    UINT64 inputReadyValue() const { return inputValue_; }
    UINT64 consumerDoneValue() const { return consumerValue_; }
    UINT64 NextOutputValue() { return ++outputValue_; }

    void WaitForOutput(UINT64 value) {
        Require(context_->Wait(outputReady11_.Get(), value), "D3D11 wait for neural output copy");
    }

    FrameStatistics Compose(UINT alpha) {
        if (alpha > 256) {
            throw std::runtime_error("GPU transport blend alpha is out of range");
        }
        // Input statistics depend only on this exact input upload, not on
        // neural progress or strength. Keep them until SignalInput changes it.
        const bool analyzeSource = !sourceStatisticsValid_ || sourceStatisticsInput_ != inputValue_;
        const UINT frameFlags = (historyValid_ ? 1u : 0u) | (analyzeSource ? 2u : 0u);
        const std::array<UINT, 4> parameters = {width_, height_, alpha, frameFlags};
        const UINT zeros[4]{};
        context_->UpdateSubresource(parameters_.Get(), 0, nullptr, parameters.data(), 0, 0);
        context_->ClearUnorderedAccessViewUint(counterUav_.Get(), zeros);
        ID3D11ShaderResourceView* inputs[] = {input_.view.Get(), output_.view.Get(), previousView_.Get()};
        ID3D11UnorderedAccessView* outputs[] = {composedUav_.Get(), counterUav_.Get()};
        ID3D11Buffer* constants[] = {parameters_.Get()};
        context_->CSSetShader(composeShader_.Get(), nullptr, 0);
        context_->CSSetConstantBuffers(0, 1, constants);
        context_->CSSetShaderResources(0, 3, inputs);
        context_->CSSetUnorderedAccessViews(0, 2, outputs, nullptr);
        context_->Dispatch((width_ + 15) / 16, (height_ + 15) / 16, 1);
        ID3D11ShaderResourceView* noInputs[3]{};
        ID3D11UnorderedAccessView* noOutputs[2]{};
        context_->CSSetShaderResources(0, 3, noInputs);
        context_->CSSetUnorderedAccessViews(0, 2, noOutputs, nullptr);
        context_->CSSetShader(nullptr, nullptr, 0);
        context_->CopyResource(counterReadback_.Get(), counters_.Get());
        SignalConsumerAndWait();

        // Only 16 bytes cross to the CPU. The completed fence bounds the wait;
        // Map must not silently start another unbounded driver wait.
        D3D11_MAPPED_SUBRESOURCE mapped{};
        Require(context_->Map(counterReadback_.Get(), 0, D3D11_MAP_READ,
                              D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped), "Map GPU transport counters");
        std::array<uint32_t, 4> values{};
        std::memcpy(values.data(), mapped.pData, sizeof(values));
        context_->Unmap(counterReadback_.Get(), 0);
        if (analyzeSource) {
            const uint64_t sourceSum = values[2] | (static_cast<uint64_t>(values[3]) << 32);
            sourceMeaningfullyNonblack_ = sourceSum >= 6ULL * width_ * height_;
            sourceStatisticsInput_ = inputValue_;
            sourceStatisticsValid_ = true;
            ++sourceAnalysisCount_;
        }
        // Endpoints already exist as GPU textures. Only an intermediate
        // strength needs the shader to materialize a third full-size image.
        displayAlpha_ = alpha;
        if (alpha == 0 || alpha == 256) {
            ++directImageCount_;
        } else {
            ++blendedImageCount_;
        }
        return {values[0] != 0, values[1] != 0, sourceMeaningfullyNonblack_};
    }

    void DrawVisible(ID3D11RenderTargetView* target) {
        ID3D11ShaderResourceView* image = displayAlpha_ == 256 ? output_.view.Get()
                                      : displayAlpha_ == 0 ? input_.view.Get() : composedView_.Get();
        Draw(image, target, opaqueShader_.Get());
    }

    void AcceptPresentation() {
        ID3D11Texture2D* image = displayAlpha_ == 256 ? output_.texture.Get()
                               : displayAlpha_ == 0 ? input_.texture.Get() : composedTexture_.Get();
        context_->CopyResource(previousTexture_.Get(), image);
        historyValid_ = true;
    }

    void FinishPresentation() {
        // Direct display/history can read the shared neural output AFTER
        // Compose's fence. Publish a new fence after those reads, including
        // occluded submissions. The next D3D12 output write waits for it.
        SignalConsumer();
    }

    uint64_t directImageCount() const { return directImageCount_; }
    uint64_t blendedImageCount() const { return blendedImageCount_; }
    uint64_t sourceAnalysisCount() const { return sourceAnalysisCount_; }

    void InvalidateHistory() { historyValid_ = false; }

private:
    struct SharedTexture {
        ComPtr<ID3D12Resource> resource;
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11ShaderResourceView> view;
    };

    void CreateSharedTexture(SharedTexture& result, UINT bindFlags, const char* name) {
        auto requireTexture = [&](HRESULT status, const char* operation) {
            if (FAILED(status)) {
                std::ostringstream detail;
                detail << operation << " (" << name << ", " << width_ << 'x' << height_
                       << ", RGBA8, d3d11_nt)";
                Require(status, detail.str().c_str());
            }
        };
        // Allocate on the D3D11 consumer/capture device, where bind flags and
        // NT-handle sharing metadata are explicit. Import the same allocation
        // into D3D12 instead of reopening a D3D12-created allocation in D3D11:
        // that reverse route returned E_INVALIDARG in the user's actual runs.
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width_;
        description.Height = height_;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = bindFlags;
        // Ownership is synchronized by the existing shared fences. Do not add
        // a keyed mutex, which would introduce a second ownership protocol.
        description.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        requireTexture(device_->CreateTexture2D(&description, nullptr, &result.texture),
                       "Create shared GPU texture in D3D11");
        ComPtr<IDXGIResource1> shared;
        requireTexture(result.texture.As(&shared), "Query shared GPU texture handle interface");
        HANDLE raw = nullptr;
        requireTexture(shared->CreateSharedHandle(nullptr,
                                                  DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                                  nullptr, &raw), "Export D3D11 GPU texture NT handle");
        Handle handle(raw);
        requireTexture(device12_->OpenSharedHandle(handle.get(), IID_PPV_ARGS(&result.resource)),
                       "Import shared GPU texture into D3D12");
        const auto imported = result.resource->GetDesc();
        if (imported.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
            imported.Width != width_ || imported.Height != height_ ||
            imported.DepthOrArraySize != 1 || imported.MipLevels != 1 ||
            imported.Format != description.Format || imported.SampleDesc.Count != 1) {
            requireTexture(E_INVALIDARG, "Imported GPU texture does not match native dimensions/format");
        }
        requireTexture(device_->CreateShaderResourceView(result.texture.Get(), nullptr, &result.view),
                       "Create shared GPU texture view");
    }

    void CreateFence(ComPtr<ID3D12Fence>& fence12, ComPtr<ID3D11Fence>& fence11) {
        Require(device12_->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12)),
                "Create shared transport fence");
        HANDLE raw = nullptr;
        Require(device12_->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &raw),
                "Export shared transport fence");
        Handle handle(raw);
        Require(device_->OpenSharedFence(handle.get(), IID_PPV_ARGS(&fence11)), "Open shared D3D11 fence");
    }

    void CreateLocalTexture(DXGI_FORMAT format, UINT flags,
                            ComPtr<ID3D11Texture2D>& texture, ComPtr<ID3D11ShaderResourceView>& view) {
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width_;
        description.Height = height_;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = format;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = flags;
        Require(device_->CreateTexture2D(&description, nullptr, &texture), "Create local GPU transport texture");
        Require(device_->CreateShaderResourceView(texture.Get(), nullptr, &view), "Create local GPU texture view");
    }

    void SignalInput() {
        Require(context_->Signal(inputReady11_.Get(), ++inputValue_), "Signal GPU capture completion");
        context_->Flush();
    }

    void SignalConsumer() {
        Require(context_->Signal(consumerDone11_.Get(), ++consumerValue_), "Signal GPU consumer completion");
        context_->Flush();
    }

    void SignalConsumerAndWait() {
        SignalConsumer();
        auto completed = [&]() {
            const UINT64 value = consumerDone11_->GetCompletedValue();
            if (value == UINT64_MAX) {
                Require(device_->GetDeviceRemovedReason(), "GPU transport device removed");
                throw std::runtime_error("GPU transport fence reported device removal");
            }
            return value;
        };
        if (completed() < consumerValue_) {
            Require(consumerDone11_->SetEventOnCompletion(consumerValue_, completionEvent_.get()),
                    "Set transport completion event");
            if (WaitForSingleObject(completionEvent_.get(), 5000) != WAIT_OBJECT_0 || completed() < consumerValue_) {
                throw std::runtime_error("Timed out waiting for GPU frame transport");
            }
        }
    }

    void Draw(ID3D11ShaderResourceView* source, ID3D11RenderTargetView* target,
              ID3D11PixelShader* shader) {
        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(width_);
        viewport.Height = static_cast<float>(height_);
        viewport.MaxDepth = 1.0f;
        context_->IASetInputLayout(nullptr);
        context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
        context_->GSSetShader(nullptr, nullptr, 0);
        context_->HSSetShader(nullptr, nullptr, 0);
        context_->DSSetShader(nullptr, nullptr, 0);
        context_->PSSetShader(shader, nullptr, 0);
        context_->PSSetShaderResources(0, 1, &source);
        context_->RSSetState(rasterizer_.Get());
        context_->RSSetViewports(1, &viewport);
        context_->OMSetDepthStencilState(depthState_.Get(), 0);
        context_->OMSetBlendState(nullptr, nullptr, 0xffffffffu);
        context_->OMSetRenderTargets(1, &target, nullptr);
        context_->Draw(3, 0);
        ID3D11ShaderResourceView* empty = nullptr;
        context_->PSSetShaderResources(0, 1, &empty);
        context_->OMSetRenderTargets(0, nullptr, nullptr);
    }

    ComPtr<ID3D11Device5> device_;
    ComPtr<ID3D11DeviceContext4> context_;
    ComPtr<ID3D12Device> device12_;
    UINT width_;
    UINT height_;
    Handle completionEvent_;
    SharedTexture input_, output_;
    ComPtr<ID3D12Fence> inputReady12_, outputReady12_, consumerDone12_;
    ComPtr<ID3D11Fence> inputReady11_, outputReady11_, consumerDone11_;
    UINT64 inputValue_ = 0, outputValue_ = 0, consumerValue_ = 0;
    ComPtr<ID3D11Texture2D> capturedTexture_, composedTexture_, previousTexture_;
    ComPtr<ID3D11ShaderResourceView> capturedView_, composedView_, previousView_;
    ComPtr<ID3D11RenderTargetView> inputTarget_;
    ComPtr<ID3D11UnorderedAccessView> composedUav_, counterUav_;
    ComPtr<ID3D11Buffer> parameters_, counters_, counterReadback_;
    ComPtr<ID3D11VertexShader> vertexShader_;
    ComPtr<ID3D11PixelShader> copyShader_, opaqueShader_;
    ComPtr<ID3D11ComputeShader> composeShader_;
    ComPtr<ID3D11RasterizerState> rasterizer_;
    ComPtr<ID3D11DepthStencilState> depthState_;
    bool historyValid_ = false;
    UINT displayAlpha_ = 256;
    bool sourceStatisticsValid_ = false;
    bool sourceMeaningfullyNonblack_ = false;
    UINT64 sourceStatisticsInput_ = 0;
    uint64_t directImageCount_ = 0;
    uint64_t blendedImageCount_ = 0;
    uint64_t sourceAnalysisCount_ = 0;
};

} // namespace bridge_gpu
