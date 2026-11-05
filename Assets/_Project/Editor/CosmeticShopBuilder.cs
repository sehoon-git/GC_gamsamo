using System;
using System.IO;
using System.Linq;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Updates only the dynamic shop content; the existing window and PC navigation stay intact.</summary>
    public static class CosmeticShopBuilder
    {
        private const string ArtPath = "Assets/_Project/Art/UI/CosmeticShop/";
        private const string ContentName = "DynamicCosmeticShop";
        private static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper" };
        private static readonly Color Ink = new Color32(72, 63, 52, 255);
        private static readonly Color Accent = new Color32(69, 110, 91, 255);
        // Keep the original 500 px of tabs/cards and the 12 px bottom frame unscaled.
        // Only the blank paper between them grows to accommodate the footer controls.
        private static readonly Vector4 TemplateBorder = new Vector4(8, 12, 8, 500);
        private const float WindowHeight = 574;
        private const float FooterTop = 514;
        private const float ButtonHeight = 34;

        public static CosmeticShopView Build(PcDesktopView desktop, CosmeticCatalog catalog,
            CosmeticAppearanceController appearance, TMP_FontAsset font)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Build the cosmetic shop outside Play mode.");
            if (desktop == null || desktop.shopWindow == null || desktop.shopImage == null
                || catalog == null || appearance == null || font == null)
                throw new ArgumentException("The existing PC shop, cosmetic catalog, appearance controller and font are required.");

            ShopLayout layout = JsonUtility.FromJson<ShopLayout>(File.ReadAllText(ArtPath + "layout.json"));
            if (layout?.slots == null || layout.slots.Length != CosmeticShopView.SlotCount
                || layout.slots.Select(slot => slot.index).Distinct().Count() != CosmeticShopView.SlotCount
                || layout.slots.Any(slot => slot.index < 0 || slot.index >= CosmeticShopView.SlotCount
                    || slot.width < 200 || slot.height < 150 || slot.x < 0 || slot.y < 0))
                throw new InvalidOperationException("Shop layout must contain eight valid card rectangles.");
            Sprite[] templates = Targets.Select(target => PrepareTemplate(ArtPath + "templates/" + target + ".png")).ToArray();
            Sprite buttonSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            if (buttonSprite == null) throw new InvalidOperationException("The Unity UI button sprite is missing.");
            Place((RectTransform)desktop.shopWindow.transform, 253, 61, 1112, WindowHeight);
            desktop.shopImage.type = Image.Type.Sliced;
            desktop.shopImage.preserveAspect = false;
            desktop.shopImage.pixelsPerUnitMultiplier = 1;

            var view = desktop.shopWindow.GetComponent<CosmeticShopView>();
            if (view == null) view = desktop.shopWindow.AddComponent<CosmeticShopView>();
            view.catalog = catalog;
            view.appearance = appearance;
            view.itemButtons = new Button[CosmeticShopView.SlotCount];
            view.thumbnails = new Image[CosmeticShopView.SlotCount];
            view.itemLabels = new TMP_Text[CosmeticShopView.SlotCount];
            view.priceLabels = new TMP_Text[CosmeticShopView.SlotCount];
            view.selectionBorders = new GameObject[CosmeticShopView.SlotCount];
            view.selectedBadges = new TMP_Text[CosmeticShopView.SlotCount];
            RectTransform content = Rect(ContentName, desktop.shopWindow.transform);
            Place(content, 0, 0, 1112, WindowHeight);

            foreach (Slot slot in layout.slots)
            {
                RectTransform card = Rect("Card" + slot.index, content);
                Place(card, slot.x, slot.y, slot.width, slot.height);
                view.itemButtons[slot.index] = HitButton(card);

                RectTransform thumb = Rect("Thumbnail", card);
                Place(thumb, 10, 10, slot.width - 20, 109);
                Image thumbnail = Component<Image>(thumb);
                thumbnail.color = Color.white;
                thumbnail.preserveAspect = true;
                thumbnail.raycastTarget = false;
                view.thumbnails[slot.index] = thumbnail;

                // Empty templates still contain a placeholder rule through the name baseline.
                // Cover that printed rule with matching paper before drawing the live label.
                Image namePaper = Solid("NamePaper", card, 12, 125, 148, 31,
                    new Color32(255, 249, 234, 255));
                namePaper.transform.SetAsFirstSibling();
                view.itemLabels[slot.index] = Label("Name", card, font, 13, 126, 146, 31, 18,
                    TextAlignmentOptions.MidlineLeft, "");
                view.priceLabels[slot.index] = Label("Price", card, font, 172, 126, 56, 27, 16,
                    TextAlignmentOptions.Center, "미정");

                RectTransform selection = Rect("Selection", card);
                Place(selection, 0, 0, slot.width - 4, slot.height - 4);
                Border(selection, slot.width - 4, slot.height - 4, Accent, 2);
                Solid("BadgeBackground", selection, 9, 9, 111, 27, Accent);
                TMP_Text badge = Label("Badge", selection, font, 11, 9, 107, 27, 16,
                    TextAlignmentOptions.Center, "미리보기 중");
                badge.color = Color.white;
                view.selectionBorders[slot.index] = selection.gameObject;
                view.selectedBadges[slot.index] = badge;
            }

            // All prices are unspecified. The source currency artwork remains unchanged.
            Solid("FooterDivider", content, 34, 505, 1041, 1, new Color32(207, 191, 165, 255));
            Solid("FooterDividerLight", content, 34, 506, 1041, 1, new Color32(255, 249, 233, 255));
            view.statusText = Label("PreviewStatus", content, font, 34, FooterTop, 265, ButtonHeight, 18,
                TextAlignmentOptions.MidlineLeft, "미리보기 · 가격 미정");
            view.restoreCategoryButton = TextButton("RestoreCategory", content, font,
                946, FooterTop, 129, ButtonHeight, "기본 복원", buttonSprite);

            RectTransform blindControls = Rect("BlindControls", content);
            Place(blindControls, 520, FooterTop, 386, ButtonHeight);
            view.blindControls = blindControls.gameObject;
            Label("Label", blindControls, font, 0, 0, 86, ButtonHeight, 17,
                TextAlignmentOptions.MidlineLeft, "블라인드");
            view.blindButtons = new Button[3];
            view.blindSelections = new GameObject[3];
            string[] labels = { "올림", "절반", "내림" };
            for (int index = 0; index < 3; index++)
            {
                Button button = TextButton("State" + index, blindControls, font,
                    88 + index * 87, 0, 79, ButtonHeight, labels[index], buttonSprite);
                view.blindButtons[index] = button;
                RectTransform selection = Rect("Selected", button.transform);
                Place(selection, 0, 0, 79, ButtonHeight);
                DisableStraightBorder(selection);
                Image selectedOutline = Component<Image>(selection);
                selectedOutline.sprite = buttonSprite;
                selectedOutline.type = Image.Type.Sliced;
                selectedOutline.fillCenter = false;
                selectedOutline.pixelsPerUnitMultiplier = 2;
                selectedOutline.color = Accent;
                selectedOutline.raycastTarget = false;
                view.blindSelections[index] = selection.gameObject;
            }

            desktop.shopView = view;
            desktop.shopSprites = templates;
            desktop.SelectTab(0);
            EditorUtility.SetDirty(view);
            EditorUtility.SetDirty(desktop);
            return view;
        }

        private static Sprite LoadSprite(string path)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("Sprite has not imported: " + path);
            return sprite;
        }

        private static Sprite PrepareTemplate(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Shop template importer is missing: " + path);
            if (importer.spriteBorder != TemplateBorder)
            {
                importer.spriteBorder = TemplateBorder;
                importer.SaveAndReimport();
            }
            return LoadSprite(path);
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                if (existing is RectTransform rect) return rect;
                throw new InvalidOperationException(name + " must be a RectTransform.");
            }
            var created = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            created.SetParent(parent, false);
            return created;
        }

        private static T Component<T>(RectTransform rect) where T : Component
        {
            T component = rect.GetComponent<T>();
            return component != null ? component : rect.gameObject.AddComponent<T>();
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;
        }

        private static Image Solid(string name, Transform parent, float x, float y,
            float width, float height, Color color)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            Image image = Component<Image>(rect);
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void Border(RectTransform parent, float width, float height, Color color, float thickness)
        {
            Solid("Top", parent, 0, 0, width, thickness, color);
            Solid("Bottom", parent, 0, height - thickness, width, thickness, color);
            Solid("Left", parent, 0, 0, thickness, height, color);
            Solid("Right", parent, width - thickness, 0, thickness, height, color);
        }

        private static TMP_Text Label(string name, Transform parent, TMP_FontAsset font,
            float x, float y, float width, float height, float size, TextAlignmentOptions alignment, string value)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            var label = Component<TextMeshProUGUI>(rect);
            label.font = font;
            label.fontSize = size;
            label.color = Ink;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            label.text = value;
            return label;
        }

        private static Button HitButton(RectTransform rect)
        {
            Image graphic = Component<Image>(rect);
            graphic.color = Color.white;
            graphic.raycastTarget = true;
            graphic.canvasRenderer.cullTransparentMesh = false;
            Button button = Component<Button>(rect);
            button.targetGraphic = graphic;
            ColorBlock colors = button.colors;
            colors.normalColor = colors.disabledColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = colors.selectedColor = new Color(0.64f, 0.79f, 0.67f, 0.12f);
            colors.pressedColor = new Color(0.64f, 0.79f, 0.67f, 0.25f);
            button.colors = colors;
            return button;
        }

        private static Button TextButton(string name, Transform parent, TMP_FontAsset font,
            float x, float y, float width, float height, string text, Sprite sprite)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            Button button = HitButton(rect);
            DisableStraightBorder(rect);
            Image rim = rect.GetComponent<Image>();
            rim.sprite = sprite;
            rim.type = Image.Type.Sliced;
            rim.pixelsPerUnitMultiplier = 2;
            rim.color = new Color32(151, 130, 101, 255);
            // A rounded paper face inset from the rim leaves a shallow lower bevel.
            Image face = Solid("Face", rect, 1.5f, 1.5f, width - 3, height - 4, Color.white);
            face.sprite = sprite;
            face.type = Image.Type.Sliced;
            face.pixelsPerUnitMultiplier = 2;
            face.transform.SetAsFirstSibling();
            button.targetGraphic = face;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color32(247, 239, 219, 255);
            colors.highlightedColor = colors.selectedColor = new Color32(233, 238, 218, 255);
            colors.pressedColor = new Color32(198, 213, 188, 255);
            colors.disabledColor = new Color32(221, 215, 198, 255);
            colors.fadeDuration = 0.1f;
            button.colors = colors;
            Solid("TopLight", rect, 6, 3, width - 12, 1, new Color32(255, 252, 238, 180));
            TMP_Text label = Label("Text", rect, font, 5, 0, width - 10, height - 1, 18,
                TextAlignmentOptions.Center, text);
            label.fontStyle = FontStyles.Bold;
            label.transform.SetAsLastSibling();
            return button;
        }

        private static void DisableStraightBorder(RectTransform parent)
        {
            // Preserve existing serialized nodes when upgrading an installed shop.
            foreach (string edge in new[] { "Top", "Bottom", "Left", "Right" })
            {
                Transform child = parent.Find(edge);
                if (child != null) child.gameObject.SetActive(false);
            }
        }

#pragma warning disable CS0649
        [Serializable] private sealed class ShopLayout { public Slot[] slots; }
        [Serializable] private sealed class Slot { public int index; public float x, y, width, height; }
#pragma warning restore CS0649
    }
}
