using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    /// <summary>The open album sits inside the existing ending paper and shares its photo and body.</summary>
    internal sealed class EndingAlbumSpread
    {
        private readonly RectTransform spread, mount, mountShadow;
        private readonly TMP_Text folio, heading, signature;
        private readonly Image photograph;
        private readonly TMP_Text paragraph;
        private readonly TextAlignmentOptions originalAlignment;
        private readonly float originalLineSpacing;

        public EndingAlbumSpread(Image photo, TMP_Text body, Sprite paperTexture)
        {
            photograph = photo;
            paragraph = body;
            originalAlignment = body.alignment;
            originalLineSpacing = body.lineSpacing;
            spread = Rect("EndingAlbumSpread", photo.transform.parent, 0, 0, 1160, 780);
            spread.SetAsFirstSibling();

            Paper("CoverShadow", spread, 43, 111, 1078, 482, new Color32(151, 128, 102, 165));
            Paper("ClothCover", spread, 39, 103, 1082, 484, new Color32(147, 96, 88, 255));
            Paper("PageEdges", spread, 46, 106, 1068, 474, new Color32(221, 207, 180, 255));
            Paper("PhotoPage", spread, 47, 106, 533, 467, new Color32(244, 233, 215, 255)).GetComponent<Image>().sprite = paperTexture;
            Paper("WritingPage", spread, 580, 106, 532, 467, Color.white).GetComponent<Image>().sprite = paperTexture;
            Paper("BindingShade", spread, 572, 107, 8, 465, new Color32(215, 193, 163, 255));
            Paper("Binding", spread, 580, 107, 3, 465, new Color32(190, 163, 135, 255));
            mountShadow = Paper("PhotoShadow", spread, 0, 0, 1, 1, new Color32(191, 170, 145, 255));
            mount = Paper("PhotoMount", spread, 0, 0, 1, 1, new Color32(255, 252, 243, 255));

            folio = Label("MomentNumber", 641, 153, 411, 34, 22, new Color32(130, 80, 72, 255));
            Paper("DateRule", spread, 642, 199, 74, 2, new Color32(176, 132, 112, 255));
            heading = Label("MomentTitle", 640, 213, 422, 95, 37, new Color32(69, 48, 41, 255));
            heading.fontStyle = FontStyles.Bold;
            heading.enableAutoSizing = true;
            heading.fontSizeMin = 26;
            heading.fontSizeMax = 37;
            signature = Label("ClosingThanks", 641, 483, 431, 61, 29, new Color32(142, 74, 68, 255));
            signature.enableAutoSizing = true;
            signature.fontSizeMin = 23;
            signature.fontSizeMax = 29;
            signature.alignment = TextAlignmentOptions.MidlineLeft;
        }

        public void Show(Sprite image, string title, string story, int number, int count)
        {
            spread.gameObject.SetActive(true);
            bool last = number == count;
            GuidebookView.Set(folio, "장면 " + number + " / " + count);
            GuidebookView.Set(heading, title);
            const string closing = "상담해주셔서 감사합니다";
            GuidebookView.Set(signature, last ? closing : "");
            GuidebookView.Set(paragraph, last && story == closing ? "" : story);
            paragraph.gameObject.SetActive(true);
            paragraph.alignment = TextAlignmentOptions.TopLeft;
            paragraph.lineSpacing = 12f;
            Place(paragraph.rectTransform, 642, 316, 418, last ? 153 : 221);
            paragraph.pageToDisplay = 1;
            paragraph.ForceMeshUpdate();

            photograph.sprite = image;
            photograph.enabled = image != null;
            photograph.preserveAspect = true;
            var dimensions = image != null ? image.rect.size : new Vector2(4, 3);
            float scale = Mathf.Min(460f / dimensions.x, 378f / dimensions.y);
            float width = dimensions.x * scale, height = dimensions.y * scale;
            float x = 308f - width * 0.5f, y = 339f - height * 0.5f;
            Place(mountShadow, x - 7, y - 5, width + 22, height + 26);
            Place(mount, x - 11, y - 10, width + 22, height + 25);
            Place(photograph.rectTransform, x, y, width, height);
        }

        public void Hide()
        {
            spread.gameObject.SetActive(false);
            paragraph.alignment = originalAlignment;
            paragraph.lineSpacing = originalLineSpacing;
            Place(photograph.rectTransform, 390, 140, 380, 250);
        }

        private TMP_Text Label(string name, float x, float y, float w, float h, float size, Color color)
        {
            var label = Rect(name, spread, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = paragraph.font;
            label.fontSize = size;
            label.color = color;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.richText = false;
            label.raycastTarget = false;
            return label;
        }

        private static RectTransform Paper(string name, Transform parent, float x, float y, float w, float h, Color tint)
        {
            var rect = Rect(name, parent, x, y, w, h);
            var layer = rect.gameObject.AddComponent<Image>();
            layer.color = tint;
            layer.raycastTarget = false;
            return rect;
        }

        private static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            Place(rect, x, y, w, h);
            return rect;
        }

        private static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
        }
    }
}
