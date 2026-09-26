// EditMode tests (spec §9): UsstReader round-trip on synthetic data and
// rejection of corrupt headers / version mismatches.

using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace GsplatLod.Tests
{
    public class UsstReaderTests
    {
        // Builds a minimal synthetic .usst: 2 nodes (root + child), 1 chunk.
        static byte[] BuildSynthetic(uint version = SplatFormat.Version,
            uint chunkSplats = SplatFormat.ChunkSplats)
        {
            const uint nodeCount = 2;
            const uint chunkCount = 1;
            const ulong totalSplats = 10;
            const ulong leafSplats = 8;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("USST"));
            w.Write(version);
            w.Write(nodeCount);
            w.Write(chunkCount);
            w.Write(chunkSplats);
            w.Write(totalSplats);
            w.Write(leafSplats);
            w.Write(0u); // rootNodeIndex
            foreach (float f in new[] { -1f, -2f, -3f, 1f, 2f, 3f })
                w.Write(f);

            // node 0: root, merged=0, leaves 1..4, one interior child (node 1)
            WriteNode(w, new float3(0, 0, 0), 5f, 0.5f, 0, 1, 1, 1, 4);
            // node 1: merged=5, leaves 6..9, no interior children
            WriteNode(w, new float3(1, 0, 0), 2f, 0.2f, 5, SplatFormat.None, 0, 6, 4);

            // chunk index: 1 chunk with 10 splats
            w.Write(0u);
            w.Write(10u);
            w.Write((ulong)(10 * SplatFormat.SplatRecordSize));
            w.Flush();
            return ms.ToArray();
        }

        static void WriteNode(BinaryWriter w, float3 center, float radius, float featureSize,
            uint merged, uint firstInterior, ushort interiorCount, uint leafFirst, uint leafCount)
        {
            w.Write(center.x);
            w.Write(center.y);
            w.Write(center.z);
            w.Write(radius);
            w.Write(featureSize);
            w.Write(merged);
            w.Write(firstInterior);
            w.Write(interiorCount);
            w.Write((ushort)0);
            w.Write(leafFirst);
            w.Write(leafCount);
        }

        [Test]
        public void RoundTrip()
        {
            using var data = UsstReader.Parse(BuildSynthetic(), Allocator.Temp);

            Assert.AreEqual(2u, data.Header.NodeCount);
            Assert.AreEqual(1u, data.Header.ChunkCount);
            Assert.AreEqual(10ul, data.Header.TotalSplats);
            Assert.AreEqual(8ul, data.Header.LeafSplats);
            Assert.AreEqual(new float3(-1, -2, -3), data.Header.AabbMin);
            Assert.AreEqual(new float3(1, 2, 3), data.Header.AabbMax);

            var root = data.Nodes[0];
            Assert.AreEqual(0u, root.MergedSplatIndex);
            Assert.AreEqual(1u, root.FirstInteriorChild);
            Assert.AreEqual(1, root.InteriorChildCount);
            Assert.AreEqual(1u, root.DirectLeafFirst);
            Assert.AreEqual(4u, root.DirectLeafCount);
            Assert.AreEqual(5f, root.Radius);
            Assert.AreEqual(0.5f, root.FeatureSize);

            var child = data.Nodes[1];
            Assert.AreEqual(SplatFormat.None, child.FirstInteriorChild);
            Assert.AreEqual(5u, child.MergedSplatIndex);

            var chunk = data.Chunks[0];
            Assert.AreEqual(10u, chunk.SplatCount);
            Assert.AreEqual((ulong)(10 * SplatFormat.SplatRecordSize), chunk.ByteLength);
        }

        [Test]
        public void RejectsBadMagic()
        {
            var bytes = BuildSynthetic();
            bytes[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(() => UsstReader.Parse(bytes, Allocator.Temp));
        }

        [Test]
        public void RejectsVersionMismatch()
        {
            var bytes = BuildSynthetic(version: 999);
            Assert.Throws<InvalidDataException>(() => UsstReader.Parse(bytes, Allocator.Temp));
        }

        [Test]
        public void RejectsWrongChunkSplats()
        {
            var bytes = BuildSynthetic(chunkSplats: 32768);
            Assert.Throws<InvalidDataException>(() => UsstReader.Parse(bytes, Allocator.Temp));
        }

        [Test]
        public void RejectsTruncatedFile()
        {
            var bytes = BuildSynthetic();
            var truncated = new byte[bytes.Length - 8];
            System.Array.Copy(bytes, truncated, truncated.Length);
            Assert.Throws<InvalidDataException>(() => UsstReader.Parse(truncated, Allocator.Temp));
        }

        [Test]
        public void NodeRecordSizeMatchesFormat()
        {
            Assert.AreEqual(SplatFormat.NodeRecordSize,
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<NodeRecord>());
            Assert.AreEqual(SplatFormat.ChunkIndexEntrySize,
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<ChunkIndexEntry>());
            Assert.AreEqual(SplatFormat.SplatRecordSize,
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<SplatRecordData>());
        }
    }
}
