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

                // A repeated evaluation of the exact same captured frame can reuse
                // its accepted correction. No motion has occurred in this case.
                if (repeatedSourceCorrection && hasHistory)
                {
                    correction = previousVisible - originalNative;
                }
                else
                {
                    // The AMD compatibility runtime exposes only colour here: no
                    // game depth, motion vectors or exposure. Its asynchronous
                    // backbuffer can therefore contain a neural residual generated
                    // for an older image. Remove the low-frequency part of that
                    // residual spatially so old exposure/colour decisions cannot
                    // make the whole scene pulse between frames. The high-frequency
                    // neural detail is retained.
                    float2 texel = 1.0 / float2(neuralWidth, neuralHeight);
                    float3 residualCenter = neuralLow - currentInput;
                    float3 residualLeft =
                        neuralImage.SampleLevel(resizeSampler, uv - float2(texel.x, 0.0), 0.0).rgb -
                        inputImage.SampleLevel(resizeSampler, uv - float2(texel.x, 0.0), 0.0).rgb;
                    float3 residualRight =
                        neuralImage.SampleLevel(resizeSampler, uv + float2(texel.x, 0.0), 0.0).rgb -
                        inputImage.SampleLevel(resizeSampler, uv + float2(texel.x, 0.0), 0.0).rgb;
                    float3 residualUp =
                        neuralImage.SampleLevel(resizeSampler, uv - float2(0.0, texel.y), 0.0).rgb -
                        inputImage.SampleLevel(resizeSampler, uv - float2(0.0, texel.y), 0.0).rgb;
                    float3 residualDown =
                        neuralImage.SampleLevel(resizeSampler, uv + float2(0.0, texel.y), 0.0).rgb -
                        inputImage.SampleLevel(resizeSampler, uv + float2(0.0, texel.y), 0.0).rgb;
                    float3 lowResidual =
                        (residualCenter * 4.0 + residualLeft + residualRight + residualUp + residualDown) / 8.0;
                    float3 detailResidual = residualCenter - lowResidual;

                    // Preserve a small amount of broad luminance change while
                    // rejecting broad chroma/exposure swings. This keeps the model's
                    // local relighting/detail instead of reducing the effect to a
                    // generic sharpen pass.
                    float sourceLuma = dot(originalNative, lumaWeights);
                    float lowLuma = dot(lowResidual, lumaWeights);
                    float maxBroadLuma = 0.012 + 0.025 * sourceLuma;
                    float safeBroadLuma = clamp(lowLuma, -maxBroadLuma, maxBroadLuma);
                    float3 filteredResidual = detailResidual + safeBroadLuma.xxx;

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

                        // Cross-frame reuse is permitted only for a literally
                        // unchanged native pixel. Near matches are deliberately not
                        // accepted: those were enough to leave colour fringes and
                        // ghost silhouettes during camera movement.
                        if (historyMatchesPreviousSource && hasHistory)
                        {
                            float3 previousNative = previousSourceImage.Load(location).rgb;
                            float nativeDelta = max(abs(originalNative.r - previousNative.r),
                                max(abs(originalNative.g - previousNative.g),
                                    abs(originalNative.b - previousNative.b)));
                            if (nativeDelta < (0.5 / 255.0))
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
                        // luminance detail and rapidly reduce stale correction
                        // magnitude. Static pixels retain the complete filtered
                        // neural detail.
                        float filteredLuma = dot(filteredResidual, lumaWeights);
                        filteredResidual = lerp(filteredResidual, filteredLuma.xxx, motion);
                        filteredResidual *= lerp(1.0, 0.12, motion);
                        correction = filteredResidual * amount;
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
