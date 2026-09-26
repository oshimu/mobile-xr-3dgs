// World-space XR panel for converting a capture (.ply/.spz/.splat/.ksplat/.sog/.zip)
// into the runtime .usst/.usc/.manifest.json triple, via the native gsplat_convert
// plugin (see Packages/com.gsplat.lod/Runtime/Convert/SplatConverter.cs). Lives in
// its own scene (ConvertScene) so it never has to share a canvas with
// LodSetupPanel — that three-column layout has no room left, and cramming a
// converter section into it broke the layout on device.
//
// Deliberately a single vertical column: no side-by-side sections, so nothing
// here can starve another column's width like the setup panel's did.
//
// Input selection has two sources:
//   - SplatFilePicker (Android/Quest only): Android's standard Storage Access
//     Framework document picker (ACTION_OPEN_DOCUMENT), confirmed on-device
//     to render and respond to input inside the headset. No permission is
//     needed (see SplatFilePicker.cs).
//   - A persistentDataPath file list (TopDirectoryOnly, no permission
//     needed at all) kept as a fallback for files pushed via `adb push`, and
//     as the only source in the Editor/other platforms where SplatFilePicker
//     is not supported.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GsplatLod.XrUI
{
    public sealed class ConvertPanel : MonoBehaviour
    {
        [Tooltip("Scene to return to via the Back button")]
        public string BackSceneName = "DemoScene";

        [Tooltip("On Start, place the panel this far in front of the main camera. 0 = keep the authored transform.")]
        public float PlaceInFrontMeters = 1.2f;

        [Tooltip("World height (meters from the floor) the panel center is placed at")]
        public float PanelHeightMeters = 1.2f;

        [Tooltip("Physical width of the panel in meters (height follows the layout)")]
        public float PanelWidthMeters = 1.0f;

        const int k_maxItemsShown = 8;
        const int k_maxLogLinesShown = 8;
        const int k_maxPathChars = 48;
        const float k_canvasUnitsWidth = 760f;   // UI-space width; scaled to meters

        const string k_helpHint = "各項目にポインタを合わせると、ここに説明が表示されます。";

        readonly List<string> m_fallbackFiles = new();

        // Current selection: either a picked fd (SplatFilePicker) or a path
        // from the persistentDataPath fallback list. Never both.
        bool m_fdSelected;
        int m_selFd = -1;
        bool m_fdConsumed;   // true once m_selFd was handed to a ConvertJob
        long m_selSize = -1;
        string m_selDisplayName = "";
        string m_selPath = "";

        bool m_pickPending;
        bool m_quality;
        bool m_cancelRequested;
        bool m_doneHandled = true;

        // Set by the "warning無視して続行" button after CheckInputSize(ByLength)
        // rejected the current selection. Consumed (and cleared) by the next
        // OnConvertOrCancel() call, which then proceeds despite the guard.
        bool m_ignoreMemoryWarning;

        ConvertJob m_job;

        TextMeshProUGUI m_fileLabel;
        TextMeshProUGUI m_statusLabel;
        TextMeshProUGUI m_outputLabel;
        TextMeshProUGUI m_logLabel;
        TextMeshProUGUI m_helpLabel;
        Transform m_browserListParent;
        Button m_convertButton;
        Button m_backButton;
        Button m_rescanButton;
        Button m_qualityButton;
        Button m_pickButton;
        Button m_ignoreWarningButton;

        void Start()
        {
            BuildCanvas();
            RefreshFallbackList();
            RefreshButtons();
            if (PlaceInFrontMeters > 0f)
                PlaceInFront();
        }

        void Update()
        {
            PollPicker();

            if (m_job == null || m_doneHandled) return;

            m_job.Poll();
            RefreshLogDisplay();

            if (!m_job.IsDone)
            {
                // Cancellation is cooperative: keep the fixed "cancelling"
                // status until the job actually finishes, so it isn't
                // clobbered by whatever log line arrives in between.
                if (!m_cancelRequested && !string.IsNullOrEmpty(m_job.LastLogLine))
                    SetStatus(m_job.LastLogLine);
                return;
            }

            m_doneHandled = true;
            bool wasCancelled = m_cancelRequested;
            m_cancelRequested = false;
            RefreshButtons();

            if (m_job.Succeeded)
            {
                SetStatus($"完了: {Path.GetFileName(m_job.OutputUsstPath)}");
                if (m_outputLabel) m_outputLabel.text = $"出力: {m_job.OutputUsstPath}";
            }
            else
            {
                SetStatus(wasCancelled
                    ? "キャンセルしました"
                    : $"<color=#ff8080>失敗: {m_job.Error}</color>");
            }
        }

        void OnDestroy()
        {
            m_job?.Dispose();
            // A picked-but-never-converted fd would otherwise stay open for
            // the lifetime of the process (Java already detached it).
            if (m_fdSelected && !m_fdConsumed)
                SplatFilePicker.CloseFd(m_selFd);
        }

        void PlaceInFront() =>
            UiFactory.PlaceInFront(transform, Camera.main, PlaceInFrontMeters, PanelHeightMeters);

        void BuildCanvas()
        {
            var canvasGO = UiFactory.BuildWorldCanvas(transform, "ConvertCanvas",
                PanelWidthMeters, k_canvasUnitsWidth, spacing: 10f);
            Transform root = canvasGO.transform;

            UiFactory.Header(root, "Gsplat LoD — Convert", 30);

            m_helpLabel = UiFactory.Label(root, k_helpHint, 16);
            m_helpLabel.color = new Color(0.72f, 0.80f, 0.90f, 1f);
            UiFactory.LayoutHeight(m_helpLabel.gameObject, 56);

            if (!SplatConverter.IsAvailable)
            {
                var warn = UiFactory.Label(root,
                    "<color=#ff8080>変換ネイティブプラグインが利用できません（このビルドには含まれていません）</color>", 18);
                UiFactory.LayoutHeight(warn.gameObject, 48);
            }

            UiFactory.Header(root, "1. 入力ファイル", 22);

            if (SplatFilePicker.IsSupported)
            {
                var pickRow = UiFactory.Row2(root, 44);
                m_pickButton = UiFactory.Button(pickRow.transform, "ファイルを選ぶ", 18, UiFactory.Row, OnPickFile);
                AddHover(m_pickButton.gameObject,
                    "Android標準のファイルピッカーを開く（クラウドストレージ含む、端末内の任意の場所から選択できる）");
            }

            UiFactory.Header(root, SplatFilePicker.IsSupported
                ? $"{Application.persistentDataPath} 内のファイル（adb push 等）"
                : "対象ファイル一覧", 16);

            m_browserListParent = UiFactory.Container("BrowserList", root).transform;
            UiFactory.VerticalList(m_browserListParent.gameObject, spacing: 4f);

            m_fileLabel = UiFactory.Label(root, "(none selected)", 18);
            UiFactory.LayoutHeight(m_fileLabel.gameObject, 28);

            var rescanRow = UiFactory.Row2(root, 44);
            m_rescanButton = UiFactory.Button(rescanRow.transform, "再読込", 18, UiFactory.Row, RefreshFallbackList);
            AddHover(m_rescanButton.gameObject, "上の一覧を読み直す");

            UiFactory.Header(root, "2. オプション", 22);
            var qualityRow = UiFactory.Row2(root, 40);
            m_qualityButton = UiFactory.Button(qualityRow.transform, QualityLabel(), 18,
                m_quality ? UiFactory.Accent : UiFactory.Row, ToggleQuality);
            AddHover(m_qualityButton.gameObject,
                "品質優先(bhatt-lod)を使う。既定はtiny-lod（高速・軽量）。ONにすると変換時間とメモリ使用量が増える");

            UiFactory.Header(root, "3. 実行", 22);
            var actionRow = UiFactory.Row2(root, 56);
            m_convertButton = UiFactory.Button(actionRow.transform, "Convert", 24, UiFactory.Accent, OnConvertOrCancel, 2f);
            m_backButton = UiFactory.Button(actionRow.transform, "戻る", 20, UiFactory.Row, OnBack, 1f);
            if (!SplatConverter.IsAvailable)
                m_convertButton.gameObject.SetActive(false);

            var warningRow = UiFactory.Row2(root, 44);
            m_ignoreWarningButton = UiFactory.Button(warningRow.transform, "警告を無視して続行", 18, UiFactory.Row, OnIgnoreWarning);
            AddHover(m_ignoreWarningButton.gameObject,
                "メモリ見積もりガードを無視して変換を続行する。見積もりが外れて実際にメモリ不足になった場合、" +
                "ネイティブ側のOOM abortでアプリが即座に強制終了する（C#側では例外として捕捉できず、ログも残らない）。");
            m_ignoreWarningButton.gameObject.SetActive(false);

            m_statusLabel = UiFactory.Label(root, "", 18);
            UiFactory.LayoutHeight(m_statusLabel.gameObject, 28);

            m_outputLabel = UiFactory.Label(root, "", 16);
            UiFactory.LayoutHeight(m_outputLabel.gameObject, 24);

            UiFactory.Header(root, "ログ", 20);
            m_logLabel = UiFactory.Label(root, "", 14);
            UiFactory.LayoutHeight(m_logLabel.gameObject, 16 * k_maxLogLinesShown + 8);

            m_helpLabel.transform.SetAsLastSibling();   // keep the help box at the bottom
        }

        void AddHover(GameObject target, string help) =>
            UiFactory.AddHover(target, help, m_helpLabel);

        // --- options ---

        string QualityLabel() => m_quality ? "Quality: High (bhatt-lod)" : "Quality: Fast (tiny-lod)";

        void ToggleQuality()
        {
            m_quality = !m_quality;
            if (m_qualityButton)
            {
                m_qualityButton.GetComponent<Image>().color = m_quality ? UiFactory.Accent : UiFactory.Row;
                var t = m_qualityButton.GetComponentInChildren<TMP_Text>();
                if (t) t.text = QualityLabel();
            }
        }

        // --- SAF file picker ---

        bool IsBusy() => (m_job != null && m_job.IsRunning) || m_pickPending;

        void OnPickFile()
        {
            if (IsBusy()) return;
            SplatFilePicker.Reset();
            SplatFilePicker.Pick();
            m_pickPending = true;
            SetStatus("ファイルを選択してください...");
        }

        void PollPicker()
        {
            if (!m_pickPending) return;

            PickState state = SplatFilePicker.Poll();
            switch (state)
            {
                case PickState.Success:
                    m_pickPending = false;
                    SelectPickedFile(SplatFilePicker.Fd, SplatFilePicker.DisplayName, SplatFilePicker.Size);
                    break;
                case PickState.Cancelled:
                    m_pickPending = false;
                    SetStatus("キャンセルしました");
                    break;
                case PickState.Error:
                    m_pickPending = false;
                    SetStatus($"<color=#ff8080>ファイル選択に失敗: {SplatFilePicker.Error}</color>");
                    break;
                default:
                    break; // still waiting
            }
        }

        void SelectPickedFile(int fd, string displayName, long size)
        {
            // Replacing an earlier picked-but-unconverted fd would otherwise
            // leak it (Java already detached it; nothing else will close it).
            if (m_fdSelected && !m_fdConsumed)
                SplatFilePicker.CloseFd(m_selFd);

            m_fdSelected = true;
            m_fdConsumed = false;
            m_selFd = fd;
            m_selDisplayName = displayName;
            m_selSize = size;
            m_selPath = "";
            m_ignoreMemoryWarning = false;
            ShowIgnoreWarningButton(false);

            string sizeText = size >= 0 ? $"{size / (1024f * 1024f):0.#} MB" : "サイズ不明";
            if (m_fileLabel) m_fileLabel.text = $"{ShortenPath(displayName)} ({sizeText})";
            SetStatus("選択しました");
            RebuildBrowserList();
        }

        // --- fallback file list ---
        // persistentDataPath, one directory level (TopDirectoryOnly), no
        // permission required. Input formats only (.usst is a runtime
        // output, not an input).

        void RefreshFallbackList()
        {
            m_fallbackFiles.Clear();
            try
            {
                foreach (var ext in SplatConverter.SupportedInputExtensions)
                    foreach (var f in Directory.EnumerateFiles(Application.persistentDataPath, "*." + ext, SearchOption.TopDirectoryOnly))
                        m_fallbackFiles.Add(f);
                m_fallbackFiles.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                SetStatus($"<color=#ff8080>一覧を取得できません: {e.Message}</color>");
            }

            RebuildBrowserList();
        }

        void SelectFallbackFile(string path)
        {
            if (IsBusy()) return;

            if (m_fdSelected && !m_fdConsumed)
                SplatFilePicker.CloseFd(m_selFd);

            m_fdSelected = false;
            m_selFd = -1;
            m_selDisplayName = "";
            m_selSize = -1;
            m_selPath = path;
            m_ignoreMemoryWarning = false;
            ShowIgnoreWarningButton(false);

            if (m_fileLabel)
                m_fileLabel.text = string.IsNullOrEmpty(path) ? "(none selected)" : ShortenPath(path);
            RebuildBrowserList();
        }

        static string ShortenPath(string p)
        {
            if (string.IsNullOrEmpty(p) || p.Length <= k_maxPathChars) return p;
            return "..." + p.Substring(p.Length - (k_maxPathChars - 3));
        }

        void RebuildBrowserList()
        {
            if (m_browserListParent == null) return;
            for (int i = m_browserListParent.childCount - 1; i >= 0; i--)
                Destroy(m_browserListParent.GetChild(i).gameObject);

            bool locked = IsBusy();

            if (m_fallbackFiles.Count == 0)
            {
                var none = UiFactory.Label(m_browserListParent, "(対象ファイルがありません)", 16);
                UiFactory.LayoutHeight(none.gameObject, 26);
            }

            int filesShown = Mathf.Min(m_fallbackFiles.Count, k_maxItemsShown);
            for (int i = 0; i < filesShown; i++)
            {
                string path = m_fallbackFiles[i];
                var row = UiFactory.Row2(m_browserListParent, 36);
                bool selected = !m_fdSelected && path == m_selPath;
                var btn = UiFactory.Button(row.transform, Path.GetFileName(path), 16,
                    selected ? UiFactory.Accent : UiFactory.Row,
                    () => SelectFallbackFile(path));
                btn.interactable = !locked;
            }
            if (m_fallbackFiles.Count > filesShown)
            {
                var more = UiFactory.Label(m_browserListParent, $"+{m_fallbackFiles.Count - filesShown} 件", 14);
                UiFactory.LayoutHeight(more.gameObject, 22);
            }
        }

        // --- convert ---

        void OnConvertOrCancel()
        {
            if (m_job != null && m_job.IsRunning)
            {
                m_job.Cancel();
                m_cancelRequested = true;
                SetStatus("キャンセル中...");
                return;
            }

            if (!m_fdSelected && string.IsNullOrEmpty(m_selPath))
            {
                SetStatus("<color=#ff8080>入力ファイルを選択してください</color>");
                return;
            }

            var options = new ConvertOptions
            {
                outDir = Application.persistentDataPath,
                quality = m_quality,
                force = true,
            };

            bool ok;
            string warning;
            if (m_fdSelected)
            {
                ok = SplatConverter.CheckInputSizeByLength(m_selSize, m_selDisplayName, SplatFilePicker.HeaderBytes, m_quality, out warning);
                options.inputFd = m_selFd;
                options.displayName = m_selDisplayName;
                options.name = Path.GetFileNameWithoutExtension(m_selDisplayName);
            }
            else
            {
                ok = SplatConverter.CheckInputSize(m_selPath, m_quality, out warning);
                options.input = m_selPath;
                options.name = Path.GetFileNameWithoutExtension(m_selPath);
            }

            if (!ok && !m_ignoreMemoryWarning)
            {
                // Left as-is (not closed/cleared): the selection stays valid
                // so a follow-up "警告を無視して続行" click can retry this
                // same conversion without re-picking the file.
                SetStatus($"<color=#ff8080>{warning}</color>");
                ShowIgnoreWarningButton(true);
                return;
            }
            ShowIgnoreWarningButton(false);

            if (!ok && m_ignoreMemoryWarning)
            {
                Debug.LogWarning($"[GsplatLod] converting despite memory guard rejection (user override): {warning}");
                warning = $"警告を無視して続行: {warning}";
            }
            m_ignoreMemoryWarning = false;

            m_job?.Dispose();
            m_job = new ConvertJob();
            m_cancelRequested = false;
            m_doneHandled = false;
            if (m_outputLabel) m_outputLabel.text = "";
            if (m_logLabel) m_logLabel.text = "";

            bool started = m_job.Start(options);
            if (m_fdSelected) m_fdConsumed = true;   // fd ownership now belongs to the native pipeline
            RefreshButtons();

            string status = started ? "変換中..." : $"<color=#ff8080>失敗: {m_job.Error}</color>";
            if (started && !string.IsNullOrEmpty(warning))
                status = $"<color=#ffcf80>{warning}</color>";
            SetStatus(status);
            if (!started) m_doneHandled = true;
        }

        void OnBack()
        {
            if (m_job != null && m_job.IsRunning) return;   // guarded by m_backButton.interactable too
            SceneManager.LoadScene(BackSceneName);
        }

        void OnIgnoreWarning()
        {
            m_ignoreMemoryWarning = true;
            ShowIgnoreWarningButton(false);
            OnConvertOrCancel();
        }

        void ShowIgnoreWarningButton(bool show)
        {
            if (m_ignoreWarningButton) m_ignoreWarningButton.gameObject.SetActive(show);
        }

        void RefreshButtons()
        {
            bool running = m_job != null && m_job.IsRunning;

            var label = m_convertButton.GetComponentInChildren<TMP_Text>();
            if (label) label.text = running ? "Cancel" : "Convert";
            m_convertButton.GetComponent<Image>().color = running ? UiFactory.Row : UiFactory.Accent;

            if (m_backButton) m_backButton.interactable = !running;
            if (m_rescanButton) m_rescanButton.interactable = !running;
            if (m_qualityButton) m_qualityButton.interactable = !running;
            if (m_pickButton) m_pickButton.interactable = !running;
            if (m_ignoreWarningButton) m_ignoreWarningButton.interactable = !running;
        }

        void RefreshLogDisplay()
        {
            if (m_logLabel == null || m_job == null) return;
            var log = m_job.AllLog;
            int start = Mathf.Max(0, log.Count - k_maxLogLinesShown);
            var sb = new StringBuilder();
            for (int i = start; i < log.Count; i++)
                sb.AppendLine(log[i]);
            m_logLabel.text = sb.ToString();
        }

        void SetStatus(string s)
        {
            if (m_statusLabel) m_statusLabel.text = s;
        }
    }
}
