using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using Gamsamo.Patients;
using Gamsamo.Save;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamsamo.Progression
{
    public static class GameProgressSession
    {
        public static string Root => Path.Combine(Application.persistentDataPath, "game-progress");
        private static string Pointer => Path.Combine(Root, "active.txt");
        public static ProgressService Open()
        {
            if (!File.Exists(Pointer)) return null;
            string name = File.ReadAllText(Pointer).Trim();
            if (!Guid.TryParseExact(name, "N", out _)) throw new IOException("진행 선택 파일이 손상되었습니다. active.txt와 진행 백업을 복구해 주세요.");
            var service = new ProgressService(new GameProgressStore(Path.Combine(Root, name + ".json")));
            if (service.State == null) throw new IOException("선택한 진행 저장이 없습니다. 기존 파일을 복구해 주세요.");
            return service;
        }
        public static ProgressService Create(string[] roster)
        {
            // A corrupt active selection is not silently replaced, even from the new game button.
            Open();
            Directory.CreateDirectory(Root);
            string fileId = Guid.NewGuid().ToString("N");
            var service = new ProgressService(new GameProgressStore(Path.Combine(Root, fileId + ".json")));
            service.Create(roster);
            string temp = Pointer + "." + fileId + ".tmp";
            File.WriteAllText(temp, fileId);
            if (File.Exists(Pointer)) File.Replace(temp, Pointer, Pointer + ".bak"); else File.Move(temp, Pointer);
            return service;
        }
        public static bool NewGameRequested;
        public static IReadOnlyList<GameProgress> SavedRuns()
        {
            if (!Directory.Exists(Root)) return new GameProgress[0];
            return Directory.GetFiles(Root, "*.json")
                .Where(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _))
                .Select(file => new GameProgressStore(file).Load()).ToArray();
        }
    }

    /// <summary>B's scene integration. Uses A's existing snapshot/session APIs without owning AI logic.</summary>
    public sealed class GameProgressRuntime : MonoBehaviour
    {
        public IRewardCalculator Rewards { get; set; }
        public Func<string, string> PhotoForPatient { get; set; }
        public event Action<ConfirmedResult> ResultCommitted;
        public ProgressService Progress { get; private set; }
        public string LastError { get; private set; }
        private PatientVisitController visits;
        private PatientAiAdapter ai;
        private bool handling, revisitPending;
        private readonly HashSet<string> prepared = new HashSet<string>();
        private IDisposable presentationLock;
        private void Start()
        {
            visits = GetComponent<PatientVisitController>(); ai = GetComponent<PatientAiAdapter>();
            try
            {
                var roster = visits.patients.Select(p => p.patientId).ToArray();
                Progress = GameProgressSession.NewGameRequested ? GameProgressSession.Create(roster) : GameProgressSession.Open();
                GameProgressSession.NewGameRequested = false;
                if (Progress == null) Progress = GameProgressSession.Create(roster);
                if (!Progress.State.Patients.Select(p => p.PatientId).SequenceEqual(roster)) throw new InvalidOperationException("저장 환자 목록과 씬 구성이 다릅니다. 기존 진행을 보존해 주세요.");
                foreach (var patientId in roster)
                {
                    string file = Path.Combine(Application.streamingAssetsPath, "GameData", "Patients", patientId + ".json");
                    if (File.Exists(file)) AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(file)));
                }
                visits.useDemoDialogue = false;
                visits.BeforeAdmission = BeforeAdmission;
                visits.BeforeDeparture = BeforeDeparture;
                visits.GetProgressIndex = () => Progress.State.CurrentIndex;
                visits.GetClearedCount = () => Progress.State.ClearedCount;
                if (ai != null) ai.StateChanged += OnAiChanged;
                RestoreRecords();
                visits.ProgressReady = true;
                if (Progress.State.Phase == ProgressPhase.Finished) visits.TryAdmitNextPatient();
                if (Progress.State.Phase != ProgressPhase.Waiting && Progress.State.Phase != ProgressPhase.Finished)
                    visits.TryAdmitNextPatient();
            }
            catch (Exception e) { Fail(e); }
        }
        private bool BeforeAdmission(string patientId)
        {
            try
            {
                if (Progress.State.Current?.PatientId != patientId) return false;
                if (visits.UsesExternalDialogue == null || !visits.UsesExternalDialogue(patientId))
                    throw new InvalidOperationException("이 환자의 정식 AI 연결이 아직 없습니다. A의 콘텐츠 연결이 필요합니다.");
                var patient = Progress.State.Current;
                if (prepared.Add(patientId))
                {
                    try
                    {
                        AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "GameData", "Patients", patientId + ".json"))));
                        var store = new AiSaveStore(PatientAiAdapter.SaveDirectoryFor(patientId));
                        var active = store.Load();
                        var saved = string.IsNullOrEmpty(patient.AiSnapshot) ? null : AiProtocol.Parse(patient.AiSnapshot);
                        // Recover the window where A saved but B had not captured its notification yet.
                        if (saved != null && active != null && (string)active["state"]?["case_id"] == patient.CaseId
                            && (int)active["local_revision"] >= (int)saved["local_revision"]) saved = active;
                        var desired = saved ?? AiSaveStore.NewSnapshot(patientId);
                        if (!JToken.DeepEquals(active, desired))
                        {
                            int revision = active == null ? -1 : (int)active["local_revision"];
                            desired["local_revision"] = revision + 1; store.Save(desired, revision);
                        }
                        var state = AiProtocol.Object(desired, "state");
                        // Bind before any network request so a restart never adopts a different case.
                        Progress.CaptureAi(patientId, AiProtocol.Id(state, "case_id"), AiProtocol.Id(AiProtocol.Current(state), "id"),
                            AiProtocol.Encode(desired), AiProtocol.Phase(state));
                    }
                    catch { prepared.Remove(patientId); throw; }
                }
                revisitPending = patient.NeedsRevisit && Progress.State.Phase != ProgressPhase.AwaitingDeparture;
                if (Progress.State.Phase == ProgressPhase.Waiting) Progress.BeginVisit();
                LastError = ""; return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }
        private void OnAiChanged()
        {
            if (handling || Progress == null || ai == null || !ai.IsAiVisit) return;
            handling = true;
            try
            {
                if (revisitPending)
                {
                    if (ai.CurrentPhase == "evaluated") { ai.Revisit(); return; }
                    if (ai.SessionId == Progress.State.Current.RoundId) return;
                    revisitPending = false;
                }
                var snapshot = new AiSaveStore(PatientAiAdapter.SaveDirectoryFor(ai.PatientId)).Load();
                if (snapshot == null) return;
                var state = AiProtocol.Object(snapshot, "state");
                Progress.CaptureAi(ai.PatientId, AiProtocol.Id(state, "case_id"), AiProtocol.Id(AiProtocol.Current(state), "id"),
                    AiProtocol.Encode(snapshot), AiProtocol.Phase(state));
                RestoreRecords();
                if (ai.CurrentPhase != "evaluated" || ai.IsBusy || !string.IsNullOrEmpty(ai.LastError)) return;
                var evaluation = AiProtocol.Object(AiProtocol.Current(state), "evaluation");
                if (ai.IsMock || AiProtocol.Text(evaluation, "model_revision").StartsWith("mock", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("모의 AI 평가는 정식 진행에 정산할 수 없습니다.");
                int patientScore = AiProtocol.Number(evaluation, "patient_score"), counselorScore = AiProtocol.Number(evaluation, "counselor_score");
                string photoId = PhotoForPatient?.Invoke(ai.PatientId);
                if (patientScore >= 70 && counselorScore >= 70 && string.IsNullOrWhiteSpace(photoId))
                    throw new InvalidOperationException("환자별 정식 사진 ID 연결 대기: D의 사진 목록이 필요합니다.");
                var receipt = Progress.Settle(new SavedEvaluation {
                    Identity = new RoundIdentity { ProgressId = Progress.State.ProgressId, PatientId = ai.PatientId, CaseId = ai.CaseId, RoundId = ai.SessionId },
                    AiSaveCompleted = true, Phase = "evaluated", PatientScore = patientScore,
                    CounselorScore = counselorScore }, Rewards, photoId);
                LastError = "";
                if (Progress.State.Phase == ProgressPhase.Presenting && presentationLock == null)
                {
                    presentationLock = visits.room?.AcquireInputBlock();
                    ResultCommitted?.Invoke(receipt);
                    // With no D presenter the durable result remains pending; never fake a completed animation.
                }
            }
            catch (Exception e) { Fail(e); }
            finally { handling = false; }
        }
        public void RetryProgress() => OnAiChanged();
        public ConfirmedResult PendingResult => Progress?.State.PendingPresentation;
        public bool CompletePresentation(string key)
        {
            try { Progress.CompletePresentation(key); presentationLock?.Dispose(); presentationLock = null; LastError = ""; return true; }
            catch (Exception e) { Fail(e); return false; }
        }
        private bool BeforeDeparture()
        {
            try { Progress.Depart(); LastError = ""; return true; }
            catch (Exception e) { Fail(e); return false; }
        }
        public void RestoreRecords()
        {
            if (visits.room == null) return;
            visits.room.ClearArchivedRecords();
            // Previous runs are read-only; browsing never changes the active progress or A's active case.
            foreach (var run in GameProgressSession.SavedRuns())
            foreach (var patient in run.Patients)
            {
                if (string.IsNullOrEmpty(patient.AiSnapshot)) continue;
                var state = AiProtocol.Object(AiProtocol.Parse(patient.AiSnapshot), "state");
                var definition = visits.patients.First(p => p.patientId == patient.PatientId);
                foreach (JObject session in AiProtocol.Array(state, "sessions"))
                {
                    var single = AiProtocol.Copy(state); single["sessions"] = new JArray(session.DeepClone());
                    visits.room.SetArchivedRecord(run.ProgressId + "/" + patient.PatientId + "/" + (string)session["id"], new MedicalRecordData {
                        registrationNumber = patient.PatientId, patientName = definition.displayName,
                        recordedDate = (string)session["started_at"], consultationNotes = "저장된 상담 회차: " + (string)session["id"],
                        consultationTranscript = AiRecordFormatter.Transcript(single), consultationResult = AiRecordFormatter.Results(single, null) });
                }
            }
        }
        public bool ReturnToStartMenu()
        {
            if (visits != null && visits.room != null && visits.room.IsOverlayBlocking) return false;
            if (ai != null && (ai.IsBusy || ai.CanRetry)) { LastError = "요청·자동 저장을 완료한 후 돌아가세요."; return false; }
            try
            {
                if (ai != null && ai.IsAiVisit)
                {
                    var snapshot = new AiSaveStore(PatientAiAdapter.SaveDirectoryFor(ai.PatientId)).Load();
                    if (snapshot == null || AiProtocol.Text(snapshot, "draft") != ai.Draft)
                        throw new InvalidOperationException("초안 자동 저장을 기다린 뒤 다시 눌러 주세요.");
                    var state = AiProtocol.Object(snapshot, "state");
                    Progress.CaptureAi(ai.PatientId, AiProtocol.Id(state, "case_id"), AiProtocol.Id(AiProtocol.Current(state), "id"),
                        AiProtocol.Encode(snapshot), AiProtocol.Phase(state));
                }
                SceneManager.LoadScene("StartMenu"); return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }
        private void Fail(Exception e)
        {
            LastError = e.Message;
            if (visits != null && visits.dialogue != null) visits.dialogue.SetStatus(LastError);
            Debug.LogWarning("Game progress: " + LastError);
        }
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 16, 480, string.IsNullOrEmpty(LastError) ? 36 : 170), GUI.skin.box);
            if (!string.IsNullOrEmpty(LastError))
            {
                GUILayout.Label("진행 저장·연결 확인 필요\n" + LastError);
                if (Progress != null && GUILayout.Button("진행 저장 / 정산 재시도")) RetryProgress();
            }
            if (GUILayout.Button("시작 화면으로")) ReturnToStartMenu();
            GUILayout.EndArea();
        }
        private void OnDestroy() { if (ai != null) ai.StateChanged -= OnAiChanged; presentationLock?.Dispose(); }
    }
}
