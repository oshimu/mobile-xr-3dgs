// .usst parser (spec §3.4, §7.2). Reads the whole file (NodeTable is designed
// to stay resident) and validates magic/version/sizes. Any inconsistency
// throws — fallback loading is forbidden by the spec.

using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace GsplatLod
{
    public sealed class UsstData : IDisposable
    {
        public UsstHeader Header;
        public NativeArray<NodeRecord> Nodes;
        public NativeArray<ChunkIndexEntry> Chunks;

        public void Dispose()
        {
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Chunks.IsCreated) Chunks.Dispose();
        }
    }

    public static class UsstReader
    {
        public static UsstData Read(string path, Allocator allocator)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(bytes, allocator, path);
        }

        public static UsstData Parse(byte[] bytes, Allocator allocator, string sourceName = "<memory>")
        {
            if (bytes.Length < SplatFormat.HeaderSize)
                throw new InvalidDataException($"{sourceName}: file too small ({bytes.Length} bytes)");

            var header = new UsstHeader
            {
                Version = ReadU32(bytes, 4),
                NodeCount = ReadU32(bytes, 8),
                ChunkCount = ReadU32(bytes, 12),
                ChunkSplats = ReadU32(bytes, 16),
                TotalSplats = ReadU64(bytes, 20),
                LeafSplats = ReadU64(bytes, 28),
                RootNodeIndex = ReadU32(bytes, 36),
                AabbMin = new float3(ReadF32(bytes, 40), ReadF32(bytes, 44), ReadF32(bytes, 48)),
                AabbMax = new float3(ReadF32(bytes, 52), ReadF32(bytes, 56), ReadF32(bytes, 60)),
            };

            if (ReadU32(bytes, 0) != SplatFormat.Magic)
                throw new InvalidDataException($"{sourceName}: bad magic (not a .usst file)");
            if (header.Version != SplatFormat.Version)
                throw new InvalidDataException(
                    $"{sourceName}: unsupported version {header.Version} (expected {SplatFormat.Version})");
            if (header.ChunkSplats != SplatFormat.ChunkSplats)
                throw new InvalidDataException(
                    $"{sourceName}: chunkSplats {header.ChunkSplats} unsupported (format fixes {SplatFormat.ChunkSplats})");

            long expected = SplatFormat.HeaderSize
                            + (long)header.NodeCount * SplatFormat.NodeRecordSize
                            + (long)header.ChunkCount * SplatFormat.ChunkIndexEntrySize;
            if (bytes.Length != expected)
                throw new InvalidDataException(
                    $"{sourceName}: size mismatch ({bytes.Length} bytes, expected {expected})");
            if (header.NodeCount == 0 || header.RootNodeIndex >= header.NodeCount)
                throw new InvalidDataException($"{sourceName}: invalid nodeCount/rootNodeIndex");

            var data = new UsstData { Header = header };
            data.Nodes = new NativeArray<NodeRecord>((int)header.NodeCount, allocator,
                NativeArrayOptions.UninitializedMemory);
            data.Chunks = new NativeArray<ChunkIndexEntry>((int)header.ChunkCount, allocator,
                NativeArrayOptions.UninitializedMemory);

            unsafe
            {
                fixed (byte* src = bytes)
                {
                    UnsafeUtility.MemCpy(data.Nodes.GetUnsafePtr(), src + SplatFormat.HeaderSize,
                        (long)header.NodeCount * SplatFormat.NodeRecordSize);
                    UnsafeUtility.MemCpy(data.Chunks.GetUnsafePtr(),
                        src + SplatFormat.HeaderSize + (long)header.NodeCount * SplatFormat.NodeRecordSize,
                        (long)header.ChunkCount * SplatFormat.ChunkIndexEntrySize);
                }
            }

            for (int i = 0; i < data.Chunks.Length; i++)
            {
                var e = data.Chunks[i];
                if (e.ChunkId != (uint)i || e.ByteLength != (ulong)e.SplatCount * SplatFormat.SplatRecordSize)
                    throw new InvalidDataException($"{sourceName}: chunk index entry {i} inconsistent");
            }

            return data;
        }

        static uint ReadU32(byte[] b, int o) =>
            (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        static ulong ReadU64(byte[] b, int o) =>
            ReadU32(b, o) | (ulong)ReadU32(b, o + 4) << 32;

        static float ReadF32(byte[] b, int o)
        {
            uint u = ReadU32(b, o);
            unsafe { return *(float*)&u; }
        }
    }
}
