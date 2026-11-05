using System;
using Gamsamo.Dialogue;
using Gamsamo.Progression;
using Gamsamo.UI.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class IntegrationSetup
{
    public static void Apply()
    {
        const string scene = "Assets/_Project/Scenes/ConsultationRoom.unity";
        EditorSceneManager.OpenScene(scene);
        Connect(UnityEngine.Object.FindFirstObjectByType<PatientVisitController>());
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        const string prefab = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        var root = PrefabUtility.LoadPrefabContents(prefab);
        try { Connect(root.GetComponentInChildren<PatientVisitController>(true)); PrefabUtility.SaveAsPrefabAsset(root, prefab); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets();
        Debug.Log("ABCD integration references saved.");
    }
    private static void Connect(PatientVisitController visits)
    {
        if (visits == null || !visits.ValidateConfiguration(out var reason)) throw new Exception("Invalid five-patient room.");
        var bridge = visits.GetComponent<GamePresentationIntegration>() ?? visits.gameObject.AddComponent<GamePresentationIntegration>();
        bridge.guidePrefab = Load("Guidebook"); bridge.endingPrefab = Load("ClearEnding");
        bridge.photoPrefab = Load("PhotoViewer"); bridge.processingPrefab = Load("ProcessingFeedback"); bridge.audioPrefab = Load("PresentationAudio");
        bridge.content = AssetDatabase.LoadAssetAtPath<EndingContentCatalog>("Assets/_Project/Settings/Presentation/EndingContent.asset");
        EditorUtility.SetDirty(bridge);
    }
    private static GameObject Load(string name) => AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Presentation/" + name + ".prefab");
}
