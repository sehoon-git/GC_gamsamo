using System;
using System.IO;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor.Presentation
{
    /// <summary>Creates only D-owned assets. Never opens, edits or saves a shared scene/root prefab.</summary>
    public static class PresentationPrefabBuilder
    {
        public const string Folder = "Assets/_Project/Prefabs/Presentation";
        public const string CatalogPath = "Assets/_Project/Settings/Presentation/EndingContent.asset";
        private static readonly Color Ink = new Color32(63, 47, 39, 255);
        private static readonly Color Paper = new Color32(247, 237, 215, 255);
        private static TMP_FontAsset Font => AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Fonts/NotoSansKR SDF.asset");

        [MenuItem("Gamsamo/Presentation/Create Missing D Prefabs")]
        public static void BuildMissing()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Create presentation prefabs in Edit mode.");
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            AssetDatabase.Refresh();
            if (AssetDatabase.LoadAssetAtPath<EndingContentCatalog>(CatalogPath) == null)
            {
                var catalog = ScriptableObject.CreateInstance<EndingContentCatalog>();
                catalog.contentVersion = "unassigned";
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            SaveMissing("Guidebook", CreateGuidebook);
            SaveMissing("ClearEnding", CreateEnding);
            SaveMissing("PhotoViewer", CreatePhotoViewer);
            SaveMissing("ProcessingFeedback", () =>
            {
                var root = CanvasRoot("ProcessingFeedback");
                root.GetComponent<Canvas>().sortingOrder = 190; // Beneath every D modal's full-screen blocker.
                var binding = root.AddComponent<DialogueProcessingBinding>();
                binding.gameResultRetryButton = Button("ResultSaveRetry", root.transform, "결과 저장 재시도", 1420, 930, 350, 70);
                binding.gameResultRetryButton.gameObject.SetActive(false);
                return root;
            });
            SaveMissing("PresentationAudio", () =>
            {
                var root = new GameObject("PresentationAudio");
                root.SetActive(false);
                var audio = root.AddComponent<PresentationAudio>();
                audio.music = root.AddComponent<AudioSource>(); audio.effects = root.AddComponent<AudioSource>();
                audio.music.playOnAwake = audio.effects.playOnAwake = false;
                audio.content = AssetDatabase.LoadAssetAtPath<EndingContentCatalog>(CatalogPath);
                return root;
            });
            Debug.Log("D presentation prefabs are ready. Shared scenes/root prefabs were not changed.");
        }

        private static void SaveMissing(string name, Func<GameObject> factory)
        {
            string path = Folder + "/" + name + ".prefab";
            if (File.Exists(path)) return; // Preserve B/designer edits and GUIDs on repeated invocation.
            GameObject root = factory();
            try { root.SetActive(true); PrefabUtility.SaveAsPrefabAsset(root, path); }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        public static GameObject CreateGuidebook()
        {
            var root = CanvasRoot("Guidebook");
            var view = root.AddComponent<GuidebookView>();
            view.openButton = Button("Book", root.transform, "게임 안내", 1670, 40, 200, 76);
            var bookLabel = view.openButton.GetComponentInChildren<TMP_Text>().rectTransform;
            bookLabel.anchoredPosition = new Vector2(65, -6); bookLabel.sizeDelta = new Vector2(129, 64);
            Image("BookLeftPage", view.openButton.transform, 12, 19, 21, 38).color = Paper;
            Image("BookRightPage", view.openButton.transform, 36, 19, 21, 38).color = Paper;
            Image("BookSpine", view.openButton.transform, 33, 18, 3, 40).color = Ink;
            view.panel = Modal(root.transform);
            Transform paper = PaperPanel(view.panel.transform);
            view.titleText = Label("Title", paper, "게임 안내", 70, 48, 850, 82, 40);
            view.bodyText = Label("Body", paper, "", 70, 160, 1020, 430, 30);
            view.previousButton = Button("Previous", paper, "이전", 70, 640, 190, 72);
            view.pageText = Label("Page", paper, "1 / 6", 400, 650, 360, 60, 26, TextAlignmentOptions.Center);
            view.nextButton = Button("Next", paper, "다음", 900, 640, 190, 72);
            view.closeButton = Button("Close", paper, "닫기", 950, 48, 140, 66);
            view.initialFocus = view.closeButton;
            view.panel.SetActive(false);
            return root;
        }

        public static GameObject CreateEnding()
        {
            var root = CanvasRoot("ClearEnding");
            var view = root.AddComponent<EndingPresentationView>();
            view.content = AssetDatabase.LoadAssetAtPath<EndingContentCatalog>(CatalogPath);
            view.panel = Modal(root.transform);
            Transform paper = PaperPanel(view.panel.transform);
            view.titleText = Label("Title", paper, "", 70, 40, 1020, 85, 38, TextAlignmentOptions.Center);
            view.bodyText = Label("Body", paper, "", 270, 150, 620, 310, 27, TextAlignmentOptions.Center);
            view.rewardText = Label("Reward", paper, "", 70, 470, 1020, 64, 36, TextAlignmentOptions.Center);
            view.photoNotice = Label("PhotoNotice", paper, "", 70, 535, 1020, 60, 25, TextAlignmentOptions.Center);
            view.feedbackText = Label("Feedback", paper, "", 70, 594, 1020, 50, 22, TextAlignmentOptions.Center);
            view.photo = Image("StoryPhoto", paper, 390, 140, 380, 250);
            // Text/photo layout is adjusted by the view when actual approved media is present.
            view.photo.enabled = false;
            var viewport = Rect("CreditsViewport", paper, 260, 135, 640, 430);
            viewport.gameObject.AddComponent<RectMask2D>();
            view.creditsViewport = viewport.gameObject;
            view.creditsBody = Rect("CreditsBody", viewport, 0, -430, 640, 500);
            view.creditsText = Label("Credits", view.creditsBody, "", 0, 0, 640, 1200, 28, TextAlignmentOptions.Top);
            view.creditsText.overflowMode = TextOverflowModes.Overflow;
            view.creditsViewport.SetActive(false);
            view.continueButton = Button("Continue", paper, "계속하기", 650, 655, 240, 70);
            view.skipButton = Button("Skip", paper, "건너뛰기", 270, 655, 240, 70);
            view.retryButton = Button("Retry", paper, "저장 재시도", 460, 655, 240, 70);
            view.retryButton.gameObject.SetActive(false);
            view.initialFocus = view.continueButton;
            view.finalPhotos = new Image[6]; view.finalCaptions = new TMP_Text[6];
            for (int i = 0; i < 6; i++)
            {
                float x = i % 2 == 0 ? 40 : 930, y = 145 + i / 2 * 155;
                view.finalPhotos[i] = Image("FinalPhoto" + i, paper, x, y, 190, 110);
                view.finalPhotos[i].enabled = false;
                view.finalCaptions[i] = Label("FinalCaption" + i, paper, "", x, y + 112, 190, 40, 19, TextAlignmentOptions.Center);
            }
            view.panel.SetActive(false);
            return root;
        }

        public static GameObject CreatePhotoViewer()
        {
            var root = CanvasRoot("PhotoViewer");
            var view = root.AddComponent<PhotoViewer>();
            view.panel = Modal(root.transform);
            Transform paper = PaperPanel(view.panel.transform);
            view.titleText = Label("Title", paper, "사진", 70, 40, 850, 80, 36);
            view.image = Image("Photo", paper, 160, 130, 840, 410);
            view.descriptionText = Label("Description", paper, "", 90, 555, 980, 110, 25);
            view.previousButton = Button("Previous", paper, "이전", 90, 685, 180, 62);
            view.pageText = Label("Page", paper, "", 400, 685, 360, 62, 25, TextAlignmentOptions.Center);
            view.nextButton = Button("Next", paper, "다음", 890, 685, 180, 62);
            view.closeButton = Button("Close", paper, "닫기", 950, 48, 140, 66);
            view.initialFocus = view.closeButton;
            view.panel.SetActive(false);
            return root;
        }

        public static GameObject CanvasRoot(string name)
        {
            var root = new GameObject(name, typeof(RectTransform)); root.SetActive(false);
            var canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            return root;
        }

        private static GameObject Modal(Transform root)
        {
            var shade = Image("ModalBlocker", root, 0, 0, 1920, 1080);
            shade.color = new Color(0.12f, 0.09f, 0.07f, 0.8f); shade.raycastTarget = true;
            return shade.gameObject;
        }
        private static Transform PaperPanel(Transform parent)
        {
            var image = Image("Paper", parent, 380, 150, 1160, 780);
            image.color = Paper; image.raycastTarget = true;
            return image.transform;
        }
        public static TMP_Text Label(string name, Transform parent, string value, float x, float y, float w, float h,
            float size, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
        {
            var label = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = Font; label.fontSize = size; label.color = Ink; label.richText = false;
            label.text = value; label.raycastTarget = false; label.alignment = alignment;
            label.overflowMode = TextOverflowModes.Page;
            return label;
        }
        public static Button Button(string name, Transform parent, string caption, float x, float y, float w, float h)
        {
            var image = Image(name, parent, x, y, w, h); image.color = new Color32(217, 207, 175, 255);
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None }; // Tab is trapped by PresentationModal.
            Label("Label", image.transform, caption, 6, 6, w - 12, h - 12, 26, TextAlignmentOptions.Center);
            return button;
        }
        public static Image Image(string name, Transform parent, float x, float y, float w, float h)
        {
            var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>();
            image.preserveAspect = true; image.raycastTarget = false;
            return image;
        }
        public static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(w, h);
            return rect;
        }
    }
}
