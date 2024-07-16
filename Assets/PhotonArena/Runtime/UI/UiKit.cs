using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PhotonArena.UI
{
    /// <summary>
    /// Builds uGUI from code. The whole UI is created at runtime, so the scene holds no UI objects to break, and
    /// there is no TextMeshPro import step (legacy Text with the built-in font).
    /// </summary>
    public static class UiKit
    {
        public static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.11f, 0.92f);
        public static readonly Color CardColor = new Color(0.12f, 0.13f, 0.17f, 1f);
        public static readonly Color Accent = new Color(0.98f, 0.62f, 0.16f, 1f);
        public static readonly Color AccentDim = new Color(0.35f, 0.26f, 0.12f, 1f);
        public static readonly Color ButtonColor = new Color(0.20f, 0.22f, 0.28f, 1f);
        public static readonly Color TextColor = new Color(0.93f, 0.94f, 0.96f, 1f);
        public static readonly Color MutedText = new Color(0.62f, 0.65f, 0.72f, 1f);
        public static readonly Color ErrorText = new Color(1f, 0.45f, 0.42f, 1f);
        public static readonly Color GoodText = new Color(0.45f, 0.9f, 0.55f, 1f);

        static Font _font;

        public static Font Font
        {
            get
            {
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _font;
            }
        }

        public static Canvas CreateCanvas(Transform parent)
        {
            var go = new GameObject("ArenaUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;

            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(parent, false);
            }
            return canvas;
        }

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Anchor(this RectTransform rt, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            return rt;
        }

        public static RectTransform Fill(this RectTransform rt) => rt.Anchor(Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        /// <summary>A box anchored at a corner/edge: pivot+anchor at <paramref name="anchor"/>, offset by <paramref name="position"/>.</summary>
        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color color)
        {
            RectTransform rt = Rect(parent, name);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        public static VerticalLayoutGroup Column(Component target, int padding = 16, float spacing = 8)
        {
            var layout = target.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static HorizontalLayoutGroup Row(Transform parent, float height, float spacing = 6, string name = "Row")
        {
            RectTransform rt = Rect(parent, name);
            var layout = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            Size(rt, height: height);
            return layout;
        }

        public static LayoutElement Size(Component c, float height = -1, float width = -1, float flexibleWidth = -1)
        {
            LayoutElement le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            if (height >= 0)
            {
                le.preferredHeight = height;
                le.minHeight = height;
            }
            if (width >= 0)
            {
                le.preferredWidth = width;
                le.minWidth = width;
            }
            if (flexibleWidth >= 0) le.flexibleWidth = flexibleWidth;
            return le;
        }

        public static Text Label(Transform parent, string text, int size = 16, TextAnchor align = TextAnchor.MiddleLeft, Color? color = null, FontStyle style = FontStyle.Normal)
        {
            RectTransform rt = Rect(parent, "Label");
            var label = rt.gameObject.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.alignment = align;
            label.color = color ?? TextColor;
            label.raycastTarget = false;
            label.supportRichText = true;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }

        /// <summary>Text with a soft shadow, for HUD text drawn over the 3D scene.</summary>
        public static Text HudLabel(Transform parent, string text, int size, TextAnchor align)
        {
            Text label = Label(parent, text, size, align, TextColor, FontStyle.Bold);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            var shadow = label.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.75f);
            shadow.effectDistance = new Vector2(2, -2);
            return label;
        }

        public static Button Button(Transform parent, string text, Action onClick, int fontSize = 16)
        {
            Image bg = Panel(parent, "Button", ButtonColor);
            var button = bg.gameObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            button.targetGraphic = bg;
            Text label = Label(bg.transform, text, fontSize, TextAnchor.MiddleCenter, TextColor, FontStyle.Bold);
            label.rectTransform.Fill();
            if (onClick != null) button.onClick.AddListener(() => onClick());
            return button;
        }

        public static void SetLabel(this Button button, string text)
        {
            Text label = button.GetComponentInChildren<Text>();
            if (label != null && label.text != text) label.text = text;
        }

        /// <summary>Marks a button as the selected option of a toggle group (accent color) or not.</summary>
        public static void SetSelected(this Button button, bool selected)
        {
            var image = (Image)button.targetGraphic;
            Color target = selected ? Accent : ButtonColor;
            if (image.color != target) image.color = target;
            Text label = button.GetComponentInChildren<Text>();
            if (label != null) label.color = selected ? new Color(0.1f, 0.08f, 0.05f, 1f) : TextColor;
        }

        public static InputField Input(Transform parent, string placeholder, string value, int characterLimit = 32)
        {
            Image bg = Panel(parent, "Input", new Color(0.05f, 0.06f, 0.08f, 1f));
            var input = bg.gameObject.AddComponent<InputField>();
            input.characterLimit = characterLimit;

            Text text = Label(bg.transform, "", 16, TextAnchor.MiddleLeft, TextColor);
            text.supportRichText = false;
            text.rectTransform.Fill().offsetMin = new Vector2(10, 2);
            text.rectTransform.offsetMax = new Vector2(-10, -2);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;

            Text hint = Label(bg.transform, placeholder, 16, TextAnchor.MiddleLeft, MutedText, FontStyle.Italic);
            hint.rectTransform.Fill().offsetMin = new Vector2(10, 2);
            hint.rectTransform.offsetMax = new Vector2(-10, -2);

            input.textComponent = text;
            input.placeholder = hint;
            input.targetGraphic = bg;
            input.text = value ?? "";
            return input;
        }

        /// <summary>A scroll view with a vertical list as its content.</summary>
        public static RectTransform ScrollList(Transform parent, out ScrollRect scroll)
        {
            Image bg = Panel(parent, "Scroll", new Color(0.05f, 0.06f, 0.08f, 0.8f));
            scroll = bg.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            RectTransform viewport = Rect(bg.transform, "Viewport").Fill();
            viewport.gameObject.AddComponent<RectMask2D>();
            scroll.viewport = viewport;

            RectTransform content = Rect(viewport, "Content");
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            VerticalLayoutGroup layout = Column(content, 6, 4);
            layout.childForceExpandHeight = false;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = content;
            return content;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        public static void SetText(this Text label, string text)
        {
            if (label.text != text) label.text = text;
        }

        public static void SetActive(this Component c, bool active)
        {
            if (c.gameObject.activeSelf != active) c.gameObject.SetActive(active);
        }

        /// <summary>Escapes player-chosen names before they go into rich text.</summary>
        public static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<​");
    }
}


    public static object ThrottleRequests(object data = null)
    {
        if (data == null) return null;
        return data;
    }

    public static object ThrottleRequests(object data = null)
    {
        if (data == null) return null;
        return data;
    }