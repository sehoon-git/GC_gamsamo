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
    /// <summary>Builds content windows in a caller-owned PC hierarchy; does not save or replace existing windows.</summary>
    public static class PcContentBuilder
    {
        private const string PackPath = "Assets/_Project/Art/UI/CounselingPack/";

        public static ProfileView BuildProfile(RectTransform desktop, TMP_FontAsset font)
        {
            ValidateParent(desktop, font);
            var existing = ExistingView<ProfileView>(desktop, "ProfileWindow");
            if (existing != null)
            {
                FitProfileText(existing);
                return existing;
            }

            string directory = PackPath + "profile/";
            var layout = Read<ProfileLayout>(directory + "layout.json");
            ValidateBounds(layout?.window_rect_in_pc, "Profile window");
            ValidateBounds(layout.close_hit_rect, "Profile close button");
            if (layout.fields == null || layout.variants == null
                || !new[] { "name", "affiliation", "bio" }.All(id => layout.fields.Count(field => field.id == id) == 1))
                throw new InvalidOperationException("Profile layout must define name, affiliation and bio once each.");
            foreach (var field in layout.fields)
                ValidateBounds(field.value_rect, "Profile field " + field.id);
            Sprite female = LoadSprite(directory + layout.variants.female);
            Sprite male = LoadSprite(directory + layout.variants.male);

            RectTransform window = Rect("ProfileWindow", desktop);
            window.gameObject.SetActive(false);
            Place(window, layout.window_rect_in_pc);
            var artwork = window.gameObject.AddComponent<Image>();
            artwork.sprite = female;
            artwork.raycastTarget = true;
            var view = window.gameObject.AddComponent<ProfileView>();
            view.backgroundImage = artwork;
            view.femaleSprite = female;
            view.maleSprite = male;

            foreach (var field in layout.fields)
            {
                TMP_Text label = Label(field.id + "Value", window, font, field.value_rect, 19,
                    TextAlignmentOptions.MidlineLeft, "—");
                switch (field.id)
                {
                    case "name": view.nameText = label; break;
                    case "affiliation": view.affiliationText = label; break;
                    case "bio": view.introText = label; break;
                }
            }
            view.closeButton = HitButton("CloseProfileButton", window, layout.close_hit_rect);
            view.SetProfile(null, null, null, ProfileAvatar.Female);
            return view;
        }

        private static void FitProfileText(ProfileView view)
        {
            // The Noto font's line metrics need more space than the glyph outline. Keep its
            // full line inside the supplied 30 px value rows, including an empty-value dash.
            foreach (TMP_Text label in new[] { view.nameText, view.affiliationText, view.introText })
                if (label != null) label.fontSize = 19;
        }

        public static PhotoGalleryView BuildPhotos(RectTransform desktop, TMP_FontAsset font)
        {
            ValidateParent(desktop, font);
            var existing = ExistingView<PhotoGalleryView>(desktop, "PhotoGalleryWindow");
            if (existing != null)
                return existing;

            string directory = PackPath + "photos/";
            var layout = Read<PhotosLayout>(directory + "layout.json");
            ValidateBounds(layout?.window_rect_in_pc, "Photo gallery window");
            ValidateBounds(layout.close_hit_rect, "Photo gallery close button");
            if (layout.slots == null || layout.slots.Length != PhotoGalleryView.MaximumPhotos
                || layout.slots.Select(slot => slot.index).Distinct().Count() != PhotoGalleryView.MaximumPhotos
                || layout.slots.Any(slot => slot.index < 0 || slot.index >= PhotoGalleryView.MaximumPhotos))
                throw new InvalidOperationException("Photo layout must define six slots numbered 0 through 5.");
            foreach (var slot in layout.slots)
            {
                ValidateBounds(slot.rect, "Photo slot " + slot.index);
                ValidateBounds(slot.thumbnail_rect, "Photo thumbnail " + slot.index);
            }
            Sprite windowSprite = LoadSprite(directory + layout.image);

            RectTransform window = Rect("PhotoGalleryWindow", desktop);
            window.gameObject.SetActive(false);
            Place(window, layout.window_rect_in_pc);
            var artwork = window.gameObject.AddComponent<Image>();
            artwork.sprite = windowSprite;
            artwork.raycastTarget = true;
            var view = window.gameObject.AddComponent<PhotoGalleryView>();
            view.thumbnails = new Image[PhotoGalleryView.MaximumPhotos];
            view.titles = new TMP_Text[PhotoGalleryView.MaximumPhotos];

            // The supplied PNG bakes in a sample achievement icon and a "cured in week one" caption.
            // Cover only its content area with UI and rebuild neutral slots; the PNG stays untouched.
            var paper = new Color32(244, 236, 218, 255);
            Solid("ContentCover", window, new PixelRect(8, 46, 1096, 206), paper);
            foreach (var slot in layout.slots)
            {
                Solid("PhotoSlot" + slot.index, window, slot.rect, new Color32(177, 157, 130, 255));
                Solid("PhotoSlotPaper" + slot.index, window,
                    new PixelRect(slot.rect.x + 2, slot.rect.y + 2, slot.rect.width - 4, slot.rect.height - 4), paper);

                RectTransform thumbnail = Rect("PhotoThumbnail" + slot.index, window);
                Place(thumbnail, slot.thumbnail_rect);
                var image = thumbnail.gameObject.AddComponent<Image>();
                image.raycastTarget = false;
                image.preserveAspect = true;
                image.enabled = false;
                view.thumbnails[slot.index] = image;
                view.titles[slot.index] = Label("PhotoTitle" + slot.index, window, font,
                    new PixelRect(slot.rect.x, 198, slot.rect.width, 32), 19,
                    TextAlignmentOptions.Center, "");
            }
            view.emptyMessage = Label("EmptyGalleryMessage", window, font,
                new PixelRect(30, 202, 1052, 34), 22, TextAlignmentOptions.Center,
                "아직 등록된 사진이 없습니다.");
            view.closeButton = HitButton("ClosePhotosButton", window, layout.close_hit_rect);
            view.SetPhotos(null);
            return view;
        }

        private static void ValidateParent(RectTransform desktop, TMP_FontAsset font)
        {
            if (desktop == null || font == null)
                throw new ArgumentException("A PC desktop RectTransform and Korean TMP font are required.");
        }

        private static T ExistingView<T>(RectTransform desktop, string name) where T : Component
        {
            Transform existing = desktop.Find(name);
            if (existing == null)
                return null;
            T view = existing.GetComponent<T>();
            if (view == null)
                throw new InvalidOperationException(name + " already exists without " + typeof(T).Name
                    + "; the builder will not overwrite it.");
            return view;
        }

        private static T Read<T>(string path)
        {
            T result = JsonUtility.FromJson<T>(File.ReadAllText(path));
            if (result == null)
                throw new InvalidOperationException("Invalid layout: " + path);
            return result;
        }

        private static void ValidateBounds(PixelRect bounds, string name)
        {
            if (bounds == null || bounds.width <= 0 || bounds.height <= 0
                || float.IsNaN(bounds.x) || float.IsNaN(bounds.y)
                || float.IsNaN(bounds.width) || float.IsNaN(bounds.height)
                || float.IsInfinity(bounds.x) || float.IsInfinity(bounds.y)
                || float.IsInfinity(bounds.width) || float.IsInfinity(bounds.height))
                throw new InvalidOperationException(name + " bounds are invalid.");
        }

        private static Sprite LoadSprite(string path)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
                throw new InvalidOperationException("Sprite has not imported: " + path);
            return sprite;
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Place(RectTransform rect, PixelRect bounds)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(bounds.x, -bounds.y);
            rect.sizeDelta = new Vector2(bounds.width, bounds.height);
        }

        private static void Solid(string name, Transform parent, PixelRect bounds, Color color)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, bounds);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static TMP_Text Label(string name, Transform parent, TMP_FontAsset font,
            PixelRect bounds, float size, TextAlignmentOptions alignment, string text)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, bounds);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = size;
            label.color = new Color32(72, 63, 52, 255);
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Truncate;
            label.richText = false;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        private static Button HitButton(string name, Transform parent, PixelRect bounds)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, bounds);
            var image = rect.gameObject.AddComponent<Image>();
            image.canvasRenderer.cullTransparentMesh = false;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = colors.disabledColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = colors.selectedColor = new Color(1, 0.94f, 0.8f, 0.15f);
            colors.pressedColor = new Color(1, 0.94f, 0.8f, 0.27f);
            button.colors = colors;
            return button;
        }

#pragma warning disable CS0649
        [Serializable] private sealed class PixelRect
        {
            public float x, y, width, height;
            public PixelRect(float x, float y, float width, float height)
            { this.x = x; this.y = y; this.width = width; this.height = height; }
        }
        [Serializable] private sealed class ProfileLayout
        {
            public PixelRect window_rect_in_pc, close_hit_rect;
            public ProfileVariants variants;
            public ProfileField[] fields;
        }
        [Serializable] private sealed class ProfileVariants { public string female, male; }
        [Serializable] private sealed class ProfileField { public string id; public PixelRect value_rect; }
        [Serializable] private sealed class PhotosLayout
        {
            public PixelRect window_rect_in_pc, close_hit_rect;
            public string image;
            public PhotoSlot[] slots;
        }
        [Serializable] private sealed class PhotoSlot { public int index; public PixelRect rect, thumbnail_rect; }
#pragma warning restore CS0649
    }
}
