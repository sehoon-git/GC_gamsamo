using System;
using System.Linq;
using Gamsamo.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamsamo.Editor
{
    public static class HanIsuProfileInstaller
    {
        const string ScenePath = "Assets/_Project/Scenes/ConsultationRoom.unity";
        const string PrefabPath = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        public const string ArtworkPath = "Assets/_Project/Art/UI/CounselingPack/profile/han-isu.png";

        [MenuItem("Gamsamo/Apply Han Isu Profile")]
        public static void Apply()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play before applying the profile.");
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ArtworkPath);
            if (sprite == null) throw new InvalidOperationException("PR #42 profile sprite is missing.");
            var scene = SceneManager.GetSceneByPath(ScenePath);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (alreadyLoaded && scene.isDirty) throw new InvalidOperationException("Save the scene before applying the profile.");
            // Patch the existing hierarchy, preserving all unrelated scene/prefab content.
            var prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                ApplyTo(prefab.GetComponentsInChildren<ProfileView>(true), sprite);
                PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
            if (!alreadyLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                ApplyTo(scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<ProfileView>(true)).ToArray(), sprite);
                EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            finally { if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true); }
            Debug.Log("HAN_ISU_PROFILE_APPLIED");
        }

        static void ApplyTo(ProfileView[] views, Sprite sprite)
        {
            if (views.Length != 1) throw new InvalidOperationException("Expected one existing profile view.");
            views[0].SetDefaultArtwork(sprite);
            EditorUtility.SetDirty(views[0]);
        }
    }
}
