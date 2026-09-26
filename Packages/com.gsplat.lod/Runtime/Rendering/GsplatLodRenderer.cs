// Renderer core (spec §7.6). The boundary with the rest of the system is
// intentionally narrow (§7.6.1): it draws a splat pool GraphicsBuffer through
// a sorted order buffer; it knows nothing about trees, slicing or streaming.

using UnityEngine;

namespace GsplatLod
{
    public sealed class GsplatLodRenderer : System.IDisposable
    {
        public const int SplatInstanceSize = 128;

        readonly Material m_material;
        readonly Mesh m_mesh;
        readonly MaterialPropertyBlock m_props;

        static readonly int[] k_splatPool =
        {
            Shader.PropertyToID("_SplatPool0"),
            Shader.PropertyToID("_SplatPool1"),
            Shader.PropertyToID("_SplatPool2"),
            Shader.PropertyToID("_SplatPool3"),
        };
        static readonly int k_splatBufferCap = Shader.PropertyToID("_SplatBufferCap");
        static readonly int k_chunkSlotTable = Shader.PropertyToID("_ChunkSlotTable");
        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_splatCount = Shader.PropertyToID("_SplatCount");
        static readonly int k_splatInstanceSize = Shader.PropertyToID("_SplatInstanceSize");
        static readonly int k_matrixM = Shader.PropertyToID("_MATRIX_M");
        static readonly int k_gammaToLinear = Shader.PropertyToID("_GammaToLinear");
        static readonly int k_kernelAlphaCutoff = Shader.PropertyToID("_KernelAlphaCutoff");
        static readonly int k_minPixelRadius = Shader.PropertyToID("_MinPixelRadius");

        /// <summary>Minimum contributing alpha; raising it shrinks splat quads
        /// (large fill-rate savings on mobile GPUs at slight quality cost).</summary>
        public float KernelAlphaCutoff = 1f / 255f;

        /// <summary>Minimum on-screen splat radius in pixels; smaller splats
        /// are culled (fill-rate savings).</summary>
        public float MinPixelRadius = 1f;

        public GsplatLodRenderer(Shader shader)
        {
            if (!shader)
                throw new System.ArgumentNullException(nameof(shader), "GsplatLod/Splat shader missing");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_mesh = CreateBatchQuadMesh(SplatInstanceSize);
            m_props = new MaterialPropertyBlock();
        }

        /// <summary>
        /// Draws <paramref name="count"/> splats. orderBuffer holds sorted
        /// global indices; slotTable maps chunkId to pool slot. The pool may
        /// span up to four buffers (platform max-buffer-size limit): each buffer
        /// serves <paramref name="bufferSplatCapacity"/> splats, so pool index i
        /// reads splatPools[i / cap] at local offset i % cap. splatPools must be
        /// length SplatPagePool.MaxBuffers with unused entries aliased to [0].
        /// <paramref name="bufferCount"/> is how many of them are distinct; the
        /// shader declares only those, because GLES caps vertex-stage shader
        /// storage blocks at 4 on Adreno and counts declared blocks.
        /// <paramref name="camera"/> restricts the draw to one camera; the
        /// caller submits once per camera so that cameras driven by a manual
        /// Camera.Render() (XREAL's capture rig) also get the splats, which an
        /// unrestricted Update-time submission misses when it renders earlier
        /// in the frame than the submission.
        /// </summary>
        public void Render(GraphicsBuffer[] splatPools, int bufferCount, int bufferSplatCapacity,
            GraphicsBuffer slotTable, GraphicsBuffer orderBuffer,
            int count, Matrix4x4 localToWorld, Bounds worldBounds, int layer, bool gammaToLinear,
            Camera camera = null)
        {
            if (count <= 0)
                return;

            // Round up to a declared variant: a 3-buffer pool uses the 4 form.
            int declared = bufferCount <= 1 ? 1 : bufferCount <= 2 ? 2 : 4;
            SetPoolVariant(declared);
            for (int b = 0; b < declared; b++)
                m_props.SetBuffer(k_splatPool[b], splatPools[b]);
            m_props.SetInteger(k_splatBufferCap, bufferSplatCapacity);
            m_props.SetBuffer(k_chunkSlotTable, slotTable);
            m_props.SetBuffer(k_orderBuffer, orderBuffer);
            m_props.SetInteger(k_splatCount, count);
            m_props.SetInteger(k_splatInstanceSize, SplatInstanceSize);
            m_props.SetInteger(k_gammaToLinear, gammaToLinear ? 1 : 0);
            m_props.SetFloat(k_kernelAlphaCutoff, KernelAlphaCutoff);
            m_props.SetFloat(k_minPixelRadius, MinPixelRadius);
            m_props.SetMatrix(k_matrixM, localToWorld);

            var rp = new RenderParams(m_material)
            {
                worldBounds = worldBounds,
                matProps = m_props,
                layer = layer,
                camera = camera,
            };
            Graphics.RenderMeshPrimitives(rp, m_mesh, 0,
                Mathf.CeilToInt(count / (float)SplatInstanceSize));
        }

        static readonly string[] k_poolKeywords = { "GSPLAT_POOLS_1", "GSPLAT_POOLS_2", "GSPLAT_POOLS_4" };

        void SetPoolVariant(int declared)
        {
            string want = declared == 1 ? k_poolKeywords[0]
                : declared == 2 ? k_poolKeywords[1] : k_poolKeywords[2];
            foreach (string kw in k_poolKeywords)
            {
                if (kw == want)
                    m_material.EnableKeyword(kw);
                else
                    m_material.DisableKeyword(kw);
            }
        }

        // Batched quad mesh: N quads per instance, vertex z carries the
        // in-batch splat index (same trick as gsplat-unity — SPI-safe because
        // it goes through regular mesh instancing).
        static Mesh CreateBatchQuadMesh(int batchSize)
        {
            var positions = new Vector3[4 * batchSize];
            var indices = new int[6 * batchSize];
            for (uint i = 0; i < (uint)batchSize; ++i)
            {
                float zBits;
                unsafe { zBits = *(float*)&i; }
                int b = (int)i * 4;
                positions[b + 0] = new Vector3(-1, -1, zBits);
                positions[b + 1] = new Vector3(1, -1, zBits);
                positions[b + 2] = new Vector3(-1, 1, zBits);
                positions[b + 3] = new Vector3(1, 1, zBits);
                int t = (int)i * 6;
                indices[t + 0] = b + 0;
                indices[t + 1] = b + 1;
                indices[t + 2] = b + 2;
                indices[t + 3] = b + 1;
                indices[t + 4] = b + 3;
                indices[t + 5] = b + 2;
            }

            return new Mesh
            {
                name = "GsplatLodBatchQuad",
                vertices = positions,
                triangles = indices,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        public void Dispose()
        {
            if (m_material)
                Object.DestroyImmediate(m_material);
            if (m_mesh)
                Object.DestroyImmediate(m_mesh);
        }
    }
}
