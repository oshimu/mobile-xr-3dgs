// Binary format definitions for .usst / .usc (docs/spec.md §3).
// All values little-endian; produced by tools/splat-pipeline/build-lod-unity.

using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace GsplatLod
{
    public static class SplatFormat
    {
        public const uint Magic = 0x54535355;        // "USST" little-endian
        // v2: the record flags carry the LoD opacity, replacing the offline
        // scale inflation. v1 assets render coarse nodes far too small.
        public const uint Version = 2;
        public const int HeaderSize = 64;
        public const int NodeRecordSize = 40;
        public const int ChunkIndexEntrySize = 16;
        public const int SplatRecordSize = 32;
        public const int ChunkSplats = 65536;        // fixed in format
        public const uint None = 0xFFFFFFFF;

        public static string ChunkFileName(string baseName, uint chunkId) =>
            $"{baseName}_{chunkId:D4}.usc";
    }

    public struct UsstHeader
    {
        public uint Version;
        public uint NodeCount;
        public uint ChunkCount;
        public uint ChunkSplats;
        public ulong TotalSplats;
        public ulong LeafSplats;
        public uint RootNodeIndex;
        public float3 AabbMin;
        public float3 AabbMax;
    }

    /// <summary>
    /// Interior-node record (spec §3.4, 40 bytes). Field order/packing matches
    /// the file exactly so the NodeTable blob can be reinterpreted in place.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = SplatFormat.NodeRecordSize)]
    public struct NodeRecord
    {
        public float3 Center;
        public float Radius;
        public float FeatureSize;
        public uint MergedSplatIndex;
        public uint FirstInteriorChild;   // None = no interior children
        public ushort InteriorChildCount;
        public ushort Flags;
        public uint DirectLeafFirst;      // globalIndex; None = no direct leaves
        public uint DirectLeafCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = SplatFormat.ChunkIndexEntrySize)]
    public struct ChunkIndexEntry
    {
        public uint ChunkId;
        public uint SplatCount;
        public ulong ByteLength;
    }

    /// <summary>CPU mirror of the 32-byte SplatRecord (spec §3.2).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = SplatFormat.SplatRecordSize)]
    public struct SplatRecordData
    {
        public float3 Position;
        public uint Rot01;
        public uint Rot23;
        public uint ScaleXY;
        public uint ScaleZFlags;
        public uint Rgba;
    }
}
