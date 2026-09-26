// Minimal programmatic uGUI builders for the XR setup panel. Kept separate so
// LodSetupPanel reads as layout rather than boilerplate. Everything is built at
// runtime (no prefabs) using TextMeshPro for text and the standard Slider part
// hierarchy so XRI ray/poke interaction works out of the box.

using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GsplatLod.XrUI
{
    static class UiFactory
    {
        public static readonly Color Panel = new(0.10f, 0.11f, 0.13f, 0.95f);
        public static readonly Color Row = new(0.16f, 0.17f, 0.20f, 1f);
        public static readonly Color Accent = new(0.20f, 0.55f, 0.95f, 1f);
        public static readonly Color AccentDim = new(0.22f, 0.28f, 0.38f, 1f);
        public static readonly Color Text = new(0.92f, 0.93f, 0.95f, 1f);

        // Attaches to any interactable; writes its help text into the shared
        // box on pointer hover (XRI ray hover fires this via XRUIInputModule).
        public sealed class PointerHelp : MonoBehaviour, IPointerEnterHandler
        {
            public string Text;
            public TMP_Text Target;
            public void OnPointerEnter(PointerEventData _)
            {
                if (Target) Target.text = Text;
            }
        }

        // Shows help on hover; also updates the box while dragging a slider.
        public static void AddHover(GameObject target, string help, TMP_Text helpLabel)
        {
            var ph = target.AddComponent<PointerHelp>();
            ph.Text = help;
            ph.Target = helpLabel;
        }

        public static void LayoutHeight(GameObject go, float h)
        {
            var le = go.GetComponent<LayoutElement>();
            if (!le) le = go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = h;
        }

        public static TextMeshProUGUI Header(Transform parent, string text, int size)
        {
            var t = Label(parent, text, size, TextAlignmentOptions.MidlineLeft);
            t.fontStyle = FontStyles.Bold;
            LayoutHeight(t.gameObject, size + 12);
            return t;
        }

        // Places transform in front of cam at a fixed height, facing it
        // (flattened to the horizontal plane). No-op without a camera.
        public static void PlaceInFront(Transform transform, Camera cam, float meters, float height)
        {
            if (!cam) return;
            var fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var pos = cam.transform.position + fwd * meters;
            pos.y = height;
            transform.position = pos;
            transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        // World-space canvas: author in UI units, scale down to meters. Adds
        // the background image and an outer VerticalLayoutGroup column ready
        // for children.
        public static GameObject BuildWorldCanvas(Transform parent, string name,
            float widthMeters, float canvasUnitsWidth, float spacing)
        {
            var canvasGO = Container(name, parent);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            canvasGO.AddComponent<TrackedDeviceGraphicRaycaster>();

            var canvasRt = Rect(canvasGO);
            float scale = widthMeters / canvasUnitsWidth;
            canvasRt.sizeDelta = new Vector2(canvasUnitsWidth, 1000f);
            canvasRt.localScale = new Vector3(scale, scale, scale);
            canvasRt.localPosition = Vector3.zero;
            canvasRt.localRotation = Quaternion.identity;

            Image(canvasGO, Panel);
            var col = canvasGO.AddComponent<VerticalLayoutGroup>();
            col.padding = new RectOffset(20, 20, 20, 20);
            col.spacing = spacing;
            col.childControlWidth = col.childControlHeight = true;
            col.childForceExpandWidth = true;
            col.childForceExpandHeight = false;
            col.childAlignment = TextAnchor.UpperCenter;
            canvasGO.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            return canvasGO;
        }

        // A vertical list column (file/browser lists): tight spacing, no
        // forced height so it sizes to its rows.
        public static VerticalLayoutGroup VerticalList(GameObject go, float spacing)
        {
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true;
            return v;
        }

        public static RectTransform Rect(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            return rt ? rt : go.AddComponent<RectTransform>();
        }

        public static GameObject Container(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Image Image(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        public static TextMeshProUGUI Label(Transform parent, string text, int size,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
        {
            var go = Container("Label", parent);
            var t = go.AddComponent<TextMeshProUGUI>();
            var f = JpFont();
            if (f) t.font = f;
            t.text = text;
            t.fontSize = size;
            t.color = Text;
            t.alignment = align;
            t.raycastTarget = false;
            return t;
        }

        // TMP's bundled LiberationSans has no CJK glyphs, so Japanese text spams
        // "glyph not found" every layout pass. Build a dynamic TMP font from an
        // OS font that covers Japanese; glyphs render on demand into an atlas.
        // Falls back to the default font if no such OS font is found.
        static TMP_FontAsset s_jpFont;
        static bool s_jpTried;
        static TMP_FontAsset JpFont()
        {
            if (s_jpTried) return s_jpFont;
            s_jpTried = true;
            string[] names =
            {
                "Noto Sans CJK JP", "NotoSansCJKjp", "Noto Sans JP",
                "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic",
                "Hiragino Sans", "Droid Sans Japanese", "sans-serif",
            };
            var os = Font.CreateDynamicFontFromOSFont(names, 36);
            if (os != null)
                s_jpFont = TMP_FontAsset.CreateFontAsset(os);
            return s_jpFont;
        }

        // A horizontal row with a fixed height, ready for children laid out by a
        // HorizontalLayoutGroup.
        public static GameObject Row2(Transform parent, float height, float spacing = 8f)
        {
            var go = Container("Row", parent);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;
            return go;
        }

        public static Button Button(Transform parent, string label, int fontSize,
            Color bg, Action onClick, float flexWidth = 1f)
        {
            var go = Container("Button", parent);
            Image(go, bg);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = Color.Lerp(bg, Color.white, 0.25f);
            colors.pressedColor = Color.Lerp(bg, Color.black, 0.2f);
            colors.selectedColor = bg;
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = flexWidth;

            var t = Label(go.transform, label, fontSize, TextAlignmentOptions.Center);
            Stretch(Rect(t.gameObject));
            return btn;
        }

        // Numeric text box. TMP_InputField needs the viewport/text pair wired by
        // hand when built without a prefab; the caret is added by TMP itself once
        // textViewport is assigned.
        public static TMP_InputField NumberField(Transform parent, string value, int fontSize,
            Action<string> onSubmit, float flexWidth = 1f)
        {
            var go = Container("Input", parent);
            Image(go, Row);
            var input = go.AddComponent<TMP_InputField>();
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = flexWidth;

            var area = Container("Text Area", go.transform);
            area.AddComponent<RectMask2D>();
            var areaRt = Rect(area);
            Stretch(areaRt);
            areaRt.offsetMin = new Vector2(8f, 2f);
            areaRt.offsetMax = new Vector2(-8f, -2f);

            var text = Label(area.transform, value, fontSize, TextAlignmentOptions.MidlineRight);
            text.enableWordWrapping = false;
            Stretch(Rect(text.gameObject));

            input.targetGraphic = go.GetComponent<Image>();
            input.textViewport = areaRt;
            input.textComponent = text;
            input.fontAsset = text.font;
            input.pointSize = fontSize;
            input.contentType = TMP_InputField.ContentType.DecimalNumber;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.caretColor = Text;
            input.selectionColor = new Color(0.20f, 0.55f, 0.95f, 0.5f);
            input.SetTextWithoutNotify(value);
#if !UNITY_EDITOR
            // On device there is no reachable system keyboard for a world-space
            // canvas, and TMP would still try to open one. Read-only keeps the
            // field selectable (so onSelect can raise the panel's own keypad)
            // without that attempt; the keypad writes the text in code.
            input.readOnly = true;
#endif
            if (onSubmit != null)
            {
                input.onEndEdit.AddListener(s => onSubmit(s));
                input.onSubmit.AddListener(s => onSubmit(s));
            }
            return input;
        }

        // Standard Slider hierarchy (Background / Fill Area+Fill / Handle).
        public static Slider Slider(Transform parent, float min, float max, float value,
            bool wholeNumbers, Action<float> onChanged)
        {
            var go = Container("Slider", parent);
            var slider = go.AddComponent<Slider>();
            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            var bg = Container("Background", go.transform);
            Image(bg, AccentDim);
            Stretch(Rect(bg), 0f, 0.35f, 0f, 0.65f);

            var fillArea = Container("Fill Area", go.transform);
            Stretch(Rect(fillArea), 0f, 0.35f, 0f, 0.65f);
            var fill = Container("Fill", fillArea.transform);
            Image(fill, Accent);
            var fillRt = Rect(fill);
            fillRt.anchorMin = new Vector2(0, 0);
            fillRt.anchorMax = new Vector2(0, 1);
            fillRt.sizeDelta = new Vector2(10, 0);

            var handleArea = Container("Handle Slide Area", go.transform);
            var haRt = Rect(handleArea);
            haRt.anchorMin = new Vector2(0, 0);
            haRt.anchorMax = new Vector2(1, 1);
            haRt.offsetMin = haRt.offsetMax = Vector2.zero;
            var handle = Container("Handle", handleArea.transform);
            Image(handle, Color.white);
            var hRt = Rect(handle);
            hRt.sizeDelta = new Vector2(20, 0);

            slider.fillRect = fillRt;
            slider.handleRect = hRt;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.value = value;
            if (onChanged != null) slider.onValueChanged.AddListener(v => onChanged(v));
            return slider;
        }

        public static void Stretch(RectTransform rt,
            float xMin = 0f, float yMin = 0f, float xMax = 1f, float yMax = 1f)
        {
            rt.anchorMin = new Vector2(xMin, yMin);
            rt.anchorMax = new Vector2(xMax, yMax);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }
}
