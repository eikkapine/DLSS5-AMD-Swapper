// Transport and composition shaders. The 1:1 path stays byte-addressed; the
// reduced-working-resolution path uses one hardware bilinear sample per pixel.
Texture2D<float4> inputImage : register(t0);
Texture2D<float4> neuralImage : register(t1);
Texture2D<float4> sourceImage : register(t2);
Texture2D<float4> previousImage : register(t3);
ByteAddressBuffer tileStatistics : register(t4);
Texture2D<float4> previousNeuralInput : register(t5);
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
    uint frameFlags; // Bit 0: display history; bit 1: source RGB sum; bit 2: native neural-delta; bit 3: prior neural input; bit 4: async output uses prior input; bit 5: same-strength history.
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
    bool hasPreviousNeuralInput = (frameFlags & 8u) != 0;
    bool preferPreviousNeuralInput = (frameFlags & 16u) != 0;
    bool stabilizeSameStrengthHistory = (frameFlags & 32u) != 0;
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
                // Keep every native source texel. Inline=0 publishes the completed
                // asynchronous neural job from the prior feed, so use that prior
                // low-resolution input deterministically instead of choosing per
                // pixel from neural-vs-input error. That choice could oscillate on
                // the very face/material changes we want to keep and appear as
                // flicker. Motion still fades stale correction where the source
                // changed significantly.
                float3 originalNative = sourceImage.Load(location).rgb;
                float2 uv = (float2(position) + 0.5) / float2(imageWidth, imageHeight);
                float3 neuralLow = neuralImage.SampleLevel(resizeSampler, uv, 0.0).rgb;
                float3 currentInput = inputImage.SampleLevel(resizeSampler, uv, 0.0).rgb;
                float3 neuralInput = currentInput;
                float temporalConfidence = 1.0;
                float motionAmount = 1.0;
                if (hasPreviousNeuralInput)
                {
                    float3 priorInput = previousNeuralInput.SampleLevel(resizeSampler, uv, 0.0).rgb;
                    float3 frameDelta = abs(currentInput - priorInput);
                    motionAmount = max(frameDelta.r, max(frameDelta.g, frameDelta.b));
                    if (preferPreviousNeuralInput)
                    {
                        neuralInput = priorInput;
                        temporalConfidence = 1.0 - smoothstep(0.03, 0.18, motionAmount);
                    }
                }

                float amount = (float)blendAlpha / 256.0;
                float3 correction = (neuralLow - neuralInput) * amount * temporalConfidence;

                // Anti-flicker operates only on the neural correction. The current
                // native source remains untouched, so temporal accumulation cannot
                // soften source detail. Reuse the history sample already required
                // for changed-RGB detection and clamp its correction contribution to
                // the current correction before blending. This adds arithmetic only:
                // no extra texture fetch, neural pass, dispatch, or full-frame copy.
                if (stabilizeSameStrengthHistory && hasPreviousNeuralInput)
                {
                    float stability = 1.0 - smoothstep(0.012, 0.065, motionAmount);
                    float3 previousCorrection = previousVisible - originalNative;
                    float3 historyRange = 0.02 + abs(correction) * 0.50;
                    float3 boundedPrevious = clamp(
                        previousCorrection, correction - historyRange, correction + historyRange);
                    correction = lerp(correction, boundedPrevious, stability * 0.55);
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
