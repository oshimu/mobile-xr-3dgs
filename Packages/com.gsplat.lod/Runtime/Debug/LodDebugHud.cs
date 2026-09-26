// Debug HUD (spec §7.9). OnGUI overlay for Editor/flat builds; on-device the
// same text can be routed to a world-space TextMesh via TargetText.

using System.Text;
using UnityEngine;

namespace GsplatLod
{
    [RequireComponent(typeof(LodStreamingDriver))]
    public sealed class LodDebugHud : MonoBehaviour
    {
        [Tooltip("Optional world-space text target (legacy TextMesh) for VR")]
        public TextMesh TargetText;

        public bool ShowOverlay = true;

        LodStreamingDriver m_driver;
        readonly StringBuilder m_sb = new(512);
        float m_smoothedDt;
        string m_text = "";
        float m_nextRefresh;

        void Awake() => m_driver = GetComponent<LodStreamingDriver>();

        void Update()
        {
            m_smoothedDt = Mathf.Lerp(m_smoothedDt <= 0 ? Time.unscaledDeltaTime : m_smoothedDt,
                Time.unscaledDeltaTime, 0.05f);

            if (Time.unscaledTime < m_nextRefresh)
                return;
            m_nextRefresh = Time.unscaledTime + 0.25f;

            var stats = m_driver.StreamerStats;
            m_sb.Clear();
            m_sb.AppendLine($"fps {1f / Mathf.Max(m_smoothedDt, 1e-6f):F1} ({m_smoothedDt * 1000f:F2} ms)");
            m_sb.AppendLine($"frontier cost {m_driver.FrontierCost:N0} / visible {m_driver.VisibleCount:N0}");
            m_sb.AppendLine($"lod target {m_driver.CurrentLodTargetPixel:F2} px");
            if (m_driver.FullResidency)
            {
                m_sb.AppendLine($"pool {m_driver.ResidentPages}/{m_driver.PoolPages} pages " +
                                $"(full residency), required {m_driver.RequiredChunkCount}");
            }
            else
            {
                // freeable 0 means every resident page backs the current draw or
                // the pending slice, so incoming chunks have to wait a frame.
                m_sb.AppendLine($"pool {m_driver.ResidentPages}/{m_driver.PoolPages} pages, " +
                                $"freeable {m_driver.FreeablePages}, " +
                                $"required {m_driver.RequiredChunkCount}" +
                                (m_driver.RequiredChunkCount > m_driver.PoolPages
                                    ? " (> pool! raise PoolPages or lower BudgetSplats)"
                                    : ""));
            }
            m_sb.AppendLine($"load queue {stats.QueueLength} / loaded {stats.LoadedChunks} " +
                            $"({stats.BytesLoaded / (1024f * 1024f):F1} MB)");
            m_sb.AppendLine(m_driver.LastSliceMs >= 0
                ? $"slice {m_driver.LastSliceMs:F2} ms (sync)"
                : "slice n/a (enable MeasureSliceSync to measure)");
            m_sb.AppendLine($"upload {stats.UploadedBytesLastFrame / 1024} KB/frame");
            m_sb.AppendLine($"fallback items {m_driver.FallbackItems}");
            if (stats.PermanentlyMissing > 0)
                m_sb.AppendLine($"MISSING CHUNKS {stats.PermanentlyMissing}");
            m_text = m_sb.ToString();

            if (TargetText)
                TargetText.text = m_text;
        }

        void OnGUI()
        {
            if (!ShowOverlay)
                return;
            GUI.Box(new Rect(8, 8, 320, 150), GUIContent.none);
            GUI.Label(new Rect(16, 12, 312, 146), m_text);
        }
    }
}
