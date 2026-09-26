// VisibleIndexBuildJob (spec §7.6.2): expands the sliced frontier into a flat
// globalIndex list, resolving unloaded chunks by walking up to the nearest
// resident ancestor's merged splat (spec §5.5). Chunk 0 is pinned resident, so
// the walk always terminates.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace GsplatLod
{
    [BurstCompile]
    public struct VisibleIndexBuildJob : IJob
    {
        [ReadOnly] public NativeArray<NodeRecord> Nodes;
        [ReadOnly] public NativeArray<uint> Parents;       // per node; None for root
        [ReadOnly] public NativeArray<DrawItem> DrawItems;
        [ReadOnly] public NativeBitArray ResidentChunks;   // chunkCount bits

        public NativeList<uint> VisibleIndices;            // out: globalIndex list
        public NativeBitArray FallbackEmitted;             // scratch, nodeCount bits
        public NativeReference<int> FallbackCount;         // out: items degraded to an ancestor

        public void Execute()
        {
            VisibleIndices.Clear();
            FallbackEmitted.Clear();
            int fallbacks = 0;

            for (int i = 0; i < DrawItems.Length; i++)
            {
                var item = DrawItems[i];
                if (item.IsMerged)
                {
                    uint g = Nodes[(int)item.NodeIndex].MergedSplatIndex;
                    if (IsResident(g))
                        VisibleIndices.Add(g);
                    else
                    {
                        EmitNearestResidentAncestor(item.NodeIndex);
                        fallbacks++;
                    }
                }
                else
                {
                    // Per-chunk granularity: emit the resident spans of the run
                    // and degrade only if some chunk is missing. Degrading the
                    // whole run on one missing chunk collapsed tens of thousands
                    // of splats to a single ancestor splat.
                    uint end = item.First + item.Count;
                    bool anyMissing = false;
                    for (uint g = item.First; g < end;)
                    {
                        uint chunk = g >> 16;
                        uint chunkEnd = (chunk + 1) << 16;
                        if (chunkEnd > end)
                            chunkEnd = end;
                        if (ResidentChunks.IsSet((int)chunk))
                        {
                            for (uint k = g; k < chunkEnd; k++)
                                VisibleIndices.Add(k);
                        }
                        else
                        {
                            anyMissing = true;
                        }
                        g = chunkEnd;
                    }
                    if (anyMissing)
                    {
                        EmitNearestResidentAncestor(item.NodeIndex);
                        fallbacks++;
                    }
                }
            }

            FallbackCount.Value = fallbacks;
        }

        bool IsResident(uint globalIndex) => ResidentChunks.IsSet((int)(globalIndex >> 16));

        // Multiple degraded items can share an ancestor; FallbackEmitted
        // deduplicates so the merged splat is drawn once.
        void EmitNearestResidentAncestor(uint nodeIndex)
        {
            uint n = nodeIndex;
            while (n != SplatFormat.None)
            {
                uint g = Nodes[(int)n].MergedSplatIndex;
                if (IsResident(g))
                {
                    if (!FallbackEmitted.IsSet((int)n))
                    {
                        FallbackEmitted.Set((int)n, true);
                        VisibleIndices.Add(g);
                    }
                    return;
                }
                n = Parents[(int)n];
            }
            // Unreachable while chunk 0 is pinned; drop the item if not.
        }
    }
}
