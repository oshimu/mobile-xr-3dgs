// CPU back-to-front sort of visible splat indices (spec §7.6.3): Burst LSD
// radix on view depth.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GsplatLod
{
    [BurstCompile]
    public struct DepthSortJob : IJob
    {
        /// <summary>Splat positions, indexed by pool slot via SlotOfChunk.</summary>
        [ReadOnly] public NativeArray<float3> Positions;
        /// <summary>List so a preceding build job may resize it; Length is
        /// read at execution time.</summary>
        [ReadOnly] public NativeList<uint> VisibleIndices;
        /// <summary>chunkId → pool slot.</summary>
        [ReadOnly] public NativeArray<uint> SlotOfChunk;
        public float3 EyePosition;
        public float3 EyeForward;                          // model space

        // scratch (persistent, owned by caller; length >= capacity)
        public NativeArray<uint> Keys;
        public NativeArray<uint> KeysTemp;
        public NativeArray<uint> IndicesTemp;
        // output; length >= capacity
        public NativeArray<uint> SortedIndices;

        public void Execute()
        {
            int Count = math.min(VisibleIndices.Length, SortedIndices.Length);
            for (int i = 0; i < Count; i++)
            {
                uint g = VisibleIndices[i];
                uint slot = SlotOfChunk[(int)(g >> 16)];
                // Evicted mid-pipeline: key is arbitrary, shader discards it.
                int posIndex = slot == SplatFormat.None
                    ? 0
                    : (int)(slot * 65536u + (g & 0xFFFFu));
                float d = math.dot(Positions[posIndex] - EyePosition, EyeForward);
                // back-to-front: invert the ascending sortable-uint encoding
                Keys[i] = ~FloatToSortableUint(d);
                SortedIndices[i] = g;
            }

            // LSD radix, 4 x 8bit
            var histogram = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var src = SortedIndices;
            var dst = IndicesTemp;
            var keySrc = Keys;
            var keyDst = KeysTemp;

            for (int pass = 0; pass < 4; pass++)
            {
                int shift = pass * 8;
                for (int i = 0; i < 256; i++) histogram[i] = 0;
                for (int i = 0; i < Count; i++)
                    histogram[(int)((keySrc[i] >> shift) & 0xFF)]++;
                int sum = 0;
                for (int i = 0; i < 256; i++)
                {
                    int c = histogram[i];
                    histogram[i] = sum;
                    sum += c;
                }
                for (int i = 0; i < Count; i++)
                {
                    int bucket = (int)((keySrc[i] >> shift) & 0xFF);
                    int o = histogram[bucket]++;
                    dst[o] = src[i];
                    keyDst[o] = keySrc[i];
                }
                (src, dst) = (dst, src);
                (keySrc, keyDst) = (keyDst, keySrc);
            }
            // 4 passes = even number of swaps; results are back in SortedIndices/Keys.
            histogram.Dispose();
        }

        static uint FloatToSortableUint(float f)
        {
            uint u = math.asuint(f);
            return (u & 0x80000000u) != 0 ? ~u : u | 0x80000000u;
        }
    }
}
