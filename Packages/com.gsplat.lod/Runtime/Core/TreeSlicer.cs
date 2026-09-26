// TreeSlicer (spec §7.3): owns the slice job lifecycle. Kicks a slice when the
// viewpoint moved enough, collects results a frame or two later — the main
// thread never blocks on Complete() for slice results.

using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GsplatLod
{
    public struct SliceResult
    {
        public NativeArray<DrawItem> DrawItems;      // views into slicer-owned lists
        public NativeArray<uint> RequiredChunks;
        public NativeArray<uint> DesiredChunks;
        public int FrontierCost;
        public float SliceMilliseconds;
    }

    public sealed class TreeSlicer : IDisposable
    {
        readonly SplatSceneAsset m_scene;
        readonly LodStreamingConfig m_config;

        NativeList<DrawItem> m_drawItems;
        NativeList<uint> m_requiredChunks;
        NativeList<uint> m_desiredChunks;
        NativeBitArray m_chunkSeen;
        NativeReference<int> m_frontierCost;
        float m_lastSliceMs = -1f;   // only measured in sync mode

        /// <summary>Max distinct chunks a frontier may reference. The driver
        /// sets this to poolPages - reserve so required always fits the pool.</summary>
        public int MaxFrontierChunks { get; set; } = int.MaxValue;

        /// <summary>True while a slice job is in flight. The slice job writes
        /// the lists that SliceResult views point into, so consumers must not
        /// schedule jobs over a previous result while this is set.</summary>
        public bool IsRunning => m_running;

        /// <summary>Parent node index per node (None for root); derived once
        /// from the NodeTable for the unloaded-chunk fallback walk (§5.5).</summary>
        public NativeArray<uint> Parents => m_parents;
        NativeArray<uint> m_parents;

        JobHandle m_handle;
        bool m_running;
        bool m_hasLastPose;
        float3 m_lastEyePos;
        quaternion m_lastEyeRot;
        int m_lastKickFrame = int.MinValue;

        public TreeSlicer(SplatSceneAsset scene, LodStreamingConfig config)
        {
            m_scene = scene;
            m_config = config;

            int budget = config.BudgetSplats;
            m_drawItems = new NativeList<DrawItem>(budget / 16 + 1024, Allocator.Persistent);
            m_requiredChunks = new NativeList<uint>((int)scene.Header.ChunkCount, Allocator.Persistent);
            m_desiredChunks = new NativeList<uint>((int)scene.Header.ChunkCount, Allocator.Persistent);
            m_chunkSeen = new NativeBitArray((int)scene.Header.ChunkCount, Allocator.Persistent);
            m_frontierCost = new NativeReference<int>(Allocator.Persistent);

            m_parents = BuildParents(scene);
        }

        static NativeArray<uint> BuildParents(SplatSceneAsset scene)
        {
            var nodes = scene.Nodes;
            var parents = new NativeArray<uint>(nodes.Length, Allocator.Persistent);
            for (int i = 0; i < parents.Length; i++)
                parents[i] = SplatFormat.None;
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.FirstInteriorChild == SplatFormat.None)
                    continue;
                for (uint c = 0; c < n.InteriorChildCount; c++)
                    parents[(int)(n.FirstInteriorChild + c)] = (uint)i;
            }
            return parents;
        }

        /// <summary>Schedules a slice if the trigger conditions (§7.3) hold.</summary>
        public bool TryKick(float3 eyePos, quaternion eyeRot, float3 eyeForward,
            FixedList512Bytes<float4> frustumPlanes, float nearClip, float peThreshold,
            bool force = false)
        {
            if (m_running)
                return false;
            if (Time.frameCount - m_lastKickFrame < m_config.SliceMinIntervalFrames && !force)
                return false;
            if (m_hasLastPose && !force)
            {
                bool moved = math.distance(eyePos, m_lastEyePos) >= m_config.SliceMoveThresholdMeters;
                bool rotated = math.degrees(math.angle(eyeRot, m_lastEyeRot)) >= m_config.SliceRotateThresholdDegrees;
                if (!moved && !rotated)
                    return false;
            }

            m_lastEyePos = eyePos;
            m_lastEyeRot = eyeRot;
            m_hasLastPose = true;
            m_lastKickFrame = Time.frameCount;

            var job = new TreeSliceJob
            {
                Nodes = m_scene.NodesArray,
                Input = new SliceInput
                {
                    EyePosition = eyePos,
                    EyeForward = eyeForward,
                    FrustumPlanes = frustumPlanes,
                    DistanceFalloffPower = m_config.DistanceFalloffPower,
                    BudgetSplats = m_config.BudgetSplats,
                    PeThreshold = peThreshold,
                    MaxChunks = math.max(1, MaxFrontierChunks),
                    CosFoveaInner = math.cos(math.radians(m_config.FoveaInnerDegrees)),
                    CosFoveaOuter = math.cos(math.radians(m_config.FoveaOuterDegrees)),
                    PeripheryFactor = m_config.PeripheryFactor,
                    NearClip = nearClip,
                    OutOfFrustumFactor = m_config.OutOfFrustumFactor,
                },
                RootNodeIndex = m_scene.Header.RootNodeIndex,
                DrawItems = m_drawItems,
                RequiredChunks = m_requiredChunks,
                DesiredChunks = m_desiredChunks,
                ChunkSeen = m_chunkSeen,
                FrontierCost = m_frontierCost,
            };

            if (m_config.MeasureSliceSync)
            {
                // Synchronous Burst execution on the main thread: exact
                // execution time against the <3ms slice budget, at the cost
                // of blocking the frame. Verification only.
                long t0 = Stopwatch.GetTimestamp();
                job.Run();
                m_lastSliceMs = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                m_handle = default;
            }
            else
            {
                m_lastSliceMs = -1f;
                m_handle = job.Schedule();
            }
            m_running = true;
            return true;
        }

        /// <summary>Returns the finished slice if any. The result arrays are
        /// views into slicer-owned memory: consume before the next kick.</summary>
        public bool TryCollect(out SliceResult result)
        {
            result = default;
            if (!m_running || !m_handle.IsCompleted)
                return false;
            m_handle.Complete();
            m_running = false;

            result = new SliceResult
            {
                DrawItems = m_drawItems.AsArray(),
                RequiredChunks = m_requiredChunks.AsArray(),
                DesiredChunks = m_desiredChunks.AsArray(),
                FrontierCost = m_frontierCost.Value,
                SliceMilliseconds = m_lastSliceMs,
            };
            return true;
        }

        public void Dispose()
        {
            if (m_running)
                m_handle.Complete();
            m_drawItems.Dispose();
            m_requiredChunks.Dispose();
            m_desiredChunks.Dispose();
            m_chunkSeen.Dispose();
            m_frontierCost.Dispose();
            m_parents.Dispose();
        }
    }
}
