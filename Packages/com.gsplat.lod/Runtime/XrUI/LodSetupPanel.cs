// World-space XR setup panel for the Start scene. Builds a uGUI canvas at
// runtime that the user operates with the existing XRI ray/poke interactors
// (the Start scene's EventSystem already runs XRUIInputModule). Lets the user
// pick a .usst file, tune the LodStreamingConfig live and spawn a
// LodStreamingDriver — no flat-screen overlay, no scene-baked UI prefab.
//
// Config edits mutate the assigned asset. Runtime knobs (KernelAlphaCutoff,
// MinPixelRadius, fovea priority weighting, thresholds) apply to a loaded
// driver immediately; FoveatedRenderingLevel and EyeResolutionScale are XR
// display settings applied once at Load, not live; BudgetSplats/PoolPages
// are read at driver init, so changing them takes effect on the next Load.

using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GsplatLod.XrUI
{
    public sealed class LodSetupPanel : MonoBehaviour
    {
        [Tooltip("Config asset edited by the panel and handed to the spawned driver")]
        public LodStreamingConfig Config;

        [Tooltip("Also show the settled non-LoD knobs (pool, upload, fill-rate, XR). Off = only the LoD knobs under tuning")]
        public bool ShowAdvancedParams;

        [Tooltip("Splat shader; auto-resolves to GsplatLod/Splat when empty")]
        public Shader SplatShader;

        [Tooltip("Selected .usst path (absolute, or relative to persistentDataPath)")]
        public string UsstPath = "";

        [Tooltip("Extra directory scanned for .usst files (absolute). persistentDataPath / StreamingAssets / StreamingData are always scanned.")]
        public string ExtraScanDirectory = "";

        [Tooltip("On Start, place the panel this far in front of the main camera. 0 = keep the authored transform.")]
        public float PlaceInFrontMeters = 1.2f;

        [Tooltip("World height (meters from the floor) the panel center is placed at")]
        public float PanelHeightMeters = 1.2f;

        [Tooltip("Physical width of the panel in meters (height follows the layout)")]
        public float PanelWidthMeters = 1.7f;

        [Header("Loaded splat transform")]
        [Tooltip("World position applied to the loaded splat object (live-editable)")]
        public Vector3 SplatPosition = Vector3.zero;

        [Tooltip("Euler rotation (degrees) applied to the loaded splat object")]
        public Vector3 SplatEuler = Vector3.zero;

        [Tooltip("Uniform scale applied to the loaded splat object")]
        public float SplatScale = 1f;

        [Tooltip("Mirror the loaded splat on X (negative X scale). Some captures are exported mirrored.")]
        public bool FlipX = false;

        // curated tunables shown as slider + number box (label, min, max, whole, get, set)
        struct Param
        {
            public string Label;
            public float Min, Max;
            public bool Whole;
            public Func<float> Get;
            public Action<float> Set;
            public string Help;
            /// <summary>Settled non-LoD knob: hidden unless ShowAdvancedParams.</summary>
            public bool Advanced;
        }

        const int k_maxFilesShown = 5;
        const float k_canvasUnitsWidth = 1240f;   // UI-space width; scaled to meters

        GameObject m_loaded;
        readonly List<string> m_found = new();
        TextMeshProUGUI m_fileLabel;
        TextMeshProUGUI m_statusLabel;
        TextMeshProUGUI m_helpLabel;
        Transform m_fileListParent;
        Button m_flipButton;
        TextMeshProUGUI m_presetLabel;
        GameObject m_keypad;
        TMP_InputField m_keypadTarget;
        TextMeshProUGUI m_keypadTitle;
        string m_keypadBuf = "";
        string m_keypadLabel = "";
        // Pushes model values back into the widgets after a preset changes them
        // behind the UI's back.
        readonly List<Action> m_refreshers = new();

        const string k_helpHint = "各項目にポインタを合わせると、ここに説明が表示されます。";

        /// <summary>True while any XR pointer is hovering the panel. Locomotion
        /// (XrealTouchpadLocomotion) suppresses movement then, so pointing at the
        /// panel means UI operation and pointing away means travel.</summary>
        public bool PointerOverPanel => m_hover != null && m_hover.PointerCount > 0;

        HoverTracker m_hover;

        // Sits on the canvas background, which spans the whole panel. Hovering a
        // child widget does not fire exit on an ancestor still under the pointer,
        // so this reports panel-wide hover. Counts pointer ids because several
        // interactors (ray / poke / hands) can hover independently.
        sealed class HoverTracker : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            readonly HashSet<int> m_ids = new();
            public int PointerCount => m_ids.Count;
            public void OnPointerEnter(PointerEventData e) => m_ids.Add(e.pointerId);
            public void OnPointerExit(PointerEventData e) => m_ids.Remove(e.pointerId);
            void OnDisable() => m_ids.Clear();
        }

        void Start()
        {
            if (!SplatShader) SplatShader = Shader.Find("GsplatLod/Splat");
            BuildCanvas();
            Rescan();
            // A splat may already be in the scene (DemoScene ships one) — adopt it
            // so the panel describes what is actually on screen. Only when there
            // is nothing to adopt do we fall back to the first scanned file.
            bool adopted = AdoptSceneSplat();
            if (!adopted && string.IsNullOrEmpty(UsstPath) && m_found.Count > 0)
                UsstPath = m_found[0];

            SelectFile(UsstPath);   // shows the saved preset in the widgets before Load
            if (adopted && TransformPresetStore.Find(UsstPath) == null)
                RefreshTransformUi();   // no preset: show the scene object's own transform
            RebuildFileList();      // repaint so the selected entry is highlighted
            if (PlaceInFrontMeters > 0f)
                PlaceInFront();
        }

        void PlaceInFront() =>
            UiFactory.PlaceInFront(transform, Camera.main, PlaceInFrontMeters, PanelHeightMeters);

        void BuildCanvas()
        {
            var canvasGO = UiFactory.BuildWorldCanvas(transform, "SetupCanvas",
                PanelWidthMeters, k_canvasUnitsWidth, spacing: 8f);
            m_hover = canvasGO.AddComponent<HoverTracker>();
            Transform root = canvasGO.transform;

            UiFactory.Header(root, "Gsplat LoD — Setup", 30);

            // Shared help box (created early so controls can target it; moved to
            // the bottom below). Updates on pointer hover of any control.
            m_helpLabel = UiFactory.Label(root, k_helpHint, 16);
            m_helpLabel.color = new Color(0.72f, 0.80f, 0.90f, 1f);
            UiFactory.LayoutHeight(m_helpLabel.gameObject, 72);

            // Three columns keep the panel wide and short enough to operate at
            // arm's length: 1 = file select, 2 = transform, 3 = actions + the two
            // collapsed sections.
            var body = UiFactory.Container("Body", root);
            var bodyH = body.AddComponent<HorizontalLayoutGroup>();
            bodyH.spacing = 20;
            bodyH.childControlWidth = bodyH.childControlHeight = true;
            bodyH.childForceExpandWidth = true;
            bodyH.childForceExpandHeight = false;
            bodyH.childAlignment = TextAnchor.UpperLeft;
            Transform fileSection = Column(body.transform);
            Transform xformSection = Column(body.transform);
            Transform right = Column(body.transform);

            // --- column 1: file select (always expanded) ---
            UiFactory.Header(fileSection, "1. Select .usst", 22);
            m_fileLabel = UiFactory.Label(fileSection, "(none selected)", 18);
            UiFactory.LayoutHeight(m_fileLabel.gameObject, 28);
#if UNITY_EDITOR
            // Native dialogs only exist in the Editor; on device the scanned
            // list below is the only way to pick a file.
            var pickRow = UiFactory.Row2(fileSection, 44);
            UiFactory.Button(pickRow.transform, "Pick .usst...", 18, UiFactory.Row, PickUsstFile);
            UiFactory.Button(pickRow.transform, "Browse folder...", 18, UiFactory.Row, BrowseFolder);
#endif
            m_fileListParent = UiFactory.Container("FileList", fileSection).transform;
            UiFactory.VerticalList(m_fileListParent.gameObject, spacing: 4f);
            var rescanRow = UiFactory.Row2(fileSection, 44);
            UiFactory.Button(rescanRow.transform, "Rescan folders", 18, UiFactory.Row, Rescan);

            // --- column 2: transform (always expanded) ---
            UiFactory.Header(xformSection, "2. Transform (live)", 22);
            foreach (var p in BuildTransformParams())
                AddParamRow(xformSection, p);
            var flipRow = UiFactory.Row2(xformSection, 40);
            m_flipButton = UiFactory.Button(flipRow.transform, FlipLabel(), 18,
                FlipX ? UiFactory.Accent : UiFactory.Row, ToggleFlipX);
            AddHover(m_flipButton.gameObject,
                "X軸ミラー反転。データによって鏡像で書き出されている場合に左右を戻す（スケールXを負にする）");
            AddHover(UiFactory.Button(flipRow.transform, "Reset transform", 18, UiFactory.Row, ResetTransform).gameObject,
                "位置と回転を0、スケールを1に戻す。ミラー反転（FlipX）は変更しない");

            var presetRow = UiFactory.Row2(xformSection, 40);
            AddHover(UiFactory.Button(presetRow.transform, "Save preset", 18, UiFactory.Row, SavePreset).gameObject,
                "現在のTransformをこの.usst専用プリセットとして保存。次回このファイルをLoadすると自動適用される");
            AddHover(UiFactory.Button(presetRow.transform, "ClearPreset", 18, UiFactory.Row, ClearPreset).gameObject,
                "この.usstのプリセットを削除。以降のLoadでは現在のTransformがそのまま使われる");
            m_presetLabel = UiFactory.Label(xformSection, "", 15);
            UiFactory.LayoutHeight(m_presetLabel.gameObject, 24);

            // --- column 3: actions + collapsed sections ---
            var actionRow = UiFactory.Row2(right, 56);
            UiFactory.Button(actionRow.transform, "Load", 24, UiFactory.Accent, OnLoad, 2f);
            UiFactory.Button(actionRow.transform, "Reset", 20, UiFactory.Row, Unload, 1f);
            // Always shown, even without the native plugin: ConvertPanel itself
            // explains when the plugin is missing, and gating the button here
            // would leave that scene unreachable from the panel.
            AddHover(UiFactory.Button(actionRow.transform, "変換", 20, UiFactory.Row,
                    () => UnityEngine.SceneManagement.SceneManager.LoadScene("ConvertScene"), 1f).gameObject,
                ".ply等を.usst/.uscに変換する画面へ移動する。変換後は「戻る」でこの画面に帰る");
            m_statusLabel = UiFactory.Label(right, "", 18);
            UiFactory.LayoutHeight(m_statusLabel.gameObject, 28);

            var configSection = Collapsible(right, "3. Config (live)", false);
            if (Config)
                foreach (var p in BuildParams())
                {
                    if (p.Advanced && !ShowAdvancedParams)
                        continue;
                    AddParamRow(configSection, p);
                }
            else
                UiFactory.Label(configSection, "<color=#ff8080>No Config assigned</color>", 18);

            BuildKeypad(root);
            m_helpLabel.transform.SetAsLastSibling();   // keep the help box at the bottom
        }

        // A vertical sub-column inside the three-column body.
        static Transform Column(Transform parent)
        {
            var go = UiFactory.Container("Column", parent);
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = 6;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            v.childAlignment = TextAnchor.UpperCenter;
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return go.transform;
        }

        // Label + slider + editable number box. Typed or preset values outside the
        // authored range widen the slider instead of being clamped — otherwise the
        // handle and the number disagree, which reads as "the value changed".
        void AddParamRow(Transform parent, Param p)
        {
            var row = UiFactory.Row2(parent, 42);
            var label = UiFactory.Label(row.transform, p.Label, 16);
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.4f;

            Slider slider = null;
            TMP_InputField field = null;
            bool applying = false;   // guards slider <-> field feedback

            void SetSlider(float v)
            {
                if (!slider) return;
                if (v < slider.minValue) slider.minValue = v;
                if (v > slider.maxValue) slider.maxValue = v;
                slider.value = v;
            }

            field = UiFactory.NumberField(row.transform, Fmt(p.Get(), p.Whole), 16, s =>
            {
                if (applying) return;
                field.SetTextWithoutNotify(Fmt(p.Get(), p.Whole));   // commit: normalize
            }, 1.1f);
            field.onSelect.AddListener(_ => OpenKeypad(field, p.Label));
            // Apply while typing (no Enter key on the Quest keyboard); the text
            // itself is left alone so partial input like "-" or "1." survives.
            field.onValueChanged.AddListener(s =>
            {
                if (applying || !float.TryParse(s, out float v)) return;
                if (p.Whole) v = Mathf.Round(v);
                applying = true;
                p.Set(v);
                SetSlider(v);
                applying = false;
                MarkConfigDirty();
            });
            AddHover(field.gameObject, p.Help);

            slider = UiFactory.Slider(row.transform, p.Min, p.Max, Mathf.Clamp(p.Get(), p.Min, p.Max),
                p.Whole, v =>
                {
                    if (applying) return;
                    applying = true;
                    p.Set(v);
                    field.SetTextWithoutNotify(Fmt(v, p.Whole));
                    applying = false;
                    MarkConfigDirty();
                });
            slider.GetComponent<LayoutElement>().flexibleWidth = 2f;
            AddHover(slider.gameObject, p.Help);

            m_refreshers.Add(() =>
            {
                applying = true;
                field.SetTextWithoutNotify(Fmt(p.Get(), p.Whole));
                SetSlider(p.Get());
                applying = false;
            });
        }

        IEnumerable<Param> BuildTransformParams()
        {
            const string posHelp = "ロード済みモデルのワールド位置(m)。原点付近で見当たらない時にずらす";
            const string rotHelp = "ロード済みモデルの回転(度)。3DGSは上下反転していることが多く、X=180などで補正";
            yield return new Param { Label = "Pos X", Min = -10f, Max = 10f, Whole = false,
                Get = () => SplatPosition.x, Set = v => { SplatPosition.x = v; ApplyTransform(); }, Help = posHelp };
            yield return new Param { Label = "Pos Y", Min = -10f, Max = 10f, Whole = false,
                Get = () => SplatPosition.y, Set = v => { SplatPosition.y = v; ApplyTransform(); }, Help = posHelp };
            yield return new Param { Label = "Pos Z", Min = -10f, Max = 10f, Whole = false,
                Get = () => SplatPosition.z, Set = v => { SplatPosition.z = v; ApplyTransform(); }, Help = posHelp };
            yield return new Param { Label = "Rot X", Min = -180f, Max = 180f, Whole = false,
                Get = () => SplatEuler.x, Set = v => { SplatEuler.x = v; ApplyTransform(); }, Help = rotHelp };
            yield return new Param { Label = "Rot Y", Min = -180f, Max = 180f, Whole = false,
                Get = () => SplatEuler.y, Set = v => { SplatEuler.y = v; ApplyTransform(); }, Help = rotHelp };
            yield return new Param { Label = "Rot Z", Min = -180f, Max = 180f, Whole = false,
                Get = () => SplatEuler.z, Set = v => { SplatEuler.z = v; ApplyTransform(); }, Help = rotHelp };
            yield return new Param { Label = "Scale", Min = 0.01f, Max = 10f, Whole = false,
                Get = () => SplatScale, Set = v => { SplatScale = v; ApplyTransform(); },
                Help = "ロード済みモデルの一様スケール。大きすぎ/小さすぎる時に調整" };
        }

        // --- on-panel numeric keypad ---
        //
        // A world-space canvas has no usable system keyboard on the headset, so
        // tapping a number box raises this keypad instead. Keys write straight
        // into the target field, whose onValueChanged applies the value live.

        void BuildKeypad(Transform root)
        {
            var go = UiFactory.Container("Keypad", root);
            UiFactory.Image(go, UiFactory.Panel);
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(10, 10, 10, 10);
            v.spacing = 6;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;

            m_keypadTitle = UiFactory.Label(go.transform, "", 20, TextAlignmentOptions.Center);
            UiFactory.LayoutHeight(m_keypadTitle.gameObject, 30);

            string[][] keys =
            {
                new[] { "7", "8", "9", "←" },
                new[] { "4", "5", "6", "C" },
                new[] { "1", "2", "3", "±" },
                new[] { "0", ".", "OK", "Close" },
            };
            foreach (var rowKeys in keys)
            {
                var row = UiFactory.Row2(go.transform, 52);
                foreach (var k in rowKeys)
                {
                    string key = k;
                    UiFactory.Button(row.transform, key, 22,
                        key == "OK" ? UiFactory.Accent : UiFactory.Row, () => KeypadPress(key));
                }
            }
            go.SetActive(false);
            m_keypad = go;
        }

        void OpenKeypad(TMP_InputField field, string label)
        {
            m_keypadTarget = field;
            m_keypadLabel = label;
            m_keypadBuf = field.text;
            if (m_keypad) m_keypad.SetActive(true);
            UpdateKeypadTitle();
        }

        void KeypadPress(string k)
        {
            if (m_keypadTarget == null) { CloseKeypad(); return; }
            switch (k)
            {
                case "OK":
                case "Close":
                    m_keypadTarget.onEndEdit.Invoke(m_keypadTarget.text);   // normalize
                    CloseKeypad();
                    return;
                case "←":
                    if (m_keypadBuf.Length > 0)
                        m_keypadBuf = m_keypadBuf.Substring(0, m_keypadBuf.Length - 1);
                    break;
                case "C":
                    m_keypadBuf = "";
                    break;
                case "±":
                    m_keypadBuf = m_keypadBuf.StartsWith("-")
                        ? m_keypadBuf.Substring(1) : "-" + m_keypadBuf;
                    break;
                default:
                    m_keypadBuf += k;
                    break;
            }
            UpdateKeypadTitle();
            m_keypadTarget.text = m_keypadBuf;   // fires onValueChanged → live apply
        }

        void UpdateKeypadTitle()
        {
            if (m_keypadTitle)
                m_keypadTitle.text =
                    $"{m_keypadLabel} = {(m_keypadBuf.Length == 0 ? "_" : m_keypadBuf)}";
        }

        void CloseKeypad()
        {
            m_keypadTarget = null;
            if (m_keypad) m_keypad.SetActive(false);
            if (EventSystem.current) EventSystem.current.SetSelectedGameObject(null);
        }

        // Header button that shows/hides the returned content container.
        Transform Collapsible(Transform parent, string title, bool expanded)
        {
            var headerRow = UiFactory.Row2(parent, 40);
            var content = UiFactory.Container($"{title} Content", parent);
            var v = content.AddComponent<VerticalLayoutGroup>();
            v.spacing = 6;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            content.SetActive(expanded);

            TMP_Text caption = null;
            var btn = UiFactory.Button(headerRow.transform, Arrow(expanded) + title, 20, UiFactory.Row, () =>
            {
                bool now = !content.activeSelf;
                content.SetActive(now);
                if (caption) caption.text = Arrow(now) + title;
            });
            caption = btn.GetComponentInChildren<TMP_Text>();
            return content.transform;
        }

        static string Arrow(bool expanded) => expanded ? "▼ " : "▶ ";

        string FlipLabel() => FlipX ? "Flip X: ON (mirrored)" : "Flip X: OFF";

        void ToggleFlipX()
        {
            FlipX = !FlipX;
            RefreshTransformUi();
            ApplyTransform();
        }

        void AddHover(GameObject target, string help) =>
            UiFactory.AddHover(target, help, m_helpLabel);

        static string Fmt(float v, bool whole) =>
            whole ? Mathf.RoundToInt(v).ToString("N0") : v.ToString("0.###");

        IEnumerable<Param> BuildParams()
        {
            var c = Config;
            yield return new Param { Label = "BudgetSplats", Min = 50_000, Max = 4_000_000, Whole = true,
                Get = () => c.BudgetSplats, Set = v => c.BudgetSplats = (int)v,
                Help = "画面に描画する最大スプラット数（フロンティア）。大きいほど高精細だが重い。Quest実効レバーはほぼこれ。目安15万〜25万。※変更はReset→Loadで反映" };
            yield return new Param { Label = "LodTargetPx", Min = 0f, Max = 8f, Whole = false,
                Get = () => c.LodTargetPixelSize, Set = v => c.LodTargetPixelSize = v,
                Help = "各ノードを画面上この大きさ(px)まで一様に精細化する（Spark方式の均一な間引き）。閾値とBudgetSplatsのどちらか先に達した方で展開が止まる。モバイル（Quest）では通常Budgetが先に飽和するため、実際に効くのはBudgetになる。0で無効（予算のみ）。即反映" };
            yield return new Param { Label = "PoolPages", Min = 0, Max = 256, Whole = true,
                Get = () => c.PoolPages, Set = v => c.PoolPages = (int)v,
                Help = "GPUに常駐させるチャンク数（1ページ=65,536スプラット）。0=自動（シーンが収まれば全常駐、収まらなければモバイル128／デスクトップ256ページ）。通常は0のままでよく、A/B比較のときだけ手動指定する。上限は端末のバッファ制限×4本（Quest3で最大256ページ≒16.8M）。※変更はReset→Loadで反映", Advanced = true };
            yield return new Param { Label = "OrderUploadKB", Min = 256f, Max = 8192f, Whole = true,
                Get = () => c.MaxOrderUploadBytesPerFrame / 1024f, Set = v => c.MaxOrderUploadBytesPerFrame = (int)v * 1024,
                Help = "描画順バッファの1フレームあたり転送量(KB)。転送中は次のスライス/ソートが走らないため、小さいとカット更新が遅れて「段階的に精細化する」挙動になる。大きくするとメインスレッドのSetDataが集中してヒッチしやすくなる。既定1024KB=26万要素/フレーム。即反映", Advanced = true };
            yield return new Param { Label = "SliceMinFrames", Min = 1f, Max = 10f, Whole = true,
                Get = () => c.SliceMinIntervalFrames, Set = v => c.SliceMinIntervalFrames = (int)v,
                Help = "スライス再計算の最小間隔(フレーム)。小さいほどカット更新が速く「段階的に精細化する」挙動が減るが、CPUのスライス負荷が増える。既定3。即反映", Advanced = true };
            yield return new Param { Label = "KernelAlphaCutoff", Min = 1f / 255f, Max = 0.1f, Whole = false,
                Get = () => c.KernelAlphaCutoff, Set = v => c.KernelAlphaCutoff = v,
                Help = "ガウシアンの薄い裾をカットする閾値。大きいほど各スプラットの矩形が小さくなりGPU塗り負荷が減る（Quest目安0.02〜0.04）。上げすぎると輪郭が硬くなる", Advanced = true };
            yield return new Param { Label = "MinPixelRadius", Min = 0.5f, Max = 4f, Whole = false,
                Get = () => c.MinPixelRadius, Set = v => c.MinPixelRadius = v,
                Help = "画面上でこの半径(px)未満のスプラットは描画を省く。大きいほど軽いが細部が消える。1.0=従来の2px直径相当、Questは1.5〜2.0が目安", Advanced = true };
            yield return new Param { Label = "DistanceFalloffPow", Min = 0.5f, Max = 1.5f, Whole = false,
                Get = () => c.DistanceFalloffPower, Set = v => c.DistanceFalloffPower = v,
                Help = "距離による精細度の落とし方。1.0=画面上の大きさどおり。大きくすると遠方を粗くして近景に予算を回す。屋外など予算が足りない場面では上げると中距離が読みやすくなる" };
            yield return new Param { Label = "OutOfFrustum", Min = 0.005f, Max = 1f, Whole = false,
                Get = () => c.OutOfFrustumFactor, Set = v => c.OutOfFrustumFactor = v,
                Help = "視界の外に出たノードの優先度の倍率。既定0.1=1/10に減衰させるため視界外は粗いまま保持され、首を振ると振り向いた先が段階的に精細化して見える。1.0にすると視界外も同じ精度で先読みするのでポップは消えるが、見えていない物に予算を使うぶん見えている側が粗くなる。即反映" };
            yield return new Param { Label = "FoveaInner(deg)", Min = 0f, Max = 90f, Whole = false,
                Get = () => c.FoveaInnerDegrees, Set = v => c.FoveaInnerDegrees = v,
                Help = "視界中心からこの角度(度)以内はフル精細を優先。中心視野の広さ" };
            yield return new Param { Label = "FoveaOuter(deg)", Min = 0f, Max = 120f, Whole = false,
                Get = () => c.FoveaOuterDegrees, Set = v => c.FoveaOuterDegrees = v,
                Help = "この角度(度)まで精細度が徐々にPeripheryFactorまで低下する。周辺視野の減衰終端" };
            yield return new Param { Label = "PeripheryFactor", Min = 0.05f, Max = 1f, Whole = false,
                Get = () => c.PeripheryFactor, Set = v => c.PeripheryFactor = v,
                Help = "視界周辺での精細度倍率。小さいほど周辺を粗くして中心に予算を回す。1.0=フォビエーション無効" };
            yield return new Param { Label = "FoveatedRender", Min = 0f, Max = 1f, Whole = false,
                Get = () => c.FoveatedRenderingLevel, Set = v => c.FoveatedRenderingLevel = v,
                Help = "起動時の固定フォビエーション描画レベル(0=off,1=最大)。周辺の描画解像度を落としGPU負荷減。OpenXRのFoveated Rendering機能が必要（実機のみ）", Advanced = true };
            yield return new Param { Label = "EyeResScale", Min = 0.5f, Max = 2f, Whole = false,
                Get = () => c.EyeResolutionScale, Set = v => c.EyeResolutionScale = v,
                Help = "目バッファの解像度倍率(1=標準)。上げるとスプラットが鮮明になるがフラグメント負荷増（頂点数は不変）。Questは1.2〜1.4が目安", Advanced = true };
            yield return new Param { Label = "StatsLogSec", Min = 0f, Max = 10f, Whole = false,
                Get = () => c.StatsLogIntervalSeconds, Set = v => c.StatsLogIntervalSeconds = v,
                Help = "0より大きいとこの秒間隔でLoDの状態をログ出力する（adb logcat -s Unity）。実機でHUDが見えないVR環境用の計測手段。0=出力しない。即反映" };
        }

        // --- presets ---

        // Takes over a splat that was already in the scene at startup: its file,
        // its mode and its current transform become the panel's state, so the
        // controls act on the thing the user is looking at.
        bool AdoptSceneSplat()
        {
            var drv = FindAnyObjectByType<LodStreamingDriver>();
            var go = drv ? drv.gameObject : null;
            if (!go) return false;

            m_loaded = go;
            UsstPath = ResolveScanned(drv.UsstPath);

            var t = go.transform;
            SplatPosition = t.position;
            var e = t.rotation.eulerAngles;
            SplatEuler = new Vector3(Wrap180(e.x), Wrap180(e.y), Wrap180(e.z));
            SplatScale = Mathf.Abs(t.localScale.y);
            FlipX = t.localScale.x < 0f;

            // Adoption takes over a driver that was already in the scene, so it
            // keeps its own Config reference — the panel's sliders are dead
            // unless that is the very same asset instance.
            if (drv && drv.Config != Config)
                Debug.LogWarning($"[GsplatLod] panel Config ({(Config ? Config.GetInstanceID().ToString() : "none")}) " +
                                 $"!= adopted driver Config ({(drv.Config ? drv.Config.GetInstanceID().ToString() : "none")}) " +
                                 "— panel edits will not reach this driver");

            Debug.Log($"[GsplatLod] adopted scene splat '{go.name}' path='{UsstPath}' " +
                      $"panelCfg={(Config ? Config.GetInstanceID() : 0)} drvCfg={(drv && drv.Config ? drv.Config.GetInstanceID() : 0)}");
            return true;
        }

        static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;

        // Driver paths may be relative to persistentDataPath; map them onto a
        // scanned absolute path so the file list can highlight the right entry.
        string ResolveScanned(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (File.Exists(raw)) return Path.GetFullPath(raw);
            string norm = raw.Replace('\\', '/');
            foreach (var f in m_found)
                if (f.Replace('\\', '/').EndsWith(norm, StringComparison.OrdinalIgnoreCase))
                    return f;
            return raw;
        }

        // Selecting a file immediately pulls its saved transform into the panel,
        // so the numbers on screen always describe the file about to be loaded.
        void SelectFile(string path)
        {
            UsstPath = path;
            if (m_fileLabel)
                m_fileLabel.text = string.IsNullOrEmpty(path)
                    ? "(none selected)"
                    // parent folder + name: two captures often share a file name
                    : $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";
            if (!string.IsNullOrEmpty(path))
                ApplyPreset(quiet: true);
            UpdatePresetLabel();
        }

        void SavePreset()
        {
            if (string.IsNullOrEmpty(UsstPath))
            {
                SetStatus("<color=#ff8080>Select a .usst first</color>");
                return;
            }
            TransformPresetStore.Save(UsstPath, SplatPosition, SplatEuler, SplatScale, FlipX);
            UpdatePresetLabel();
            SetStatus($"Preset saved for {Path.GetFileName(UsstPath)}");
        }

        // Back to identity: position and rotation 0, uniform scale 1. FlipX is a
        // property of the capture, not of the placement, so it is left alone.
        void ResetTransform()
        {
            SplatPosition = Vector3.zero;
            SplatEuler = Vector3.zero;
            SplatScale = 1f;
            RefreshTransformUi();
            ApplyTransform();
            SetStatus("Transform reset (pos 0, rot 0, scale 1)");
        }

        void ClearPreset()
        {
            if (string.IsNullOrEmpty(UsstPath)) return;
            bool removed = TransformPresetStore.Delete(UsstPath);
            UpdatePresetLabel();
            SetStatus(removed ? "Preset cleared" : "No preset for this file");
        }

        // Applies the stored preset for the selected file. Returns false when
        // there is none (the current panel transform is then used as-is).
        // quiet: called before anything is loaded (startup / file pick), where a
        // missing splat object is expected and must not raise a status warning.
        bool ApplyPreset(bool quiet = false)
        {
            var p = TransformPresetStore.Find(UsstPath);
            if (p == null)
                return false;
            Debug.Log($"[GsplatLod] preset applied '{p.Key}' pos={p.Position} euler={p.Euler} " +
                      $"scale={p.Scale} flipX={p.FlipX}");
            SplatPosition = p.Position;
            SplatEuler = p.Euler;
            SplatScale = p.Scale;
            FlipX = p.FlipX;
            RefreshTransformUi();
            ApplyTransform(quiet);
            return true;
        }

        void RefreshTransformUi()
        {
            foreach (var r in m_refreshers) r();
            if (m_flipButton)
            {
                m_flipButton.GetComponent<Image>().color = FlipX ? UiFactory.Accent : UiFactory.Row;
                var t = m_flipButton.GetComponentInChildren<TMP_Text>();
                if (t) t.text = FlipLabel();
            }
        }

        void UpdatePresetLabel()
        {
            if (!m_presetLabel) return;
            if (string.IsNullOrEmpty(UsstPath))
            {
                m_presetLabel.text = "";
                return;
            }
            var p = TransformPresetStore.Find(UsstPath);
            m_presetLabel.text = p != null
                ? $"<color=#80d080>preset: pos({p.Position.x:0.##},{p.Position.y:0.##},{p.Position.z:0.##}) " +
                  $"rot({p.Euler.x:0.#},{p.Euler.y:0.#},{p.Euler.z:0.#}) s{p.Scale:0.###}</color>"
                : "<color=#a0a0a0>preset: none</color>";
        }

        void MarkConfigDirty()
        {
#if UNITY_EDITOR
            if (Config) UnityEditor.EditorUtility.SetDirty(Config);
#endif
            // The slice is gated on viewpoint movement, and tuning a slider means
            // standing still — without this kick the edit has no visible effect
            // until the user happens to move past SliceMoveThresholdMeters or
            // turn past SliceRotateThresholdDegrees. Runs on every drag frame,
            // so it goes through m_loaded rather than a scene search.
            if (m_loaded)
            {
                var drv = m_loaded.GetComponent<LodStreamingDriver>();
                if (drv && drv.Config == Config)
                    drv.RequestReslice();
            }
        }

        // --- file scanning ---

#if UNITY_EDITOR
        void PickUsstFile()
        {
            string start = string.IsNullOrEmpty(ExtraScanDirectory)
                ? Application.persistentDataPath : ExtraScanDirectory;
            string p = UnityEditor.EditorUtility.OpenFilePanel("Select .usst", start, "usst");
            if (string.IsNullOrEmpty(p)) return;
            SelectFile(p);
            SetStatus($"Selected {Path.GetFileName(p)}");
        }

        void BrowseFolder()
        {
            string start = string.IsNullOrEmpty(ExtraScanDirectory)
                ? Application.persistentDataPath : ExtraScanDirectory;
            string p = UnityEditor.EditorUtility.OpenFolderPanel(
                "Select folder to scan for .usst", start, "");
            if (string.IsNullOrEmpty(p)) return;
            ExtraScanDirectory = p;
            Rescan();
            SetStatus($"Scanned {p}");
        }
#endif

        void Rescan()
        {
            m_found.Clear();
            var seen = new HashSet<string>();
            foreach (var dir in ScanDirs())
                ScanDir(dir, seen);
            RebuildFileList();
        }

        IEnumerable<string> ScanDirs()
        {
            yield return Application.persistentDataPath;
            yield return Application.streamingAssetsPath;
            yield return Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? "", "StreamingData");
            if (!string.IsNullOrEmpty(ExtraScanDirectory))
                yield return ExtraScanDirectory;
        }

        void ScanDir(string dir, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var p in Directory.EnumerateFiles(dir, "*.usst", SearchOption.AllDirectories))
                    if (seen.Add(p)) m_found.Add(p);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GsplatLod] scan '{dir}' failed: {e.Message}");
            }
        }

        void RebuildFileList()
        {
            if (m_fileListParent == null) return;
            for (int i = m_fileListParent.childCount - 1; i >= 0; i--)
                Destroy(m_fileListParent.GetChild(i).gameObject);

            if (m_found.Count == 0)
            {
                var none = UiFactory.Label(m_fileListParent, "(no .usst found — check scan folders)", 16);
                UiFactory.LayoutHeight(none.gameObject, 26);
                return;
            }
            int shown = Mathf.Min(m_found.Count, k_maxFilesShown);
            for (int i = 0; i < shown; i++)
            {
                string path = m_found[i];
                var row = UiFactory.Row2(m_fileListParent, 36);
                bool selected = path == UsstPath;
                UiFactory.Button(row.transform,
                    $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}", 16,
                    selected ? UiFactory.Accent : UiFactory.Row,
                    () => { SelectFile(path); RebuildFileList(); });
            }
            if (m_found.Count > shown)
            {
                var more = UiFactory.Label(m_fileListParent, $"+{m_found.Count - shown} more", 14);
                UiFactory.LayoutHeight(more.gameObject, 22);
            }
        }

        // --- spawn ---

        void OnLoad()
        {
            if (string.IsNullOrEmpty(UsstPath))
            {
                SetStatus("<color=#ff8080>Select a .usst first</color>");
                return;
            }
            if (!Config)
            {
                SetStatus("<color=#ff8080>No Config assigned</color>");
                return;
            }

            Unload();
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            if (!SplatShader) SplatShader = Shader.Find("GsplatLod/Splat");

            var go = new GameObject($"GsplatLod_{Path.GetFileNameWithoutExtension(UsstPath)}");
            // Configure while inactive: the component reads UsstPath/Config in
            // OnEnable, which would otherwise fire during AddComponent (before we
            // assign the fields) and skip initialization → nothing renders.
            go.SetActive(false);
            var driver = go.AddComponent<LodStreamingDriver>();
            driver.UsstPath = UsstPath;
            driver.Config = Config;
            driver.SplatShader = SplatShader;
            driver.GammaToLinear = linear;
            go.AddComponent<LodDebugHud>();
            m_loaded = go;
            bool hasPreset = ApplyPreset();   // per-capture transform, if saved
            ApplyTransform();
            go.SetActive(true);   // now OnEnable runs (synchronously) with fields set

            var drv = go.GetComponent<LodStreamingDriver>();
            if (drv != null && !string.IsNullOrEmpty(drv.LoadError))
            {
                SetStatus($"<color=#ff8080>Load failed: {drv.LoadError}</color>");
                return;
            }

            SetStatus($"Loaded {Path.GetFileName(UsstPath)}"
                      + (hasPreset ? " — preset applied" : ""));
            UpdatePresetLabel();
        }

        // Removes the panel's own spawn AND any splat object already present in
        // the scene, so Load always swaps in a single active display.
        void Unload()
        {
            foreach (var d in FindObjectsByType<LodStreamingDriver>(FindObjectsSortMode.None))
                Destroy(d.gameObject);
            m_loaded = null;
        }

        // Pushes the panel's transform fields onto the loaded splat object. The
        // target is re-resolved when m_loaded is gone (destroyed, domain reload,
        // or a splat spawned by something other than this panel) — otherwise the
        // controls silently do nothing.
        void ApplyTransform(bool quiet = false)
        {
            if (m_loaded == null)
            {
                var drv = FindAnyObjectByType<LodStreamingDriver>();
                if (drv) m_loaded = drv.gameObject;
                if (m_loaded == null)
                {
                    if (!quiet) SetStatus("<color=#ffc060>Transform: nothing loaded yet</color>");
                    return;
                }
            }
            var t = m_loaded.transform;
            t.position = SplatPosition;
            t.rotation = Quaternion.Euler(SplatEuler);
            t.localScale = new Vector3(FlipX ? -SplatScale : SplatScale, SplatScale, SplatScale);
        }

        void SetStatus(string s)
        {
            if (m_statusLabel) m_statusLabel.text = s;
        }
    }
}
