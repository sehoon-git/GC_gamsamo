#if UNITY_EDITOR
using System;
using System.IO;
using Gamsamo.Dialogue;
using Gamsamo.Progression;
using Gamsamo.Save;
using Gamsamo.UI;
using UnityEditor;
using UnityEngine;

public static class BProgressIntegrationChecks
{
    // Test-only calculation; never attached to a scene or registered with the runtime.
    private sealed class TestRewards : IRewardCalculator
    {
        public RewardCalculation Calculate(SavedEvaluation evaluation, EconomyState previous, bool first)
        {
            previous.Balance += 17;
            return new RewardCalculation { SettlementKey = evaluation.Identity.Key, Granted = 17, Economy = previous };
        }
    }
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }

    [MenuItem("Gamsamo/B/Run Progress Integration Checks")]
    public static void Run()
    {
        checks = 0;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/ConsultationRoomUI.prefab");
        Check(prefab != null, "Room prefab missing");
        var visits = prefab.GetComponentInChildren<PatientVisitController>(true);
        Check(visits != null && visits.ValidateConfiguration(out _), "Visit references invalid");
        var menu = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/StartMenuUI.prefab");
        Check(menu != null && menu.GetComponentInChildren<StartMenuController>(true) != null, "Menu references invalid");
        var obj = new GameObject("B input lease test");
        try
        {
            var room = obj.AddComponent<ConsultationRoomController>();
            var first = room.AcquireInputBlock(); var second = room.AcquireInputBlock();
            Check(room.IsOverlayBlocking, "Modal did not block");
            first.Dispose(); first.Dispose(); Check(room.IsOverlayBlocking, "Nested modal released too early");
            second.Dispose(); Check(!room.IsOverlayBlocking, "Modal state not restored");
        }
        finally { UnityEngine.Object.DestroyImmediate(obj); }
        string path = Path.Combine(Path.GetTempPath(), "b-unity-progress-" + Guid.NewGuid().ToString("N"), "run.json");
        var store = new GameProgressStore(path); var service = new ProgressService(store);
        service.Create(new[] { "a", "b", "c", "d", "e", "f" }); service.BeginVisit();
        service.CaptureAi("a", "case_a", "r1", "test-only", "evaluated");
        var evaluation = new SavedEvaluation { Identity = new RoundIdentity { ProgressId = service.State.ProgressId,
            PatientId = "a", CaseId = "case_a", RoundId = "r1" }, AiSaveCompleted = true, Phase = "evaluated", PatientScore = 70, CounselorScore = 70 };
        var result = service.Settle(evaluation, new TestRewards(), "test_photo");
        service = new ProgressService(store);
        Check(service.State.Phase == ProgressPhase.Presenting && service.State.ClearedCount == 1, "Unity restore failed");
        service.Settle(evaluation, new TestRewards(), "test_photo");
        Check(service.State.Economy.Balance == 17 && service.State.Photos.Count == 1, "Unity duplicate settlement");
        service.CompletePresentation(result.Identity.Key); service.Depart();
        Check(service.State.CurrentIndex == 1, "Unity progression failed");
        Debug.Log("B_PROGRESS_CHECKS_PASS " + checks + " (Editor integration; not PlayMode/server)");
    }
}
#endif
