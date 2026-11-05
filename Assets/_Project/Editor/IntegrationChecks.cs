using System;
using System.IO;
using System.Linq;
using Gamsamo.Economy;
using Gamsamo.Progression;
using Gamsamo.Save;
using Gamsamo.UI;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class IntegrationChecks
{
    private static int count;
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); count++; Debug.Log("INTEGRATION PASS " + description); }
    public static void Run()
    {
        count = 0;
        BProgressIntegrationChecks.Run();
        Gamsamo.Editor.Presentation.PresentationChecks.Run();
        Gamsamo.Editor.CosmeticPriceChecks.Run();
        Gamsamo.Editor.PatientAiInputChecks.Run();
        var aiRoot = Path.Combine(Path.GetTempPath(), "abcd-ai-" + Guid.NewGuid().ToString("N"));
        var repository = new Gamsamo.Networking.AiCaseRepository(aiRoot,
            new Gamsamo.Networking.AiPatientCatalog(Path.Combine(Application.streamingAssetsPath, "GameData")));
        var key = new Gamsamo.Networking.AiConversationKey(Guid.NewGuid().ToString("N"), "seoyun", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        repository.StartNewCase(key);
        Check(Gamsamo.Networking.AiConversationKey.FromState(repository.Resume(key).State).SessionId == key.SessionId, "Windows Mono scoped AI save handles long game/case paths");
        var file = Path.Combine(Path.GetTempPath(), "abcd-check-" + Guid.NewGuid().ToString("N"), "progress.json");
        var store = new GameProgressStore(file);
        var progress = new ProgressService(store);
        progress.Create(new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" });
        progress.PrepareAiOperation();
        var original = progress.State.Current;
        progress.PrepareAiOperation();
        Check(progress.State.Current.RoundId == original.RoundId, "Durable AI intent survives repeated preparation");
        progress.BeginVisit();
        progress.CaptureAi("seoyun", original.CaseId, original.RoundId, "test-only", "evaluated");
        progress.CompleteAiOperation(original.RoundId);
        var evaluation = new SavedEvaluation { Identity = new RoundIdentity { ProgressId = progress.State.ProgressId,
            PatientId = "seoyun", CaseId = original.CaseId, RoundId = original.RoundId },
            AiSaveCompleted = true, Phase = "evaluated", PatientScore = 70, CounselorScore = 70 };
        var bridge = new EconomyIntegration(progress);
        var receipt = progress.Settle(evaluation, bridge, "seoyun_smile");
        Check(receipt.Granted == 240 && progress.State.Economy.Ledger.balance == 240, "Real C policy committed atomically with B progress");
        progress.Settle(evaluation, bridge, "seoyun_smile");
        Check(progress.State.Economy.Ledger.settlements.Count == 1 && progress.State.Photos.Count == 1, "Duplicate result grants once");
        progress.SavePresentationStage(receipt.Identity.Key, "ending");
        Check(new ProgressService(store).State.PresentationStage == "ending", "Playback stage survives restart");
        progress.SavePresentationStage(receipt.Identity.Key, "skipped"); progress.Depart();
        Check(progress.State.Current.PatientId == "dohyeon", "Missing media does not block next patient");
        var catalog = AssetDatabase.LoadAssetAtPath<CosmeticCatalog>("Assets/_Project/Settings/CosmeticCatalog.asset");
        var wallet = new EconomyWallet(progress.State.ProgressId, bridge, catalog);
        Check(wallet.Reload() == EconomyStatus.Success && wallet.Balance == 240, "Wallet reads common save");
        Check(wallet.Purchase("window.oak", 50) == EconomyStatus.Success && wallet.Balance == 190, "Purchase commits to common save");
        Check(wallet.Equip("window.oak") == EconomyStatus.Success, "Equip purchased item");
        var restored = new ProgressService(store);
        Check(restored.State.Economy.Ledger.balance == 190 && restored.State.Economy.Ledger.equippedItems.Any(i => i.itemId == "window.oak"), "Restart restores coins ownership and equipment");
        store.BeforeReplace = () => { throw new IOException("injected test-only write failure"); };
        Check(wallet.Purchase("door.oak", 40) != EconomyStatus.Success, "Failed write rejects purchase");
        Check(store.Load().Economy.Ledger.balance == 190 && !store.Load().Economy.Ledger.ownedItemIds.Contains("door.oak"), "Failed write preserves money and inventory");
        store.BeforeReplace = null;
        for (int i = 1; i < 5; i++)
        {
            progress.PrepareAiOperation();
            var patient = progress.State.Current;
            progress.BeginVisit();
            progress.CaptureAi(patient.PatientId, patient.CaseId, patient.RoundId, "test-only", "evaluated");
            progress.CompleteAiOperation(patient.RoundId);
            var result = progress.Settle(new SavedEvaluation { Identity = new RoundIdentity {
                ProgressId = progress.State.ProgressId, PatientId = patient.PatientId, CaseId = patient.CaseId, RoundId = patient.RoundId },
                AiSaveCompleted = true, Phase = "evaluated", PatientScore = 70, CounselorScore = 70 }, bridge, patient.PatientId + "_smile");
            progress.SavePresentationStage(result.Identity.Key, "completed"); progress.Depart();
        }
        Check(progress.State.Phase == ProgressPhase.Finished && progress.State.ClearedCount == 5 && progress.State.FinalEndingUnlocked,
            "Five real progression/economy transactions unlock final ending without media");
        progress.CompleteFinalEnding();
        Check(new ProgressService(store).State.FinalEndingPlayed && progress.State.Economy.Ledger.settlements.Count == 5,
            "Final ending completion persists without an extra reward");
        Debug.Log("ABCD_INTEGRATION_CHECKS_PASS " + count);
    }
    public static void Build()
    {
        Run();
        string oldName = PlayerSettings.productName;
        try
        {
            // Distinct Application.persistentDataPath keeps manual E2E away from player saves.
            PlayerSettings.productName = "GC_gamsamo_Integration_E2E";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/_Project/Scenes/StartMenu.unity", "Assets/_Project/Scenes/ConsultationRoom.unity" },
                locationPathName = "artifacts/integration/player/GC_gamsamo_E2E.exe",
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("E2E build failed: " + report.summary.result);
            Debug.Log("ABCD_WINDOWS_BUILD_PASS");
        }
        finally { PlayerSettings.productName = oldName; }
    }
}
