// Byte-preserving transport only: no filtering, resizing, or neural operations.
Texture2D<float4> inputImage : register(t0);
Texture2D<float4> neuralImage : register(t1);
Texture2D<float4> previousImage : register(t2);
RWTexture2D<unorm float4> composedImage : register(u0);
RWByteAddressBuffer counters : register(u1);

cbuffer FrameParameters : register(b0)
{
    uint imageWidth;
    uint imageHeight;
    uint blendAlpha; // Same integer blend as BlendFrames: 0..256.
    uint frameFlags; // Bit 0: history valid; bit 1: compute source RGB sum.
};

float4 FullscreenVS(uint id : SV_VertexID) : SV_Position
{
    float2 position = float2((id << 1) & 2, id & 2);
    return float4(position * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

float4 CopyPS(float4 position : SV_Position) : SV_Target
{
    // BGRA SRVs expose logical RGBA components, matching the CPU byte swizzle.
    return inputImage.Load(int3(int2(position.xy), 0));
}

float4 OpaquePS(float4 position : SV_Position) : SV_Target
{
    return float4(inputImage.Load(int3(int2(position.xy), 0)).rgb, 1.0);
}

#if BRIDGE_COMPUTE
groupshared uint groupChanged[64];
groupshared uint groupNonblack[64];
groupshared uint groupSourceSum[64];

[numthreads(16, 4, 1)]
void ComposeCS(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    bool hasHistory = (frameFlags & 1u) != 0;
    bool computeSourceSum = (frameFlags & 2u) != 0;
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
            // The neural nonblack guard applies even when blendAlpha is zero.
            uint3 neural = (uint3)round(neuralImage.Load(location).rgb * 255.0);
            uint3 original = uint3(0, 0, 0);
            [branch]
            if (computeSourceSum || blendAlpha < 256)
                original = (uint3)round(inputImage.Load(location).rgb * 255.0);

            uint3 result = neural;
            if (blendAlpha == 0)
                result = original;
            else if (blendAlpha < 256)
            {
                result = (original * (256 - blendAlpha) + neural * blendAlpha + 128) >> 8;
                composedImage[position] = float4((float3)result / 255.0, 1.0);
            }
            // The parent displays the original/neural texture directly at endpoints.

            bool changed = !hasHistory;
            if (hasHistory)
            {
                uint3 previous = (uint3)round(previousImage.Load(location).rgb * 255.0);
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
        // Publish each reduced statistic once; increment the high word on carry.
        uint unused;
        if (groupChanged[0] != 0)
            counters.InterlockedOr(0, 1, unused);
        if (groupNonblack[0] != 0)
            counters.InterlockedOr(4, 1, unused);
        if (computeSourceSum && groupSourceSum[0] != 0)
        {
            // A full tile sums to at most 16 * 16 * 3 * 255 = 195840.
            uint sourceSum = groupSourceSum[0];
            uint priorSum;
            counters.InterlockedAdd(8, sourceSum, priorSum);
            // Exact 64-bit RGB sum, including fully white 3840x2160 input.
            if (priorSum > 0xffffffffu - sourceSum)
                counters.InterlockedAdd(12, 1, unused);
        }
    }
}
#endif
