using System;
using System.IO;
using System.Linq;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using B = Gamsamo.Editor.Presentation.PresentationPrefabBuilder;

namespace Gamsamo.Editor.Presentation
{
    public static class RoomPaperPresentationBuilder
    {
        public const string StatusPath = "Assets/_Project/Resources/Presentation/RoomStatus.prefab";
        const string GuidePath = "Assets/_Project/Prefabs/Presentation/Guidebook.prefab";
        const string SurfacePath = "Assets/_Project/Art/UI/Dialogue/WarmPaperSurface.asset";
        static readonly Color Ink = new Color32(75, 48, 37, 255);
        static readonly Color Rule = new Color32(174, 127, 98, 255);
        static Sprite ButtonPaper => AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/Dialogue/PinkPaperButton.asset");
        static Sprite Surface()
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(SurfacePath);
            if (sprite != null) return sprite;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Project/Art/UI/Dialogue/DialoguePanel.png");
            sprite = Sprite.Create(texture, new Rect(texture.width * .25f, texture.height * .25f, texture.width * .5f, texture.height * .35f), new Vector2(.5f, .5f), 100);
            sprite.name = "WarmPaperSurface"; AssetDatabase.CreateAsset(sprite, SurfacePath); return sprite;
        }
        static void Paper(Image image)
        {
            image.sprite = Surface(); image.preserveAspect = false; image.color = Color.white;
            var outline = image.GetComponent<Outline>() ?? image.gameObject.AddComponent<Outline>();
            outline.effectColor = Rule; outline.effectDistance = new Vector2(2, -2);
            var shadow = image.GetComponents<Shadow>().FirstOrDefault(s => !(s is Outline)) ?? image.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(.22f, .12f, .08f, .22f); shadow.effectDistance = new Vector2(5, -7);
        }
        static void Button(Button button)
        {
            var image = button.GetComponent<Image>(); image.sprite = ButtonPaper; image.type = Image.Type.Sliced;
            image.preserveAspect = false; image.pixelsPerUnitMultiplier = 5; image.color = Color.white;
            var colors = button.colors; colors.normalColor = Color.white;
            colors.highlightedColor = colors.selectedColor = new Color32(255, 236, 220, 255);
            colors.pressedColor = new Color32(217, 165, 142, 255); colors.disabledColor = new Color32(192, 181, 169, 150);
            button.colors = colors;
            var label = button.GetComponentInChildren<TMP_Text>(); label.color = Ink; label.alignment = TextAlignmentOptions.Midline;
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }
        static void Place(RectTransform rect, float x, float y, float w, float h)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(w, h);
        }
        public static void StyleGuide(GuidebookView view)
        {
            var book = view.openButton;
            Place(book.GetComponent<RectTransform>(), 1620, 32, 260, 80); Button(book);
            var label = book.GetComponentInChildren<TMP_Text>(); Place(label.rectTransform, 70, 10, 162, 58); label.fontSize = 25;
            foreach (string name in new[] { "BookLeftPage", "BookRightPage", "BookSpine" })
            {
                var icon = book.transform.Find(name)?.GetComponent<Image>();
                if (icon == null) continue;
                icon.color = name == "BookSpine" ? Rule : new Color32(255, 241, 214, 255);
                if (name == "BookLeftPage") Place(icon.rectTransform, 32, 24, 18, 32);
                else if (name == "BookRightPage") Place(icon.rectTransform, 52, 24, 18, 32);
                else Place(icon.rectTransform, 50, 23, 2, 34);
            }
            var paper = view.titleText.transform.parent.GetComponent<Image>(); Paper(paper);
            var root = paper.transform;
            // Notebook details remain outside the text area.
            if (root.Find("Binding") == null)
            {
                B.Image("Binding", root, 30, 30, 3, 690).color = new Color32(201, 158, 123, 255);
                B.Image("HeadingRule", root, 72, 154, 1016, 2).color = Rule;
                var tab = B.Image("GuideTab", root, 64, 20, 240, 54); tab.sprite = ButtonPaper; tab.preserveAspect = false;
                tab.type = Image.Type.Sliced; tab.pixelsPerUnitMultiplier = 5;
                B.Label("GuideTabLabel", tab.transform, "상담실 안내서", 12, 8, 216, 36, 23, TextAlignmentOptions.Midline);
                B.Label("KeyboardHint", root, "← → 페이지 이동    ·    Esc 닫기", 330, 705, 500, 28, 18, TextAlignmentOptions.Center).color = Rule;
            }
            Place(view.titleText.rectTransform, 72, 88, 850, 56); view.titleText.fontSize = 34; view.titleText.fontStyle = FontStyles.Bold;
            Place(view.bodyText.rectTransform, 72, 190, 1016, 414); view.bodyText.fontSize = 28; view.bodyText.lineSpacing = 12;
            view.bodyText.color = Ink;
            Button(view.previousButton); Button(view.nextButton); Button(view.closeButton);
            Place(view.closeButton.GetComponent<RectTransform>(), 950, 46, 140, 66);
            view.panel.GetComponent<Image>().color = new Color(.16f, .09f, .06f, .58f);
        }
        public static GameObject CreateStatus()
        {
            var root = B.CanvasRoot("RoomStatus"); root.GetComponent<Canvas>().sortingOrder = 80;
            var view = root.AddComponent<RoomStatusView>(); view.controls = root.AddComponent<CanvasGroup>();
            view.homeButton = B.Button("Home", root.transform, "시작 화면으로", 32, 32, 250, 80); Button(view.homeButton);
            var card = B.Image("StatusPaper", root.transform, 40, 120, 480, 280); Paper(card); card.raycastTarget = true;
            view.errorCard = card.gameObject;
            B.Label("Heading", card.transform, "진행을 확인해 주세요", 26, 20, 428, 38, 26).fontStyle = FontStyles.Bold;
            B.Image("Rule", card.transform, 26, 66, 428, 2).color = Rule;
            var viewport = B.Image("MessageViewport", card.transform, 26, 82, 428, 110); viewport.color = Color.clear; viewport.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();
            view.message = B.Label("Message", viewport.transform, "", 0, 0, 422, 110, 22);
            view.message.raycastTarget = true; view.message.overflowMode = TextOverflowModes.Overflow;
            view.message.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            view.messageScroll = viewport.gameObject.AddComponent<ScrollRect>(); view.messageScroll.viewport = viewport.rectTransform;
            view.messageScroll.content = view.message.rectTransform; view.messageScroll.horizontal = false;
            view.messageScroll.movementType = ScrollRect.MovementType.Clamped; view.messageScroll.scrollSensitivity = 24;
            view.retryButton = B.Button("Retry", card.transform, "저장 · 연결 재시도", 24, 208, 432, 58); Button(view.retryButton);
            view.retryButton.navigation = view.homeButton.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            card.gameObject.SetActive(false);
            return root;
        }
        [MenuItem("Gamsamo/Presentation/Apply Room Paper Theme")]
        public static void Install()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play before updating presentation prefabs.");
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)); AssetDatabase.Refresh();
            var guide = PrefabUtility.LoadPrefabContents(GuidePath);
            try { StyleGuide(guide.GetComponent<GuidebookView>()); PrefabUtility.SaveAsPrefabAsset(guide, GuidePath); }
            finally { PrefabUtility.UnloadPrefabContents(guide); }
            var status = CreateStatus();
            try { status.SetActive(true); PrefabUtility.SaveAsPrefabAsset(status, StatusPath); }
            finally { UnityEngine.Object.DestroyImmediate(status); }
            AssetDatabase.SaveAssets(); Debug.Log("ROOM_PAPER_THEME_INSTALLED");
        }
    }
}
