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
#include "kBridgeScalePS.h"
#include "kBridgeOpaquePS.h"
#include "kBridgeComposeCS.h"
#include "kBridgeReduceStatisticsCS.h"

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
                   ID3D12Device* device12, UINT width, UINT height,
                   UINT displayWidth, UINT displayHeight, bool nativeResidualComposite)
        : device12_(device12), width_(width), height_(height),
          displayWidth_(displayWidth), displayHeight_(displayHeight),
          nativeResidualComposite_(nativeResidualComposite),
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
        CreateSharedTexture(outputs_[0], D3D11_BIND_SHADER_RESOURCE, "output 0");
        CreateSharedTexture(outputs_[1], D3D11_BIND_SHADER_RESOURCE, "output 1");
        Require(device_->CreateRenderTargetView(input_.texture.Get(), nullptr, &inputTarget_),
                "Create shared input render target");
        CreateFence(inputReady12_, inputReady11_);
        CreateFence(outputReady12_, outputReady11_);
        CreateFence(consumerDone12_, consumerDone11_);

        if (nativeResidualComposite_) {
            CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM,
                               D3D11_BIND_SHADER_RESOURCE,
                               width_, height_, previousNeuralInputTexture_, previousNeuralInputView_);
            CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM,
                               D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
                               displayWidth_, displayHeight_, sourceTexture_, sourceView_);
            Require(device_->CreateRenderTargetView(sourceTexture_.Get(), nullptr, &sourceTarget_),
                    "Create native source render target");
            CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM,
                               D3D11_BIND_SHADER_RESOURCE,
                               displayWidth_, displayHeight_, historyTexture_, historyView_);
        }
        CreateLocalTexture(DXGI_FORMAT_R8G8B8A8_UNORM,
                           D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS,
                           displayWidth_, displayHeight_, composedTexture_, composedView_);
        Require(device_->CreateUnorderedAccessView(composedTexture_.Get(), nullptr, &composedUav_),
                "Create composed image UAV");

        Require(device_->CreateVertexShader(kBridgeFullscreenVS, sizeof(kBridgeFullscreenVS),
                                            nullptr, &vertexShader_), "Create transport vertex shader");
        Require(device_->CreatePixelShader(kBridgeCopyPS, sizeof(kBridgeCopyPS),
                                           nullptr, &copyShader_), "Create transport copy shader");
        Require(device_->CreatePixelShader(kBridgeScalePS, sizeof(kBridgeScalePS),
                                           nullptr, &scaleShader_), "Create transport scale shader");
        Require(device_->CreatePixelShader(kBridgeOpaquePS, sizeof(kBridgeOpaquePS),
                                           nullptr, &opaqueShader_), "Create transport opaque shader");
        Require(device_->CreateComputeShader(kBridgeComposeCS, sizeof(kBridgeComposeCS),
                                             nullptr, &composeShader_), "Create transport composition shader");
        Require(device_->CreateComputeShader(kBridgeReduceStatisticsCS, sizeof(kBridgeReduceStatisticsCS),
                                             nullptr, &reduceShader_), "Create transport statistics reduction shader");

        D3D11_RASTERIZER_DESC rasterizer{};
        rasterizer.FillMode = D3D11_FILL_SOLID;
        rasterizer.CullMode = D3D11_CULL_NONE;
        rasterizer.DepthClipEnable = TRUE;
        Require(device_->CreateRasterizerState(&rasterizer, &rasterizer_), "Create transport rasterizer");
        D3D11_DEPTH_STENCIL_DESC depth{};
        depth.DepthEnable = FALSE;
        depth.StencilEnable = FALSE;
        Require(device_->CreateDepthStencilState(&depth, &depthState_), "Create transport depth state");
        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        Require(device_->CreateSamplerState(&sampler, &resizeSampler_), "Create transport resize sampler");

        D3D11_BUFFER_DESC parameters{};
        parameters.ByteWidth = 32;
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

        // One 16-byte record per 16x16 visible-output tile: 225 KiB at 2560x1440,
        // at most 518400 bytes at the supported 3840x2160 limit.
        D3D11_BUFFER_DESC tiles{};
        tiles.ByteWidth = ((displayWidth_ + 15) / 16) * ((displayHeight_ + 15) / 16) * 16;
        tiles.Usage = D3D11_USAGE_DEFAULT;
        tiles.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        tiles.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
        Require(device_->CreateBuffer(&tiles, nullptr, &tileStatistics_), "Create transport tile statistics");
        counterView.Buffer.NumElements = tiles.ByteWidth / sizeof(UINT);
        Require(device_->CreateUnorderedAccessView(tileStatistics_.Get(), &counterView, &tileStatisticsUav_),
                "Create transport tile statistics UAV");
        D3D11_SHADER_RESOURCE_VIEW_DESC tileView{};
        tileView.Format = DXGI_FORMAT_R32_TYPELESS;
        tileView.ViewDimension = D3D11_SRV_DIMENSION_BUFFEREX;
        tileView.BufferEx.NumElements = counterView.Buffer.NumElements;
        tileView.BufferEx.Flags = D3D11_BUFFEREX_SRV_FLAG_RAW;
        Require(device_->CreateShaderResourceView(tileStatistics_.Get(), &tileView, &tileStatisticsView_),
                "Create transport tile statistics SRV");
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

    void DrainForShutdown() {
        SignalConsumerAndWait();
        activeCaptureView_.Reset();
    }

    void SeedInput(const uint8_t* rgba, UINT rowPitch) {
        context_->UpdateSubresource(input_.texture.Get(), 0, nullptr, rgba, rowPitch, 0);
        inputInitialized_ = true;
        SignalInput();
    }

    void SeedNativeSource(const uint8_t* rgba, UINT rowPitch) {
        if (!nativeResidualComposite_) {
            return;
        }
        context_->UpdateSubresource(sourceTexture_.Get(), 0, nullptr, rgba, rowPitch, 0);
    }

    void UpdateInput(ID3D11Texture2D* texture) {
        D3D11_TEXTURE2D_DESC description{};
        texture->GetDesc(&description);
        if (description.Width == 0 || description.Height == 0 ||
            description.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
            description.SampleDesc.Count != 1 || description.MipLevels != 1 || description.ArraySize != 1) {
            throw std::runtime_error("GPU capture format is unsupported by the shared transport");
        }
        if (sourceWidth_ == 0) {
            sourceWidth_ = description.Width;
            sourceHeight_ = description.Height;
        } else if (description.Width != sourceWidth_ || description.Height != sourceHeight_) {
            throw std::runtime_error("GPU capture dimensions changed; restart scaling at the new source resolution");
        }
        if (activeCaptureView_) {
            throw std::runtime_error("Prior direct capture has not completed its consumer fence");
        }
        if (nativeResidualComposite_ && inputInitialized_) {
            // Preserve the input from the immediately preceding feed before a new
            // capture replaces it. Repeated feeds of the same capture are handled
            // by CommitSubmittedNeuralInput() after composition.
            context_->CopyResource(previousNeuralInputTexture_.Get(), input_.texture.Get());
            previousNeuralInputValid_ = true;
            previousNeuralInputValue_ = inputValue_;
        }
        // WGC surfaces which support SRVs can feed the existing exact BGRA
        // conversion directly. Keep this view only while its frame is checked
        // out; Compose/Drain releases it before the caller returns the frame.
        // Unsupported surfaces retain the established local-copy path.
        if (!directCaptureUnavailable_ && (description.BindFlags & D3D11_BIND_SHADER_RESOURCE)) {
            if (FAILED(device_->CreateShaderResourceView(texture, nullptr, &activeCaptureView_))) {
                activeCaptureView_.Reset();
                directCaptureUnavailable_ = true;
            }
        }
        if (activeCaptureView_) {
            DrawCapture(activeCaptureView_.Get(), description.Width, description.Height);
            ++directCaptureCount_;
        } else {
            EnsureCapturedTexture(description.Width, description.Height);
            context_->CopyResource(capturedTexture_.Get(), texture);
            DrawCapture(capturedView_.Get(), description.Width, description.Height);
            ++copiedCaptureCount_;
        }
        inputInitialized_ = true;
        SignalInput();
    }

    ID3D12Resource* inputResource() const { return input_.resource.Get(); }
    void BeginOutputWrite() {
        if (nativeResidualComposite_) {
            // Native display history lives in a separate full-resolution local
            // texture, so the two low-resolution neural output slots can simply
            // alternate. The consumer fence still protects reuse.
            outputIndex_ = 1u - outputIndex_;
            return;
        }
        // Keep the last accepted full-strength image immutable. Duplicate
        // outputs overwrite the other slot until a new image is accepted.
        // CopyOutputGpu still waits on the latest consumer fence before write.
        outputIndex_ = historyValid_ ? 1u - historyOutputIndex_ : outputIndex_;
    }
    ID3D12Resource* outputResource() const { return outputs_[outputIndex_].resource.Get(); }
    ID3D12Fence* inputReadyFence() const { return inputReady12_.Get(); }
    ID3D12Fence* outputReadyFence() const { return outputReady12_.Get(); }
    ID3D12Fence* consumerDoneFence() const { return consumerDone12_.Get(); }
    UINT64 inputReadyValue() const { return inputValue_; }
    UINT64 consumerDoneValue() const { return consumerValue_; }
    UINT64 NextOutputValue() { return ++outputValue_; }

    void WaitForOutput(UINT64 value) {
        Require(context_->Wait(outputReady11_.Get(), value), "D3D11 wait for neural output copy");
    }

    void CommitSubmittedNeuralInput() {
        if (!nativeResidualComposite_ || !inputInitialized_) {
            return;
        }
        // Inline=0 is one submitted job ahead of the published backbuffer. The
        // next Compose must therefore subtract the input used by this exact feed.
        // A capture may be fed multiple times between WGC updates, so recording
        // history only when UpdateInput() runs is insufficient and caused the
        // compositor to subtract an older frame from a newer neural output.
        if (!previousNeuralInputValid_ || previousNeuralInputValue_ != inputValue_) {
            context_->CopyResource(previousNeuralInputTexture_.Get(), input_.texture.Get());
            previousNeuralInputValid_ = true;
            previousNeuralInputValue_ = inputValue_;
        }
    }

    FrameStatistics Compose(UINT alpha, bool preferPreviousNeuralInput = false) {
        const UINT maxAlpha = nativeResidualComposite_ ? 1024u : 256u;
        if (alpha > maxAlpha) {
            throw std::runtime_error("GPU transport blend alpha is out of range");
        }
        // Input statistics depend only on this exact input upload, not on
        // neural progress or strength. Keep them until SignalInput changes it.
        const bool analyzeSource = !sourceStatisticsValid_ || sourceStatisticsInput_ != inputValue_;
        const UINT64 neuralInputValue = preferPreviousNeuralInput && previousNeuralInputValid_
            ? previousNeuralInputValue_ : inputValue_;
        const bool matchedHistory = historyValid_ && alpha != 0 && historyAlpha_ == alpha &&
            lastComposedMappingValid_ && lastComposedSourceInputValue_ == inputValue_ &&
            lastComposedNeuralInputValue_ == neuralInputValue;
        const UINT frameFlags = (historyValid_ ? 1u : 0u) | (analyzeSource ? 2u : 0u) |
                                (nativeResidualComposite_ ? 4u : 0u) |
                                (previousNeuralInputValid_ ? 8u : 0u) |
                                (preferPreviousNeuralInput ? 16u : 0u) |
                                (matchedHistory ? 32u : 0u);
        const std::array<UINT, 8> parameters = {
            width_, height_, displayWidth_, displayHeight_, alpha, frameFlags, 0u, 0u
        };
        context_->UpdateSubresource(parameters_.Get(), 0, nullptr, parameters.data(), 0, 0);
        ID3D11ShaderResourceView* source = nativeResidualComposite_ ? sourceView_.Get() : input_.view.Get();
        ID3D11ShaderResourceView* history = nativeResidualComposite_ ? historyView_.Get()
                                                                      : outputs_[historyOutputIndex_].view.Get();
        ID3D11ShaderResourceView* inputs[] = {
            input_.view.Get(), outputs_[outputIndex_].view.Get(), source, history,
            nullptr, previousNeuralInputView_.Get()
        };
        ID3D11UnorderedAccessView* outputs[] = {composedUav_.Get(), nullptr, tileStatisticsUav_.Get()};
        ID3D11Buffer* constants[] = {parameters_.Get()};
        context_->CSSetShader(composeShader_.Get(), nullptr, 0);
        context_->CSSetConstantBuffers(0, 1, constants);
        context_->CSSetShaderResources(0, 6, inputs);
        ID3D11SamplerState* samplers[] = {resizeSampler_.Get()};
        context_->CSSetSamplers(0, 1, samplers);
        context_->CSSetUnorderedAccessViews(0, 3, outputs, nullptr);
        context_->Dispatch((displayWidth_ + 15) / 16, (displayHeight_ + 15) / 16, 1);
        ID3D11ShaderResourceView* noInputs[6]{};
        ID3D11UnorderedAccessView* noOutputs[3]{};
        context_->CSSetShaderResources(0, 6, noInputs);
        ID3D11SamplerState* noSamplers[] = {nullptr};
        context_->CSSetSamplers(0, 1, noSamplers);
        context_->CSSetUnorderedAccessViews(0, 3, noOutputs, nullptr);

        // Rebind the first pass's UAV as an SRV. The D3D11 immediate context
        // orders this producer/consumer dependency; no CPU wait or Flush is
        // needed between dispatches. Never bind the same buffer as both views.
        ID3D11ShaderResourceView* tileInputs[] = {tileStatisticsView_.Get()};
        ID3D11UnorderedAccessView* reducedOutputs[] = {counterUav_.Get()};
        context_->CSSetShader(reduceShader_.Get(), nullptr, 0);
        context_->CSSetShaderResources(4, 1, tileInputs);
        context_->CSSetUnorderedAccessViews(1, 1, reducedOutputs, nullptr);
        context_->Dispatch(1, 1, 1);
        context_->CSSetShaderResources(0, 6, noInputs);
        context_->CSSetUnorderedAccessViews(0, 3, noOutputs, nullptr);
        context_->CSSetShader(nullptr, nullptr, 0);
        context_->CopyResource(counterReadback_.Get(), counters_.Get());
        SignalConsumerAndWait();
        activeCaptureView_.Reset();

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
            sourceMeaningfullyNonblack_ = sourceSum >= 6ULL * displayWidth_ * displayHeight_;
            sourceStatisticsInput_ = inputValue_;
            sourceStatisticsValid_ = true;
            ++sourceAnalysisCount_;
        }
        // Native residual mode always materializes the effect into the native
        // composed texture; bypass can still draw the untouched source directly.
        displayAlpha_ = alpha;
        if (!nativeResidualComposite_ && (alpha == 0 || alpha == 256)) {
            ++directImageCount_;
        } else {
            ++blendedImageCount_;
        }
        lastComposedSourceInputValue_ = inputValue_;
        lastComposedNeuralInputValue_ = neuralInputValue;
        lastComposedMappingValid_ = true;
        return {values[0] != 0, values[1] != 0, sourceMeaningfullyNonblack_};
    }

    void DrawVisible(ID3D11RenderTargetView* target) {
        if (nativeResidualComposite_) {
            ID3D11ShaderResourceView* image = displayAlpha_ == 0 ? sourceView_.Get() : composedView_.Get();
            D3D11_VIEWPORT viewport{};
            viewport.Width = static_cast<float>(displayWidth_);
            viewport.Height = static_cast<float>(displayHeight_);
            viewport.MaxDepth = 1.0f;
            Draw(image, target, opaqueShader_.Get(), &viewport);
            return;
        }
        ID3D11ShaderResourceView* image = displayAlpha_ == 256 ? outputs_[outputIndex_].view.Get()
                                      : displayAlpha_ == 0 ? input_.view.Get() : composedView_.Get();
        Draw(image, target, opaqueShader_.Get());
    }

    void AcceptPresentation() {
        if (nativeResidualComposite_) {
            ID3D11Texture2D* image = displayAlpha_ == 0 ? sourceTexture_.Get() : composedTexture_.Get();
            context_->CopyResource(historyTexture_.Get(), image);
            ++historyCopies_;
            historyAlpha_ = displayAlpha_;
            historyValid_ = true;
            return;
        }
        if (displayAlpha_ == 256) {
            // Retain this already-populated shared texture as exact history,
            // removing a full-frame GPU copy on each accepted neural image.
            ++historyCopiesAvoided_;
        } else {
            // All current neural reads are complete and shader views unbound.
            // Reuse this output slot for exact bypass/blend history instead of
            // allocating a third full-size texture. The consumer fence below
            // protects this D3D11 write before either D3D12 slot is reused.
            ID3D11Texture2D* image = displayAlpha_ == 0 ? input_.texture.Get() : composedTexture_.Get();
            context_->CopyResource(outputs_[outputIndex_].texture.Get(), image);
            ++historyCopies_;
        }
        historyOutputIndex_ = outputIndex_;
        historyAlpha_ = displayAlpha_;
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
    uint64_t directCaptureCount() const { return directCaptureCount_; }
    uint64_t copiedCaptureCount() const { return copiedCaptureCount_; }
    uint64_t scaledCaptureCount() const { return scaledCaptureCount_; }
    uint64_t historyCopiesAvoided() const { return historyCopiesAvoided_; }
    uint64_t historyCopies() const { return historyCopies_; }

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
        CreateLocalTexture(format, flags, width_, height_, texture, view);
    }

    void CreateLocalTexture(DXGI_FORMAT format, UINT flags, UINT width, UINT height,
                            ComPtr<ID3D11Texture2D>& texture, ComPtr<ID3D11ShaderResourceView>& view) {
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = format;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = flags;
        Require(device_->CreateTexture2D(&description, nullptr, &texture), "Create local GPU transport texture");
        Require(device_->CreateShaderResourceView(texture.Get(), nullptr, &view), "Create local GPU texture view");
    }

    void EnsureCapturedTexture(UINT width, UINT height) {
        if (capturedTexture_ && capturedWidth_ == width && capturedHeight_ == height) {
            return;
        }
        capturedView_.Reset();
        capturedTexture_.Reset();
        CreateLocalTexture(DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_SHADER_RESOURCE,
                           width, height, capturedTexture_, capturedView_);
        capturedWidth_ = width;
        capturedHeight_ = height;
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

    void DrawCapture(ID3D11ShaderResourceView* source, UINT sourceWidth, UINT sourceHeight) {
        if (nativeResidualComposite_) {
            if (sourceWidth != displayWidth_ || sourceHeight != displayHeight_) {
                throw std::runtime_error("Native residual source dimensions changed; restart scaling at the new source resolution");
            }

            D3D11_VIEWPORT nativeViewport{};
            nativeViewport.Width = static_cast<float>(displayWidth_);
            nativeViewport.Height = static_cast<float>(displayHeight_);
            nativeViewport.MaxDepth = 1.0f;
            Draw(source, sourceTarget_.Get(), copyShader_.Get(), &nativeViewport, nullptr);

            if (displayWidth_ == width_ && displayHeight_ == height_) {
                Draw(sourceView_.Get(), inputTarget_.Get(), copyShader_.Get(), nullptr, nullptr);
            } else {
                D3D11_VIEWPORT neuralViewport{};
                neuralViewport.Width = static_cast<float>(width_);
                neuralViewport.Height = static_cast<float>(height_);
                neuralViewport.MaxDepth = 1.0f;
                Draw(sourceView_.Get(), inputTarget_.Get(), scaleShader_.Get(), &neuralViewport, resizeSampler_.Get());
                ++scaledCaptureCount_;
            }
            return;
        }

        if (sourceWidth == width_ && sourceHeight == height_) {
            Draw(source, inputTarget_.Get(), copyShader_.Get(), nullptr, nullptr);
            return;
        }

        const double fit = std::min(static_cast<double>(width_) / sourceWidth,
                                    static_cast<double>(height_) / sourceHeight);
        const UINT drawWidth = std::max<UINT>(1, static_cast<UINT>(sourceWidth * fit));
        const UINT drawHeight = std::max<UINT>(1, static_cast<UINT>(sourceHeight * fit));
        D3D11_VIEWPORT viewport{};
        viewport.TopLeftX = static_cast<float>((width_ - drawWidth) / 2);
        viewport.TopLeftY = static_cast<float>((height_ - drawHeight) / 2);
        viewport.Width = static_cast<float>(drawWidth);
        viewport.Height = static_cast<float>(drawHeight);
        viewport.MaxDepth = 1.0f;
        constexpr float black[4] = {0.0f, 0.0f, 0.0f, 0.0f};
        context_->ClearRenderTargetView(inputTarget_.Get(), black);
        Draw(source, inputTarget_.Get(), scaleShader_.Get(), &viewport, resizeSampler_.Get());
        ++scaledCaptureCount_;
    }

    void Draw(ID3D11ShaderResourceView* source, ID3D11RenderTargetView* target,
              ID3D11PixelShader* shader, const D3D11_VIEWPORT* customViewport = nullptr,
              ID3D11SamplerState* sampler = nullptr) {
        D3D11_VIEWPORT viewport{};
        if (customViewport) {
            viewport = *customViewport;
        } else {
            viewport.Width = static_cast<float>(width_);
            viewport.Height = static_cast<float>(height_);
            viewport.MaxDepth = 1.0f;
        }
        context_->IASetInputLayout(nullptr);
        context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context_->VSSetShader(vertexShader_.Get(), nullptr, 0);
        context_->GSSetShader(nullptr, nullptr, 0);
        context_->HSSetShader(nullptr, nullptr, 0);
        context_->DSSetShader(nullptr, nullptr, 0);
        context_->PSSetShader(shader, nullptr, 0);
        context_->PSSetShaderResources(0, 1, &source);
        if (sampler) {
            context_->PSSetSamplers(0, 1, &sampler);
        }
        context_->RSSetState(rasterizer_.Get());
        context_->RSSetViewports(1, &viewport);
        context_->OMSetDepthStencilState(depthState_.Get(), 0);
        context_->OMSetBlendState(nullptr, nullptr, 0xffffffffu);
        context_->OMSetRenderTargets(1, &target, nullptr);
        context_->Draw(3, 0);
        ID3D11ShaderResourceView* empty = nullptr;
        context_->PSSetShaderResources(0, 1, &empty);
        if (sampler) {
            ID3D11SamplerState* noSampler = nullptr;
            context_->PSSetSamplers(0, 1, &noSampler);
        }
        context_->OMSetRenderTargets(0, nullptr, nullptr);
    }

    ComPtr<ID3D11Device5> device_;
    ComPtr<ID3D11DeviceContext4> context_;
    ComPtr<ID3D12Device> device12_;
    UINT width_;
    UINT height_;
    UINT displayWidth_;
    UINT displayHeight_;
    bool nativeResidualComposite_ = false;
    Handle completionEvent_;
    SharedTexture input_;
    std::array<SharedTexture, 2> outputs_;
    UINT outputIndex_ = 0, historyOutputIndex_ = 0;
    ComPtr<ID3D11ShaderResourceView> activeCaptureView_;
    bool directCaptureUnavailable_ = false;
    ComPtr<ID3D12Fence> inputReady12_, outputReady12_, consumerDone12_;
    ComPtr<ID3D11Fence> inputReady11_, outputReady11_, consumerDone11_;
    UINT64 inputValue_ = 0, outputValue_ = 0, consumerValue_ = 0;
    UINT sourceWidth_ = 0, sourceHeight_ = 0;
    UINT capturedWidth_ = 0, capturedHeight_ = 0;
    ComPtr<ID3D11Texture2D> capturedTexture_, sourceTexture_, composedTexture_, historyTexture_;
    ComPtr<ID3D11Texture2D> previousNeuralInputTexture_;
    ComPtr<ID3D11ShaderResourceView> capturedView_, sourceView_, composedView_, historyView_;
    ComPtr<ID3D11ShaderResourceView> previousNeuralInputView_;
    ComPtr<ID3D11RenderTargetView> inputTarget_, sourceTarget_;
    ComPtr<ID3D11UnorderedAccessView> composedUav_, counterUav_, tileStatisticsUav_;
    ComPtr<ID3D11ShaderResourceView> tileStatisticsView_;
    ComPtr<ID3D11Buffer> parameters_, counters_, counterReadback_, tileStatistics_;
    ComPtr<ID3D11VertexShader> vertexShader_;
    ComPtr<ID3D11PixelShader> copyShader_, scaleShader_, opaqueShader_;
    ComPtr<ID3D11ComputeShader> composeShader_, reduceShader_;
    ComPtr<ID3D11RasterizerState> rasterizer_;
    ComPtr<ID3D11DepthStencilState> depthState_;
    ComPtr<ID3D11SamplerState> resizeSampler_;
    bool historyValid_ = false;
    bool inputInitialized_ = false;
    bool previousNeuralInputValid_ = false;
    UINT64 previousNeuralInputValue_ = 0;
    UINT64 lastComposedSourceInputValue_ = 0, lastComposedNeuralInputValue_ = 0;
    bool lastComposedMappingValid_ = false;
    UINT displayAlpha_ = 256;
    UINT historyAlpha_ = 256;
    bool sourceStatisticsValid_ = false;
    bool sourceMeaningfullyNonblack_ = false;
    UINT64 sourceStatisticsInput_ = 0;
    uint64_t directImageCount_ = 0;
    uint64_t blendedImageCount_ = 0;
    uint64_t sourceAnalysisCount_ = 0;
    uint64_t directCaptureCount_ = 0, copiedCaptureCount_ = 0;
    uint64_t scaledCaptureCount_ = 0;
    uint64_t historyCopiesAvoided_ = 0, historyCopies_ = 0;
};

} // namespace bridge_gpu
