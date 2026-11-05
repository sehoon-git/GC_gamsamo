using System;
using System.IO;
using System.Linq;
using Gamsamo.Progression;
using Gamsamo.Save;
using Gamsamo.UI.Presentation;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class ProgressionContentChecks
{
    private static int count;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Debug.Log("CONTENT PASS " + message); }
    public static void Run()
    {
        count = 0;
        string root = Path.Combine(Path.GetTempPath(), "progress-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        var store = new GameProgressStore(path); var service = new ProgressService(store);
        service.Create(new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" });
        service.SaveProfile("상담사", "첫째 줄\n둘째 줄");
        Check(new ProgressService(store).State.Profile.Name == "상담사", "profile restart");
        var original = File.ReadAllText(path); int revision = service.State.Revision;
        bool failed = false, succeeded = false;
        store.BeforeReplace = () => { throw new IOException("injected write failure"); };
        service.RequestProfileSave("다른 이름", "", _ => succeeded = true, _ => failed = true);
        Check(failed && !succeeded && File.ReadAllText(path) == original && service.State.Revision == revision && service.State.Profile.Name == "상담사", "profile failed write preserves memory and disk");
        store.BeforeReplace = null; failed = false;
        service.RequestProfileSave(new string('a', 41), "", _ => succeeded = true, _ => failed = true);
        Check(failed && File.ReadAllText(path) == original, "profile validation");
        string second = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        var other = new ProgressService(new GameProgressStore(second)); other.Create(service.State.Patients.Select(p => p.PatientId));
        Check(other.State.Profile.Name == "" && service.State.Profile.Name == "상담사", "profile belongs to run");
        var json = JObject.Parse(original); json.Remove("Profile"); File.WriteAllText(second, json.ToString());
        Check(new GameProgressStore(second).Load().Profile.Name == "", "v1 missing optional profile remains readable");
        json["Patients"].Last.AddAfterSelf(JObject.FromObject(new PatientProgress { PatientId = "minseok" }));
        File.WriteAllText(second, json.ToString());
        Check(ProgressSaveInspection.Inspect(second).Kind == SaveKind.LegacySixPatients, "six-patient preflight");
        var legacyBytes = File.ReadAllBytes(second);
        string export = second + ".copy"; ProgressSaveInspection.CopyForRecovery(second, export);
        Check(File.ReadAllBytes(second).SequenceEqual(legacyBytes) && File.ReadAllBytes(export).SequenceEqual(legacyBytes), "explicit backup preserves original without migration");
        bool rejected = false; try { ProgressSaveInspection.CopyForRecovery(second, second); } catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(second).SequenceEqual(legacyBytes), "cannot overwrite recovery destination");
        json = JObject.Parse(original); json["Economy"]["Balance"] = 10; File.WriteAllText(second, json.ToString());
        Check(ProgressSaveInspection.Inspect(second).Kind == SaveKind.LegacyEconomy, "old economy identified");
        json["Version"] = 99; File.WriteAllText(second, json.ToString());
        Check(ProgressSaveInspection.Inspect(second).Kind == SaveKind.UnsupportedVersion, "future schema identified");
        File.WriteAllText(second, "{broken");
        var scan = ProgressSaveInspection.Scan(root);
        Check(scan.Count(s => s.Kind == SaveKind.Current) == 1 && scan.Count(s => s.Kind == SaveKind.Corrupt) == 1, "one corrupt archive does not hide valid run");
        Check(File.ReadAllText(second) == "{broken", "scan never rewrites source");
        Check(ApprovedRankPolicy.Evaluate(5, null, false).Current == "승급 기준 준비 중", "no invented rank policy");
        var catalog = AssetDatabase.LoadAssetAtPath<EndingContentCatalog>("Assets/_Project/Settings/Presentation/EndingContent.asset");
        Check(catalog != null && catalog.patients.Length == 5 && catalog.patients.All(p => !p.approved && p.photoId == p.patientId + "_smile"), "five pending content entries preserve entitlements");
        Check(catalog.Audit().Contains("roomMusic 누락") && catalog.Audit().Contains("크레딧 승인 대기"), "approval and missing assets audited separately");
        Check(PresentationAudioRegistration.Validate(Path.Combine(root, "missing.json")).Length == 1, "missing manifest cannot register");
        string manifest = Path.Combine(root, "audio.json");
        File.WriteAllText(manifest, "{\"tracks\":[{\"slot\":\"roomMusic\",\"path\":\"../bad.wav\",\"approved\":false}]}");
        Check(PresentationAudioRegistration.Validate(manifest).Length >= 5, "manifest approval provenance paths and required slots checked");
        var backup = ProgressSaveInspection.CopyForRecovery(path + ".bak", Path.Combine(root, "recovered.json"));
        Check(backup.Kind == SaveKind.Current && File.ReadAllText(path) == original, "backup inspection and recovery copy never replace active save");
        ReplayChecks(service);
        Debug.Log("PROGRESSION_CONTENT_CHECKS_PASS " + count);
        IntegrationChecks.Run();
    }
    private static void ReplayChecks(ProgressService service)
    {
        service.PrepareAiOperation(); service.BeginVisit(); var p = service.State.Current;
        service.CaptureAi(p.PatientId, p.CaseId, p.RoundId, "synthetic-test-only", "evaluated"); service.CompleteAiOperation(p.RoundId);
        var receipt = service.Settle(new SavedEvaluation { Identity = new RoundIdentity { ProgressId = service.State.ProgressId,
            PatientId = p.PatientId, CaseId = p.CaseId, RoundId = p.RoundId }, AiSaveCompleted = true, Phase = "evaluated",
            PatientScore = 70, CounselorScore = 70 }, new EconomyIntegration(service), p.PatientId + "_smile");
        service.SavePresentationStage(receipt.Identity.Key, "skipped");
        var host = new GameObject("test-only replay integration");
        var panel = new GameObject("test-only replay panel");
        var fixture = ScriptableObject.CreateInstance<EndingContentCatalog>();
        try
        {
            var runtime = host.AddComponent<GameProgressRuntime>();
            typeof(GameProgressRuntime).GetProperty("Progress").SetValue(runtime, service);
            var adapter = host.AddComponent<GamePresentationIntegration>();
            var view = host.AddComponent<EndingPresentationView>(); view.panel = panel; view.content = fixture;
            view.BindModalGate(() => true, () => { });
            int writes = 0; view.PlaybackSaveRequested += (_, __, ___, ____, _____) => writes++;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/ConsultationRoomUI.prefab");
            var visits = prefab.GetComponentInChildren<Gamsamo.Dialogue.PatientVisitController>(true);
            foreach (var pair in new[] { new { Name = "runtime", Value = (object)runtime }, new { Name = "visits", Value = (object)visits }, new { Name = "ending", Value = (object)view } })
                typeof(GamePresentationIntegration).GetField(pair.Name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(adapter, pair.Value);
            adapter.content = fixture;
            Check(adapter.GetEndingAvailability()[0].Unlocked && !adapter.GetEndingAvailability()[0].Ready && !adapter.TryReplayEnding(p.PatientId, out _), "unlocked but missing story is pending");
            fixture.patients = new[] { new EndingContent { patientId = p.PatientId, photoId = p.PatientId + "_smile", approved = true, title = "TEST ONLY", description = "Synthetic replay fixture" } };
            string before = Newtonsoft.Json.JsonConvert.SerializeObject(service.State);
            Check(adapter.TryReplayEnding(p.PatientId, out _) && view.IsReplay && view.IsOpen, "public replay adapter opens approved unlocked ending without photo");
            view.Skip();
            Check(!view.IsOpen && writes == 0 && Newtonsoft.Json.JsonConvert.SerializeObject(service.State) == before, "replay skip leaves receipt coins and first playback state unchanged");
            Check(adapter.TryReplayEnding(p.PatientId, out _), "replay can reopen"); view.Skip();
            Check(writes == 0 && service.State.Settlements.Count == 1, "repeat replay never settles");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(panel); UnityEngine.Object.DestroyImmediate(fixture); }
    }
    public static void Build()
    {
        Run();
        string oldName = PlayerSettings.productName;
        try
        {
            PlayerSettings.productName = "GC_gamsamo_ProgressionContent_E2E";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes = new[] { "Assets/_Project/Scenes/StartMenu.unity", "Assets/_Project/Scenes/ConsultationRoom.unity" },
                locationPathName = "artifacts/progression-content/player/GC_gamsamo_E2E.exe",
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("Windows build failed: " + report.summary.result);
            Debug.Log("PROGRESSION_CONTENT_WINDOWS_BUILD_PASS");
        }
        finally { PlayerSettings.productName = oldName; }
    }
}
