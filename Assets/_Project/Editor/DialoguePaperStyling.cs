using System;
using System.Linq;
using Gamsamo.Dialogue;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    // Targeted visual patch: no patient roster, events, AI state, or room reconstruction.
    public static class DialoguePaperStyling
    {
        const string SpritePath = "Assets/_Project/Art/UI/Dialogue/PinkPaperButton.asset";
        const string PrefabPath = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        const string ScenePath = "Assets/_Project/Scenes/ConsultationRoom.unity";
        static readonly Color Ink = new Color32(69, 47, 38, 255);

        static Sprite PaperSprite()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(SpritePath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Project/Art/UI/Dialogue/PinkPaperButton.png");
            if (existing != null && existing.texture == texture) return existing;
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect, new Vector4(90, 90, 90, 90));
            sprite.name = "PinkPaperButton";
            if (existing != null) { EditorUtility.CopySerialized(sprite, existing); UnityEngine.Object.DestroyImmediate(sprite); EditorUtility.SetDirty(existing); return existing; }
            AssetDatabase.CreateAsset(sprite, SpritePath);
            return sprite;
        }

        public static void Apply(PatientDialogueView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            var paper = PaperSprite();
            Place(view.speakerText.rectTransform, 36, 74, 128, 44);
            view.speakerText.alignment = TextAlignmentOptions.Midline;
            view.speakerText.margin = Vector4.zero;
            view.speakerText.fontSize = 26;
            view.speakerText.enableAutoSizing = false;
            view.speakerText.textWrappingMode = TextWrappingModes.NoWrap;
            Place(view.connectionText.rectTransform, 190, 70, 645, 24);
            Place(view.statusText.rectTransform, 190, 100, 645, 28);
            Place(view.bodyText.rectTransform, 45, 140, 790, 76);
            Place((RectTransform)view.replyRoot.transform, 40, 222, 800, 52);
            Place(view.replyInput.GetComponent<RectTransform>(), 0, 0, 650, 52);
            Place(view.replyInput.textViewport, 12, 5, 626, 42);
            Place(view.replyInput.textComponent.rectTransform, 0, 0, 626, 42);
            Place(view.replyInput.placeholder.rectTransform, 0, 0, 626, 42);
            Style(view.sendButton, paper, 665, 0, 135, 52);
            Style(view.previousPageButton, paper, 32, 282, 110, 44);
            Place(view.pageText.rectTransform, 145, 282, 76, 44);
            Style(view.historyButton, paper, 228, 282, 130, 44);
            Style(view.retryButton, paper, 368, 282, 145, 44);
            Style(view.nextButton, paper, 525, 282, 155, 44);
            Style(view.endButton, paper, 690, 282, 155, 44);
            Style(view.dismissButton, paper, 690, 282, 155, 44);
            // The confirmation dialog uses the same paper controls.
            Restyle(view.confirmEndButton, paper);
            Restyle(view.cancelEndButton, paper);
            var inputColors = view.replyInput.colors;
            inputColors.normalColor = Color.white;
            inputColors.highlightedColor = new Color32(255, 246, 237, 255);
            inputColors.selectedColor = inputColors.highlightedColor;
            inputColors.disabledColor = new Color32(210, 195, 178, 255);
            view.replyInput.colors = inputColors;
            EditorUtility.SetDirty(view);
        }
        static void Style(Button button, Sprite paper, float x, float y, float width, float height)
        {
            if (button == null) return;
            Place(button.GetComponent<RectTransform>(), x, y, width, height);
            Restyle(button, paper);
        }
        static void Restyle(Button button, Sprite paper)
        {
            if (button == null) return;
            var art = button.GetComponent<Image>();
            art.sprite = paper; art.type = Image.Type.Sliced; art.color = Color.white;
            art.pixelsPerUnitMultiplier = 5; button.targetGraphic = art;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color32(255, 232, 218, 255);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = new Color32(220, 168, 148, 255);
            colors.disabledColor = new Color32(190, 181, 173, 180);
            colors.fadeDuration = .12f; button.colors = colors;
            var label = button.GetComponentInChildren<TMP_Text>(true);
            label.color = Ink; label.alignment = TextAlignmentOptions.Midline;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            var rect = label.rectTransform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, .5f);
            rect.offsetMin = new Vector2(7, 5); rect.offsetMax = new Vector2(-7, -5);
            label.margin = Vector4.zero;
        }
        static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        }
        [MenuItem("Gamsamo/Apply Dialogue Paper Styling")]
        public static void Install()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play before applying dialogue styling.");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) throw new InvalidOperationException("Close prefab editing first.");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes first.");
            var prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try { Apply(prefab.GetComponentInChildren<PatientDialogueView>(true)); PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
            var scene = EditorSceneManager.OpenScene(ScenePath);
            Apply(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PatientDialogueView>(true)).Single());
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("DIALOGUE_PAPER_STYLING_APPLIED");
        }

        // Isolated visual fixture built from the actual room. No AI, progression or player saves.
        public static void BuildPreview()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var visits = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PatientVisitController>(true)).Single();
            var view = visits.dialogue;
            var speaker = view.speakerText; var portrait = visits.portraitImage;
            var patients = visits.patients.ToArray();
            var root = view.root;
            root.gameObject.SetActive(true); root.alpha = 1; root.interactable = true; root.blocksRaycasts = true;
            visits.portraitGroup.alpha = 1;
            view.bodyText.text = "카페에서 일하면서 자기소개서를 쓰고 있어요. 오늘은 제 이야기를 조금 더 나누고 싶어요.";
            view.bodyText.maxVisibleCharacters = int.MaxValue;
            view.statusText.text = "답변을 작성해 주세요";
            view.connectionText.text = "UI 배치 검증 · 서버 연결 없음";
            speaker.text = patients[0].displayName; portrait.sprite = patients[0].portrait;
            view.replyRoot.SetActive(true); view.replyInput.interactable = true;
            view.previousPageButton.gameObject.SetActive(true); view.previousPageButton.interactable = false;
            view.pageText.text = "1 / 2"; view.historyButton.gameObject.SetActive(true);
            view.nextButton.gameObject.SetActive(true); view.nextButton.GetComponentInChildren<TMP_Text>().text = "다음 쪽";
            view.endButton.gameObject.SetActive(true); if (view.dismissButton != null) view.dismissButton.gameObject.SetActive(false);
            view.retryButton.gameObject.SetActive(false); view.confirmationPanel.SetActive(false);
            foreach (var patient in patients)
            {
                speaker.text = patient.displayName; speaker.ForceMeshUpdate(true);
                var delta = (Vector2)speaker.textBounds.center - speaker.rectTransform.rect.center;
                if (Mathf.Abs(delta.x) > 2 || Mathf.Abs(delta.y) > 2)
                    throw new InvalidOperationException(patient.displayName + " name is not centered: " + delta);
                Debug.Log("DIALOGUE_NAME_CENTER_PASS " + patient.displayName + " offset=" + delta);
            }
            speaker.text = patients[0].displayName;
            // Remove game behaviours only in this unsaved fixture. Serialized production scene remains unchanged.
            foreach (var behaviour in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).ToArray())
                if (behaviour != null && behaviour.GetType().Namespace?.StartsWith("Gamsamo", StringComparison.Ordinal) == true)
                    UnityEngine.Object.DestroyImmediate(behaviour);
            const string previewPath = "Assets/__DialoguePaperPreview.unity";
            string oldName = PlayerSettings.productName;
            try
            {
                EditorSceneManager.SaveScene(scene, previewPath);
                PlayerSettings.productName = "GC_gamsamo_DialogueStyle_Preview";
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { previewPath },
                    locationPathName = "artifacts/dialogue-style/clean-player/DialogueStyle.exe", target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development });
                if (result.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("Preview build failed");
                Debug.Log("DIALOGUE_STYLE_PREVIEW_BUILD_PASS");
            }
            finally { PlayerSettings.productName = oldName; AssetDatabase.DeleteAsset(previewPath); }
        }
    }
}
