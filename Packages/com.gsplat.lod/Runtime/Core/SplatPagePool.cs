// SplatPagePool (spec §7.5): fixed GraphicsBuffer pool where one page holds
// one chunk (65,536 splats). Eviction follows spark's SplatPager.driveFetchers:
// the caller declares the chunk set the current frame needs, and only pages
// outside that set are freeable (LRU among them). A page the active draw list
// references can therefore never be stolen mid-frame.

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace GsplatLod
{
    public sealed class SplatPagePool : IDisposable
    {
        // The pool is split across up to MaxBuffers GraphicsBuffers because a
        // single buffer is capped by SystemInfo.maxGraphicsBufferSize (128MB on
        // Quest 3, i.e. 64 pages of 65,536×32B). Each buffer holds exactly
        // PagesPerBuffer pages (the last one may hold fewer), so a pool index
        // routes to buffer (poolIndex / BufferSplatCapacity) at local offset
        // (poolIndex % BufferSplatCapacity). Unused buffer slots alias
        // SplatBuffers[0] so all bindings stay valid (they are never indexed).
        public const int MaxBuffers = 4;

        /// <summary>Splat pool buffers; always length MaxBuffers, trailing
        /// unused entries alias SplatBuffers[0]. Buffer b serves pages
        /// [b*PagesPerBuffer, (b+1)*PagesPerBuffer).</summary>
        public GraphicsBuffer[] SplatBuffers => m_buffers;

        /// <summary>Number of buffers actually backed by distinct storage.</summary>
        public int BufferCount { get; }

        /// <summary>Splats served by each buffer (PagesPerBuffer × chunkSplats);
        /// the divisor the shader uses to route a pool index to its buffer.</summary>
        public int BufferSplatCapacity => m_pagesPerBuffer * m_chunkSplats;

        public GraphicsBuffer ChunkSlotTable { get; }   // uint x chunkCount; None = not resident

        /// <summary>Residency bits per chunk, readable by Burst jobs.</summary>
        public NativeBitArray ResidencyBits;

        /// <summary>CPU mirror of resident splat positions, indexed by pool
        /// index (slot * chunkSplats + i). Bounded by the pool size, so the
        /// CPU depth sort stays within the fixed memory budget (R1).</summary>
        public NativeArray<Unity.Mathematics.float3> PoolPositions;

        /// <summary>CPU mirror of the chunk→slot table for Burst jobs.</summary>
        public NativeArray<uint> SlotOfChunk;

        public int PageCount { get; }
        public int ResidentCount { get; private set; }

        /// <summary>Resident pages outside the current needed set, i.e. the
        /// pages an upload may evict. 0 while every resident page is in use.</summary>
        public int FreeableCount { get; private set; }

        readonly int m_chunkSplats;
        readonly int m_chunkCount;
        readonly int m_pagesPerBuffer;   // pages served by each buffer
        readonly GraphicsBuffer[] m_buffers = new GraphicsBuffer[MaxBuffers];

        // Per-page state.
        readonly uint[] m_chunkOfPage;    // page -> chunkId, None if free
        readonly bool[] m_needed;         // per chunk; set by SetNeeded
        readonly long[] m_lastUse;        // per page, monotonic counter
        readonly uint[] m_slotUpload = new uint[1];
        long m_useCounter;

        // Persistent staging for the Burst position-extraction job: jobs
        // reject Temp/None-allocated containers even via Run().
        NativeArray<SplatRecordData> m_extractStaging;

        /// <summary>Pages this device can back, i.e. MaxBuffers buffers each
        /// filled to the per-buffer byte cap. The hard ceiling on PageCount.</summary>
        /// <param name="maxBytesPerBuffer">Per-buffer byte cap; 0 = use
        /// SystemInfo.maxGraphicsBufferSize (test seam).</param>
        public static int MaxDevicePages(int chunkSplats, long maxBytesPerBuffer = 0) =>
            MaxBuffers * PagesPerBuffer(chunkSplats, maxBytesPerBuffer);

        static int PagesPerBuffer(int chunkSplats, long maxBytesPerBuffer)
        {
            if (maxBytesPerBuffer <= 0)
                maxBytesPerBuffer = SystemInfo.maxGraphicsBufferSize;
            long bytesPerPage = (long)chunkSplats * SplatFormat.SplatRecordSize;
            return (int)Math.Max(1, maxBytesPerBuffer / bytesPerPage);
        }

        /// <param name="maxBytesPerBuffer">Per-buffer byte cap; 0 = use
        /// SystemInfo.maxGraphicsBufferSize (test seam).</param>
        public SplatPagePool(int pageCount, int chunkSplats, int chunkCount, long maxBytesPerBuffer = 0)
        {
            PageCount = pageCount;
            m_chunkSplats = chunkSplats;
            m_chunkCount = chunkCount;

            int maxPagesPerBuffer = PagesPerBuffer(chunkSplats, maxBytesPerBuffer);
            if (pageCount > MaxBuffers * maxPagesPerBuffer)
                throw new ArgumentException(
                    $"pool of {pageCount} pages exceeds {MaxBuffers} graphics buffers " +
                    $"({maxPagesPerBuffer} pages each, max buffer " +
                    $"{(maxBytesPerBuffer <= 0 ? SystemInfo.maxGraphicsBufferSize : maxBytesPerBuffer)} bytes)");

            m_pagesPerBuffer = maxPagesPerBuffer;
            int bufferCount = Math.Max(1, (pageCount + maxPagesPerBuffer - 1) / maxPagesPerBuffer);
            BufferCount = bufferCount;
            for (int b = 0; b < bufferCount; b++)
            {
                int pagesInBuffer = Math.Min(maxPagesPerBuffer, pageCount - b * maxPagesPerBuffer);
                m_buffers[b] = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    pagesInBuffer * chunkSplats, SplatFormat.SplatRecordSize);
            }
            for (int b = bufferCount; b < MaxBuffers; b++)
                m_buffers[b] = m_buffers[0];   // alias unused: bound but never indexed
            ChunkSlotTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunkCount, sizeof(uint));
            ResidencyBits = new NativeBitArray(chunkCount, Allocator.Persistent);
            PoolPositions = new NativeArray<Unity.Mathematics.float3>(pageCount * chunkSplats,
                Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            SlotOfChunk = new NativeArray<uint>(chunkCount, Allocator.Persistent);
            m_extractStaging = new NativeArray<SplatRecordData>(chunkSplats, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            m_chunkOfPage = new uint[pageCount];
            m_needed = new bool[chunkCount];
            m_needed[0] = true;   // chunk 0 (tree root) is needed for the whole session
            m_lastUse = new long[pageCount];
            for (int i = 0; i < chunkCount; i++)
                SlotOfChunk[i] = SplatFormat.None;
            Array.Fill(m_chunkOfPage, SplatFormat.None);

            var init = new uint[chunkCount];
            Array.Fill(init, SplatFormat.None);
            ChunkSlotTable.SetData(init);
        }

        public bool IsResident(uint chunkId) => SlotOfChunk[(int)chunkId] != SplatFormat.None;

        /// <summary>
        /// Declares the chunks the current draw list and the pending slice
        /// need. Everything resident outside this set becomes freeable; pages
        /// inside it are never evicted (spark SplatPager.driveFetchers, where
        /// freeablePages = residentPages - needed). Chunk 0 is always needed.
        /// Pass every set that is still referenced — notably the chunks behind
        /// the *active* order buffer, not just the newest slice.
        /// </summary>
        public void SetNeeded(ReadOnlySpan<uint> primary, ReadOnlySpan<uint> secondary = default)
        {
            Array.Clear(m_needed, 0, m_needed.Length);
            m_needed[0] = true;
            foreach (uint c in primary)
                m_needed[c] = true;
            foreach (uint c in secondary)
                m_needed[c] = true;

            int freeable = 0;
            for (int p = 0; p < PageCount; p++)
            {
                uint occupant = m_chunkOfPage[p];
                if (occupant != SplatFormat.None && !m_needed[occupant])
                    freeable++;
            }
            FreeableCount = freeable;
        }

        /// <summary>Marks every chunk needed; nothing is evictable. Used when
        /// the pool holds the whole scene, where eviction is pure waste.</summary>
        public void SetAllNeeded()
        {
            for (int i = 0; i < m_needed.Length; i++)
                m_needed[i] = true;
            FreeableCount = 0;
        }

        public void Touch(ReadOnlySpan<uint> chunkIds)
        {
            m_useCounter++;
            foreach (uint c in chunkIds)
            {
                uint slot = SlotOfChunk[(int)c];
                if (slot != SplatFormat.None)
                    m_lastUse[slot] = m_useCounter;
            }
        }

        /// <summary>
        /// Uploads a chunk into a free/freeable page (main thread only; SetData).
        /// Returns false when every page is in the needed set — retry next frame.
        /// </summary>
        public bool Upload(uint chunkId, NativeArray<SplatRecordData> records, int splatCount)
        {
            if (IsResident(chunkId))
                return true;

            if (!TryAcquirePage(out uint page))
                return false;

            int buffer = (int)page / m_pagesPerBuffer;
            int localPage = (int)page - buffer * m_pagesPerBuffer;
            m_buffers[buffer].SetData(records, 0, localPage * m_chunkSplats, splatCount);
            // Synchronous Burst run: a managed loop over 65k records costs
            // milliseconds in the editor and causes visible upload hitches.
            NativeArray<SplatRecordData>.Copy(records, 0, m_extractStaging, 0, splatCount);
            new ExtractPositionsJob
            {
                Records = m_extractStaging,
                Count = splatCount,
                PoolBase = (int)page * m_chunkSplats,
                PoolPositions = PoolPositions,
            }.Run();

            // A chunk loaded outside the needed set (prefetch) is itself freeable.
            if (!m_needed[chunkId])
                FreeableCount++;
            m_chunkOfPage[page] = chunkId;
            SlotOfChunk[(int)chunkId] = page;
            m_lastUse[page] = ++m_useCounter;
            ResidencyBits.Set((int)chunkId, true);
            ResidentCount++;
            UploadSlotEntry(chunkId, page);
            return true;
        }

        // Free page first, else the LRU page outside the needed set. Pages the
        // needed set covers are never candidates, so an upload can never steal
        // a page the active order buffer is still drawing from.
        bool TryAcquirePage(out uint page)
        {
            long best = long.MaxValue;
            int bestPage = -1;
            for (int p = 0; p < PageCount; p++)
            {
                uint occupant = m_chunkOfPage[p];
                if (occupant == SplatFormat.None)
                {
                    page = (uint)p;
                    return true;
                }
                if (m_needed[occupant])
                    continue;
                if (m_lastUse[p] < best)
                {
                    best = m_lastUse[p];
                    bestPage = p;
                }
            }

            if (bestPage < 0)
            {
                page = SplatFormat.None;
                return false;
            }

            FreeableCount--;
            uint evicted = m_chunkOfPage[bestPage];
            SlotOfChunk[(int)evicted] = SplatFormat.None;
            ResidencyBits.Set((int)evicted, false);
            ResidentCount--;
            UploadSlotEntry(evicted, SplatFormat.None);
            m_chunkOfPage[bestPage] = SplatFormat.None;
            page = (uint)bestPage;
            return true;
        }

        // Element-granular SetData keeps slot-table updates allocation-free.
        void UploadSlotEntry(uint chunkId, uint slot)
        {
            m_slotUpload[0] = slot;
            ChunkSlotTable.SetData(m_slotUpload, 0, (int)chunkId, 1);
        }

        [BurstCompile]
        struct ExtractPositionsJob : IJob
        {
            [ReadOnly] public NativeArray<SplatRecordData> Records;
            public int Count;
            public int PoolBase;
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<Unity.Mathematics.float3> PoolPositions;

            public void Execute()
            {
                for (int i = 0; i < Count; i++)
                    PoolPositions[PoolBase + i] = Records[i].Position;
            }
        }

        public void Dispose()
        {
            // Only [0, BufferCount) are distinct; trailing entries alias [0].
            for (int b = 0; b < BufferCount; b++)
                m_buffers[b]?.Dispose();
            ChunkSlotTable?.Dispose();
            if (ResidencyBits.IsCreated)
                ResidencyBits.Dispose();
            if (PoolPositions.IsCreated)
                PoolPositions.Dispose();
            if (SlotOfChunk.IsCreated)
                SlotOfChunk.Dispose();
            if (m_extractStaging.IsCreated)
                m_extractStaging.Dispose();
        }
    }
}
