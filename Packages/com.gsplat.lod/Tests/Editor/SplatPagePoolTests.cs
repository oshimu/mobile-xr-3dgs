// SplatPagePool LRU/needed-set state transition tests (spec §9).

using NUnit.Framework;
using Unity.Collections;

namespace GsplatLod.Tests
{
    public class SplatPagePoolTests
    {
        static NativeArray<SplatRecordData> MakeChunk(int count)
        {
            return new NativeArray<SplatRecordData>(count, Allocator.Temp);
        }

        [Test]
        public void LruEvictionSkipsNeeded()
        {
            using var pool = new SplatPagePool(pageCount: 2, chunkSplats: 16, chunkCount: 4);
            using (var data = MakeChunk(16))
            {
                Assert.IsTrue(pool.Upload(0, data, 16));
                Assert.IsTrue(pool.Upload(1, data, 16));
                Assert.AreEqual(2, pool.ResidentCount);

                // Chunk 0 is needed implicitly (root); chunk 1 is not.
                pool.SetNeeded(stackalloc uint[] { 0 });
                Assert.AreEqual(1, pool.FreeableCount);
                pool.Touch(stackalloc uint[] { 1 });   // chunk 1 more recent than 0

                // Pool full; chunk 0 needed → chunk 1 must be evicted despite recency.
                Assert.IsTrue(pool.Upload(2, data, 16));
                Assert.IsTrue(pool.IsResident(0), "needed chunk was evicted");
                Assert.IsFalse(pool.IsResident(1));
                Assert.IsTrue(pool.IsResident(2));
                Assert.AreEqual(2, pool.ResidentCount);
                Assert.AreEqual(1, pool.FreeableCount, "the newly loaded chunk 2 is itself freeable");

                // Everything needed → upload must be refused, not evict.
                pool.SetNeeded(stackalloc uint[] { 0, 2 });
                Assert.AreEqual(0, pool.FreeableCount);
                Assert.IsFalse(pool.Upload(3, data, 16), "upload evicted a needed page");
                Assert.IsTrue(pool.IsResident(2));

                // Drop chunk 2 from the needed set → succeeds again.
                pool.SetNeeded(stackalloc uint[] { 0 });
                Assert.AreEqual(1, pool.FreeableCount);
                Assert.IsTrue(pool.Upload(3, data, 16));
                Assert.IsFalse(pool.IsResident(2));
            }
        }

        [Test]
        public void SecondaryNeededSetIsProtected()
        {
            // The chunks behind the active order buffer are passed as the
            // secondary set; they must survive a slice that no longer wants them.
            using var pool = new SplatPagePool(pageCount: 3, chunkSplats: 16, chunkCount: 5);
            using var data = MakeChunk(16);

            Assert.IsTrue(pool.Upload(0, data, 16));
            Assert.IsTrue(pool.Upload(1, data, 16));
            Assert.IsTrue(pool.Upload(2, data, 16));

            // The new slice no longer wants chunk 1, but it still backs the
            // active order buffer, so it is passed as the secondary set.
            pool.SetNeeded(stackalloc uint[] { 0, 3 }, stackalloc uint[] { 1 });
            Assert.AreEqual(1, pool.FreeableCount);   // only chunk 2

            Assert.IsTrue(pool.Upload(3, data, 16));
            Assert.IsTrue(pool.IsResident(1), "chunk behind the active order buffer was evicted");
            Assert.IsFalse(pool.IsResident(2));
        }

        [Test]
        public void SetAllNeededForbidsEviction()
        {
            using var pool = new SplatPagePool(pageCount: 2, chunkSplats: 16, chunkCount: 3);
            using var data = MakeChunk(16);

            Assert.IsTrue(pool.Upload(0, data, 16));
            Assert.IsTrue(pool.Upload(1, data, 16));
            pool.SetAllNeeded();
            Assert.AreEqual(0, pool.FreeableCount);
            Assert.IsFalse(pool.Upload(2, data, 16));
        }

        [Test]
        public void MaxDevicePagesMatchesConstructorLimit()
        {
            long cap = 2 * 16 * SplatFormat.SplatRecordSize;   // 2 pages per buffer
            int max = SplatPagePool.MaxDevicePages(chunkSplats: 16, maxBytesPerBuffer: cap);
            Assert.AreEqual(2 * SplatPagePool.MaxBuffers, max);

            using var pool = new SplatPagePool(pageCount: max, chunkSplats: 16,
                chunkCount: max, maxBytesPerBuffer: cap);
            Assert.AreEqual(max, pool.PageCount);
            Assert.Throws<System.ArgumentException>(() =>
                new SplatPagePool(pageCount: max + 1, chunkSplats: 16, chunkCount: max + 1,
                    maxBytesPerBuffer: cap));
        }

        [Test]
        public void SplitPoolUploadsAcrossAllBuffers([Values(4, 7)] int pageCount)
        {
            // Force a split: 2 pages of 16 splats fit per buffer (1024B cap).
            // 4 pages → 2 buffers; 7 pages → 4 buffers (2,2,2,1).
            using var pool = new SplatPagePool(pageCount: pageCount, chunkSplats: 16,
                chunkCount: pageCount,
                maxBytesPerBuffer: 2 * 16 * SplatFormat.SplatRecordSize);

            int expectedBuffers = (pageCount + 1) / 2;   // ceil(pageCount / 2)
            Assert.AreEqual(expectedBuffers, pool.BufferCount);
            Assert.AreEqual(2 * 16, pool.BufferSplatCapacity);
            Assert.AreEqual(SplatPagePool.MaxBuffers, pool.SplatBuffers.Length);

            using var data = new NativeArray<SplatRecordData>(16, Allocator.Temp);
            for (uint c = 0; c < pageCount; c++)
            {
                var records = data;
                for (int i = 0; i < 16; i++)
                {
                    var r = records[i];
                    r.Position = new Unity.Mathematics.float3(c, i, 0);
                    records[i] = r;
                }
                Assert.IsTrue(pool.Upload(c, records, 16));
            }
            Assert.AreEqual(pageCount, pool.ResidentCount);

            // Round-trip: the buffer a slot maps to holds its chunk, and the
            // CPU position mirror matches the upload.
            var back = new SplatRecordData[16];
            for (uint c = 0; c < pageCount; c++)
            {
                Assert.IsTrue(pool.IsResident(c));
                uint slot = pool.SlotOfChunk[(int)c];
                int buffer = (int)slot / 2;      // pagesPerBuffer = 2
                int localPage = (int)slot - buffer * 2;
                pool.SplatBuffers[buffer].GetData(back, 0, localPage * 16, 16);
                Assert.AreEqual(new Unity.Mathematics.float3(c, 5, 0), back[5].Position,
                    $"chunk {c} (slot {slot}) buffer contents mismatch");
                Assert.AreEqual(new Unity.Mathematics.float3(c, 5, 0),
                    pool.PoolPositions[(int)slot * 16 + 5],
                    $"chunk {c} (slot {slot}) position mirror mismatch");
            }
        }

        [Test]
        public void OversizedPoolThrows()
        {
            // 9 pages cannot fit into four 2-page buffers (max 8).
            Assert.Throws<System.ArgumentException>(() =>
                new SplatPagePool(pageCount: 9, chunkSplats: 16, chunkCount: 9,
                    maxBytesPerBuffer: 2 * 16 * SplatFormat.SplatRecordSize));
        }

        [Test]
        public void SlotTableTracksResidency()
        {
            using var pool = new SplatPagePool(pageCount: 2, chunkSplats: 16, chunkCount: 3);
            using var data = MakeChunk(16);

            Assert.IsFalse(pool.IsResident(0));
            pool.Upload(0, data, 16);
            Assert.IsTrue(pool.IsResident(0));
            uint slot = pool.SlotOfChunk[0];
            Assert.Less(slot, 2u);
            Assert.IsTrue(pool.ResidencyBits.IsSet(0));
            Assert.AreEqual(slot, pool.SlotOfChunk[0]);
        }
    }
}
