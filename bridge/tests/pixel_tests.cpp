// Exercise the production pixel helpers, including AVX2 tails, against scalar
// references. This executable does not load the private neural runtime.
#define wmain BridgeEntryPointForPixelTests
#include "../src/main.cpp"
#undef wmain

static void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

static unsigned startupDrains = 0;
static void CountStartupDrain() noexcept { ++startupDrains; }

static void CheckStartupDiagnostics() {
    const auto stalled = ParseRuntimeHealthLog(
        "hooked IDXGISwapChain::Present\nengine init ok\n");
    Require(stalled.engineInitialized && !stalled.completedJob && !stalled.fatal,
            "Issue #3 initialization-only log was accepted as healthy");
    Require(stalled.detail.find("engine initialized") != std::string::npos,
            "Startup stall did not identify initialized engine");
    const auto hooks = ParseRuntimeHealthLog("detour of Present failed (2)\n");
    Require(hooks.renderHookFailure && !hooks.engineInitialized && !hooks.completedJob,
            "Render hook failure phase was lost");
    const auto completed = ParseRuntimeHealthLog("ENGINE INIT OK\nnetwork job 12 done in 22 ms\n");
    Require(completed.completedJob && !completed.fatal, "Completed job not recognized");
    const auto fatal = ParseRuntimeHealthLog("network job 12 done\nGPU errors\n");
    Require(fatal.fatal, "Completed job masked a later GPU failure");

    Require(BridgeRuntimeConfigurationError({1, -1, 0, 0, -1, 1}).empty(),
            "Legacy async color-only configuration rejected");
    Require(BridgeRuntimeConfigurationError({1, 1, 0, 0, 0, 1}).empty(),
            "Current async color-only configuration rejected");
    Require(!BridgeRuntimeConfigurationError({1, 0, 0, 0, 0, 1}).empty(),
            "Legacy Inline=0 incorrectly overrode current Async=0");
    Require(BridgeRuntimeConfigurationError({1, 1, 1, 0, 0, 1}).empty(),
            "Current Async=1 did not supersede legacy Inline");
    Require(!BridgeRuntimeConfigurationError({1, 1, 0, 0, 1, 1}).empty(),
            "FSR pre-upscale setting accepted for color-only bridge");
    Require(!BridgeRuntimeConfigurationError({1, 1, 0, 0, -1, 1}).empty(),
            "Current runtime's default pre-upscale path accepted without explicit override");
    Require(!BridgeRuntimeConfigurationError({1, 1, 0, 1, 0, 1}).empty(),
            "FSR input setting accepted for color-only bridge");
    Require(!BridgeRuntimeConfigurationError({0, 1, 0, 0, 0, 1}).empty(),
            "Disabled runtime accepted");

    std::ostringstream log;
    startupDrains = 0;
    {
        StartupDiagnostics diagnostics(log, CountStartupDrain);
        const HipWorkProgress hip{true, 1, 0};
        Require(diagnostics.Sample(std::chrono::milliseconds(0), "warmup", 1, 1, 1,
                    DXGI_STATUS_OCCLUDED, true, stalled, hip), "First startup sample suppressed");
        Require(startupDrains == 1, "HIP samples were not drained before startup readiness");
        Require(!diagnostics.Sample(std::chrono::milliseconds(10), "health_wait", 1, 2, 1,
                    S_OK, true, stalled, hip), "Startup diagnostics were not rate limited");
        Require(diagnostics.Sample(std::chrono::milliseconds(1000), "health_wait", 3, 3, 1,
                    S_OK, true, stalled, hip), "Periodic startup sample suppressed");
        Require(diagnostics.Sample(std::chrono::milliseconds(1010), "health_timeout", 3, 3, 1,
                    S_OK, true, stalled, hip, true), "Final failed-startup sample suppressed");
    }
    Require(startupDrains == 4, "HIP samples were not drained on startup scope exit");
    Require(log.str().find("feed_occluded=1 last_present_hr=0x87a0001") != std::string::npos,
            "Occluded Present result lost from startup diagnostics");
    Require(log.str().find("engine_initialized=1 render_hook_failure=0 completed_job=0") != std::string::npos,
            "Issue #3 runtime phase lost from startup diagnostics");
    Require(log.str().find("phase=health_timeout") != std::string::npos,
            "Startup timeout not recorded");
}

int wmain() {
    try {
        CheckStartupDiagnostics();
        uint32_t randomState = 0x83dcb159;
        auto nextByte = [&]() -> uint8_t {
            randomState = randomState * 1664525u + 1013904223u;
            return static_cast<uint8_t>(randomState >> 24);
        };
        uint64_t comparedBytes = 0;
        for (const UINT pixels : {1u, 2u, 3u, 4u, 5u, 7u, 8u, 9u, 15u, 16u, 17u, 31u, 32u, 33u, 257u}) {
            FramePixels original{pixels, 1, std::vector<uint8_t>(pixels * 4)};
            FramePixels neural = original;
            for (auto& value : original.rgba) value = nextByte();
            for (auto& value : neural.rgba) value = nextByte();
            std::vector<uint8_t> converted(pixels * 4 + 32, 0xa5);
            SwizzleBgraToRgba(original.rgba.data(), converted.data() + 16, pixels);
            for (size_t i = 0; i < pixels; ++i) {
                for (size_t channel = 0; channel < 4; ++channel) {
                    const size_t sourceChannel = channel == 0 ? 2 : channel == 2 ? 0 : channel;
                    Require(converted[16 + i * 4 + channel] == original.rgba[i * 4 + sourceChannel], "BGRA swizzle mismatch");
                }
            }
            SwizzleRgbaToBgraOpaque(original.rgba.data(), converted.data() + 16, pixels);
            for (size_t i = 0; i < pixels; ++i) {
                Require(converted[16 + i * 4] == original.rgba[i * 4 + 2], "Opaque swizzle blue mismatch");
                Require(converted[16 + i * 4 + 1] == original.rgba[i * 4 + 1], "Opaque swizzle green mismatch");
                Require(converted[16 + i * 4 + 2] == original.rgba[i * 4], "Opaque swizzle red mismatch");
                Require(converted[16 + i * 4 + 3] == 255, "Opaque swizzle alpha mismatch");
            }
            for (size_t i = 0; i < 16; ++i) {
                Require(converted[i] == 0xa5 && converted[pixels * 4 + 16 + i] == 0xa5, "Swizzle wrote outside destination");
            }
            for (uint32_t alpha = 0; alpha <= 256; ++alpha) {
                const auto result = BlendFrames(original, neural, alpha / 256.0f, true);
                for (size_t i = 0; i < result.rgba.size(); ++i) {
                    uint8_t expected = original.rgba[i];
                    if (alpha > 0) {
                        expected = i % 4 == 3 ? 255 : static_cast<uint8_t>(
                            (original.rgba[i] * (256 - alpha) + neural.rgba[i] * alpha + 128) >> 8);
                    }
                    Require(result.rgba[i] == expected, "SIMD blend differs from scalar reference");
                    ++comparedBytes;
                }
            }
            Require(BlendFrames(original, neural, 1.0f, false).rgba == original.rgba, "Bypass changed pixels");
            Require(ScaleToFit(original, pixels, 1).rgba == original.rgba, "Native identity changed pixels");
            Require(BlendFrames(original, neural, -1.0f, true).rgba == original.rgba, "Negative strength did not bypass");
            Require(BlendFrames(original, FramePixels{}, 1.0f, true).rgba == original.rgba, "Invalid neural frame did not bypass");
        }
        FramePixels dark{3, 1, std::vector<uint8_t>(12, 0)};
        Require(!SourceMeaningfullyNonBlack(dark), "Black frame failed guard");
        dark.rgba[0] = 17;
        Require(!SourceMeaningfullyNonBlack(dark), "Below-threshold frame failed guard");
        dark.rgba[0] = 18;
        Require(SourceMeaningfullyNonBlack(dark), "Exact-threshold frame failed guard");
        Require(!SourceMeaningfullyNonBlack(FramePixels{}), "Empty frame failed guard");
        std::cout << "Pixel tests passed: " << comparedBytes << " blend bytes; SIMD tails, channel order, alpha, bypass, native identity, black guard; startup phases, HIP drain, async configuration.\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << ex.what() << "\n";
        return 1;
    }
}
