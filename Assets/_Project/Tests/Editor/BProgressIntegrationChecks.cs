#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using Gamsamo.Patients;
using Gamsamo.Progression;
using Gamsamo.Save;
using Gamsamo.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class BProgressIntegrationChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }

    [MenuItem("Gamsamo/B/Run Progress Integration Checks")]
    public static void Run()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Run these isolated checks outside PlayMode.");
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
        service.Create(new[] { "a", "b", "c", "d", "e" }); service.BeginVisit();
        service.CaptureAi("a", "case_a", "r1", "test-only", "evaluated");
        var evaluation = new SavedEvaluation { Identity = new RoundIdentity { ProgressId = service.State.ProgressId,
            PatientId = "a", CaseId = "case_a", RoundId = "r1" }, AiSaveCompleted = true, Phase = "evaluated", PatientScore = 70, CounselorScore = 70 };
        var result = service.Settle(evaluation, new EconomyIntegration(service), "test_photo");
        service = new ProgressService(store);
        Check(service.State.Phase == ProgressPhase.Presenting && service.State.ClearedCount == 1, "Unity restore failed");
        service.Settle(evaluation, new EconomyIntegration(service), "test_photo");
        Check(service.State.Economy.Balance == 440 && service.State.Photos.Count == 1, "Unity duplicate settlement preserves starting coins");
        service.CompletePresentation(result.Identity.Key); service.Depart();
        Check(service.State.CurrentIndex == 1, "Unity progression failed");
        CheckRecordReset(prefab);
        Debug.Log("B_PROGRESS_CHECKS_PASS " + checks + " (Editor integration; not PlayMode/server)");
    }

    // Editor-only fixtures. Never open GameProgressSession or write the player's active save folder.
    private static void CheckRecordReset(GameObject prefab)
    {
        var fixture = new GameObject("B isolated record reset checks");
        fixture.SetActive(false);
        try
        {
            var instance = UnityEngine.Object.Instantiate(prefab, fixture.transform);
            var visits = instance.GetComponentInChildren<PatientVisitController>(true);
            var room = visits.room;
            var view = room.recordView;
            var runtime = visits.gameObject.AddComponent<GameProgressRuntime>();
            SetPrivate(runtime, "visits", visits);
            view.EnsureDetailsControls();
            room.ClearRecords();

            string root = Path.Combine(Path.GetTempPath(), "b-record-reset-" + Guid.NewGuid().ToString("N"));
            string[] roster = visits.patients.Select(patient => patient.patientId).ToArray();
            string patientId = roster[0];
            AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath,
                "GameData", "Patients", patientId + ".json"))));
            var oldStore = new GameProgressStore(Path.Combine(root, "previous.json"));
            var oldRun = new ProgressService(oldStore);
            oldRun.Create(roster);
            oldRun.CaptureAi(patientId, "record_case", "record_round_2", RecordSnapshot(oldRun, patientId), "active");
            byte[] oldSaved = File.ReadAllBytes(Path.Combine(root, "previous.json"));
            BindProgress(runtime, oldRun);
            runtime.RestoreRecords();
            var records = Private<List<MedicalRecordData>>(room, "patientRecords");
            Check(records.Count == 2 && records.All(record => record.gameProgressId == oldRun.State.ProgressId),
                "restore uses only the selected run and retains both session archives");
            Check(records[0].consultationTranscript.Contains("TEST_FIRST") && records[1].consultationTranscript.Contains("TEST_SECOND"),
                "restored session transcripts remain separate");

            var active = new MedicalRecordData { gameProgressId = oldRun.State.ProgressId, caseId = "record_case", sessionId = "record_round_2",
                registrationNumber = patientId, patientName = "TEST_NAME", gender = "TEST_GENDER", age = "TEST_AGE",
                occupation = "TEST_JOB", recordedDate = "TEST_DATE", visitReason = "TEST_REASON", medicalHistory = "TEST_HISTORY",
                medication = "TEST_MEDICATION", specialNotes = "TEST_FACT", counselor = "TEST_COUNSELOR",
                consultationNotes = "TEST_NOTES", consultationTranscript = "TEST_TRANSCRIPT", consultationResult = "TEST_RESULT" };
            room.SetRecord(active);
            Check(records.Count == 3, "current record coexists with this run's archives");
            Check(view.SetInformationState(patientId, "medicalHistory", MedicalRecordView.InformationState.Disclosed, "TEST_DISCLOSED"),
                "fixture has disclosed record fields");
            Check(view.SetCommittedSettlement(patientId, true, "TEST_SETTLEMENT"), "fixture has committed settlement display");
            view.SelectSection(MedicalRecordView.RecordSection.Result);
            Check(view.consultationNotes.text.Contains("TEST_RESULT") && view.consultationNotes.text.Contains("TEST_SETTLEMENT"),
                "fixture displays result and settlement before reset");
            view.consultationNotes.pageToDisplay = 4;
            SetPrivate(room, "selectedRecord", 2);

            room.ClearRecords();
            Check(string.IsNullOrEmpty(room.record.registrationNumber) && string.IsNullOrEmpty(room.record.consultationNotes)
                && string.IsNullOrEmpty(room.record.consultationTranscript) && string.IsNullOrEmpty(room.record.consultationResult)
                && records.Count == 0 && Private<Dictionary<string, MedicalRecordData>>(room, "archivedRecords").Count == 0,
                "clear removes active and archived record caches");
            Check(room.record.counselor == "TEST_COUNSELOR", "clear preserves the counselor setting for the next consultation");
            Check(Private<int>(room, "selectedRecord") == 0 && Private<int>(view, "recordCount") == 0
                && view.DetailPage == 1 && view.Section == MedicalRecordView.RecordSection.Summary,
                "clear resets document selection, section and pagination");
            Check(new[] { view.registrationNumber, view.patientName, view.gender, view.age, view.occupation, view.recordedDate,
                view.visitReason, view.medicalHistory, view.medication, view.specialNotes, view.counselor }.All(label => label.text == "—"),
                "clear removes all patient fields including disclosed values");
            foreach (MedicalRecordView.RecordSection section in Enum.GetValues(typeof(MedicalRecordView.RecordSection)))
            {
                view.SelectSection(section);
                Check(!view.consultationNotes.text.Contains("TEST_") && !view.consultationNotes.text.Contains("정산 내역"),
                    "clear removes detail and settlement text from " + section);
            }
            Check(Private<TMP_Text>(view, "recordLabel").text == ""
                && !Private<UnityEngine.UI.Button>(view, "previousRecord").gameObject.activeSelf
                && !Private<UnityEngine.UI.Button>(view, "nextRecord").gameObject.activeSelf,
                "empty record hides document navigation");

            var newStore = new GameProgressStore(Path.Combine(root, "new.json"));
            var newRun = new ProgressService(newStore);
            newRun.Create(roster);
            byte[] newSaved = File.ReadAllBytes(Path.Combine(root, "new.json"));
            BindProgress(runtime, newRun);
            runtime.RestoreRecords();
            Check(records.Count == 0 && string.IsNullOrEmpty(room.record.consultationTranscript), "new game does not restore earlier run transcripts");
            room.SetRecord(new MedicalRecordData { gameProgressId = newRun.State.ProgressId, caseId = "new_case", sessionId = "new_round",
                registrationNumber = patientId, patientName = "TEST_NEW_NAME" });
            Check(view.patientName.text == "TEST_NEW_NAME" && view.medicalHistory.text == "—",
                "same patient in a new run does not inherit disclosed fields");
            view.SelectSection(MedicalRecordView.RecordSection.Result);
            Check(!view.consultationNotes.text.Contains("TEST_") && !view.consultationNotes.text.Contains("정산 내역"),
                "same patient in a new run does not inherit result or reward display");

            room.ClearRecords();
            BindProgress(runtime, new ProgressService(oldStore));
            runtime.RestoreRecords();
            Check(records.Count == 2 && records.All(record => record.gameProgressId == oldRun.State.ProgressId),
                "explicit continue still restores saved session archives after reset");
            Check(oldSaved.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "previous.json")))
                && newSaved.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "new.json"))),
                "display reset and restore do not modify either persisted progress or economy");
        }
        finally { UnityEngine.Object.DestroyImmediate(fixture); }
    }

    private static string RecordSnapshot(ProgressService progress, string patientId)
    {
        var state = AiProtocol.EmptyState(patientId, new AiConversationKey(progress.State.ProgressId, patientId, "record_case", "record_round_1"));
        var first = AiProtocol.Current(state);
        first["opening"] = new JObject { ["id"] = "first_opening", ["patient_text"] = "TEST_FIRST" };
        var second = AiProtocol.Copy(first);
        second["id"] = "record_round_2";
        second["opening"] = new JObject { ["id"] = "second_opening", ["patient_text"] = "TEST_SECOND" };
        AiProtocol.Array(state, "sessions").Add(second);
        return AiProtocol.Encode(new JObject { ["state"] = state });
    }

    private static T Private<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    private static void SetPrivate(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static void BindProgress(GameProgressRuntime runtime, ProgressService progress) =>
        typeof(GameProgressRuntime).GetProperty("Progress").GetSetMethod(true).Invoke(runtime, new object[] { progress });
}
#endif
