using System;
using System.IO;
using System.Linq;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Creates the title once. Existing user scenes and prefabs are never regenerated.</summary>
    public static class StartMenuSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/StartMenu.unity";
        public const string PrefabPath = "Assets/_Project/Prefabs/StartMenuUI.prefab";
        private const string FontPath = "Assets/_Project/Fonts/NotoSansKR SDF.asset";
        private static readonly Color Cream = new Color32(255, 239, 208, 255);
        private static readonly Color Ink = new Color32(71, 54, 43, 255);
        private static readonly Color Olive = new Color32(101, 106, 67, 255);

        [MenuItem("Gamsamo/Create Start Menu Scene")]
        public static void CreateScene()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before creating the start menu.");
            if (File.Exists(ScenePath) || File.Exists(PrefabPath))
            {
                Debug.Log("Start menu already exists. Edit the saved scene/prefab; the builder will not overwrite it.");
                return;
            }
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            var artwork = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Project/Art/Backgrounds/ConsultationRoom.png");
            if (font == null || artwork == null || !File.Exists(ConsultationRoomSceneBuilder.ScenePath))
                throw new InvalidOperationException("The existing Korean font, room artwork and consultation scene are required.");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camera.tag = "MainCamera";
                camera.transform.position = new Vector3(0, 0, -10);
                camera.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
                camera.GetComponent<Camera>().backgroundColor = new Color32(48, 38, 35, 255);
                camera.GetComponent<Camera>().orthographic = true;
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                RectTransform canvas = Rect("StartMenuUI", null);
                canvas.gameObject.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                canvas.gameObject.AddComponent<GraphicRaycaster>();
                var menu = canvas.gameObject.AddComponent<StartMenuController>();

                RectTransform background = Rect("RoomBackground", canvas);
                Stretch(background);
                var fit = background.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = artwork.width / (float)artwork.height;
                var image = background.gameObject.AddComponent<RawImage>();
                image.texture = artwork;
                image.color = new Color(0.86f, 0.86f, 0.86f, 1);
                image.raycastTarget = false;
                RectTransform shade = Rect("BrownGradient", canvas);
                Stretch(shade);
                shade.gameObject.AddComponent<StartMenuShade>().raycastTarget = false;

                RectTransform content = Rect("TitleAndMenu", canvas);
                content.anchorMin = content.anchorMax = new Vector2(0.086f, 1);
                content.pivot = new Vector2(0, 1);
                content.sizeDelta = new Vector2(480, 840);
                content.anchoredPosition = Vector2.zero;
                menu.menuGroup = content.gameObject.AddComponent<CanvasGroup>();
                Text("TitleFirst", content, font, 0, 180, 400, 110, 62, "상담", Cream).characterSpacing = 8;
                Text("TitleLast", content, font, -3, 243, 450, 180, 108, "커피톡", Cream);
                Solid("TitleRule", content, 0, 425, 42, 2, new Color32(182, 182, 136, 230));
                menu.menuItems = new StartMenuButton[4];
                string[] captions = { "새 게임", "불러오기", "설정", "게임 종료" };
                float[] rows = { 477, 547, 645, 715 };
                for (int i = 0; i < captions.Length; i++)
                {
                    Button button = Button("Menu" + i, content, font, 0, rows[i], 340, 64, captions[i], true);
                    var item = button.gameObject.AddComponent<StartMenuButton>();
                    item.menu = menu;
                    item.button = button;
                    item.label = button.GetComponentInChildren<TMP_Text>();
                    Place((RectTransform)item.label.transform, 27, 0, 310, 64);
                    item.label.alignment = TextAlignmentOptions.MidlineLeft;
                    item.label.fontSize = 29;
                    item.label.characterSpacing = 1.5f;
                    var dot = Solid("SelectedDot", button.transform, 6, 28, 7, 7, new Color32(183, 186, 134, 255));
                    dot.sprite = Builtin("Knob.psd");
                    item.dot = dot.gameObject;
                    item.underline = Solid("SelectedUnderline", button.transform, 27, 58,
                        item.label.GetPreferredValues(captions[i]).x, 1, new Color32(210, 201, 154, 140)).gameObject;
                    button.interactable = i != 1;
                    item.SetSelected(i == 0);
                    menu.menuItems[i] = item;
                }
                menu.newGameButton = menu.menuItems[0].button;
                menu.loadButton = menu.menuItems[1].button;
                menu.settingsButton = menu.menuItems[2].button;
                menu.quitButton = menu.menuItems[3].button;
                VerticalNavigation(new Selectable[] { menu.newGameButton, menu.settingsButton, menu.quitButton });
                menu.saveStatus = Text("SaveStatus", content, font, 27, 612, 400, 26, 14,
                    "저장된 진행이 없습니다", new Color32(156, 142, 122, 255));
                menu.transitionStatus = Text("TransitionStatus", content, font, 27, 802, 440, 60, 18, "", Cream);

                BuildSettings(menu, canvas, font);
                BuildQuit(menu, canvas, font);
                var fade = Solid("SceneFade", canvas, 0, 0, 0, 0, Color.black);
                Stretch(fade.rectTransform);
                fade.raycastTarget = true;
                menu.fade = fade.gameObject.AddComponent<CanvasGroup>();
                menu.fade.alpha = 0;
                menu.fade.blocksRaycasts = false;
                PrefabUtility.SaveAsPrefabAssetAndConnect(canvas.gameObject, PrefabPath, InteractionMode.AutomatedAction);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssetIfDirty(font);
                var scenes = EditorBuildSettings.scenes.Where(entry => entry.path != ScenePath).ToList();
                var roomEntry = scenes.Find(entry => entry.path == ConsultationRoomSceneBuilder.ScenePath);
                if (roomEntry == null) scenes.Add(new EditorBuildSettingsScene(ConsultationRoomSceneBuilder.ScenePath, true));
                else roomEntry.enabled = true;
                scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
                AssetDatabase.SaveAssets();
                Debug.Log("StartMenu scene and prefab created; StartMenu is the first build scene. ConsultationRoom was preserved.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [MenuItem("Gamsamo/Open Start Menu Scene")]
        public static void OpenScene()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before opening the start menu.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save your open scene before switching to the start menu.");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static void BuildSettings(StartMenuController menu, RectTransform canvas, TMP_FontAsset font)
        {
            RectTransform modal = Modal("SettingsModal", canvas, out Button backdrop);
            menu.settingsModal = modal.gameObject;
            menu.settingsBackdrop = backdrop;
            RectTransform panel = Panel("SettingsPanel", modal, 720, 740);
            var view = panel.gameObject.AddComponent<PcSettingsView>();
            menu.settingsView = view;
            Text("Title", panel, font, 40, 25, 640, 65, 40, "설정", Ink);
            Text("GraphicsHeading", panel, font, 40, 112, 640, 42, 27, "그래픽", Olive);
            Text("ModeLabel", panel, font, 40, 166, 225, 48, 23, "화면 모드", Ink);
            Text("ResolutionLabel", panel, font, 40, 229, 225, 48, 23, "해상도", Ink);
            view.modeDropdown = Dropdown("ScreenMode", panel, font, 280, 166);
            view.resolutionDropdown = Dropdown("Resolution", panel, font, 280, 229);
            Solid("SectionRule", panel, 40, 310, 640, 1, new Color32(177, 155, 120, 100));
            Text("AudioHeading", panel, font, 40, 329, 640, 42, 27, "오디오", Olive);
            view.masterSlider = Volume("Master", "전체", panel, font, 388, out TMP_Text master);
            view.masterPercent = master;
            view.musicSlider = Volume("Music", "배경음", panel, font, 455, out TMP_Text music);
            view.musicPercent = music;
            view.effectsSlider = Volume("Effects", "효과음", panel, font, 522, out TMP_Text effects);
            view.effectsPercent = effects;
            menu.settingsStatus = Text("AppliedStatus", panel, font, 40, 593, 640, 34, 19, "", Olive);
            Solid("FooterRule", panel, 40, 639, 640, 1, new Color32(177, 155, 120, 100));
            view.closeButton = Button("CancelSettings", panel, font, 394, 665, 130, 48, "취소");
            view.applyButton = Button("ApplySettings", panel, font, 548, 665, 130, 48, "적용");
            VerticalNavigation(new Selectable[] { view.modeDropdown, view.resolutionDropdown,
                view.masterSlider, view.musicSlider, view.effectsSlider, view.closeButton, view.applyButton });
            HorizontalPair(view.closeButton, view.applyButton);
            modal.gameObject.SetActive(false);
        }

        private static void BuildQuit(StartMenuController menu, RectTransform canvas, TMP_FontAsset font)
        {
            RectTransform modal = Modal("QuitModal", canvas, out Button backdrop);
            menu.quitModal = modal.gameObject;
            menu.quitBackdrop = backdrop;
            RectTransform panel = Panel("QuitPanel", modal, 640, 300);
            Text("Title", panel, font, 40, 25, 560, 65, 37, "게임 종료", Ink);
            Text("Message", panel, font, 40, 112, 560, 55, 24, "게임을 종료하시겠습니까?", Ink);
            menu.cancelQuitButton = Button("CancelQuit", panel, font, 292, 210, 130, 50, "취소");
            menu.confirmQuitButton = Button("ConfirmQuit", panel, font, 444, 210, 156, 50, "게임 종료");
            VerticalNavigation(new Selectable[] { menu.cancelQuitButton, menu.confirmQuitButton });
            HorizontalPair(menu.cancelQuitButton, menu.confirmQuitButton);
            modal.gameObject.SetActive(false);
        }

        private static TMP_Dropdown Dropdown(string name, RectTransform parent, TMP_FontAsset font, float x, float y)
        {
            GameObject root = TMP_DefaultControls.CreateDropdown(new TMP_DefaultControls.Resources
            {
                standard = Builtin("UISprite.psd"), background = Builtin("Background.psd"),
                checkmark = Builtin("Checkmark.psd"), dropdown = Builtin("DropdownArrow.psd"), mask = Builtin("UIMask.psd")
            });
            root.name = name;
            root.transform.SetParent(parent, false);
            Place((RectTransform)root.transform, x, y, 398, 48);
            var dropdown = root.GetComponent<TMP_Dropdown>();
            foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true))
            {
                label.font = font;
                label.fontSize = 22;
                label.color = Ink;
                label.richText = false;
                label.raycastTarget = false;
            }
            dropdown.ClearOptions();
            dropdown.AddOptions(new System.Collections.Generic.List<string> { "—" });
            dropdown.alphaFadeSpeed = 0.12f;
            var item = (RectTransform)dropdown.itemText.transform.parent;
            item.sizeDelta = new Vector2(item.sizeDelta.x, 42);
            var content = (RectTransform)item.parent;
            content.sizeDelta = new Vector2(content.sizeDelta.x, 42);
            dropdown.template.sizeDelta = new Vector2(dropdown.template.sizeDelta.x, 190);
            dropdown.GetComponent<Image>().color = new Color32(255, 249, 234, 255);
            return dropdown;
        }

        private static Slider Volume(string name, string caption, RectTransform panel, TMP_FontAsset font,
            float y, out TMP_Text percent)
        {
            Text(name + "Label", panel, font, 40, y, 170, 46, 23, caption, Ink);
            GameObject root = DefaultControls.CreateSlider(new DefaultControls.Resources
                { standard = Builtin("UISprite.psd"), background = Builtin("Background.psd"), knob = Builtin("Knob.psd") });
            root.name = name + "Slider";
            root.transform.SetParent(panel, false);
            Place((RectTransform)root.transform, 231, y, 348, 46);
            var slider = root.GetComponent<Slider>();
            slider.minValue = 0;
            slider.maxValue = 100;
            slider.wholeNumbers = true;
            slider.fillRect.GetComponent<Image>().color = Olive;
            slider.targetGraphic.color = new Color32(155, 153, 105, 255);
            percent = Text(name + "Percent", panel, font, 595, y, 84, 46, 23, "100%", Ink);
            percent.alignment = TextAlignmentOptions.MidlineRight;
            return slider;
        }

        private static RectTransform Modal(string name, Transform parent, out Button backdrop)
        {
            RectTransform root = Rect(name, parent);
            Stretch(root);
            Image shade = Solid("Backdrop", root, 0, 0, 0, 0, new Color(0.08f, 0.06f, 0.05f, 0.65f));
            Stretch(shade.rectTransform);
            shade.raycastTarget = true;
            backdrop = shade.gameObject.AddComponent<Button>();
            backdrop.targetGraphic = shade;
            backdrop.transition = Selectable.Transition.None;
            backdrop.navigation = new Navigation { mode = Navigation.Mode.None };
            return root;
        }

        private static RectTransform Panel(string name, Transform parent, float width, float height)
        {
            RectTransform rect = Rect(name, parent);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            Image paper = rect.gameObject.AddComponent<Image>();
            paper.sprite = Builtin("UISprite.psd");
            paper.type = Image.Type.Sliced;
            paper.color = new Color32(244, 232, 207, 255);
            return rect;
        }

        private static Button Button(string name, Transform parent, TMP_FontAsset font,
            float x, float y, float width, float height, string caption, bool textOnly = false)
        {
            Image image = Solid(name, parent, x, y, width, height,
                textOnly ? Color.clear : new Color32(222, 211, 184, 255));
            image.raycastTarget = true;
            image.canvasRenderer.cullTransparentMesh = false;
            if (!textOnly) { image.sprite = Builtin("UISprite.psd"); image.type = Image.Type.Sliced; }
            Button button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            if (textOnly) button.transition = Selectable.Transition.None;
            else
            {
                ColorBlock colors = button.colors;
                colors.highlightedColor = colors.selectedColor = new Color32(200, 202, 160, 255);
                colors.pressedColor = new Color32(168, 173, 130, 255);
                button.colors = colors;
            }
            TMP_Text label = Text("Label", image.transform, font, 8, 0, width - 16, height, 23, caption, Ink);
            label.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private static void VerticalNavigation(Selectable[] controls)
        {
            for (int i = 0; i < controls.Length; i++)
                controls[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnUp = controls[(i + controls.Length - 1) % controls.Length],
                    selectOnDown = controls[(i + 1) % controls.Length] };
        }

        private static void HorizontalPair(Selectable left, Selectable right)
        {
            Navigation a = left.navigation, b = right.navigation;
            a.selectOnRight = right;
            b.selectOnLeft = left;
            left.navigation = a;
            right.navigation = b;
        }

        private static TMP_Text Text(string name, Transform parent, TMP_FontAsset font,
            float x, float y, float width, float height, float size, string value, Color color)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = size;
            label.color = color;
            label.text = value;
            label.richText = false;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            return label;
        }

        private static Image Solid(string name, Transform parent, float x, float y, float width, float height, Color color)
        {
            RectTransform rect = Rect(name, parent);
            Place(rect, x, y, width, height);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Sprite Builtin(string name) => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/" + name);
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
    }
}
