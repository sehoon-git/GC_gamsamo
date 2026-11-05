using System;
using System.Linq;
using Gamsamo.Dialogue;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor.Presentation
{
    public static class RoomPaperChecks
    {
        public static void InstallAndBuild() { RoomPaperPresentationBuilder.Install(); BuildPreview(); }
        public static void Run()
        {
            var obj = RoomPaperPresentationBuilder.CreateStatus();
            try
            {
                var view = obj.GetComponent<RoomStatusView>();
                view.Render(null, false, true);
                Require(!view.errorCard.activeSelf && view.homeButton.gameObject.activeSelf, "normal state keeps only home");
                view.Render("저장 확인 필요", true, true);
                Require(view.errorCard.activeSelf && view.retryButton.gameObject.activeSelf && view.message.text == "저장 확인 필요", "error and retry retained");
                view.Render("불러오기 확인 필요", false, false);
                Require(!view.retryButton.gameObject.activeSelf && !view.controls.blocksRaycasts && !view.controls.interactable, "no retry without progress and modal blocks controls");
                view.Render(string.Concat(Enumerable.Repeat("긴 저장 오류 안내입니다.\n", 30)), true, true);
                obj.SetActive(true); Canvas.ForceUpdateCanvases();
                Require(view.message.rectTransform.rect.height > view.messageScroll.viewport.rect.height, "long errors scroll without truncation");
                Require(obj.GetComponent<CanvasScaler>().referenceResolution == new Vector2(1920, 1080), "resolution scaling");
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
            PresentationChecks.Run();
            Debug.Log("ROOM_PAPER_CHECKS_PASS 5 + existing presentation checks");
        }
        static void Require(bool ok, string label) { if (!ok) throw new Exception(label); Debug.Log("ROOM_PAPER_PASS " + label); }
        public static void BuildPreview()
        {
            Run();
            var scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/ConsultationRoom.unity");
            var visits = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PatientVisitController>(true)).Single();
            var view = visits.dialogue; visits.portraitGroup.alpha = 1;
            view.root.alpha = 1; view.root.interactable = true; view.root.blocksRaycasts = true;
            view.speakerText.text = "서윤"; view.bodyText.text = "오늘은 제 이야기를 조금 더 나누고 싶어요.";
            view.bodyText.maxVisibleCharacters = int.MaxValue; view.statusText.text = string.Empty;
            view.replyRoot.SetActive(true); view.replyInput.interactable = true; view.confirmationPanel.SetActive(false);
            view.retryButton.gameObject.SetActive(false); if (view.dismissButton != null) view.dismissButton.gameObject.SetActive(false);
            foreach (var behaviour in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true)).ToArray())
                if (behaviour != null && behaviour.GetType().Namespace?.StartsWith("Gamsamo", StringComparison.Ordinal) == true)
                    UnityEngine.Object.DestroyImmediate(behaviour);
            var guide = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Presentation/Guidebook.prefab")).GetComponent<GuidebookView>();
            var status = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(RoomPaperPresentationBuilder.StatusPath)).GetComponent<RoomStatusView>();
            var driver = new GameObject("Visual fixture only").AddComponent<RoomPaperPreviewDriver>(); driver.guide = guide; driver.status = status;
            const string preview = "Assets/__RoomPaperPreview.unity";
            string oldName = PlayerSettings.productName;
            try
            {
                EditorSceneManager.SaveScene(scene, preview); PlayerSettings.productName = "GC_gamsamo_PaperGuide_Preview";
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { preview },
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development,
                    locationPathName = "artifacts/paper-guide/player/PaperGuide.exe" });
                if (result.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("Build failed");
                Debug.Log("ROOM_PAPER_WINDOWS_BUILD_PASS");
            }
            finally { PlayerSettings.productName = oldName; AssetDatabase.DeleteAsset(preview); }
        }
    }
}
