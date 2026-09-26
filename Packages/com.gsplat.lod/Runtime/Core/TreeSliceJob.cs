// LoD slice (spec §5): greedy budgeted expansion of the node tree into a
// frontier of draw items, run as a single Burst job off the main thread.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GsplatLod
{
    /// <summary>
    /// Frontier element. Count == 0 → Merged(NodeIndex);
    /// otherwise LeafRun(First, Count) owned by NodeIndex (spec §5.1).
    /// </summary>
    public struct DrawItem
    {
        public uint NodeIndex;
        public uint First;   // globalIndex of first leaf (LeafRun only)
        public uint Count;   // leaf count; 0 marks a Merged item

        public bool IsMerged => Count == 0;

        public static DrawItem Merged(uint nodeIndex) =>
            new() { NodeIndex = nodeIndex, First = SplatFormat.None, Count = 0 };

        public static DrawItem LeafRun(uint nodeIndex, uint first, uint count) =>
            new() { NodeIndex = nodeIndex, First = first, Count = count };
    }

    public struct SliceInput
    {
        public float3 EyePosition;                 // model space
        public float3 EyeForward;                  // model space, normalized
        public FixedList512Bytes<float4> FrustumPlanes; // 6 planes, model space, xyz=normal (inward), w=dist
        /// <summary>Exponent k in pe = FeatureSize / dist^k. Values below 1
        /// flatten the distance falloff: near nodes lose a little priority so
        /// far regions refine past the "single giant merged blob" stage
        /// instead of the budget all going to the foreground. 0 or negative
        /// is treated as 1 (proportional falloff).</summary>
        public float DistanceFalloffPower;
        public int BudgetSplats;
        /// <summary>Uniform screen-space-error threshold (spec: Spark-style
        /// cut). A node is expanded only while its projected error exceeds this;
        /// once pe &lt;= PeThreshold the node's merged splat is small enough on
        /// screen and it is drawn as a single Merged item. Expansion stops at
        /// whichever of this threshold or BudgetSplats is reached first — on
        /// mobile (Quest) the budget usually saturates first, so BudgetSplats
        /// is what actually governs the cut in practice. Driven as
        /// targetPixels / focalPixels. 0 or negative disables the threshold
        /// (falls back to pure budget expansion).</summary>
        public float PeThreshold;
        /// <summary>Foveated weighting: full priority inside the inner cone,
        /// falling to PeripheryFactor at the outer cone (Spark-style — detail
        /// concentrates where the user is looking). Inactive unless
        /// CosInner > CosOuter and PeripheryFactor < 1.</summary>
        public float CosFoveaInner;
        public float CosFoveaOuter;
        public float PeripheryFactor;
        /// <summary>Max distinct chunks the frontier may reference (pool
        /// pages minus reserve). Keeps required ⊆ resident by construction:
        /// a required set larger than the pool thrashes the LRU and leaves
        /// starved regions rendering nothing.</summary>
        public int MaxChunks;
        public float NearClip;
        public float OutOfFrustumFactor;
    }

    [BurstCompile]
    public struct TreeSliceJob : IJob
    {
        [ReadOnly] public NativeArray<NodeRecord> Nodes;
        public SliceInput Input;
        public uint RootNodeIndex;

        public NativeList<DrawItem> DrawItems;
        public NativeList<uint> RequiredChunks;   // dedup'd, ~pe-descending
        public NativeList<uint> DesiredChunks;    // one-step-expansion prefetch
        public NativeBitArray ChunkSeen;          // scratch, chunkCount bits
        public NativeReference<int> FrontierCost;

        struct HeapItem
        {
            public float Pe;
            public uint Node;
        }

        public void Execute()
        {
            DrawItems.Clear();
            RequiredChunks.Clear();
            DesiredChunks.Clear();
            ChunkSeen.Clear();

            var heap = new NativeList<HeapItem>(1024, Allocator.Temp);
            int frontierCost = 1;
            int chunkCount = 0;
            // Root's merged splat lives in chunk 0, which is always pinned.
            ReserveChunk(Nodes[(int)RootNodeIndex].MergedSplatIndex >> 16, ref chunkCount);
            HeapPush(heap, new HeapItem { Pe = ProjectedError(RootNodeIndex), Node = RootNodeIndex });

            while (heap.Length > 0)
            {
                var top = HeapPop(heap);
                var node = Nodes[(int)top.Node];
                int expandCost = node.InteriorChildCount + (int)node.DirectLeafCount;

                // Stops when either the node's merged splat is already small
                // enough on screen (threshold) or the frontier hits
                // BudgetSplats, whichever comes first. On mobile (Quest) the
                // budget usually saturates first, so BudgetSplats is what
                // actually governs the cut in practice.
                bool belowThreshold = Input.PeThreshold > 0f && top.Pe <= Input.PeThreshold;

                if (expandCost == 0
                    || belowThreshold
                    || frontierCost - 1 + expandCost > Input.BudgetSplats
                    || !TryReserveExpansion(node, ref chunkCount))
                {
                    // Not expanded: draw the merged splat (chunk reserved when
                    // this node's chunks were claimed by its parent expansion).
                    DrawItems.Add(DrawItem.Merged(top.Node));
                    continue;
                }

                frontierCost += expandCost - 1;
                if (node.DirectLeafFirst != SplatFormat.None)
                    DrawItems.Add(DrawItem.LeafRun(top.Node, node.DirectLeafFirst, node.DirectLeafCount));
                if (node.FirstInteriorChild != SplatFormat.None)
                {
                    for (uint c = 0; c < node.InteriorChildCount; c++)
                    {
                        uint child = node.FirstInteriorChild + c;
                        HeapPush(heap, new HeapItem { Pe = ProjectedError(child), Node = child });
                    }
                }
            }

            heap.Dispose();
            FrontierCost.Value = frontierCost;

            // Prefetch candidates: chunks needed if any frontier merged node
            // were expanded one more step (spec §5.4).
            int frontierLen = DrawItems.Length;
            for (int i = 0; i < frontierLen; i++)
            {
                var item = DrawItems[i];
                if (!item.IsMerged)
                    continue;
                var node = Nodes[(int)item.NodeIndex];
                if (node.DirectLeafFirst != SplatFormat.None)
                {
                    uint first = node.DirectLeafFirst >> 16;
                    uint last = (node.DirectLeafFirst + node.DirectLeafCount - 1) >> 16;
                    for (uint c = first; c <= last; c++)
                        AddDesired(c);
                }
                if (node.FirstInteriorChild != SplatFormat.None)
                    for (uint c = 0; c < node.InteriorChildCount; c++)
                        AddDesired(Nodes[(int)(node.FirstInteriorChild + c)].MergedSplatIndex >> 16);
            }
        }

        /// <summary>Marks a chunk as required (dedup'd, budget-checked).</summary>
        bool ReserveChunk(uint chunkId, ref int chunkCount)
        {
            if (ChunkSeen.IsSet((int)chunkId))
                return true;
            if (chunkCount >= Input.MaxChunks)
                return false;
            ChunkSeen.Set((int)chunkId, true);
            RequiredChunks.Add(chunkId);
            chunkCount++;
            return true;
        }

        /// <summary>
        /// Reserves every chunk an expansion introduces: the direct-leaf run
        /// plus each interior child's merged splat (children enter the
        /// frontier at least as Merged items). All-or-nothing: on failure the
        /// partial reservations are rolled back and the node stays merged.
        /// </summary>
        bool TryReserveExpansion(in NodeRecord node, ref int chunkCount)
        {
            // Unconstrained (the pool holds the whole scene): record the chunks
            // but skip the budget check and the rollback bookkeeping entirely.
            // The undo list below caps an expansion at 31 new chunks, which
            // would otherwise reject scattered expansions for no memory reason.
            if (Input.MaxChunks >= ChunkSeen.Length)
            {
                if (node.DirectLeafFirst != SplatFormat.None)
                {
                    uint first = node.DirectLeafFirst >> 16;
                    uint last = (node.DirectLeafFirst + node.DirectLeafCount - 1) >> 16;
                    for (uint c = first; c <= last; c++)
                        ReserveChunk(c, ref chunkCount);
                }
                if (node.FirstInteriorChild != SplatFormat.None)
                    for (uint i = 0; i < node.InteriorChildCount; i++)
                        ReserveChunk(Nodes[(int)(node.FirstInteriorChild + i)].MergedSplatIndex >> 16,
                            ref chunkCount);
                return true;
            }

            var undo = new FixedList128Bytes<uint>();
            bool ok = true;

            if (node.DirectLeafFirst != SplatFormat.None)
            {
                uint first = node.DirectLeafFirst >> 16;
                uint last = (node.DirectLeafFirst + node.DirectLeafCount - 1) >> 16;
                for (uint c = first; c <= last && ok; c++)
                    ok = ReserveTracked(c, ref undo, ref chunkCount);
            }
            if (ok && node.FirstInteriorChild != SplatFormat.None)
            {
                for (uint i = 0; i < node.InteriorChildCount && ok; i++)
                {
                    uint c = Nodes[(int)(node.FirstInteriorChild + i)].MergedSplatIndex >> 16;
                    ok = ReserveTracked(c, ref undo, ref chunkCount);
                }
            }

            if (!ok)
            {
                for (int i = 0; i < undo.Length; i++)
                    ChunkSeen.Set((int)undo[i], false);
                chunkCount -= undo.Length;
                RequiredChunks.ResizeUninitialized(RequiredChunks.Length - undo.Length);
            }
            return ok;
        }

        bool ReserveTracked(uint chunkId, ref FixedList128Bytes<uint> undo, ref int chunkCount)
        {
            if (ChunkSeen.IsSet((int)chunkId))
                return true;
            // A full undo list means an implausibly scattered expansion;
            // treat it as over budget rather than lose rollback safety.
            if (undo.Length >= undo.Capacity)
                return false;
            if (!ReserveChunk(chunkId, ref chunkCount))
                return false;
            undo.Add(chunkId);
            return true;
        }

        void AddDesired(uint chunkId)
        {
            if (ChunkSeen.IsSet((int)chunkId))
                return;
            ChunkSeen.Set((int)chunkId, true);
            DesiredChunks.Add(chunkId);
        }

        float ProjectedError(uint nodeIndex)
        {
            var node = Nodes[(int)nodeIndex];
            float3 toNode = node.Center - Input.EyePosition;
            float centerDist = math.length(toNode);
            float dist = math.max(centerDist - node.Radius, Input.NearClip);
            if (Input.DistanceFalloffPower > 0f && Input.DistanceFalloffPower != 1f)
                dist = math.pow(dist, Input.DistanceFalloffPower);
            float pe = node.FeatureSize / dist;

            // sphere vs frustum: fully outside any plane → damped, never culled (spec §5.2)
            for (int p = 0; p < 6; p++)
            {
                float4 plane = Input.FrustumPlanes[p];
                float d = math.dot(plane.xyz, node.Center) + plane.w;
                if (d < -node.Radius)
                    return pe * Input.OutOfFrustumFactor;
            }

            // Foveated weighting inside the frustum. Nodes the eye is inside
            // of (or that cover the gaze cone) keep full priority.
            if (Input.PeripheryFactor < 1f && Input.CosFoveaInner > Input.CosFoveaOuter
                && centerDist > node.Radius)
            {
                float cosA = math.dot(toNode / centerDist, Input.EyeForward);
                // widen by the node's angular radius so large nodes that reach
                // into the fovea are not penalized
                cosA += node.Radius / centerDist;
                float t = math.saturate(
                    (cosA - Input.CosFoveaOuter) / (Input.CosFoveaInner - Input.CosFoveaOuter));
                pe *= math.lerp(Input.PeripheryFactor, 1f, t);
            }
            return pe;
        }

        // Binary max-heap on Pe.
        static void HeapPush(NativeList<HeapItem> heap, HeapItem item)
        {
            heap.Add(item);
            int i = heap.Length - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heap[parent].Pe >= heap[i].Pe)
                    break;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        static HeapItem HeapPop(NativeList<HeapItem> heap)
        {
            var top = heap[0];
            int last = heap.Length - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            int n = heap.Length;
            while (true)
            {
                int l = i * 2 + 1;
                int r = l + 1;
                int largest = i;
                if (l < n && heap[l].Pe > heap[largest].Pe) largest = l;
                if (r < n && heap[r].Pe > heap[largest].Pe) largest = r;
                if (largest == i)
                    break;
                (heap[largest], heap[i]) = (heap[i], heap[largest]);
                i = largest;
            }
            return top;
        }
    }
}
