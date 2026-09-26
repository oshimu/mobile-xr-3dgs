// ChunkStreamer (spec §7.4): a single I/O thread reads .usc chunks into fixed
// staging buffers; Update() consumes finished loads on the main thread within
// the per-frame SetData budget. Files are read with plain FileStream — the
// whole scene is never resident in RAM.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace GsplatLod
{
    public struct StreamerStats
    {
        public int QueueLength;
        public int LoadedChunks;
        public int PermanentlyMissing;
        public long BytesLoaded;
        public int UploadedBytesLastFrame;
    }

    /// <summary>Local-file source with a small LRU cache of open handles.</summary>
    public sealed class LocalChunkSource : IDisposable
    {
        readonly SplatSceneAsset m_scene;
        readonly int m_maxHandles;
        readonly Dictionary<uint, FileStream> m_handles = new();
        readonly LinkedList<uint> m_lru = new();

        public LocalChunkSource(SplatSceneAsset scene, int maxHandles)
        {
            m_scene = scene;
            m_maxHandles = maxHandles;
        }

        public int ReadChunk(uint chunkId, byte[] dst)
        {
            try
            {
                if (!m_handles.TryGetValue(chunkId, out var stream))
                {
                    stream = new FileStream(m_scene.ChunkPath(chunkId), FileMode.Open, FileAccess.Read,
                        FileShare.Read, bufferSize: 1 << 20, useAsync: false);
                    m_handles[chunkId] = stream;
                    m_lru.AddLast(chunkId);
                    if (m_handles.Count > m_maxHandles)
                    {
                        uint oldest = m_lru.First.Value;
                        m_lru.RemoveFirst();
                        m_handles[oldest].Dispose();
                        m_handles.Remove(oldest);
                    }
                }

                stream.Seek(0, SeekOrigin.Begin);
                int total = 0;
                int len = (int)Math.Min(stream.Length, dst.Length);
                while (total < len)
                {
                    int n = stream.Read(dst, total, len - total);
                    if (n <= 0)
                        break;
                    total += n;
                }
                return total;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public void Dispose()
        {
            foreach (var s in m_handles.Values)
                s.Dispose();
            m_handles.Clear();
            m_lru.Clear();
        }
    }

    public sealed class ChunkStreamer : IDisposable
    {
        readonly SplatSceneAsset m_scene;
        readonly SplatPagePool m_pool;
        readonly LodStreamingConfig m_config;
        readonly LocalChunkSource m_source;

        // Request state (main thread writes, I/O thread reads under lock).
        readonly object m_lock = new();
        readonly List<uint> m_queue = new();          // ordered: required first
        readonly HashSet<uint> m_queued = new();
        readonly HashSet<uint> m_inFlight = new();
        readonly bool[] m_missing;                    // permanently missing chunks
        readonly int[] m_retries;

        // Fixed staging buffers cycled between threads.
        readonly Queue<byte[]> m_freeStaging = new();
        readonly Queue<(uint chunkId, byte[] buffer, int bytes)> m_completed = new();

        readonly Thread m_thread;
        readonly AutoResetEvent m_wake = new(false);
        volatile bool m_stop;

        long m_bytesLoaded;
        int m_loadedChunks;
        int m_missingCount;
        int m_uploadedLastFrame;

        public ChunkStreamer(SplatSceneAsset scene, SplatPagePool pool, LodStreamingConfig config)
        {
            m_scene = scene;
            m_pool = pool;
            m_config = config;
            m_source = new LocalChunkSource(scene, config.FileHandleCacheSize);
            m_missing = new bool[scene.Header.ChunkCount];
            m_retries = new int[scene.Header.ChunkCount];

            int stagingBytes = SplatFormat.ChunkSplats * SplatFormat.SplatRecordSize;
            for (int i = 0; i < config.MaxConcurrentLoads; i++)
                m_freeStaging.Enqueue(new byte[stagingBytes]);

            m_thread = new Thread(IoLoop) { Name = "GsplatLod.ChunkStreamer", IsBackground = true };
            m_thread.Start();
        }

        public StreamerStats Stats
        {
            get
            {
                lock (m_lock)
                {
                    return new StreamerStats
                    {
                        QueueLength = m_queue.Count + m_inFlight.Count,
                        LoadedChunks = m_loadedChunks,
                        PermanentlyMissing = m_missingCount,
                        BytesLoaded = m_bytesLoaded,
                        UploadedBytesLastFrame = m_uploadedLastFrame,
                    };
                }
            }
        }

        /// <summary>Replaces the queue with the new demand set (stale requests
        /// from previous slices are implicitly dropped; spec §7.4).</summary>
        public void Request(ReadOnlySpan<uint> required, ReadOnlySpan<uint> desired)
        {
            lock (m_lock)
            {
                m_queue.Clear();
                m_queued.Clear();
                foreach (uint c in required)
                    EnqueueLocked(c);
                if (m_config.PrefetchEnabled)
                    foreach (uint c in desired)
                        EnqueueLocked(c);
            }
            m_wake.Set();
        }

        void EnqueueLocked(uint chunkId)
        {
            if (m_pool.IsResident(chunkId) || m_missing[chunkId] ||
                m_inFlight.Contains(chunkId) || !m_queued.Add(chunkId))
                return;
            m_queue.Add(chunkId);
        }

        /// <summary>Main thread, once per frame: uploads finished chunks
        /// within MaxUploadBytesPerFrame.</summary>
        public void Update()
        {
            m_uploadedLastFrame = 0;
            while (m_uploadedLastFrame < m_config.MaxUploadBytesPerFrame)
            {
                uint chunkId;
                byte[] buffer;
                int bytes;
                lock (m_lock)
                {
                    if (m_completed.Count == 0)
                        break;
                    (chunkId, buffer, bytes) = m_completed.Dequeue();
                }

                bool recycle = true;
                if (bytes < 0)
                {
                    RetryOrMarkMissing(chunkId, $"chunk {chunkId} failed to load; " +
                                                 "marking permanently missing (parent fallback active)");
                }
                else
                {
                    int expected = (int)m_scene.Chunks[(int)chunkId].ByteLength;
                    if (bytes != expected)
                    {
                        RetryOrMarkMissing(chunkId, $"chunk {chunkId} size mismatch " +
                                                     $"({bytes} != {expected}); marking permanently missing");
                    }
                    else
                    {
                        int splatCount = bytes / SplatFormat.SplatRecordSize;
                        if (UploadToPool(chunkId, buffer, splatCount))
                        {
                            m_loadedChunks++;
                            m_uploadedLastFrame += bytes;
                        }
                        else
                        {
                            // No evictable page this frame; requeue and stop.
                            lock (m_lock)
                            {
                                m_completed.Enqueue((chunkId, buffer, bytes));
                            }
                            recycle = false;
                            break;
                        }
                    }
                }

                if (recycle)
                {
                    lock (m_lock)
                    {
                        m_freeStaging.Enqueue(buffer);
                    }
                    m_wake.Set();
                }
            }
        }

        // One retry, then mark permanently missing and keep rendering via the
        // parent fallback (spec §7.4).
        void RetryOrMarkMissing(uint chunkId, string missingMessage)
        {
            if (m_retries[chunkId]++ == 0)
            {
                lock (m_lock)
                {
                    if (m_queued.Add(chunkId))
                        m_queue.Insert(0, chunkId);
                }
                m_wake.Set();
            }
            else
            {
                m_missing[chunkId] = true;
                m_missingCount++;
                Debug.LogError($"[GsplatLod] {missingMessage}");
            }
        }

        unsafe bool UploadToPool(uint chunkId, byte[] buffer, int splatCount)
        {
            fixed (byte* p = buffer)
            {
                var na = Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                    .ConvertExistingDataToNativeArray<SplatRecordData>(p, splatCount, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safety = Unity.Collections.LowLevel.Unsafe.AtomicSafetyHandle.Create();
                Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, safety);
                try
                {
                    return m_pool.Upload(chunkId, na, splatCount);
                }
                finally
                {
                    Unity.Collections.LowLevel.Unsafe.AtomicSafetyHandle.Release(safety);
                }
#else
                return m_pool.Upload(chunkId, na, splatCount);
#endif
            }
        }

        void IoLoop()
        {
            while (!m_stop)
            {
                uint chunkId = SplatFormat.None;
                byte[] staging = null;
                lock (m_lock)
                {
                    if (m_queue.Count > 0 && m_freeStaging.Count > 0)
                    {
                        chunkId = m_queue[0];
                        m_queue.RemoveAt(0);
                        m_queued.Remove(chunkId);
                        m_inFlight.Add(chunkId);
                        staging = m_freeStaging.Dequeue();
                    }
                }

                if (staging == null)
                {
                    m_wake.WaitOne(100);
                    continue;
                }

                int bytes = m_source.ReadChunk(chunkId, staging);
                lock (m_lock)
                {
                    m_inFlight.Remove(chunkId);
                    m_completed.Enqueue((chunkId, staging, bytes));
                    if (bytes > 0)
                        m_bytesLoaded += bytes;
                }
            }
        }

        public void Dispose()
        {
            m_stop = true;
            m_wake.Set();
            m_thread.Join(1000);
            m_source.Dispose();
            m_wake.Dispose();
        }
    }
}
