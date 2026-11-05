using System;
using System.IO;
using System.Linq;
using Gamsamo.Patients;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Creates an editable scene and prefab from the two supplied artworks.</summary>
    public static class ConsultationRoomSceneBuilder
    {
        public const string ScenePath = "Assets/_Project/Scenes/ConsultationRoom.unity";
        const string BackgroundPath = "Assets/_Project/Art/Backgrounds/ConsultationRoom.png";
        const string RecordPath = "Assets/_Project/Art/UI/MedicalRecord.png";
        const string FontPath = "Assets/_Project/Fonts/NotoSansKR SDF.asset";

        [MenuItem("Gamsamo/Create Consultation Room Scene")]
        public static void CreateScene()
        {
            if (File.Exists(ScenePath))
            {
                Debug.Log("ConsultationRoom already exists. Open the saved scene to edit it.");
                return;
            }

            if (!EnsureTextResources()) return;
            ImportArt(BackgroundPath, false);
            ImportArt(RecordPath, true);
            var font = CreateFont();
            var backgroundTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(BackgroundPath);
            var recordSprite = AssetDatabase.LoadAssetAtPath<Sprite>(RecordPath);
            var shader = Shader.Find("Gamsamo/UI/Consultation Background Blur");
            if (backgroundTexture == null || recordSprite == null || font == null || shader == null)
                throw new InvalidOperationException("Required consultation assets have not imported successfully.");

            var activeScene = SceneManager.GetActiveScene();
            bool untitled = string.IsNullOrEmpty(activeScene.path);
            if (untitled && activeScene.isDirty && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                untitled ? NewSceneMode.Single : NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var camera = new GameObject("Main Camera", typeof(Camera));
            camera.tag = "MainCamera";
            var cameraComponent = camera.GetComponent<Camera>();
            cameraComponent.clearFlags = CameraClearFlags.SolidColor;
            cameraComponent.backgroundColor = new Color(0.09f, 0.07f, 0.065f);
            cameraComponent.orthographic = true;
            camera.transform.position = new Vector3(0, 0, -10);
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var canvasObject = new GameObject("ConsultationRoomUI", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1672, 941);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            // Fit the entire supplied room. Hotspot and backdrop share this coordinate system.
            var room = Rect("RoomFrame", canvasObject.transform);
            Stretch(room);
            var roomAspect = room.gameObject.AddComponent<AspectRatioFitter>();
            roomAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            roomAspect.aspectRatio = 1672f / 941f;
            var background = Rect("RoomBackground", room).gameObject.AddComponent<RawImage>();
            Stretch(background.rectTransform);
            background.texture = backgroundTexture;
            background.raycastTarget = false;

            var openRect = Rect("DeskRecordHotspot", room);
            // Coordinates measured on the supplied 1672 x 941 room, with top-left origin.
            SetNormalizedRect(openRect, 35f / 1672, 672f / 941, 305f / 1672, 218f / 941);
            var openImage = openRect.gameObject.AddComponent<Image>();
            openImage.color = Color.white;
            openImage.canvasRenderer.cullTransparentMesh = false;
            var open = openRect.gameObject.AddComponent<Button>();
            open.targetGraphic = openImage;
            var colors = open.colors;
            colors.normalColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = new Color(1, 0.94f, 0.8f, 0.12f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = new Color(1, 0.94f, 0.8f, 0.2f);
            colors.disabledColor = colors.normalColor;
            open.colors = colors;
            // A transparent graphic still receives pointer events, constrained to the actual board.
            var polygon = openRect.gameObject.AddComponent<PolygonRaycastFilter>();
            polygon.normalizedPoints = new[] { new Vector2(0, 0), new Vector2(0.91f, 0),
                new Vector2(1, 0.86f), new Vector2(0.67f, 0.91f), new Vector2(0.65f, 1),
                new Vector2(0.56f, 1), new Vector2(0.53f, 0.88f), new Vector2(0.31f, 0.86f) };

            var modalRect = Rect("MedicalRecordModal", canvasObject.transform);
            Stretch(modalRect);
            var modal = modalRect.gameObject.AddComponent<CanvasGroup>();
            var backdrop = Rect("DimmedBackdrop", modalRect).gameObject.AddComponent<Image>();
            Stretch(backdrop.rectTransform);
            backdrop.color = new Color(0.06f, 0.04f, 0.035f, 0.42f);
            var backdropButton = backdrop.gameObject.AddComponent<Button>();
            backdropButton.transition = Selectable.Transition.None;
            backdropButton.targetGraphic = backdrop;
            backdropButton.navigation = new Navigation { mode = Navigation.Mode.None };

            var safeArea = Rect("RecordSafeArea", modalRect);
            Stretch(safeArea);
            safeArea.offsetMin = new Vector2(74, 24);
            safeArea.offsetMax = new Vector2(-74, -24);
            var panel = Rect("RecordPanel", safeArea);
            Stretch(panel);
            var aspect = panel.gameObject.AddComponent<AspectRatioFitter>();
            aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            aspect.aspectRatio = 1024f / 1536f;
            var artwork = Rect("RecordArtwork", panel);
            artwork.anchorMin = artwork.anchorMax = artwork.pivot = new Vector2(0.5f, 0.5f);
            artwork.sizeDelta = new Vector2(1024, 1536);
            artwork.gameObject.AddComponent<ReferenceRectFitter>();
            var paper = artwork.gameObject.AddComponent<Image>();
            paper.sprite = recordSprite;
            paper.preserveAspect = true;
            paper.raycastTarget = true;

            var recordView = artwork.gameObject.AddComponent<MedicalRecordView>();
            recordView.registrationNumber = Field(artwork, font, "RegistrationNumber", 302, 356, 375, 39);
            recordView.patientName = Field(artwork, font, "PatientName", 302, 400, 375, 39);
            recordView.gender = Field(artwork, font, "Gender", 302, 444, 375, 35);
            recordView.age = Field(artwork, font, "Age", 302, 485, 375, 34);
            recordView.occupation = Field(artwork, font, "Occupation", 302, 526, 375, 36);
            recordView.recordedDate = Field(artwork, font, "RecordedDate", 302, 569, 375, 34);
            recordView.visitReason = Field(artwork, font, "VisitReason", 296, 639, 592, 77, 21);
            recordView.medicalHistory = Field(artwork, font, "MedicalHistory", 296, 726, 592, 66, 21);
            recordView.medication = Field(artwork, font, "Medication", 296, 805, 592, 62, 21);
            recordView.consultationNotes = Field(artwork, font, "ConsultationNotes", 296, 884, 592, 327, 22, true);
            recordView.specialNotes = Field(artwork, font, "SpecialNotes", 296, 1230, 592, 83, 21, true);
            recordView.counselor = Field(artwork, font, "Counselor", 294, 1355, 594, 44, 22);
            recordView.ShowRecord(MedicalRecordData.CreateSample());

            var closeRect = Rect("CloseRecordButton", panel);
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1, 1);
            closeRect.pivot = new Vector2(0, 1);
            closeRect.anchoredPosition = new Vector2(8, -12);
            closeRect.sizeDelta = new Vector2(48, 48);
            var closeImage = closeRect.gameObject.AddComponent<Image>();
            closeImage.color = new Color(0.18f, 0.135f, 0.115f, 0.95f);
            var close = closeRect.gameObject.AddComponent<Button>();
            close.targetGraphic = closeImage;
            var closeLabel = Rect("CloseLabel", closeRect).gameObject.AddComponent<TextMeshProUGUI>();
            Stretch(closeLabel.rectTransform);
            closeLabel.font = font;
            closeLabel.text = "×";
            closeLabel.fontSize = 34;
            closeLabel.alignment = TextAlignmentOptions.Center;
            closeLabel.color = new Color(0.96f, 0.91f, 0.83f);
            closeLabel.raycastTarget = false;

            var controller = canvasObject.AddComponent<ConsultationRoomController>();
            controller.background = background;
            controller.backgroundTexture = backgroundTexture;
            controller.blurShader = shader;
            controller.backgroundBlur = canvasObject.AddComponent<ConsultationBackgroundBlur>();
            controller.modal = modal;
            controller.recordPanel = panel;
            controller.openRecordButton = open;
            controller.closeRecordButton = close;
            controller.backdropButton = backdropButton;
            controller.recordView = recordView;
            controller.record = MedicalRecordData.CreateSample();
            modal.alpha = 0;
            modal.blocksRaycasts = false;
            modal.interactable = false;
            modalRect.gameObject.SetActive(false);

            PrefabUtility.SaveAsPrefabAsset(canvasObject, "Assets/_Project/Prefabs/ConsultationRoomUI.prefab");
            EditorSceneManager.SaveScene(scene, ScenePath);
            if (!EditorBuildSettings.scenes.Any(item => item.path == ScenePath))
                EditorBuildSettings.scenes = EditorBuildSettings.scenes.Concat(new[] { new EditorBuildSettingsScene(ScenePath, true) }).ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("ConsultationRoom scene and UI prefab created. Open ConsultationRoom.unity and press Play.");
        }

        static bool EnsureTextResources()
        {
            if (Shader.Find("TextMeshPro/Distance Field") != null && TMP_Settings.instance != null)
                return true;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Text).Assembly);
            AssetDatabase.importPackageCompleted -= ContinueAfterTextImport;
            AssetDatabase.importPackageCompleted += ContinueAfterTextImport;
            AssetDatabase.ImportPackage(Path.Combine(package.resolvedPath, "Package Resources/TMP Essential Resources.unitypackage"), false);
            return false;
        }

        static void ContinueAfterTextImport(string packageName)
        {
            AssetDatabase.importPackageCompleted -= ContinueAfterTextImport;
            EditorApplication.delayCall += CreateScene;
        }

        static TMP_FontAsset CreateFont()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (existing != null && existing.material != null && existing.atlasTexture != null) return existing;
            var source = AssetDatabase.LoadAssetAtPath<Font>("Assets/_Project/Fonts/NotoSansCJKkr-Regular.otf");
            var font = TMP_FontAsset.CreateFontAsset(source, 40, 6, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            font.name = "NotoSansKR SDF";
            if (existing == null)
                AssetDatabase.CreateAsset(font, FontPath);
            else
            {
                EditorUtility.CopySerialized(font, existing);
                UnityEngine.Object.DestroyImmediate(font);
                font = existing;
            }
            AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (var atlas in font.atlasTextures) AssetDatabase.AddObjectToAsset(atlas, font);
            var sample = MedicalRecordData.CreateSample();
            string characters = string.Concat(Enumerable.Range(32, 95).Select(c => (char)c)) + "×—" + JsonUtility.ToJson(sample);
            font.TryAddCharacters(characters, out string missing);
            if (!string.IsNullOrEmpty(missing)) Debug.LogWarning("Font missing characters: " + missing);
            EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            return font;
        }

        static void ImportArt(string path, bool sprite)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100;
            importer.alphaIsTransparency = sprite;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        static TextMeshProUGUI Field(Transform parent, TMP_FontAsset font, string name, float x, float y,
            float width, float height, float fontSize = 23, bool top = false)
        {
            var rect = Rect(name, parent);
            SetNormalizedRect(rect, x / 1024, y / 1536, width / 1024, height / 1536);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            // All typography is authored in the original 1024 x 1536 artwork coordinates.
            text.fontSize = fontSize;
            text.fontSizeMin = 10;
            text.fontSizeMax = text.fontSize;
            text.enableAutoSizing = false;
            text.color = new Color(0.22f, 0.18f, 0.16f);
            text.alignment = top ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.richText = false;
            text.raycastTarget = false;
            text.margin = Vector4.zero;
            return text;
        }

        static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        static void SetNormalizedRect(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(x, 1 - y - height);
            rect.anchorMax = new Vector2(x + width, 1 - y);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
