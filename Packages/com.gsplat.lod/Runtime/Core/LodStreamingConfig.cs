// Tunable parameters (spec §7.8).

using UnityEngine;

namespace GsplatLod
{
    [CreateAssetMenu(fileName = "LodStreamingConfig", menuName = "Gsplat LoD/Streaming Config")]
    public sealed class LodStreamingConfig : ScriptableObject
    {
        [Header("LoD budget")]
        [Tooltip("Cap on frontier splats per slice (N), and the basis for pool sizing (MinPoolPages). Refinement expands the highest-priority nodes first and stops at this cap, so on mobile GPUs this is the main cost/quality knob")]
        public int BudgetSplats = 1_000_000;

        [Header("LoD uniform threshold (Spark-style cut)")]
        [Tooltip("Target on-screen size (px) every node is refined to: a node draws as a single merged splat once its projected size drops below this, otherwise it refines. Expansion stops at whichever of this threshold or BudgetSplats is reached first; on mobile (Quest) the budget usually saturates first, so BudgetSplats is what actually governs. Smaller = sharper/more splats. 0 disables the threshold (pure budget expansion)")]
        [Range(0f, 8f)]
        public float LodTargetPixelSize = 1.5f;

        [Tooltip("Exponent k in projectedError = FeatureSize / dist^k. k=1 makes projectedError proportional to true on-screen size; k>1 shifts the budget toward nearer content")]
        [Range(0.5f, 1.5f)]
        public float DistanceFalloffPower = 1.0f;

        [Tooltip("projectedError multiplier for nodes fully outside the frustum (no hard culling)")]
        public float OutOfFrustumFactor = 0.1f;

        [Header("Foveation (視界中心の優先)")]
        [Tooltip("Full detail priority within this angle from the view center (degrees)")]
        public float FoveaInnerDegrees = 25f;

        [Tooltip("Priority falls to PeripheryFactor at this angle (degrees)")]
        public float FoveaOuterDegrees = 70f;

        [Tooltip("projectedError multiplier at the view periphery; 1 = no foveation")]
        [Range(0.05f, 1f)]
        public float PeripheryFactor = 0.35f;

        [Header("GPU pool")]
        [Tooltip("Pages of 65,536 splats. 0 = auto: the whole scene if it fits, else 128 pages on mobile / 256 on desktop, capped by what the device can back. Set a value only to override for A/B testing; it must then be >= ceil(N/65536)+2")]
        public int PoolPages;

        [Tooltip("GraphicsBuffer.SetData budget per frame for chunk uploads (bytes)")]
        public int MaxUploadBytesPerFrame = 4 * 1024 * 1024;

        [Tooltip("SetData budget per frame for the sorted order buffer (bytes); smaller = smoother but sort results apply later")]
        public int MaxOrderUploadBytesPerFrame = 1024 * 1024;

        [Tooltip("Staging buffers / concurrent chunk loads")]
        public int MaxConcurrentLoads = 4;

        [Header("Slice triggers")]
        public float SliceMoveThresholdMeters = 0.1f;
        public float SliceRotateThresholdDegrees = 5.0f;
        public int SliceMinIntervalFrames = 3;

        [Header("Streaming")]
        public bool PrefetchEnabled = true;

        [Tooltip("Rebuild the visible list at most this often while chunks are still loading (frames); it always rebuilds once the load queue drains")]
        public int ResidencyRebuildIntervalFrames = 10;

        [Tooltip("LRU cache size for open chunk file handles")]
        public int FileHandleCacheSize = 8;

        [Header("Debug")]
        [Tooltip("Run the slice job synchronously on the main thread and measure exact execution time (blocks the frame; verification only)")]
        public bool MeasureSliceSync;

        [Tooltip("Log a one-line LoD status summary via Debug.Log at this interval (seconds), for XR headsets where the OnGUI HUD is not visible (check with adb logcat -s Unity). 0 = disabled")]
        [Range(0f, 10f)]
        public float StatsLogIntervalSeconds = 0f;

        [Header("Rendering")]
        [Tooltip("Fixed Foveated Rendering level applied at startup (0 = off, 1 = max). Requires the OpenXR Foveated Rendering feature on device")]
        [Range(0f, 1f)]
        public float FoveatedRenderingLevel = 1f;

        [Tooltip("XR eye-buffer resolution scale applied at startup (1 = platform default). Raising it sharpens splats at fragment-only cost — vertex count is unchanged, so try 1.2–1.4 on Quest")]
        [Range(0.5f, 2f)]
        public float EyeResolutionScale = 1f;

        [Tooltip("Min contributing splat alpha. Larger = smaller quads = less GPU fill (Quest: try 0.02–0.04); 0.004 ≈ full quality")]
        [Range(1f / 255f, 0.1f)]
        public float KernelAlphaCutoff = 1f / 255f;

        [Tooltip("Minimum on-screen splat radius in pixels; smaller splats are culled (fill-rate savings). Raise for Quest (try 1.5–2.0)")]
        [Range(0.5f, 4f)]
        public float MinPixelRadius = 1.0f;

        [Header("Sorting")]
        [Tooltip("Re-sort when the camera moved this far (m)")]
        public float SortMoveThresholdMeters = 0.05f;

        [Tooltip("Re-sort when the camera rotated this much (deg)")]
        public float SortRotateThresholdDegrees = 2.0f;

        public int MinPoolPages(int budgetSplats) =>
            (budgetSplats + SplatFormat.ChunkSplats - 1) / SplatFormat.ChunkSplats + 2;

        void OnValidate()
        {
            // 0 means auto-size at load; only an explicit override has a floor.
            if (PoolPages <= 0)
            {
                PoolPages = 0;
                return;
            }
            int minPages = MinPoolPages(BudgetSplats);
            if (PoolPages < minPages)
                PoolPages = minPages;
        }
    }
}
