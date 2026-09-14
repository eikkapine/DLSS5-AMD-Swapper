#include <windows.h>
#include <wrl/client.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <psapi.h>
#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;
namespace {
void Check(HRESULT result, const char* operation) {
    if (FAILED(result)) {
        std::ostringstream text;
        text << operation << " failed: 0x" << std::hex << static_cast<unsigned long>(result);
        throw std::runtime_error(text.str());
    }
}
LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    return DefWindowProcW(window, message, wparam, lparam);
}
struct HiddenWindow {
    HWND handle = nullptr;
    HiddenWindow() {
        WNDCLASSW type{};
        type.lpfnWndProc = WindowProc;
        type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"SwapperD3D11Smoke";
        if (!RegisterClassW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            throw std::runtime_error("RegisterClass failed");
        handle = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, type.lpszClassName,
            L"Swapper hidden D3D11 test", WS_OVERLAPPED, 0, 0, 640, 360,
            nullptr, nullptr, type.hInstance, nullptr);
        if (!handle) throw std::runtime_error("CreateWindow failed");
        // Deliberately never show, activate, or focus this window.
    }
    ~HiddenWindow() { if (handle) DestroyWindow(handle); }
};
const char* Shader = R"(
struct Vertex { float4 position:SV_Position; float3 color:COLOR; };
Vertex VS(uint id:SV_VertexID) {
    float2 positions[3] = {float2(-0.8,-0.8),float2(0,0.8),float2(0.8,-0.8)};
    float3 colors[3] = {float3(1,0,0),float3(0,1,0),float3(0,0,1)};
    Vertex result; result.position=float4(positions[id],0,1); result.color=colors[id]; return result;
}
float4 PS(Vertex vertex):SV_Target { return float4(vertex.color,1); }
)";

void Run(const std::filesystem::path& out) {
    HiddenWindow window;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swapchain;
    DXGI_SWAP_CHAIN_DESC swap{};
    swap.BufferDesc.Width = 640; swap.BufferDesc.Height = 360;
    swap.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swap.SampleDesc.Count = 1; swap.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swap.BufferCount = 2; swap.OutputWindow = window.handle; swap.Windowed = TRUE;
    swap.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    D3D_FEATURE_LEVEL level{};
    const D3D_FEATURE_LEVEL requested[] = { D3D_FEATURE_LEVEL_11_0 };
    Check(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        requested, 1, D3D11_SDK_VERSION, &swap, &swapchain, &device, &level, &context), "D3D11CreateDeviceAndSwapChain");
    ComPtr<ID3D11Texture2D> buffer;
    Check(swapchain->GetBuffer(0, IID_PPV_ARGS(&buffer)), "GetBuffer");
    ComPtr<ID3D11RenderTargetView> target;
    Check(device->CreateRenderTargetView(buffer.Get(), nullptr, &target), "CreateRenderTargetView");
    ComPtr<ID3DBlob> vsCode, psCode, errors;
    Check(D3DCompile(Shader, strlen(Shader), "probe", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vsCode, &errors), "Compile VS");
    Check(D3DCompile(Shader, strlen(Shader), "probe", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &psCode, &errors), "Compile PS");
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    Check(device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &vs), "Create VS");
    Check(device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &ps), "Create PS");
    context->VSSetShader(vs.Get(), nullptr, 0);
    context->PSSetShader(ps.Get(), nullptr, 0);
    context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    D3D11_VIEWPORT viewport{0, 0, 640, 360, 0, 1};
    context->RSSetViewports(1, &viewport);
    context->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
    const float background[] = { 0.025f, 0.075f, 0.15f, 1 };
    UINT presents = 0, occluded = 0;
    for (UINT frame = 0; frame < 12; ++frame) {
        context->ClearRenderTargetView(target.Get(), background);
        context->Draw(3, 0);
        // Read back before Present; hidden HWNDs may report DXGI_STATUS_OCCLUDED.
        if (frame != 11) {
            const auto hr = swapchain->Present(0, 0);
            Check(hr, "Present");
            if (hr == DXGI_STATUS_OCCLUDED) ++occluded; else ++presents;
        }
    }
    D3D11_TEXTURE2D_DESC stagingDesc{};
    buffer->GetDesc(&stagingDesc);
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0; stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    Check(device->CreateTexture2D(&stagingDesc, nullptr, &staging), "Create staging texture");
    context->CopyResource(staging.Get(), buffer.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Read GPU texture");
    std::ofstream ppm(out / "frame.ppm", std::ios::binary);
    ppm << "P6\n640 360\n255\n";
    uint64_t nonBackground = 0, checksum = 14695981039346656037ull;
    for (UINT y = 0; y < 360; ++y) {
        const auto* row = static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch;
        for (UINT x = 0; x < 640; ++x) {
            const auto* pixel = row + x * 4;
            if (pixel[0] > 20 || pixel[1] > 35 || pixel[2] > 55) ++nonBackground;
            ppm.write(reinterpret_cast<const char*>(pixel), 3);
            for (UINT channel = 0; channel < 3; ++channel) { checksum ^= pixel[channel]; checksum *= 1099511628211ull; }
        }
    }
    context->Unmap(staging.Get(), 0);
    if (nonBackground < 1000) throw std::runtime_error("GPU readback did not contain the rendered triangle");
    Check(device->GetDeviceRemovedReason(), "Device removed reason");
    std::ofstream report(out / "report.txt");
    report << "runtime=d3d11\nvisible=0\nframes_rendered=12\ntriangle_pixels=" << nonBackground
        << "\npixel_fnv1a=" << checksum << "\npresent_ok_count=" << presents
        << "\npresent_occluded_count=" << occluded << "\npresent_failure_count=0\ndevice_removed_reason=0x0\n";
    HMODULE modules[1024]{}; DWORD needed{};
    if (EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &needed)) {
        const DWORD count = std::min<DWORD>(needed / sizeof(HMODULE), 1024);
        for (DWORD index = 0; index < count; ++index) {
            wchar_t path[32768]{};
            if (GetModuleFileNameW(modules[index], path, 32768))
                report << "module=" << std::filesystem::path(path).string() << "\n";
        }
    }
    std::cout << "D3D11 rendered 12 frames; " << nonBackground << " triangle pixels read back.\n";
}
}
int wmain(int argc, wchar_t** argv) {
    const auto out = argc == 2 ? std::filesystem::path(argv[1]) : std::filesystem::current_path();
    std::filesystem::create_directories(out);
    try { Run(out); return 0; }
    catch (const std::exception& error) {
        std::ofstream(out / "probe-error.txt") << error.what() << "\n";
        std::cerr << error.what() << "\n"; return 1;
    }
}
