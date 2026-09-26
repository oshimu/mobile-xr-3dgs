// SplatRecord (spec §3.2, 32 bytes) decode + pool addressing (spec §7.5).
#ifndef GSPLAT_LOD_RECORD_INCLUDED
#define GSPLAT_LOD_RECORD_INCLUDED

struct SplatRecord      // 32 bytes
{
    float3 position;    // 12
    uint   rot01;       // x:low16, y:high16 (unorm16)
    uint   rot23;       // z:low16, w:high16 (unorm16)
    uint   scaleXY;     // half sx: low16, half sy: high16
    uint   scaleZ_flags;// half sz: low16, flags: high16
    uint   rgba;        // packed unorm8x4
};

// The pool spans up to four buffers: a single StructuredBuffer is capped by
// the platform max buffer size (128MB on Quest 3). Each buffer serves
// _SplatBufferCap splats, so pool index i reads buffer (i / _SplatBufferCap)
// at local offset (i % _SplatBufferCap).
//
// Only the buffers the pool actually uses are declared, selected by the
// GSPLAT_POOLS_* keyword. GLES caps shader storage blocks in the vertex
// stage hard (4 on Adreno), and a *declared* block counts even when never
// indexed: declaring all four plus the slot table and the order buffer is
// six blocks and fails to link on Adreno with
// "BufferBlock location or component exceeds max allowed".
StructuredBuffer<SplatRecord> _SplatPool0;
#if !defined(GSPLAT_POOLS_1)
StructuredBuffer<SplatRecord> _SplatPool1;
#endif
#if defined(GSPLAT_POOLS_4)
StructuredBuffer<SplatRecord> _SplatPool2;
StructuredBuffer<SplatRecord> _SplatPool3;
#endif
uint _SplatBufferCap;
StructuredBuffer<uint> _ChunkSlotTable;   // chunkId -> pool slot, 0xFFFFFFFF = not resident

// globalIndex -> index into the splat pool (spec §7.5 addressing).
// Returns false when the chunk was evicted between sort and draw — the
// caller must discard the splat.
bool TrySplatPoolIndex(uint globalIndex, out uint poolIndex)
{
    uint slot = _ChunkSlotTable[globalIndex >> 16];
    poolIndex = slot * 65536u + (globalIndex & 0xFFFFu);
    return slot != 0xFFFFFFFFu;
}

SplatRecord LoadSplat(uint poolIndex)
{
#if defined(GSPLAT_POOLS_1)
    return _SplatPool0[poolIndex];
#else
    uint b = _SplatBufferCap == 0u ? 0u : poolIndex / _SplatBufferCap;
    uint local = poolIndex - b * _SplatBufferCap;
    if (b == 0u) return _SplatPool0[local];
    #if defined(GSPLAT_POOLS_4)
    if (b == 1u) return _SplatPool1[local];
    if (b == 2u) return _SplatPool2[local];
    return _SplatPool3[local];
    #else
    return _SplatPool1[local];
    #endif
#endif
}

float DecodeUnorm16(uint u)
{
    return (u & 0xFFFFu) / 65535.0 * 2.0 - 1.0;
}

// Decoded quaternion as (x, y, z, w); MUST be normalized after decode (spec §3.2).
float4 DecodeRotation(SplatRecord rec)
{
    float4 q = float4(
        DecodeUnorm16(rec.rot01),
        DecodeUnorm16(rec.rot01 >> 16),
        DecodeUnorm16(rec.rot23),
        DecodeUnorm16(rec.rot23 >> 16));
    return normalize(q);
}

float3 DecodeScale(SplatRecord rec)
{
    return float3(
        f16tof32(rec.scaleXY),
        f16tof32(rec.scaleXY >> 16),
        f16tof32(rec.scaleZ_flags));
}

float4 DecodeColor(SplatRecord rec)
{
    return float4(
        (rec.rgba & 0xFFu) / 255.0,
        ((rec.rgba >> 8) & 0xFFu) / 255.0,
        ((rec.rgba >> 16) & 0xFFu) / 255.0,
        ((rec.rgba >> 24) & 0xFFu) / 255.0);
}

// Base render extent in standard deviations. Matches spark's default
// SparkRenderer.maxStdDev = sqrt(8).
#define GSPLAT_MAX_STD_DEV 2.8284271

// LoD opacity D (spec: "how many source splats this representative stands in
// for", 1..5) stored as unorm8 (D-1)/4 in the low byte of the record flags.
// 0 marks an ordinary leaf. Representatives are drawn both more opaque and
// over a wider extent so they cover the cell they stand in for, matching
// spark's splatVertex.glsl:
//     adjustedStdDev = maxStdDev + 0.7 * (D - 1)
// Baking the equivalent into the scales offline (spark's non-default
// lodInflate path) left coarse nodes covering only ~half their node radius.
float DecodeLodOpacity(SplatRecord rec)
{
    uint code = (rec.scaleZ_flags >> 16) & 0xFFu;   // 0 = leaf, 1..255 = D 1..5
    return code == 0u ? 0.0 : 1.0 + 4.0 * ((code - 1u) / 254.0);
}

// Render extent in standard deviations for a splat with LoD opacity d
// (0 = leaf). Leaves keep GSPLAT_MAX_STD_DEV.
float LodStdDev(float d)
{
    return GSPLAT_MAX_STD_DEV + 0.7 * max(d - 1.0, 0.0);
}

// How many source splats a representative with alpha a stands in for.
// Same expression spark uses in splatFragment.glsl.
float LodSplatCount(float a)
{
    return exp((a * a - 1.0) / 2.718281828459045);
}

// Alpha at squared distance z2 (in sigma^2 units, i.e. stdDev^2 at the quad
// edge). At or below 1 this is an ordinary Gaussian. Above it the splat
// stands in for N others, so the coverage is the probability that at least
// one of them hits the pixel: 1 - (1 - g)^N. Clamping alpha to 1 instead
// draws a flat opaque disc with a hard rim — visible as large round dots.
float LodAlpha(float a, float z2)
{
    float g = exp(-0.5 * z2);
    if (a <= 1.0)
        return a * g;
    return 1.0 - exp(LodSplatCount(a) * log(max(1.0 - g, 1e-20)));
}

// Quad trim factor: the normalized radius at which LodAlpha falls to cutoff,
// so the quad ends where the splat stops contributing. Inverse of LodAlpha.
float LodClip(float a, float stdDev, float cutoff)
{
    float g;
    if (a <= 1.0)
        g = cutoff / a;                      // a * exp(-z2/2) = cutoff
    else                                     // 1 - (1 - g)^N = cutoff
        g = 1.0 - exp(log(1.0 - cutoff) / LodSplatCount(a));
    if (g >= 1.0)
        return 0.0;
    return min(1.0, sqrt(-2.0 * log(max(g, 1e-20))) / stdDev);
}

#endif
