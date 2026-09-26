// LodStreamingDriver (spec §7.7): frame loop tying together TreeSlicer,
// ChunkStreamer, SplatPagePool, VisibleIndexBuildJob and the renderer.
//
// The pool is sized to fit the whole scene when it can (FullResidency);
// otherwise a fixed-size pool streams chunks in and out based on the LoD cut.

using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GsplatLod
{
    public sealed class LodStreamingDriver : MonoBehaviour
    {
        [Tooltip(".usst path; absolute, or relative to persistentDataPath")]
        public string UsstPath;

        public LodStreamingConfig Config;
        public Shader SplatShader;
        public bool GammaToLinear = true;

        // --- stats for the debug HUD (§7.9) ---
        public int VisibleCount { get; private set; }
        public int FrontierCost { get; private set; }
        public int RequiredChunkCount { get; private set; }
        public float LastSliceMs { get; private set; }
        /// <summary>Uniform LoD target in pixels fed to the slice job.</summary>
        public float CurrentLodTargetPixel => Config ? Config.LodTargetPixelSize : 0f;
        public int FallbackItems { get; private set; }
        public int ResidentPages => m_pool?.ResidentCount ?? 0;
        public int PoolPages => m_pool?.PageCount ?? 0;
        public int FreeablePages => m_pool?.FreeableCount ?? 0;
        /// <summary>The pool holds every chunk: nothing is ever evicted and the
        /// slice runs without a chunk-count constraint (spark's normal case).</summary>
        public bool FullResidency { get; private set; }

        /// <summary>Re-slice on the next frame even though the viewpoint has not
        /// moved. Call this after editing the Config at runtime — the movement
        /// gate in TreeSlicer would otherwise hold the previous cut indefinitely
        /// while the user stands still tuning sliders.</summary>
        public void RequestReslice() => m_forceNextSlice = true;
        public StreamerStats StreamerStats => m_streamer?.Stats ?? default;
        public bool Loaded => m_scene != null;
        public string LoadError { get; private set; }

        /// <summary>Scene AABB in object space; valid once loaded.</summary>
        public Bounds LocalBounds { get; private set; }

        SplatSceneAsset m_scene;
        SplatPagePool m_pool;
        ChunkStreamer m_streamer;
        TreeSlicer m_slicer;
        GsplatLodRenderer m_renderer;

        // Double-buffered order buffer: sorted indices are uploaded into the
        // inactive buffer in per-frame slices (a single 4MB SetData at sort
        // completion is a visible hitch), then the buffers flip atomically.
        readonly GraphicsBuffer[] m_orderBuffers = new GraphicsBuffer[2];
        int m_activeOrder;
        bool m_orderUploadPending;
        int m_orderUploadOffset;
        int m_pendingVisibleCount;
        int m_pendingFallbackItems;

        // index build + sort pipeline
        NativeList<uint> m_visibleIndices;
        NativeBitArray m_fallbackEmitted;
        NativeReference<int> m_fallbackCount;
        NativeArray<uint> m_sortKeys;
        NativeArray<uint> m_sortKeysTemp;
        NativeArray<uint> m_sortIndicesTemp;
        NativeArray<uint> m_sortedIndices;

        JobHandle m_jobHandle;
        bool m_jobsRunning;
        bool m_firstSliceDone;

        // Set by RequestReslice(): a config edit changes the cut without the
        // viewpoint moving, and TryKick is gated on movement — without this the
        // frontier stays frozen on the last slice and every LoD knob looks dead.
        bool m_forceNextSlice;
        // Set when chunk uploads changed residency; the visible index list is
        // then rebuilt from the last slice so freshly loaded regions refine
        // without waiting for the next camera-triggered slice.
        bool m_residencyDirty;
        bool m_haveLastSlice;
        SliceResult m_lastSlice;
        int m_lastRebuildFrame = int.MinValue;
        Vector3 m_lastSortPos;
        Quaternion m_lastSortRot;
        bool m_sortedOnce;

        // Required-chunk sets kept alive for the pool's needed set. m_active
        // backs the order buffer currently being drawn, m_pending the newest
        // slice; their union is what may not be evicted.
        NativeList<uint> m_pendingRequired;
        NativeList<uint> m_activeRequired;

        readonly Plane[] m_planeCache = new Plane[6];    // avoids per-frame GC
        readonly Plane[] m_planeCacheR = new Plane[6];   // right eye, for the stereo union
        bool m_loggedProjection;

        // --- periodic stats logging (Config.StatsLogIntervalSeconds); see §7.9 ---
        float m_statsSmoothedDt;
        float m_nextStatsLog;

        /// <summary>Resolves a .usst path: absolute/existing paths pass through,
        /// otherwise it is treated as relative to persistentDataPath.</summary>
        static string ResolvePath(string usstPath)
        {
            if (string.IsNullOrEmpty(usstPath))
                return null;
            if (Path.IsPathRooted(usstPath) || File.Exists(usstPath))
                return usstPath;
            return Path.Combine(Application.persistentDataPath, usstPath);
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += Draw;
            LoadError = null;
            string path = ResolvePath(UsstPath);
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                Initialize(path);
            }
            catch (System.Exception e)
            {
                LoadError = e.Message;
                Debug.LogError($"[GsplatLod] driver init failed: {e}", this);
                Shutdown();
            }
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
            Shutdown();
        }

        void Initialize(string path)
        {
            if (!Config)
                throw new System.InvalidOperationException("LodStreamingConfig is not assigned");

            m_scene = SplatSceneAsset.Load(path, Allocator.Persistent);
            var header = m_scene.Header;
            int chunkCount = (int)header.ChunkCount;

            var lb = new Bounds();
            lb.SetMinMax((Vector3)header.AabbMin, (Vector3)header.AabbMax);
            LocalBounds = lb;

            // Pool size follows spark's page policy (SparkRenderer.ts: 128 pages
            // on mobile, 256 on desktop) rather than an authored page count,
            // clamped to what the scene needs and what the device can back.
            // Config.PoolPages > 0 overrides it for A/B testing.
            int devicePages = SplatPagePool.MaxDevicePages((int)header.ChunkSplats);
            int defaultPages = Application.isMobilePlatform ? 128 : 256;
            int pages = Config.PoolPages > 0
                ? Mathf.Max(Config.PoolPages, Config.MinPoolPages(Config.BudgetSplats))
                : defaultPages;
            pages = Mathf.Min(pages, chunkCount);    // never larger than the scene
            pages = Mathf.Min(pages, devicePages);   // never more than the device can back
            FullResidency = pages >= chunkCount;

            m_pool = new SplatPagePool(pages, (int)header.ChunkSplats, chunkCount);
            m_streamer = new ChunkStreamer(m_scene, m_pool, Config);
            m_slicer = new TreeSlicer(m_scene, Config)
            {
                // With the whole scene resident there is nothing to starve, so
                // the cut runs unconstrained like spark's traverseLodTrees.
                // Otherwise the frontier must fit the pool (2 pages spare for
                // prefetch/swap) or the LRU thrashes.
                MaxFrontierChunks = FullResidency ? chunkCount : Mathf.Max(1, pages - 2),
            };

            int capacity = Config.BudgetSplats;
            m_visibleIndices = new NativeList<uint>(capacity, Allocator.Persistent);
            m_fallbackEmitted = new NativeBitArray((int)header.NodeCount, Allocator.Persistent);
            m_fallbackCount = new NativeReference<int>(Allocator.Persistent);
            m_sortKeys = new NativeArray<uint>(capacity, Allocator.Persistent);
            m_sortKeysTemp = new NativeArray<uint>(capacity, Allocator.Persistent);
            m_sortIndicesTemp = new NativeArray<uint>(capacity, Allocator.Persistent);
            m_sortedIndices = new NativeArray<uint>(capacity, Allocator.Persistent);
            m_orderBuffers[0] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(uint));
            m_orderBuffers[1] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(uint));
            m_activeOrder = 0;
            m_orderUploadPending = false;
            m_pendingRequired = new NativeList<uint>(chunkCount, Allocator.Persistent);
            m_activeRequired = new NativeList<uint>(chunkCount, Allocator.Persistent);

            if (!SplatShader)
                SplatShader = Shader.Find("GsplatLod/Splat");
            m_renderer = new GsplatLodRenderer(SplatShader);

            // With the whole scene resident nothing may ever be evicted; the
            // per-slice needed-set bookkeeping is then pure overhead.
            if (FullResidency)
                m_pool.SetAllNeeded();

            // Chunk 0 (tree root) loads synchronously and is needed for the
            // whole session so the ancestor walk always terminates (§5.5).
            LoadChunk0Sync();

            // Initial frontier: root only — the coarse full view (R2).
            m_visibleIndices.Add(m_scene.Nodes[(int)header.RootNodeIndex].MergedSplatIndex);
            m_orderBuffers[m_activeOrder].SetData(m_visibleIndices.AsArray(), 0, 0, 1);
            VisibleCount = 1;
            FrontierCost = 1;

            ApplyXrDisplaySettings();

            Debug.Log($"[GsplatLod] driver ready: {header.TotalSplats} splats, {chunkCount} chunks, " +
                      $"{pages} pool pages ({(long)pages * header.ChunkSplats * SplatFormat.SplatRecordSize / (1024 * 1024)} MB, " +
                      $"{(FullResidency ? "full residency" : "streaming")}; device max {devicePages})", this);
        }

        // Checking the OpenXR "Foveated Rendering" feature box alone does
        // nothing: the level must be set on the display subsystem at runtime.
        void ApplyXrDisplaySettings()
        {
            // Both settings are written unconditionally: skipping the neutral
            // value (scale 1, level 0) meant a setting could be turned on but
            // never back off, because nothing ever restored the default.
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = Config.EyeResolutionScale;
            Debug.Log($"[GsplatLod] eye texture resolution scale {Config.EyeResolutionScale} applied", this);

            float level = Mathf.Clamp01(Config.FoveatedRenderingLevel);
            var displays = new System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
            {
                display.foveatedRenderingLevel = level;
                display.foveatedRenderingFlags =
                    UnityEngine.XR.XRDisplaySubsystem.FoveatedRenderingFlags.GazeAllowed;
            }
            if (displays.Count > 0)
                Debug.Log($"[GsplatLod] foveated rendering level {level} applied", this);
        }

        unsafe void LoadChunk0Sync()
        {
            byte[] bytes = File.ReadAllBytes(m_scene.ChunkPath(0));
            int count = bytes.Length / SplatFormat.SplatRecordSize;
            using var na = new NativeArray<byte>(bytes, Allocator.Temp);
            if (!m_pool.Upload(0, na.Reinterpret<SplatRecordData>(1), count))
                throw new System.InvalidOperationException("failed to upload chunk 0");
        }

        void Shutdown()
        {
            if (m_jobsRunning)
            {
                m_jobHandle.Complete();
                m_jobsRunning = false;
            }
            m_streamer?.Dispose();
            m_streamer = null;
            m_slicer?.Dispose();
            m_slicer = null;
            m_renderer?.Dispose();
            m_renderer = null;
            m_pool?.Dispose();
            m_pool = null;
            m_orderBuffers[0]?.Dispose();
            m_orderBuffers[1]?.Dispose();
            m_orderBuffers[0] = m_orderBuffers[1] = null;
            m_orderUploadPending = false;

            if (m_visibleIndices.IsCreated) m_visibleIndices.Dispose();
            if (m_fallbackEmitted.IsCreated) m_fallbackEmitted.Dispose();
            if (m_fallbackCount.IsCreated) m_fallbackCount.Dispose();
            if (m_sortKeys.IsCreated) m_sortKeys.Dispose();
            if (m_sortKeysTemp.IsCreated) m_sortKeysTemp.Dispose();
            if (m_sortIndicesTemp.IsCreated) m_sortIndicesTemp.Dispose();
            if (m_sortedIndices.IsCreated) m_sortedIndices.Dispose();
            if (m_pendingRequired.IsCreated) m_pendingRequired.Dispose();
            if (m_activeRequired.IsCreated) m_activeRequired.Dispose();

            m_scene?.Dispose();
            m_scene = null;
            m_firstSliceDone = false;
            m_sortedOnce = false;
            VisibleCount = 0;
        }

        void Update()
        {
            if (m_scene == null)
                return;

            var cam = Camera.main;
            if (!cam)
                return;

            // Model-space eye + frustum (VR: main camera pose is the center eye).
            var worldToLocal = transform.worldToLocalMatrix;
            float3 eyePos = worldToLocal.MultiplyPoint3x4(cam.transform.position);
            quaternion eyeRot = cam.transform.rotation;

            // 1. kick a slice if the viewpoint moved (§7.3). Gated on the
            // build/sort pipeline (TreeSliceJob writes DrawItems that
            // VisibleIndexBuildJob reads) and on the staged order upload
            // (which reads m_sortedIndices that the sort job writes).
            if (!m_jobsRunning && !m_orderUploadPending)
            {
                float3 eyeFwd = math.normalize(worldToLocal.MultiplyVector(cam.transform.forward));
                if (m_slicer.TryKick(eyePos, eyeRot, eyeFwd, FrustumPlanesModelSpace(cam),
                        cam.nearClipPlane, ComputePeThreshold(cam),
                        force: !m_firstSliceDone || m_forceNextSlice))
                    m_forceNextSlice = false;
            }

            // 2. collect a finished slice → demand + index build (§7.7-2)
            if (!m_jobsRunning && !m_orderUploadPending && m_slicer.TryCollect(out var slice))
            {
                m_firstSliceDone = true;
                FrontierCost = slice.FrontierCost;
                LastSliceMs = slice.SliceMilliseconds;

                m_pool.Touch(slice.RequiredChunks.AsReadOnlySpan());
                RequiredChunkCount = slice.RequiredChunks.Length;

                // The newest required set joins the one backing the active
                // order buffer as the pool's needed set, so an incoming
                // chunk can only land on a page nothing is drawing from.
                m_pendingRequired.Clear();
                m_pendingRequired.AddRange(slice.RequiredChunks);
                RefreshNeededSet();

                // Prefetch only what fits outside the needed set — a longer
                // desired list just cycles through the spare pages forever.
                int desiredCap = FullResidency
                    ? slice.DesiredChunks.Length
                    : Mathf.Max(0, m_pool.PageCount - m_pendingRequired.Length - 2);
                m_streamer.Request(slice.RequiredChunks.AsReadOnlySpan(),
                    slice.DesiredChunks.AsReadOnlySpan()
                        .Slice(0, Mathf.Min(desiredCap, slice.DesiredChunks.Length)));

                m_lastSlice = slice;
                m_haveLastSlice = true;
                KickBuildAndSort(slice, cam);
            }
            // residency changed (chunk loads finished) → rebuild the visible
            // list from the last slice so loaded regions actually refine.
            // Throttled: rebuilding after every single chunk would stall the
            // upload lane (jobs block uploads) and multiply time-to-detail.
            // Gated on the slicer: its job rewrites the lists m_lastSlice
            // points into.
            else if (!m_jobsRunning && !m_orderUploadPending && !m_slicer.IsRunning
                     && m_haveLastSlice && m_residencyDirty
                     && (m_streamer.Stats.QueueLength == 0
                         || Time.frameCount - m_lastRebuildFrame >= Config.ResidencyRebuildIntervalFrames))
            {
                m_residencyDirty = false;
                m_lastRebuildFrame = Time.frameCount;
                KickBuildAndSort(m_lastSlice, cam);
            }
            // sort-only refresh when the camera moved but the frontier didn't change
            else if (!m_jobsRunning && !m_orderUploadPending && m_firstSliceDone && NeedsResort(cam))
            {
                var sort = MakeSortJob(cam);
                m_jobHandle = sort.Schedule();
                m_jobsRunning = true;
                m_lastSortPos = cam.transform.position;
                m_lastSortRot = cam.transform.rotation;
            }

            // 3. collect finished index build/sort → begin the staged upload
            if (m_jobsRunning && m_jobHandle.IsCompleted)
            {
                m_jobHandle.Complete();
                m_jobsRunning = false;
                m_pendingVisibleCount = math.min(m_visibleIndices.Length, m_sortedIndices.Length);
                m_pendingFallbackItems = m_fallbackCount.Value;
                m_orderUploadOffset = 0;
                m_orderUploadPending = true;
            }

            // 4. staged order-buffer upload (own per-frame budget)
            if (m_orderUploadPending)
            {
                int elemsPerFrame = Mathf.Max(1, Config.MaxOrderUploadBytesPerFrame / sizeof(uint));
                int remaining = m_pendingVisibleCount - m_orderUploadOffset;
                int n = Mathf.Min(elemsPerFrame, remaining);
                var inactive = m_orderBuffers[1 - m_activeOrder];
                if (n > 0)
                    inactive.SetData(m_sortedIndices, m_orderUploadOffset, m_orderUploadOffset, n);
                m_orderUploadOffset += n;
                if (m_orderUploadOffset >= m_pendingVisibleCount)
                {
                    m_activeOrder = 1 - m_activeOrder;   // atomic flip
                    VisibleCount = m_pendingVisibleCount;
                    FallbackItems = m_pendingFallbackItems;
                    m_orderUploadPending = false;
                    // The buffer now being drawn is backed by the pending set;
                    // the one it replaced no longer needs protecting.
                    m_activeRequired.Clear();
                    m_activeRequired.AddRange(m_pendingRequired.AsArray());
                    RefreshNeededSet();
                }
            }

            // 5. chunk uploads run in parallel with the order upload (each has
            // its own budget); only in-flight jobs block them, because uploads
            // mutate the residency/slot tables the jobs read.
            if (!m_jobsRunning)
            {
                m_streamer.Update();
                if (m_streamer.Stats.UploadedBytesLastFrame > 0)
                    m_residencyDirty = true;
            }

            // 6. the draw itself is submitted per camera from Draw(), because a
            // single unrestricted submission here only reaches cameras that
            // render after this Update. XREAL's capture rig renders its camera
            // from inside another MonoBehaviour's Update (FrameBlender calls
            // Camera.Render() directly), so it can miss the submission entirely
            // and record the scene without any splats.

            // 7. optional periodic stats log for headsets where OnGUI (LodDebugHud)
            // is not visible; adb logcat -s Unity picks up the "[GsplatLod]" lines.
            if (Config.StatsLogIntervalSeconds > 0f)
                LogStatsIfDue();
        }

        // Submitted once per camera, before that camera culls.
        void Draw(ScriptableRenderContext ctx, Camera cam)
        {
            if (m_scene == null || VisibleCount <= 0)
                return;

            m_renderer.KernelAlphaCutoff = Config.KernelAlphaCutoff;
            m_renderer.MinPixelRadius = Config.MinPixelRadius;
            var worldBounds = TransformBounds(LocalBounds, transform.localToWorldMatrix);
            m_renderer.Render(m_pool.SplatBuffers, m_pool.BufferCount, m_pool.BufferSplatCapacity,
                m_pool.ChunkSlotTable, m_orderBuffers[m_activeOrder],
                VisibleCount, transform.localToWorldMatrix, worldBounds, gameObject.layer, GammaToLinear,
                cam);
        }

        void LogStatsIfDue()
        {
            m_statsSmoothedDt = Mathf.Lerp(m_statsSmoothedDt <= 0 ? Time.unscaledDeltaTime : m_statsSmoothedDt,
                Time.unscaledDeltaTime, 0.05f);

            if (Time.unscaledTime < m_nextStatsLog)
                return;
            m_nextStatsLog = Time.unscaledTime + Config.StatsLogIntervalSeconds;

            string pool = FullResidency
                ? $"{ResidentPages}/{PoolPages} (full residency)"
                : $"{ResidentPages}/{PoolPages}";
            float fps = 1f / Mathf.Max(m_statsSmoothedDt, 1e-6f);
            // cfg = what the panel wrote into the asset THIS driver holds; cfg
            // not tracking the panel localises a dead knob.
            Debug.Log($"[GsplatLod] {name}: frontier {FrontierCost:N0}  " +
                      $"visible {VisibleCount:N0}  budget {Config.BudgetSplats:N0}  required {RequiredChunkCount}  " +
                      $"pool {pool}  fallback {FallbackItems}  fps {fps:F1}  " +
                      $"cfg[id {Config.GetInstanceID()} px {Config.LodTargetPixelSize:F2} " +
                      $"fovea {Config.FoveaInnerDegrees:F0}/{Config.FoveaOuterDegrees:F0} " +
                      $"periph {Config.PeripheryFactor:F2} oof {Config.OutOfFrustumFactor:F3}]");
        }

        void KickBuildAndSort(in SliceResult slice, Camera cam)
        {
            var build = new VisibleIndexBuildJob
            {
                Nodes = m_scene.NodesArray,
                Parents = m_slicer.Parents,
                DrawItems = slice.DrawItems,
                ResidentChunks = m_pool.ResidencyBits,
                VisibleIndices = m_visibleIndices,
                FallbackEmitted = m_fallbackEmitted,
                FallbackCount = m_fallbackCount,
            };
            var sort = MakeSortJob(cam);
            m_jobHandle = sort.Schedule(build.Schedule());
            m_jobsRunning = true;
            m_lastSortPos = cam.transform.position;
            m_lastSortRot = cam.transform.rotation;
            m_sortedOnce = true;
        }

        // freeable = resident - (pending required ∪ active required), mirroring
        // spark's SplatPager.driveFetchers. Skipped when everything is resident,
        // where SetAllNeeded already forbids eviction outright.
        void RefreshNeededSet()
        {
            if (FullResidency)
                return;
            m_pool.SetNeeded(m_pendingRequired.AsArray().AsReadOnlySpan(),
                             m_activeRequired.AsArray().AsReadOnlySpan());
        }

        bool NeedsResort(Camera cam)
        {
            if (!m_sortedOnce)
                return true;
            return (cam.transform.position - m_lastSortPos).magnitude >= Config.SortMoveThresholdMeters
                   || Quaternion.Angle(cam.transform.rotation, m_lastSortRot) >= Config.SortRotateThresholdDegrees;
        }

        DepthSortJob MakeSortJob(Camera cam)
        {
            var worldToLocal = transform.worldToLocalMatrix;
            return new DepthSortJob
            {
                Positions = m_pool.PoolPositions,
                VisibleIndices = m_visibleIndices,
                SlotOfChunk = m_pool.SlotOfChunk,
                EyePosition = worldToLocal.MultiplyPoint3x4(cam.transform.position),
                EyeForward = math.normalize(worldToLocal.MultiplyVector(cam.transform.forward)),
                Keys = m_sortKeys,
                KeysTemp = m_sortKeysTemp,
                IndicesTemp = m_sortIndicesTemp,
                SortedIndices = m_sortedIndices,
            };
        }

        // Focal length in pixels: a world segment of length L at distance d
        // covers L * focalPx / d pixels vertically. Derived from the eye
        // projection (m11 = cot(fovY/2)) and the eye-buffer height, so it is
        // correct for both XR (per-eye projection + eyeTextureHeight) and flat.
        float ComputeFocalPixels(Camera cam)
        {
            float h = UnityEngine.XR.XRSettings.enabled ? UnityEngine.XR.XRSettings.eyeTextureHeight : 0f;
            if (h <= 0f) h = cam.pixelHeight;
            if (h <= 0f) h = Screen.height;
            // Must be the eye projection to match the eye-buffer height above;
            // cam.projectionMatrix here is the mono one (see FrustumPlanesModelSpace).
            float m11 = Mathf.Abs(cam.stereoEnabled
                ? cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left).m11
                : cam.projectionMatrix.m11);

            if (!m_loggedProjection)
            {
                m_loggedProjection = true;
                Debug.Log($"[GsplatLod] projection: stereo={cam.stereoEnabled} " +
                          $"eyeTex={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} " +
                          $"screen={Screen.width}x{Screen.height} camAspect={cam.aspect:F3} camFov={cam.fieldOfView:F1} " +
                          $"mono(m00,m11)=({cam.projectionMatrix.m00:F3},{cam.projectionMatrix.m11:F3}) " +
                          $"eyeL(m00,m11)=({cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left).m00:F3}," +
                          $"{cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left).m11:F3}) " +
                          $"focalPx={0.5f * h * m11:F1}", this);
            }
            return 0.5f * h * m11;
        }

        // Converts the pixel target into the pe cutoff the slice job compares
        // against (pe = FeatureSize/dist^k ≈ angular size; pixels = pe * focalPx).
        // 0 target → threshold disabled (pure budget).
        float ComputePeThreshold(Camera cam)
        {
            float target = Config.LodTargetPixelSize;
            if (target <= 0f)
                return 0f;
            float focalPx = ComputeFocalPixels(cam);
            return focalPx > 0f ? target / focalPx : 0f;
        }

        FixedList512Bytes<float4> FrustumPlanesModelSpace(Camera cam)
        {
            // Planes of (P * V * M) are the camera frustum in model space,
            // normals pointing inward.
            //
            // cam.projectionMatrix outside a render callback is the *mono*
            // projection built from fieldOfView and aspect, not the XR eye
            // projection. On a phone-driven headset the player window is
            // portrait, so that frustum is far narrower horizontally than what
            // the eye buffers actually show, and everything to the left and
            // right of centre was damped by OutOfFrustumFactor — a dense band
            // in the middle with a sparse surround, unaffected by the fovea
            // settings. Use the union of the two eye frusta instead.
            var model = transform.localToWorldMatrix;
            GeometryUtility.CalculateFrustumPlanes(
                StereoOrMonoMatrix(cam, Camera.StereoscopicEye.Left) * model, m_planeCache);

            // CalculateFrustumPlanes order: 0 Left, 1 Right, 2 Down, 3 Up,
            // 4 Near, 5 Far. The eyes differ only horizontally, so the union is
            // the left eye's left plane and the right eye's right plane.
            if (cam.stereoEnabled)
            {
                GeometryUtility.CalculateFrustumPlanes(
                    StereoOrMonoMatrix(cam, Camera.StereoscopicEye.Right) * model, m_planeCacheR);
                m_planeCache[1] = m_planeCacheR[1];
            }

            var list = new FixedList512Bytes<float4>();
            for (int i = 0; i < 6; i++)
            {
                var n = m_planeCache[i].normal;
                list.Add(new float4(n.x, n.y, n.z, m_planeCache[i].distance));
            }
            return list;
        }

        static Matrix4x4 StereoOrMonoMatrix(Camera cam, Camera.StereoscopicEye eye) =>
            cam.stereoEnabled
                ? cam.GetStereoProjectionMatrix(eye) * cam.GetStereoViewMatrix(eye)
                : cam.projectionMatrix * cam.worldToCameraMatrix;

        /// <summary>Transforms an object-space AABB by m, conservatively (the
        /// result is itself axis-aligned).</summary>
        static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            var center = m.MultiplyPoint3x4(local.center);
            var ext = local.extents;
            var newExt = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                var axis = m.GetColumn(i);
                newExt += new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) * ext[i];
            }
            return new Bounds(center, newExt * 2f);
        }
    }
}
