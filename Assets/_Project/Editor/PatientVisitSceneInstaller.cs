using System;
using System.IO;
using System.Linq;
using Gamsamo.Dialogue;
using Gamsamo.Patients;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Adds patient visits without rebuilding the existing room, cosmetics, or PC UI.</summary>
    public static class PatientVisitSceneInstaller
    {
        public const string RosterPath = "Assets/_Project/Settings/PatientVisits.json";
        public const string PanelPath = "Assets/_Project/Art/UI/Dialogue/DialoguePanel.png";
        private const string PortraitMaterialPath = "Assets/_Project/Materials/PatientPortrait.mat";
        private const string PrefabPath = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        private const string FontPath = "Assets/_Project/Fonts/NotoSansKR SDF.asset";
        private static readonly Color Ink = new Color32(69, 47, 38, 255);
        private static readonly Color Paper = new Color32(246, 231, 207, 255);

        [MenuItem("Gamsamo/Install Patient Visits")]
        public static void Install()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Stop Play mode before installing patient visits.");
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == PrefabPath)
                throw new InvalidOperationException("Close the consultation prefab stage before installing patient visits.");
            var scene = SceneManager.GetSceneByPath(ConsultationRoomSceneBuilder.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ConsultationRoomSceneBuilder.ScenePath, OpenSceneMode.Additive);
            if (scene.isDirty)
                throw new InvalidOperationException("Save the existing consultation scene before installing patient visits.");

            AssetDatabase.Refresh();
            var roster = LoadRoster();
            ImportSprite(PanelPath);
            foreach (var entry in roster.patients) ImportSprite(entry.portraitPath);
            var panel = AssetDatabase.LoadAssetAtPath<Sprite>(PanelPath);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (panel == null || font == null)
                throw new InvalidOperationException("The generated dialogue panel and Korean font are required.");
            string lettering = string.Join("", roster.patients.Select(p => p.displayName + string.Join("", p.demoLines)))
                + "계속 듣기상담 마치기이번 상담을 마칠까요?환자가 퇴장하면 문을 눌러 다음 환자를 맞이할 수 있어요."
                + "계속 상담하기문을 눌러 환자를 맞이하세요내담자 정보를 확인해 주세요오늘의 상담을 모두 마쳤습니다"
                + "내담자가 들어오고 있어요다음 만남을 준비하고 있어요상담 중 · 0123456 /…";
            char[] requiredGlyphs = lettering.Where(c => !char.IsWhiteSpace(c)).Distinct().ToArray();
            string toAdd = new string(requiredGlyphs.Where(c => !font.HasCharacter(c)).ToArray());
            if (toAdd.Length > 0) font.TryAddCharacters(toAdd, out _);
            string missing = new string(requiredGlyphs.Where(c => !font.HasCharacter(c)).ToArray());
            if (!string.IsNullOrEmpty(missing))
                throw new InvalidOperationException("The Korean dialogue font could not prepare these glyphs: " + missing);
            var portraitMaterial = PortraitMaterial();
            var room = scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<ConsultationRoomController>(true)).Single();
            var prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Build(room, roster, panel, portraitMaterial, font);
                Build(prefab.GetComponent<ConsultationRoomController>(), roster, panel, portraitMaterial, font);
                PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssetIfDirty(font);
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
            Debug.Log("Six patient visits and the generated dialogue panel installed in scene and prefab.");
        }

        private static VisitRoster LoadRoster()
        {
            if (!File.Exists(RosterPath)) throw new FileNotFoundException("Patient roster is missing.", RosterPath);
            var roster = JsonUtility.FromJson<VisitRoster>(File.ReadAllText(RosterPath));
            if (roster?.patients == null || roster.patients.Length != 6
                || roster.patients.Any(p => p == null || string.IsNullOrWhiteSpace(p.patientId)
                    || string.IsNullOrWhiteSpace(p.displayName) || p.displayName.Contains("?")
                    || string.IsNullOrWhiteSpace(p.portraitPath) || !p.portraitPath.StartsWith("Assets/", StringComparison.Ordinal)
                    || p.demoLines == null || p.demoLines.Length == 0 || p.demoLines.Any(string.IsNullOrWhiteSpace))
                || roster.patients.Select(p => p.patientId).Distinct().Count() != 6
                || roster.patients.Select(p => p.displayName).Distinct().Count() != 6)
                throw new InvalidOperationException("The visit roster must contain six unique named patients with portraits and temporary dialogue.");
            return roster;
        }

        private static void Build(ConsultationRoomController room, VisitRoster roster, Sprite panel, Material portraitMaterial, TMP_FontAsset font)
        {
            if (room == null || room.openRecordButton == null || room.openPcButton == null)
                throw new InvalidOperationException("Install the existing consultation room and PC first.");
            var roomFrame = room.transform.Find("RoomFrame") as RectTransform;
            if (roomFrame == null) throw new InvalidOperationException("The room frame is missing.");

            // The original room and all new content share the same fitted 16:9 bounds.
            var layer = Child(roomFrame, "PatientVisits");
            layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(.5f, .5f);
            layer.anchoredPosition = Vector2.zero;
            layer.sizeDelta = new Vector2(1920, 1080);
            var fitter = GetOrAdd<ReferenceRectFitter>(layer.gameObject);
            fitter.referenceSize = new Vector2(1920, 1080);
            layer.SetAsLastSibling();
            layer.gameObject.SetActive(true);

            var controller = GetOrAdd<PatientVisitController>(room.gameObject);
            controller.room = room;
            controller.patients = roster.patients.Select(p => new PatientVisitDefinition
            {
                patientId = p.patientId,
                displayName = p.displayName,
                portrait = AssetDatabase.LoadAssetAtPath<Sprite>(p.portraitPath),
                demoLines = (string[])p.demoLines.Clone()
            }).ToArray();
            if (controller.patients.Any(p => p.portrait == null))
                throw new InvalidOperationException("A patient portrait did not import as a sprite.");

            var door = Child(layer, "DoorVisitHotspot");
            Place(door, 151, 103, 430, 644);
            var doorArt = GetOrAdd<Image>(door.gameObject);
            doorArt.color = Color.white;
            doorArt.raycastTarget = true;
            doorArt.canvasRenderer.cullTransparentMesh = false;
            controller.doorButton = GetOrAdd<Button>(door.gameObject);
            controller.doorButton.targetGraphic = doorArt;
            controller.doorButton.navigation = new Navigation { mode = Navigation.Mode.None };
            var doorColors = controller.doorButton.colors;
            doorColors.normalColor = Color.clear;
            doorColors.highlightedColor = new Color(1f, .85f, .57f, .10f);
            doorColors.selectedColor = doorColors.highlightedColor;
            doorColors.pressedColor = new Color(1f, .85f, .57f, .18f);
            doorColors.disabledColor = Color.clear;
            controller.doorButton.colors = doorColors;

            // Cropping at the tabletop seats the portrait behind the foreground desk.
            var portraitMask = Child(layer, "PortraitBehindDesk");
            Place(portraitMask, 0, 0, 1920, 750);
            GetOrAdd<RectMask2D>(portraitMask.gameObject);
            controller.portraitRoot = Child(portraitMask, "PatientPortrait");
            Place(controller.portraitRoot, 660, 180, 720, 720);
            controller.portraitImage = GetOrAdd<Image>(controller.portraitRoot.gameObject);
            controller.portraitImage.sprite = controller.patients[0].portrait;
            controller.portraitImage.material = portraitMaterial;
            controller.portraitImage.preserveAspect = true;
            controller.portraitImage.raycastTarget = false;
            controller.portraitGroup = GetOrAdd<CanvasGroup>(controller.portraitRoot.gameObject);
            controller.portraitGroup.alpha = 0;
            controller.portraitGroup.blocksRaycasts = false;
            controller.portraitGroup.interactable = false;
            controller.entryOffset = new Vector2(-500, 0);
            controller.entryDuration = .65f;
            controller.exitDuration = .3f;

            var panelRect = Child(layer, "PatientDialogue");
            Place(panelRect, 455, 740, 880, 293);
            var panelArt = GetOrAdd<Image>(panelRect.gameObject);
            panelArt.sprite = panel;
            panelArt.preserveAspect = false;
            panelArt.raycastTarget = true;
            var view = GetOrAdd<PatientDialogueView>(panelRect.gameObject);
            controller.dialogue = view;
            view.root = GetOrAdd<CanvasGroup>(panelRect.gameObject);
            view.root.alpha = 0;
            view.root.blocksRaycasts = false;
            view.root.interactable = false;
            view.speakerText = Text(panelRect, "SpeakerName", font, 32, 43, 135, 60, 26, FontStyles.Bold);
            view.speakerText.alignment = TextAlignmentOptions.Center;
            view.bodyText = Text(panelRect, "PatientLine", font, 45, 112, 790, 100, 28);
            view.bodyText.alignment = TextAlignmentOptions.TopLeft;
            view.bodyText.overflowMode = TextOverflowModes.Truncate;
            view.nextButton = Button(panelRect, "ContinueDialogue", font, "계속 듣기", 525, 230, 155, 39, false);
            view.endButton = Button(panelRect, "RequestEndVisit", font, "상담 마치기", 690, 230, 155, 39, true);

            var status = Child(layer, "VisitStatus");
            Place(status, 178, 651, 375, 49);
            var statusArt = GetOrAdd<Image>(status.gameObject);
            statusArt.color = new Color(0.25f, .18f, .14f, .89f);
            statusArt.raycastTarget = false;
            view.statusText = Text(status, "StatusText", font, 12, 4, 351, 41, 21);
            view.statusText.color = Paper;
            view.statusText.alignment = TextAlignmentOptions.Center;
            view.statusText.text = "문을 눌러 환자를 맞이하세요";

            var confirmation = Child(layer, "EndVisitConfirmation");
            Place(confirmation, 0, 0, 1920, 1080);
            var dim = GetOrAdd<Image>(confirmation.gameObject);
            dim.color = new Color(.14f, .09f, .06f, .52f);
            dim.raycastTarget = true;
            var card = Child(confirmation, "ConfirmationCard");
            Place(card, 660, 380, 600, 255);
            var cardArt = GetOrAdd<Image>(card.gameObject);
            cardArt.color = Paper;
            cardArt.raycastTarget = true;
            var outline = GetOrAdd<Outline>(card.gameObject);
            outline.effectColor = new Color32(103, 73, 54, 255);
            outline.effectDistance = new Vector2(2, -2);
            Text(card, "Title", font, 34, 24, 532, 46, 29, FontStyles.Bold).text = "이번 상담을 마칠까요?";
            var explanation = Text(card, "Explanation", font, 34, 78, 532, 90, 23);
            explanation.text = "환자가 퇴장하면 문을 눌러\n다음 환자를 맞이할 수 있어요.";
            view.cancelEndButton = Button(card, "KeepTalking", font, "계속 상담하기", 34, 177, 252, 51, false);
            view.confirmEndButton = Button(card, "ConfirmEndVisit", font, "상담 마치기", 302, 177, 264, 51, true);
            view.confirmationPanel = confirmation.gameObject;
            confirmation.gameObject.SetActive(false);
            EditorUtility.SetDirty(view);
            EditorUtility.SetDirty(controller);
        }

        private static TMP_Text Text(Transform parent, string name, TMP_FontAsset font,
            float x, float y, float width, float height, float size, FontStyles style = FontStyles.Normal)
        {
            var rect = Child(parent, name);
            Place(rect, x, y, width, height);
            var label = GetOrAdd<TextMeshProUGUI>(rect.gameObject);
            label.font = font;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = Ink;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;
            label.richText = false;
            label.enableAutoSizing = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Truncate;
            return label;
        }

        private static Button Button(Transform parent, string name, TMP_FontAsset font, string caption,
            float x, float y, float width, float height, bool primary)
        {
            var rect = Child(parent, name);
            Place(rect, x, y, width, height);
            var art = GetOrAdd<Image>(rect.gameObject);
            art.color = primary ? new Color32(130, 82, 60, 255) : new Color32(226, 206, 175, 255);
            var button = GetOrAdd<Button>(rect.gameObject);
            button.targetGraphic = art;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var text = Text(rect, "Label", font, 5, 0, width - 10, height, 20);
            text.text = caption;
            text.alignment = TextAlignmentOptions.Center;
            text.color = primary ? Paper : Ink;
            return button;
        }

        private static RectTransform Child(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return (RectTransform)existing;
            var child = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            child.SetParent(parent, false);
            return child;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            var component = target.GetComponent<T>();
            return component != null ? component : target.AddComponent<T>();
        }

        private static void ImportSprite(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new FileNotFoundException("Portrait or dialogue artwork is missing.", path);
            if (importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Single
                && importer.alphaIsTransparency && !importer.mipmapEnabled && importer.textureCompression == TextureImporterCompression.Uncompressed)
                return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
        }

        private static Material PortraitMaterial()
        {
            var shader = Shader.Find("UI/Gamsamo/PatientPortrait");
            if (shader == null)
                throw new InvalidOperationException("Import the patient portrait UI shader before installing visits.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(PortraitMaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "PatientPortrait" };
                AssetDatabase.CreateAsset(material, PortraitMaterialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
            }
            return material;
        }

        [Serializable] private sealed class VisitRoster { public VisitEntry[] patients; }
        [Serializable] private sealed class VisitEntry
        {
            public string patientId;
            public string displayName;
            public string portraitPath;
            public string[] demoLines;
        }
    }
}
