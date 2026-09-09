// Transport and composition shaders. The 1:1 path stays byte-addressed; the
// reduced-working-resolution path uses one hardware bilinear sample per pixel.
Texture2D<float4> inputImage : register(t0);
Texture2D<float4> neuralImage : register(t1);
Texture2D<float4> sourceImage : register(t2);
Texture2D<float4> previousImage : register(t3);
ByteAddressBuffer tileStatistics : register(t4);
Texture2D<float4> previousInputImage : register(t5);
Texture2D<float4> previousSourceImage : register(t6);
RWTexture2D<unorm float4> composedImage : register(u0);
RWByteAddressBuffer counters : register(u1);
RWByteAddressBuffer tileStatisticsOutput : register(u2);
SamplerState resizeSampler : register(s0);

cbuffer FrameParameters : register(b0)
{
    uint neuralWidth;
    uint neuralHeight;
    uint imageWidth;
    uint imageHeight;
    uint blendAlpha; // 0..256 normally; native-detail mode allows up to 1024 (4x).
    uint frameFlags; // Bit 0: display history; bit 1: source RGB sum; bit 2: native async-residual composite; bit 3: repeated exact source; bit 4: previous source available; bit 5: display history belongs to previous source.
    uint reserved0;
    uint reserved1;
};

struct FullscreenVertex
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

FullscreenVertex FullscreenVS(uint id : SV_VertexID)
{
    FullscreenVertex output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.position = float4(output.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float4 CopyPS(float4 position : SV_Position) : SV_Target
{
    // BGRA SRVs expose logical RGBA components, matching the CPU byte swizzle.
    return inputImage.Load(int3(int2(position.xy), 0));
}

float4 ScalePS(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    // The viewport is already the aspect-fit destination rectangle. Sampling
    // normalized UVs lets the fixed-function sampler do the resize without a
    // CPU readback or an extra full-size intermediate texture.
    return inputImage.SampleLevel(resizeSampler, uv, 0.0);
}

float4 OpaquePS(float4 position : SV_Position) : SV_Target
{
    return float4(inputImage.Load(int3(int2(position.xy), 0)).rgb, 1.0);
}

#if BRIDGE_COMPUTE
groupshared uint groupChanged[64];
groupshared uint groupNonblack[64];
groupshared uint groupSourceSum[64];
groupshared uint4 finalStatistics[64];

[numthreads(16, 4, 1)]
void ComposeCS(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    bool hasHistory = (frameFlags & 1u) != 0;
    bool computeSourceSum = (frameFlags & 2u) != 0;
    bool nativeResidualComposite = (frameFlags & 4u) != 0;
    bool repeatedSourceCorrection = (frameFlags & 8u) != 0;
    bool hasPreviousSource = (frameFlags & 16u) != 0;
    bool historyMatchesPreviousSource = (frameFlags & 32u) != 0;
    uint laneChanged = 0;
    uint laneNonblack = 0;
    uint laneSourceSum = 0;

    // Keep the existing 16x16 dispatch tile; each lane visits four rows.
    [unroll]
    for (uint row = 0; row < 4; ++row)
    {
        uint2 position = uint2(group.x * 16 + thread.x, group.y * 16 + thread.y + 4 * row);
        if (position.x < imageWidth && position.y < imageHeight)
        {
            int3 location = int3(position, 0);
            uint3 original = uint3(0, 0, 0);
            uint3 neural = uint3(0, 0, 0);
            uint3 result = uint3(0, 0, 0);

            float3 previousVisible = hasHistory ? previousImage.Load(location).rgb : 0.0;
            if (nativeResidualComposite)
            {
                // The AMD runtime reports this path as "async (residual from an
                // earlier frame)": it applies the most recently completed neural
                // residual to the backbuffer being presented now. Therefore the
                // correction is the async backbuffer minus THIS feed's low-res
                // input. Subtracting an older captured frame injects ordinary
                // camera/exposure changes into the correction and shows up as
                // brightness pulses and movement flicker.
                float3 originalNative = sourceImage.Load(location).rgb;
                float2 uv = (float2(position) + 0.5) / float2(imageWidth, imageHeight);
                float3 neuralLow = neuralImage.SampleLevel(resizeSampler, uv, 0.0).rgb;
                float3 currentInput = inputImage.SampleLevel(resizeSampler, uv, 0.0).rgb;

                float amount = (float)blendAlpha / 256.0;
                const float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
                float3 correction = 0.0;

                // Always recompute duplicate presentations from the current neural
                // output. Reusing the previously corrected image here can freeze a
                // stale edge correction indefinitely when WGC has no new source
                // frame to deliver after movement stops.
                {
                    // The AMD compatibility runtime exposes only colour here: no
                    // game depth, motion vectors or exposure. Its asynchronous
                    // backbuffer can therefore contain a neural residual generated
                    // for an older image. Remove the low-frequency part of that
                    // residual spatially so old exposure/colour decisions cannot
                    // make the whole scene pulse between frames. The high-frequency
                    // neural detail is retained.
                    float2 texel = 1.0 / float2(neuralWidth, neuralHeight);
                    float3 inputLeft = inputImage.SampleLevel(
                        resizeSampler, uv - float2(texel.x, 0.0), 0.0).rgb;
                    float3 inputRight = inputImage.SampleLevel(
                        resizeSampler, uv + float2(texel.x, 0.0), 0.0).rgb;
                    float3 inputUp = inputImage.SampleLevel(
                        resizeSampler, uv - float2(0.0, texel.y), 0.0).rgb;
                    float3 inputDown = inputImage.SampleLevel(
                        resizeSampler, uv + float2(0.0, texel.y), 0.0).rgb;
                    float3 residualCenter = neuralLow - currentInput;
                    float3 residualLeft =
                        neuralImage.SampleLevel(resizeSampler, uv - float2(texel.x, 0.0), 0.0).rgb - inputLeft;
                    float3 residualRight =
                        neuralImage.SampleLevel(resizeSampler, uv + float2(texel.x, 0.0), 0.0).rgb - inputRight;
                    float3 residualUp =
                        neuralImage.SampleLevel(resizeSampler, uv - float2(0.0, texel.y), 0.0).rgb - inputUp;
                    float3 residualDown =
                        neuralImage.SampleLevel(resizeSampler, uv + float2(0.0, texel.y), 0.0).rgb - inputDown;
                    float3 lowResidual =
                        (residualCenter * 4.0 + residualLeft + residualRight + residualUp + residualDown) / 8.0;
                    float3 detailResidual = residualCenter - lowResidual;

                    // Experimental clarity branch: keep the stable dev.14 residual
                    // treatment, but bias the retained neural signal toward local
                    // structure and luminance/shading. Broad chroma remains rejected
                    // because it is the least stable part of the colour-only async
                    // path and was responsible for most visible flicker.
                    float sourceLuma = dot(originalNative, lumaWeights);
                    float lowLuma = dot(lowResidual, lumaWeights);
                    float maxBroadLuma = 0.016 + 0.032 * sourceLuma;
                    float safeBroadLuma = clamp(lowLuma, -maxBroadLuma, maxBroadLuma);
                    float3 filteredResidual = detailResidual * 1.30 + safeBroadLuma.xxx * 1.15;

                    // Add a current-frame-only clarity term from the native source.
                    // Keeping this calculation at native resolution prevents the
                    // 480p neural input from creating a soft halo around moving edges.
                    int nativeX = int(position.x);
                    int nativeY = int(position.y);
                    int leftX = max(nativeX - 1, 0);
                    int rightX = min(nativeX + 1, int(imageWidth) - 1);
                    int upY = max(nativeY - 1, 0);
                    int downY = min(nativeY + 1, int(imageHeight) - 1);
                    float3 nativeLeft = sourceImage.Load(int3(leftX, nativeY, 0)).rgb;
                    float3 nativeRight = sourceImage.Load(int3(rightX, nativeY, 0)).rgb;
                    float3 nativeUp = sourceImage.Load(int3(nativeX, upY, 0)).rgb;
                    float3 nativeDown = sourceImage.Load(int3(nativeX, downY, 0)).rgb;
                    float3 nativeBlur =
                        (originalNative * 4.0 + nativeLeft + nativeRight + nativeUp + nativeDown) / 8.0;
                    float localLuma = dot(originalNative - nativeBlur, lumaWeights);
                    float localClarity = clamp(localLuma * 0.70, -0.024, 0.024);
                    float maxChannel = max(originalNative.r, max(originalNative.g, originalNative.b));
                    float minChannel = min(originalNative.r, min(originalNative.g, originalNative.b));
                    float saturation = maxChannel - minChannel;
                    float neutralMask = 1.0 - smoothstep(0.045, 0.20, saturation);
                    float brightMask = smoothstep(0.36, 0.82, sourceLuma);
                    float veilReduction = neutralMask * brightMask *
                        (0.004 + 0.010 * smoothstep(0.50, 0.90, sourceLuma));
                    float sourceClarityLuma = localClarity - veilReduction;

                    float motion = 0.0;
                    bool reusedExactStaticCorrection = false;
                    if (hasPreviousSource)
                    {
                        float3 previousInput = previousInputImage.SampleLevel(resizeSampler, uv, 0.0).rgb;
                        float3 frameDelta = abs(currentInput - previousInput);
                        float absoluteMotion = max(frameDelta.r, max(frameDelta.g, frameDelta.b));
                        float3 relativeDelta = frameDelta /
                            (max(abs(currentInput), abs(previousInput)) + 0.02);
                        float relativeMotion = max(relativeDelta.r, max(relativeDelta.g, relativeDelta.b));
                        motion = max(
                            smoothstep(1.5 / 255.0, 10.0 / 255.0, absoluteMotion),
                            smoothstep(0.025, 0.12, relativeMotion));

                        // The low-resolution neural input can blur a moving edge
                        // enough that its temporal delta looks smaller than the
                        // real native-resolution motion. Fold the native pixel delta
                        // into the rejection mask so those edge pixels do not keep
                        // a faint correction from the previous scene position.
                        float3 previousNative = previousSourceImage.Load(location).rgb;
                        float3 previousNativeLeft = previousSourceImage.Load(int3(leftX, nativeY, 0)).rgb;
                        float3 previousNativeRight = previousSourceImage.Load(int3(rightX, nativeY, 0)).rgb;
                        float3 previousNativeUp = previousSourceImage.Load(int3(nativeX, upY, 0)).rgb;
                        float3 previousNativeDown = previousSourceImage.Load(int3(nativeX, downY, 0)).rgb;
                        float nativeDelta = max(abs(originalNative.r - previousNative.r),
                            max(abs(originalNative.g - previousNative.g),
                                abs(originalNative.b - previousNative.b)));
                        float nativeLeftDelta = max(abs(nativeLeft.r - previousNativeLeft.r),
                            max(abs(nativeLeft.g - previousNativeLeft.g), abs(nativeLeft.b - previousNativeLeft.b)));
                        float nativeRightDelta = max(abs(nativeRight.r - previousNativeRight.r),
                            max(abs(nativeRight.g - previousNativeRight.g), abs(nativeRight.b - previousNativeRight.b)));
                        float nativeUpDelta = max(abs(nativeUp.r - previousNativeUp.r),
                            max(abs(nativeUp.g - previousNativeUp.g), abs(nativeUp.b - previousNativeUp.b)));
                        float nativeDownDelta = max(abs(nativeDown.r - previousNativeDown.r),
                            max(abs(nativeDown.g - previousNativeDown.g), abs(nativeDown.b - previousNativeDown.b)));
                        float nativeNeighborhoodDelta = max(nativeDelta,
                            max(max(nativeLeftDelta, nativeRightDelta), max(nativeUpDelta, nativeDownDelta)));
                        float nativeMotion = smoothstep(1.0 / 255.0, 8.0 / 255.0, nativeNeighborhoodDelta);
                        motion = max(motion, nativeMotion);

                        // Cross-frame reuse is permitted only for a literally
                        // unchanged native pixel whose low-resolution input is also
                        // locally static. This keeps the stationary anti-flicker
                        // benefit without carrying a previous correction through a
                        // moving edge that happens to quantize to the same RGB value.
                        if (!repeatedSourceCorrection && historyMatchesPreviousSource && hasHistory)
                        {
                            if (nativeNeighborhoodDelta < (0.5 / 255.0) && motion < 0.02)
                            {
                                correction = previousVisible - previousNative;
                                reusedExactStaticCorrection = true;
                            }
                        }
                    }

                    if (!reusedExactStaticCorrection)
                    {
                        // Stale colour residuals are the source of the weak
                        // chromatic-aberration trail. As motion rises, keep only
                        // luminance detail and drive stale neural correction all the
                        // way to zero at strong motion. Static pixels retain the
                        // complete filtered neural detail.
                        float filteredLuma = dot(filteredResidual, lumaWeights);
                        float strengthMotionBoost = lerp(
                            1.0, 1.75, saturate((amount - 1.0) / 3.0));
                        float effectiveMotion = saturate(motion * strengthMotionBoost);
                        float chromaReject = saturate(effectiveMotion * 1.50);
                        float staleResidualWeight =
                            1.0 - smoothstep(0.04, 0.55, effectiveMotion);
                        staleResidualWeight *= staleResidualWeight;
                        filteredResidual = lerp(filteredResidual, filteredLuma.xxx, chromaReject);
                        filteredResidual *= staleResidualWeight;

                        // Neural correction is motion-gated; the source-derived
                        // clarity component stays active because it belongs to the
                        // current frame. Strength scales both, with a clamp keeping
                        // the clarity tone adjustment bounded even at 4x.
                        float clarityAmount = min(amount, 4.0);
                        float boundedSourceClarity = clamp(
                            sourceClarityLuma * clarityAmount, -0.055, 0.055);
                        correction = filteredResidual * amount + boundedSourceClarity.xxx;
                    }
                }

                float3 nativeResult = saturate(originalNative + correction);
                original = (uint3)round(originalNative * 255.0);
                neural = (uint3)round(neuralLow * 255.0);
                result = (uint3)round(nativeResult * 255.0);
                composedImage[position] = float4(nativeResult, 1.0);
            }
            else
            {
                // Legacy same-resolution behavior is retained for rollback and
                // diagnostics which do not use the native residual path.
                neural = (uint3)round(neuralImage.Load(location).rgb * 255.0);
                if (computeSourceSum || blendAlpha < 256)
                    original = (uint3)round(inputImage.Load(location).rgb * 255.0);

                result = neural;
                if (blendAlpha == 0)
                    result = original;
                else if (blendAlpha < 256)
                {
                    result = (original * (256 - blendAlpha) + neural * blendAlpha + 128) >> 8;
                    composedImage[position] = float4((float3)result / 255.0, 1.0);
                }
            }

            bool changed = !hasHistory;
            if (hasHistory)
            {
                uint3 previous = (uint3)round(previousVisible * 255.0);
                changed = any(previous != result);
            }
            laneChanged |= changed ? 1u : 0u;
            laneNonblack |= any(neural != 0) ? 1u : 0u;
            if (computeSourceSum)
                laneSourceSum += original.r + original.g + original.b;
        }
    }

    // Out-of-bounds lanes contribute zero and still reach every group barrier.
    groupChanged[index] = laneChanged;
    groupNonblack[index] = laneNonblack;
    groupSourceSum[index] = laneSourceSum;
    GroupMemoryBarrierWithGroupSync();

    [unroll]
    for (uint stride = 32; stride > 0; stride >>= 1)
    {
        if (index < stride)
        {
            groupChanged[index] |= groupChanged[index + stride];
            groupNonblack[index] |= groupNonblack[index + stride];
            if (computeSourceSum)
                groupSourceSum[index] += groupSourceSum[index + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (index == 0)
    {
        // Each tile owns one slot, eliminating contention on a single global
        // counter. Every slot is overwritten, including zero-valued tail tiles.
        // A tile sums to at most 16 * 16 * 3 * 255 = 195840, fitting in uint.
        uint tile = group.y * ((imageWidth + 15) / 16) + group.x;
        tileStatisticsOutput.Store4(tile * 16,
            uint4(groupChanged[0], groupNonblack[0], groupSourceSum[0], 0));
    }
}

uint4 CombineStatistics(uint4 left, uint4 right)
{
    uint low = left.z + right.z;
    uint high = left.w + right.w + (low < left.z ? 1u : 0u);
    return uint4(left.x | right.x, left.y | right.y, low, high);
}

[numthreads(64, 1, 1)]
void ReduceStatisticsCS(uint index : SV_GroupIndex)
{
    uint tileCount = ((imageWidth + 15) / 16) * ((imageHeight + 15) / 16);
    uint4 value = uint4(0, 0, 0, 0);
    for (uint tile = index; tile < tileCount; tile += 64)
        value = CombineStatistics(value, tileStatistics.Load4(tile * 16));

    finalStatistics[index] = value;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint stride = 32; stride > 0; stride >>= 1)
    {
        if (index < stride)
            finalStatistics[index] = CombineStatistics(
                finalStatistics[index], finalStatistics[index + stride]);
        GroupMemoryBarrierWithGroupSync();
    }
    // One writer replaces all four counters. No clear or global atomics are
    // required. Carry propagation preserves the exact 64-bit sum at 4K.
    if (index == 0)
        counters.Store4(0, finalStatistics[0]);
}
#endif
