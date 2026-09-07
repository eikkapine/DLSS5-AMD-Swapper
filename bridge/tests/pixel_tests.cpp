// Exercise the production pixel helpers, including AVX2 tails, against scalar
// references. This executable does not load the private neural runtime.
#define wmain BridgeEntryPointForPixelTests
#include "../src/main.cpp"
#undef wmain

static void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

int wmain() {
    try {
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
        std::cout << "Pixel tests passed: " << comparedBytes << " blend bytes; SIMD tails, channel order, alpha, bypass, native identity, black guard.\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << ex.what() << "\n";
        return 1;
    }
}
