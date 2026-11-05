using System;
using System.IO;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Builds only the supplied settings window; the desktop owns open/close navigation.</summary>
    public static class PcSettingsBuilder
    {
        const string ArtPath = "Assets/_Project/Art/UI/CounselingPack/settings/";

        public static PcSettingsView Build(RectTransform desktop, TMP_FontAsset font)
        {
            if (desktop == null || font == null) throw new ArgumentException("A desktop and Korean font are required.");
            Transform existing = desktop.Find("PcSettingsWindow");
            if (existing != null)
            {
                PcSettingsView existingView = existing.GetComponent<PcSettingsView>();
                if (existingView == null)
                    throw new InvalidOperationException("PcSettingsWindow exists without its settings view; preserve and reconnect it before installing.");
                ApplySliderGeometry(existingView);
                return existingView;
            }
            var layout = JsonUtility.FromJson<SettingsLayout>(File.ReadAllText(ArtPath + "layout.json"));
            if (layout?.selectors?.Length != 2 || layout.sliders?.Length != 3 || layout.buttons == null)
                throw new InvalidOperationException("Settings layout must contain two selectors and three sliders.");
            Sprite panel = LoadSprite("settings-panel.png");
            Sprite fill = LoadSprite("slider-fill.png");
            Sprite thumb = LoadSprite("slider-thumb.png");

            var root = Rect("PcSettingsWindow", desktop);
            Place(root, 260, 92, 1000, 510);
            root.gameObject.SetActive(false);
            root.gameObject.AddComponent<Image>().sprite = panel;
            var view = root.gameObject.AddComponent<PcSettingsView>();

            foreach (SelectorLayout selector in layout.selectors)
            {
                TMP_Dropdown dropdown = Dropdown(root, selector.id, selector.bounds, font);
                if (selector.id == "resolution") view.resolutionDropdown = dropdown;
                else if (selector.id == "screen-mode") view.modeDropdown = dropdown;
                else throw new InvalidOperationException("Unsupported settings selector: " + selector.id);
            }

            foreach (SliderLayout slider in layout.sliders)
            {
                Slider control = VolumeSlider(root, slider, fill, thumb);
                TMP_Text percentage = Text(slider.id + "Percent", root, font,
                    slider.percent_baseline[0] - 90, slider.percent_baseline[1] - 22, 90, 27, 17,
                    TextAlignmentOptions.MidlineRight, "—");
                percentage.color = new Color32(76, 103, 94, 255);
                switch (slider.id)
                {
                    case "master": view.masterSlider = control; view.masterPercent = percentage; break;
                    case "music": view.musicSlider = control; view.musicPercent = percentage; break;
                    case "effects": view.effectsSlider = control; view.effectsPercent = percentage; break;
                    default: throw new InvalidOperationException("Unsupported settings slider: " + slider.id);
                }
            }

            view.applyButton = HitButton("ApplySettings", root, layout.buttons.apply);
            view.closeButton = HitButton("CloseSettings", root, layout.buttons.title_close);
            view.secondaryCloseButton = HitButton("CloseSettingsFooter", root, layout.buttons.close);
            return view;
        }

        private static TMP_Dropdown Dropdown(RectTransform parent, string name, PixelRect bounds, TMP_FontAsset font)
        {
            RectTransform rect = Rect(name + "Dropdown", parent);
            Place(rect, bounds.x, bounds.y, bounds.width, bounds.height);
            Image target = TransparentImage(rect);
            TMP_Dropdown dropdown = rect.gameObject.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = target;
            dropdown.colors = OverlayColors();
            // Keep the list alive through the input frame so Cancel cannot also close the PC.
            dropdown.alphaFadeSpeed = 0.12f;
            dropdown.captionText = Text("Value", rect, font, 16, 3, bounds.width - 62, bounds.height - 6,
                20, TextAlignmentOptions.MidlineLeft, "—");

            RectTransform template = Rect("Template", rect);
            template.anchorMin = new Vector2(0, 0);
            template.anchorMax = new Vector2(1, 0);
            template.pivot = new Vector2(0.5f, 1);
            template.anchoredPosition = new Vector2(0, -3);
            template.sizeDelta = new Vector2(0, 172);
            template.gameObject.AddComponent<Image>().color = new Color32(240, 242, 227, 255);
            Canvas popupCanvas = template.gameObject.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            popupCanvas.sortingOrder = 30000;
            template.gameObject.AddComponent<GraphicRaycaster>();
            template.gameObject.AddComponent<CanvasGroup>();
            ScrollRect scroll = template.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            scroll.scrollSensitivity = 26;

            RectTransform viewport = Rect("Viewport", template);
            Stretch(viewport);
            viewport.offsetMin = new Vector2(2, 2);
            viewport.offsetMax = new Vector2(-2, -2);
            viewport.gameObject.AddComponent<RectMask2D>();
            RectTransform content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.sizeDelta = new Vector2(0, 40);
            content.anchoredPosition = Vector2.zero;
            scroll.viewport = viewport;
            scroll.content = content;

            RectTransform item = Rect("Item", content);
            item.anchorMin = new Vector2(0, 0.5f);
            item.anchorMax = new Vector2(1, 0.5f);
            item.sizeDelta = new Vector2(0, 40);
            item.anchoredPosition = Vector2.zero;
            Image itemBackground = item.gameObject.AddComponent<Image>();
            Toggle toggle = item.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = itemBackground;
            ColorBlock colors = toggle.colors;
            colors.normalColor = new Color32(240, 242, 227, 255);
            colors.highlightedColor = colors.selectedColor = new Color32(194, 212, 212, 255);
            colors.pressedColor = new Color32(164, 191, 194, 255);
            toggle.colors = colors;
            RectTransform check = Rect("Selected", item);
            Place(check, 7, 14, 12, 12);
            Image checkImage = check.gameObject.AddComponent<Image>();
            checkImage.color = new Color32(76, 103, 94, 255);
            checkImage.raycastTarget = false;
            toggle.graphic = checkImage;
            toggle.isOn = false;
            dropdown.itemText = Text("ItemText", item, font, 28, 0, bounds.width - 40, 40,
                20, TextAlignmentOptions.MidlineLeft, "선택");
            dropdown.template = template;
            dropdown.AddOptions(new System.Collections.Generic.List<string> { "—" });
            template.gameObject.SetActive(false);
            return dropdown;
        }

        private static Slider VolumeSlider(RectTransform parent, SliderLayout layout, Sprite fillSprite, Sprite thumbSprite)
        {
            PixelRect bounds = layout.bounds;
            RectTransform root = Rect(layout.id + "Slider", parent);
            Place(root, bounds.x, bounds.y, bounds.width, bounds.height);
            TransparentImage(root).color = new Color(1, 1, 1, 0);
            Slider slider = root.gameObject.AddComponent<Slider>();
            slider.minValue = 0;
            slider.maxValue = 100;
            slider.wholeNumbers = true;
            slider.direction = Slider.Direction.LeftToRight;

            float start = layout.track_start[0] - bounds.x;
            float trackWidth = layout.track_end[0] - layout.track_start[0];
            RectTransform fillArea = Rect("FillArea", root);
            Place(fillArea, start, layout.fill_top - bounds.y, trackWidth, layout.fill_height);
            RectTransform fill = Rect("Fill", fillArea);
            Stretch(fill);
            Image fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.sprite = fillSprite;
            fillImage.raycastTarget = false;
            slider.fillRect = fill;

            RectTransform handleArea = Rect("HandleArea", root);
            RectTransform handle = Rect("Handle", handleArea);
            Image handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.sprite = thumbSprite;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            SetHandleGeometry(slider, layout, thumbSprite);
            return slider;
        }

        /// <summary>Repairs existing sliders without recreating the settings window or changing their values.</summary>
        public static void ApplySliderGeometry(PcSettingsView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            var layout = JsonUtility.FromJson<SettingsLayout>(File.ReadAllText(ArtPath + "layout.json"));
            if (layout?.sliders?.Length != 3)
                throw new InvalidOperationException("Settings layout must contain three sliders.");
            Sprite thumb = LoadSprite("slider-thumb.png");
            var controls = new Slider[layout.sliders.Length];
            for (int i = 0; i < controls.Length; i++)
            {
                string id = layout.sliders[i].id;
                controls[i] = id == "master" ? view.masterSlider : id == "music" ? view.musicSlider
                    : id == "effects" ? view.effectsSlider : null;
                if (controls[i] == null || controls[i].handleRect == null
                    || !(controls[i].handleRect.parent is RectTransform))
                    throw new InvalidOperationException("Settings slider handle is missing: " + id);
            }
            for (int i = 0; i < controls.Length; i++) SetHandleGeometry(controls[i], layout.sliders[i], thumb);
        }

        private static void SetHandleGeometry(Slider slider, SliderLayout layout, Sprite thumb)
        {
            RectTransform handle = slider.handleRect;
            var area = (RectTransform)handle.parent;
            // Slider.UpdateVisuals stretches the handle anchors vertically (0..1). A zero-height
            // track leaves sizeDelta equal to the original sprite's 26px instead of adding 30px.
            Place(area, layout.track_start[0] - layout.bounds.x, layout.track_start[1] - layout.bounds.y,
                layout.track_end[0] - layout.track_start[0], 0);
            handle.anchorMin = new Vector2(slider.normalizedValue, 0);
            handle.anchorMax = new Vector2(slider.normalizedValue, 1);
            handle.pivot = new Vector2(layout.thumb_anchor[0] / thumb.rect.width,
                1f - layout.thumb_anchor[1] / thumb.rect.height);
            handle.sizeDelta = thumb.rect.size;
            handle.anchoredPosition = Vector2.zero;
            EditorUtility.SetDirty(area);
            EditorUtility.SetDirty(handle);
        }

        private static Button HitButton(string name, Transform parent, PixelRect bounds)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, bounds.x, bounds.y, bounds.width, bounds.height);
            Image image = TransparentImage(rect);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = OverlayColors();
            return button;
        }

        private static Image TransparentImage(RectTransform rect)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = Color.white;
            image.canvasRenderer.cullTransparentMesh = false;
            return image;
        }

        private static ColorBlock OverlayColors()
        {
            ColorBlock colors = ColorBlock.defaultColorBlock;
            colors.normalColor = colors.disabledColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = colors.selectedColor = new Color(1, 1, 1, 0.12f);
            colors.pressedColor = new Color(0.7f, 0.8f, 0.8f, 0.18f);
            return colors;
        }

        private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font, float x, float y,
            float width, float height, float size, TextAlignmentOptions alignment, string value)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.color = new Color32(53, 70, 83, 255);
            text.alignment = alignment;
            text.text = value;
            text.richText = false;
            text.raycastTarget = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private static Sprite LoadSprite(string name)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ArtPath + name);
            if (sprite == null) throw new InvalidOperationException("Settings sprite is missing or not imported: " + name);
            return sprite;
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        [Serializable] private sealed class PixelRect { public float x, y, width, height; }
        [Serializable] private sealed class SelectorLayout { public string id; public PixelRect bounds; }
        [Serializable] private sealed class SliderLayout
        {
            public string id;
            public PixelRect bounds;
            public float[] track_start, track_end, thumb_anchor, percent_baseline;
            public float fill_top, fill_height;
        }
        [Serializable] private sealed class ButtonLayout { public PixelRect apply, close, title_close; }
        [Serializable] private sealed class SettingsLayout
        {
            public SelectorLayout[] selectors;
            public SliderLayout[] sliders;
            public ButtonLayout buttons;
        }
    }
}
