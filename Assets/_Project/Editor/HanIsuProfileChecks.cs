using System;
using System.Linq;
using Gamsamo.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gamsamo.Editor
{
    public static class HanIsuProfileChecks
    {
        public static void InstallAndCheck()
        {
            HanIsuProfileInstaller.Apply();
            Run();
        }
        public static void Run()
        {
            var root = PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/ConsultationRoomUI.prefab");
            try
            {
                var view = root.GetComponentInChildren<ProfileView>(true);
                var artwork = AssetDatabase.LoadAssetAtPath<Sprite>(HanIsuProfileInstaller.ArtworkPath);
                Require(view.defaultProfileSprite == artwork, "PR 42 sprite assigned");
                view.SetProfile(null, null, null);
                Require(view.UsesDefaultArtwork && view.backgroundImage.sprite == artwork, "approved default displayed");
                Require(!view.nameText.enabled && !view.affiliationText.enabled && !view.introText.enabled, "no duplicate text over baked values");
                view.SetProfile("검증 이름", "검증 소속", "검증 소개");
                Require(!view.UsesDefaultArtwork && view.backgroundImage.sprite == view.femaleSprite && view.nameText.enabled
                    && view.nameText.text == "검증 이름" && view.introText.text == "검증 소개", "custom female values use blank card");
                view.SetProfile("검증 이름", "검증 소속", "검증 소개", ProfileAvatar.Male);
                Require(view.backgroundImage.sprite == view.maleSprite && view.introText.enabled, "male variant retained");
                view.SetProfile(null, null, null);
                Require(view.UsesDefaultArtwork && view.nameText.text == "—", "default artwork does not become profile data");
                view.SetProgress("승급 기준 준비 중", "서윤", "상담 중", 0, 5);
                Require(view.progressText.enabled && view.progressText.text.Contains("0 / 5") && view.UsesDefaultArtwork, "progress remains visible");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Presentation.PresentationChecks.Run();
            Debug.Log("HAN_ISU_PROFILE_CHECKS_PASS 7 + existing presentation checks");
        }
        public static void BuildPreview()
        {
            Run();
            var scene = EditorSceneManager.OpenScene("Assets/_Project/Scenes/ConsultationRoom.unity");
            foreach (var behaviour in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)).ToArray())
                if (behaviour is Gamsamo.Dialogue.PatientVisitController || behaviour is Gamsamo.Networking.PatientAiAdapter
                    || behaviour is Gamsamo.Progression.GameProgressRuntime || behaviour is Gamsamo.Progression.GamePresentationIntegration)
                    UnityEngine.Object.DestroyImmediate(behaviour);
            const string preview = "Assets/__HanIsuProfilePreview.unity";
            string oldName = PlayerSettings.productName;
            try
            {
                EditorSceneManager.SaveScene(scene, preview); PlayerSettings.productName = "GC_gamsamo_Profile_Preview";
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { preview },
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development,
                    locationPathName = "artifacts/profile/player/ProfilePreview.exe" });
                if (result.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("Profile preview build failed");
                Debug.Log("HAN_ISU_PROFILE_WINDOWS_BUILD_PASS");
            }
            finally { PlayerSettings.productName = oldName; AssetDatabase.DeleteAsset(preview); }
        }
        static void Require(bool ok, string message)
        { if (!ok) throw new Exception(message); Debug.Log("HAN_ISU_PASS " + message); }
    }
}
