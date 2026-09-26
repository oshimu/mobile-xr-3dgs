// EditMode tests (spec §9): TreeSliceJob frontier invariants and
// VisibleIndexBuildJob fallback behaviour on synthetic trees.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GsplatLod.Tests
{
    /// <summary>Builds synthetic NodeTables in the exact layout the encoder
    /// produces (preorder splat indices, block-allocated sibling nodes).</summary>
    class SyntheticTree
    {
        public readonly List<NodeRecord> Nodes = new();
        public uint TotalSplats;
        uint m_rng;

        public SyntheticTree(uint seed) => m_rng = seed | 1;

        uint NextRand(uint max) // xorshift
        {
            m_rng ^= m_rng << 13;
            m_rng ^= m_rng >> 17;
            m_rng ^= m_rng << 5;
            return m_rng % max;
        }

        public void Build(int depth, int maxInterior, int maxLeaves)
        {
            Nodes.Clear();
            TotalSplats = 0;
            Nodes.Add(default);
            BuildNode(0, depth, maxInterior, maxLeaves, new float3(0), 100f);
        }

        void BuildNode(int slot, int depth, int maxInterior, int maxLeaves, float3 center, float radius)
        {
            uint merged = TotalSplats++;
            int leaves = depth == 0 ? 1 + (int)NextRand((uint)maxLeaves) : (int)NextRand((uint)maxLeaves + 1);
            int interior = depth == 0 ? 0 : 1 + (int)NextRand((uint)maxInterior);

            uint leafFirst = leaves > 0 ? TotalSplats : SplatFormat.None;
            TotalSplats += (uint)leaves;

            uint firstChild = interior > 0 ? (uint)Nodes.Count : SplatFormat.None;
            for (int i = 0; i < interior; i++)
                Nodes.Add(default);

            Nodes[slot] = new NodeRecord
            {
                Center = center,
                Radius = radius,
                FeatureSize = radius * 0.5f,
                MergedSplatIndex = merged,
                FirstInteriorChild = firstChild,
                InteriorChildCount = (ushort)interior,
                DirectLeafFirst = leafFirst,
                DirectLeafCount = (uint)leaves,
            };

            for (int i = 0; i < interior; i++)
            {
                var childCenter = center + new float3(NextRand(100) / 50f - 1f, 0, NextRand(100) / 50f - 1f) * radius * 0.4f;
                BuildNode((int)(firstChild + i), depth - 1, maxInterior, maxLeaves, childCenter, radius * 0.5f);
            }
        }

        public NativeArray<NodeRecord> ToNative(Allocator alloc)
        {
            var arr = new NativeArray<NodeRecord>(Nodes.Count, alloc);
            for (int i = 0; i < Nodes.Count; i++)
                arr[i] = Nodes[i];
            return arr;
        }

        /// <summary>All descendant leaf globals of a node (direct + recursive).</summary>
        public void CollectLeaves(int slot, HashSet<uint> outLeaves)
        {
            var n = Nodes[slot];
            if (n.DirectLeafFirst != SplatFormat.None)
                for (uint i = 0; i < n.DirectLeafCount; i++)
                    outLeaves.Add(n.DirectLeafFirst + i);
            if (n.FirstInteriorChild != SplatFormat.None)
                for (int c = 0; c < n.InteriorChildCount; c++)
                    CollectLeaves((int)(n.FirstInteriorChild + c), outLeaves);
        }
    }

    public class TreeSliceTests
    {
        static FixedList512Bytes<float4> OpenFrustum()
        {
            // planes with huge positive distance: everything inside
            var l = new FixedList512Bytes<float4>();
            for (int i = 0; i < 6; i++)
                l.Add(new float4(0, 1, 0, 1e9f));
            return l;
        }

        static (NativeList<DrawItem>, int) RunSlice(NativeArray<NodeRecord> nodes, int budget)
        {
            var (items, cost, _) = RunSlice(nodes, budget, int.MaxValue);
            return (items, cost);
        }

        static (NativeList<DrawItem>, int, int) RunSlice(NativeArray<NodeRecord> nodes, int budget, int maxChunks)
        {
            var drawItems = new NativeList<DrawItem>(1024, Allocator.TempJob);
            var required = new NativeList<uint>(64, Allocator.TempJob);
            var desired = new NativeList<uint>(64, Allocator.TempJob);
            var seen = new NativeBitArray(1024, Allocator.TempJob);
            var cost = new NativeReference<int>(Allocator.TempJob);

            var job = new TreeSliceJob
            {
                Nodes = nodes,
                Input = new SliceInput
                {
                    EyePosition = new float3(0, 0, -5),
                    FrustumPlanes = OpenFrustum(),
                    BudgetSplats = budget,
                    MaxChunks = maxChunks,
                    NearClip = 0.1f,
                    OutOfFrustumFactor = 0.1f,
                },
                RootNodeIndex = 0,
                DrawItems = drawItems,
                RequiredChunks = required,
                DesiredChunks = desired,
                ChunkSeen = seen,
                FrontierCost = cost,
            };
            job.Run();

            int frontierCost = cost.Value;
            int requiredCount = required.Length;
            required.Dispose();
            desired.Dispose();
            seen.Dispose();
            cost.Dispose();
            return (drawItems, frontierCost, requiredCount);
        }

        // Uniform screen-space threshold run: budget/chunks are left effectively
        // unlimited so the cut is governed purely by PeThreshold.
        static (NativeList<DrawItem>, int) RunSliceThreshold(NativeArray<NodeRecord> nodes, float peThreshold)
        {
            var drawItems = new NativeList<DrawItem>(1024, Allocator.TempJob);
            var required = new NativeList<uint>(64, Allocator.TempJob);
            var desired = new NativeList<uint>(64, Allocator.TempJob);
            var seen = new NativeBitArray(4096, Allocator.TempJob);
            var cost = new NativeReference<int>(Allocator.TempJob);

            var job = new TreeSliceJob
            {
                Nodes = nodes,
                Input = new SliceInput
                {
                    EyePosition = new float3(0, 0, -5),
                    FrustumPlanes = OpenFrustum(),
                    BudgetSplats = 100_000_000,
                    PeThreshold = peThreshold,
                    MaxChunks = int.MaxValue,
                    NearClip = 0.1f,
                    OutOfFrustumFactor = 0.1f,
                },
                RootNodeIndex = 0,
                DrawItems = drawItems,
                RequiredChunks = required,
                DesiredChunks = desired,
                ChunkSeen = seen,
                FrontierCost = cost,
            };
            job.Run();

            int frontierCost = cost.Value;
            required.Dispose();
            desired.Dispose();
            seen.Dispose();
            cost.Dispose();
            return (drawItems, frontierCost);
        }

        // Mirrors TreeSliceJob.ProjectedError for the test's conditions:
        // DistanceFalloffPower=1, open frustum, no foveation.
        static float NodePe(NodeRecord n, float3 eye, float nearClip)
        {
            float centerDist = math.length(n.Center - eye);
            float dist = math.max(centerDist - n.Radius, nearClip);
            return n.FeatureSize / dist;
        }

        // The Spark-style uniform cut: every merged node is small enough on
        // screen (pe <= threshold) and every expanded node exceeded it. This is
        // the invariant the budget heap did NOT satisfy (it stopped at a global
        // budget line regardless of a node's own screen error).
        [Test]
        public void ThresholdProducesUniformScreenErrorCut(
            [Values(0.05f, 0.2f, 0.5f, 1.0f, 5f)] float threshold)
        {
            var tree = new SyntheticTree(2024);
            tree.Build(depth: 5, maxInterior: 3, maxLeaves: 5);
            using var nodes = tree.ToNative(Allocator.TempJob);

            var (items, _) = RunSliceThreshold(nodes, threshold);

            var eye = new float3(0, 0, -5);
            const float nearClip = 0.1f;
            const float eps = 1e-4f;

            // parent map
            var parent = new uint[nodes.Length];
            for (int i = 0; i < parent.Length; i++)
                parent[i] = SplatFormat.None;
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.FirstInteriorChild == SplatFormat.None)
                    continue;
                for (int c = 0; c < n.InteriorChildCount; c++)
                    parent[(int)(n.FirstInteriorChild + c)] = (uint)i;
            }

            var expanded = new HashSet<uint>();
            foreach (var item in items)
            {
                if (item.IsMerged)
                {
                    float pe = NodePe(nodes[(int)item.NodeIndex], eye, nearClip);
                    Assert.LessOrEqual(pe, threshold + eps,
                        $"merged node {item.NodeIndex} pe {pe} > threshold {threshold} (not a uniform stop)");
                }
                else
                {
                    expanded.Add(item.NodeIndex); // owning node was expanded
                }
                // every strict ancestor of a drawn node was necessarily expanded
                uint p = parent[(int)item.NodeIndex];
                while (p != SplatFormat.None)
                {
                    expanded.Add(p);
                    p = parent[(int)p];
                }
            }

            foreach (uint e in expanded)
            {
                float pe = NodePe(nodes[(int)e], eye, nearClip);
                Assert.Greater(pe, threshold - eps,
                    $"expanded node {e} pe {pe} <= threshold {threshold} (should have merged)");
            }

            items.Dispose();
        }

        [Test]
        public void FrontierIsCutAndWithinBudget([Values(10, 100, 1000, 100000)] int budget)
        {
            var tree = new SyntheticTree(12345);
            tree.Build(depth: 4, maxInterior: 3, maxLeaves: 5);
            using var nodes = tree.ToNative(Allocator.TempJob);

            var (items, frontierCost) = RunSlice(nodes, budget);

            // (b) cost within budget (root-only frontier may still be 1)
            int cost = 0;
            foreach (var item in items)
                cost += item.IsMerged ? 1 : (int)item.Count;
            Assert.AreEqual(frontierCost, cost, "reported frontier cost mismatch");
            Assert.LessOrEqual(cost, math.max(budget, 1));

            // (a) frontier forms a cut: every leaf covered exactly once
            var covered = new Dictionary<uint, int>();
            foreach (var item in items)
            {
                var leaves = new HashSet<uint>();
                if (item.IsMerged)
                    tree.CollectLeaves((int)item.NodeIndex, leaves);
                else
                    for (uint k = 0; k < item.Count; k++)
                        leaves.Add(item.First + k);
                foreach (uint g in leaves)
                    covered[g] = covered.TryGetValue(g, out int c) ? c + 1 : 1;
            }

            var allLeaves = new HashSet<uint>();
            tree.CollectLeaves(0, allLeaves);
            Assert.AreEqual(allLeaves.Count, covered.Count, "leaf coverage incomplete");
            foreach (var kv in covered)
                Assert.AreEqual(1, kv.Value, $"leaf {kv.Key} covered {kv.Value} times (not a cut)");

            items.Dispose();
        }

        [Test]
        public void ChunkBudgetBoundsRequiredSetAndKeepsCut([Values(2, 3, 5)] int maxChunks)
        {
            // Multi-chunk tree (large leaf counts push TotalSplats > 65,536).
            var tree = new SyntheticTree(4242);
            tree.Build(depth: 4, maxInterior: 3, maxLeaves: 10000);
            using var nodes = tree.ToNative(Allocator.TempJob);
            Assert.Greater(tree.TotalSplats, (uint)SplatFormat.ChunkSplats);

            var (items, _, requiredCount) = RunSlice(nodes, 10_000_000, maxChunks);

            Assert.LessOrEqual(requiredCount, maxChunks, "required chunk set exceeds MaxChunks");

            // Still a cut: every leaf covered exactly once.
            var covered = new Dictionary<uint, int>();
            foreach (var item in items)
            {
                var leaves = new HashSet<uint>();
                if (item.IsMerged)
                    tree.CollectLeaves((int)item.NodeIndex, leaves);
                else
                    for (uint k = 0; k < item.Count; k++)
                        leaves.Add(item.First + k);
                foreach (uint g in leaves)
                    covered[g] = covered.TryGetValue(g, out int c) ? c + 1 : 1;
            }
            var allLeaves = new HashSet<uint>();
            tree.CollectLeaves(0, allLeaves);
            Assert.AreEqual(allLeaves.Count, covered.Count, "leaf coverage incomplete");
            foreach (var kv in covered)
                Assert.AreEqual(1, kv.Value, "not a cut");

            items.Dispose();
        }

        [Test]
        public void LargeBudgetExpandsEverything()
        {
            var tree = new SyntheticTree(999);
            tree.Build(depth: 3, maxInterior: 2, maxLeaves: 4);
            using var nodes = tree.ToNative(Allocator.TempJob);

            var (items, _) = RunSlice(nodes, 1_000_000);
            // Every synthetic node has children, so an unlimited budget must
            // expand the whole tree: the frontier contains leaf runs only.
            foreach (var item in items)
                Assert.IsFalse(item.IsMerged, "expandable node left merged despite budget");
            items.Dispose();
        }
    }

    public class VisibleIndexBuildTests
    {
        [Test]
        public void OutputReferencesOnlyResidentChunksOrRootFallback()
        {
            // Large leaf counts push TotalSplats past 65,536 so the synthetic
            // scene spans multiple chunks and the fallback path is exercised.
            var tree = new SyntheticTree(777);
            tree.Build(depth: 4, maxInterior: 3, maxLeaves: 10000);
            using var nodes = tree.ToNative(Allocator.TempJob);
            Assert.Greater(tree.TotalSplats, (uint)SplatFormat.ChunkSplats,
                "synthetic tree must span multiple chunks");

            // parents
            var parents = new NativeArray<uint>(nodes.Length, Allocator.TempJob);
            for (int i = 0; i < parents.Length; i++)
                parents[i] = SplatFormat.None;
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.FirstInteriorChild == SplatFormat.None)
                    continue;
                for (int c = 0; c < n.InteriorChildCount; c++)
                    parents[(int)(n.FirstInteriorChild + c)] = (uint)i;
            }

            // frontier = fully expanded (every node emits merged-less leaf runs
            // + deepest merged); reuse the slicer with a huge budget
            var drawItems = new NativeList<DrawItem>(1024, Allocator.TempJob);
            var required = new NativeList<uint>(64, Allocator.TempJob);
            var desired = new NativeList<uint>(64, Allocator.TempJob);
            var seen = new NativeBitArray(1024, Allocator.TempJob);
            var cost = new NativeReference<int>(Allocator.TempJob);
            var frustum = new FixedList512Bytes<float4>();
            for (int i = 0; i < 6; i++)
                frustum.Add(new float4(0, 1, 0, 1e9f));
            new TreeSliceJob
            {
                Nodes = nodes,
                Input = new SliceInput
                {
                    EyePosition = new float3(0, 0, -5),
                    FrustumPlanes = frustum,
                    BudgetSplats = 1_000_000,
                    MaxChunks = int.MaxValue,
                    NearClip = 0.1f,
                    OutOfFrustumFactor = 0.1f,
                },
                RootNodeIndex = 0,
                DrawItems = drawItems,
                RequiredChunks = required,
                DesiredChunks = desired,
                ChunkSeen = seen,
                FrontierCost = cost,
            }.Run();

            int chunkCount = (int)(tree.TotalSplats >> 16) + 1;

            // Try several residency patterns; chunk 0 always resident (pinned).
            for (uint pattern = 0; pattern < 8; pattern++)
            {
                var resident = new NativeBitArray(chunkCount, Allocator.TempJob);
                resident.Set(0, true);
                for (int c = 1; c < chunkCount; c++)
                    resident.Set(c, ((pattern >> (c % 3)) & 1) != 0);

                var visible = new NativeList<uint>(1024, Allocator.TempJob);
                var emitted = new NativeBitArray(nodes.Length, Allocator.TempJob);
                var fallbackCount = new NativeReference<int>(Allocator.TempJob);

                new VisibleIndexBuildJob
                {
                    Nodes = nodes,
                    Parents = parents,
                    DrawItems = drawItems.AsArray(),
                    ResidentChunks = resident,
                    VisibleIndices = visible,
                    FallbackEmitted = emitted,
                    FallbackCount = fallbackCount,
                }.Run();

                // Invariant (§9): every output index points into a resident chunk.
                var seenIndices = new HashSet<uint>();
                foreach (uint g in visible.AsArray())
                {
                    Assert.IsTrue(resident.IsSet((int)(g >> 16)),
                        $"visible index {g} references non-resident chunk {g >> 16} (pattern {pattern})");
                    Assert.IsTrue(seenIndices.Add(g), $"duplicate visible index {g}");
                }
                Assert.Greater(visible.Length, 0);

                visible.Dispose();
                emitted.Dispose();
                fallbackCount.Dispose();
                resident.Dispose();
            }

            drawItems.Dispose();
            required.Dispose();
            desired.Dispose();
            seen.Dispose();
            cost.Dispose();
            parents.Dispose();
        }

        /// A leaf run spanning two chunks with only the first resident must
        /// still draw the resident half. Degrading the whole run collapsed tens
        /// of thousands of splats to one ancestor splat; spark pages at chunk
        /// granularity, so we do too.
        [Test]
        public void PartiallyResidentLeafRunKeepsItsResidentChunks()
        {
            const uint first = 65530;   // 6 splats in chunk 0, 6 in chunk 1
            const uint count = 12;

            using var nodes = new NativeArray<NodeRecord>(new[]
            {
                new NodeRecord
                {
                    MergedSplatIndex = 0,
                    FirstInteriorChild = 1,
                    InteriorChildCount = 1,
                    DirectLeafFirst = SplatFormat.None,
                },
                new NodeRecord
                {
                    MergedSplatIndex = 1,
                    FirstInteriorChild = SplatFormat.None,
                    DirectLeafFirst = first,
                    DirectLeafCount = count,
                },
            }, Allocator.TempJob);

            using var parents = new NativeArray<uint>(
                new[] { SplatFormat.None, 0u }, Allocator.TempJob);
            using var drawItems = new NativeArray<DrawItem>(
                new[] { DrawItem.LeafRun(1u, first, count) }, Allocator.TempJob);

            using var resident = new NativeBitArray(2, Allocator.TempJob);
            resident.Set(0, true);    // chunk 1 missing

            using var visible = new NativeList<uint>(32, Allocator.TempJob);
            using var emitted = new NativeBitArray(nodes.Length, Allocator.TempJob);
            using var fallbackCount = new NativeReference<int>(Allocator.TempJob);

            new VisibleIndexBuildJob
            {
                Nodes = nodes,
                Parents = parents,
                DrawItems = drawItems,
                ResidentChunks = resident,
                VisibleIndices = visible,
                FallbackEmitted = emitted,
                FallbackCount = fallbackCount,
            }.Run();

            var got = new HashSet<uint>(visible.AsArray().ToArray());
            for (uint g = first; g < 65536; g++)
                Assert.IsTrue(got.Contains(g), $"resident leaf {g} was dropped");
            for (uint g = 65536; g < first + count; g++)
                Assert.IsFalse(got.Contains(g), $"non-resident leaf {g} was emitted");

            // The missing half degrades to the run's owning node (chunk 0).
            Assert.IsTrue(got.Contains(1u), "no ancestor fallback for the missing chunk");
            Assert.AreEqual(1, fallbackCount.Value);
        }
    }
}
