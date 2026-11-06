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
        public static string Root => Path.Combine(Application.persistentDataPath,
            DevelopmentProgressMode.Enabled ? "game-progress-development" : "game-progress");
        private static string Pointer => Path.Combine(Root, "active.txt");
        public static ProgressService Open()
        {
            if (!File.Exists(Pointer)) return null;
            string name = File.ReadAllText(Pointer).Trim();
            if (!Guid.TryParseExact(name, "N", out _)) throw new IOException("진행 선택 파일이 손상되었습니다. active.txt와 진행 백업을 복구해 주세요.");
            var service = new ProgressService(new GameProgressStore(Path.Combine(Root, name + ".json")));
            if (service.State == null) throw new IOException("선택한 진행 저장이 없습니다. 기존 파일을 복구해 주세요.");
            if (service.State.DevelopmentMode != DevelopmentProgressMode.Enabled)
                throw new IOException("일반 진행과 개발 진행을 서로 불러올 수 없습니다.");
            return service;
        }
        public static ProgressService Create(string[] roster)
        {
            // A corrupt active selection is not silently replaced, even from the new game button.
            Open();
            Directory.CreateDirectory(Root);
            string fileId = Guid.NewGuid().ToString("N");
            var service = new ProgressService(new GameProgressStore(Path.Combine(Root, fileId + ".json")));
            service.Create(roster, DevelopmentProgressMode.Enabled);
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
                .Select(file => new GameProgressStore(file).Load())
                .Where(run => run.DevelopmentMode == DevelopmentProgressMode.Enabled).ToArray();
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
        private bool handling;
        public Gamsamo.Economy.EconomyWallet Wallet { get; private set; }
        public GamePresentationIntegration Presentation { get; private set; }
        private readonly Dictionary<string, string> settlementDescriptions = new Dictionary<string, string>();
        private string notifiedResult;
        private Gamsamo.UI.Presentation.RoomStatusView roomStatus;
        public bool RoomControlsAvailable => visits?.room == null || !visits.room.IsOverlayBlocking;
        private void Awake()
        {
            var prefab = Resources.Load<GameObject>("Presentation/RoomStatus");
            if (prefab == null) { Debug.LogError("RoomStatus presentation prefab is missing."); return; }
            roomStatus = Instantiate(prefab).GetComponent<Gamsamo.UI.Presentation.RoomStatusView>();
            roomStatus.Bind(this);
        }
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
                if (DevelopmentProgressMode.Enabled)
                {
                    // This is an explicit offline traversal, never an AI error fallback.
                    if (ai != null) ai.enabled = false;
                    var bridge = GetComponent<PatientAiInputBridge>();
                    if (bridge != null) bridge.enabled = false;
                    visits.UsesExternalDialogue = _ => false;
                    visits.ExternalConfirmEnd = null; visits.ExternalFinishVisit = null;
                    visits.PatientArrived += DevelopmentArrived;
                }
                foreach (var patientId in roster)
                {
                    string file = Path.Combine(Application.streamingAssetsPath, "GameData", "Patients", patientId + ".json");
                    if (File.Exists(file)) AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(file)));
                }
                var economy = new EconomyIntegration(Progress);
                Rewards = economy;
                var pc = visits.room?.pcDesktopView;
                if (pc?.shopView?.catalog != null)
                {
                    Wallet = new Gamsamo.Economy.EconomyWallet(Progress.State.ProgressId, economy, pc.shopView.catalog);
                    Wallet.Reload(); pc.BindEconomy(Wallet);
                }
                Presentation = GetComponent<GamePresentationIntegration>();
                if (Presentation != null) Presentation.Bind(this, visits, DevelopmentProgressMode.Enabled ? null : ai);
                visits.useDemoDialogue = false;
                visits.BeforeAdmission = BeforeAdmission;
                visits.BeforeDeparture = BeforeDeparture;
                visits.GetProgressIndex = () => Progress.State.CurrentIndex;
                visits.GetClearedCount = () => Progress.State.ClearedCount;
                if (visits.room != null) visits.room.CommittedSettlementForRecord = data =>
                    settlementDescriptions.TryGetValue(data.gameProgressId + "/" + data.registrationNumber + "/" + data.caseId + "/" + data.sessionId, out var description) ? description : "";
                if (ai != null && !DevelopmentProgressMode.Enabled) ai.StateChanged += OnAiChanged;
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
                if (DevelopmentProgressMode.Enabled)
                {
                    Progress.BeginDevelopmentVisit(); LastError = ""; return true;
                }
                if (visits.UsesExternalDialogue == null || !visits.UsesExternalDialogue(patientId))
                    throw new InvalidOperationException("이 환자의 정식 AI 연결이 아직 없습니다. A의 콘텐츠 연결이 필요합니다.");
                Progress.PrepareAiOperation();
                var patient = Progress.State.Current;
                var key = new AiConversationKey(Progress.State.ProgressId, patientId, patient.CaseId, patient.RoundId);
                bool accepted;
                if (patient.PendingAiOperation == "start") accepted = ai.StartNewCase(key);
                else if (patient.PendingAiOperation == "revisit")
                    accepted = ai.StartRevisit(key.WithSession(patient.PreviousRoundId), patient.RoundId);
                else accepted = ai.ResumeSession(key);
                if (!accepted) throw new InvalidOperationException(ai.LastError);
                if (Progress.State.Phase == ProgressPhase.Waiting) Progress.BeginVisit();
                LastError = ""; return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }
        private void OnAiChanged()
        {
            if (DevelopmentProgressMode.Enabled || handling || Progress == null || ai == null || !ai.IsAiVisit) return;
            handling = true;
            try
            {
                var key = ai.ConversationKey;
                if (key == null || key.GameProgressId != Progress.State.ProgressId) return;
                // During a pending revisit, A's committed state still belongs to the previous round.
                if (key.SessionId != Progress.State.Current?.RoundId) return;
                var snapshot = PatientAiAdapter.CreateCaseRepository().Store(key).Load();
                if (snapshot == null) return;
                var state = AiProtocol.Object(snapshot, "state");
                Progress.CaptureAi(ai.PatientId, key.CaseId, key.SessionId, AiProtocol.Encode(snapshot), AiProtocol.Phase(state));
                Progress.CompleteAiOperation(key.SessionId);
                RestoreRecords();
                if (!ai.TryGetSavedEvaluation(out var evaluation)) return;
                Presentation?.BeginResultSave();
                var receipt = Progress.Settle(new SavedEvaluation {
                    Identity = new RoundIdentity { ProgressId = evaluation.Key.GameProgressId, PatientId = evaluation.Key.PatientId,
                        CaseId = evaluation.Key.CaseId, RoundId = evaluation.Key.SessionId },
                    AiSaveCompleted = true, Phase = "evaluated", PatientScore = evaluation.PatientScore,
                    CounselorScore = evaluation.CounselorScore }, Rewards, PhotoForPatient?.Invoke(ai.PatientId));
                Wallet?.Reload();
                RestoreRecords();
                Presentation?.CompleteResultSave();
                LastError = "";
                if (Progress.State.Phase == ProgressPhase.Presenting && notifiedResult != receipt.Identity.Key)
                {
                    notifiedResult = receipt.Identity.Key;
                    // D acquires the presentation lease; persisted phase also gates departure.
                    ResultCommitted?.Invoke(receipt);
                    // With no D presenter the durable result remains pending; never fake a completed animation.
                }
            }
            catch (Exception e) { Fail(e); Presentation?.FailResultSave(LastError); }
            finally { handling = false; }
        }
        public void RetryProgress()
        {
            if (DevelopmentProgressMode.Enabled) SkipDevelopmentPatient(); else OnAiChanged();
        }

        private void DevelopmentArrived(string visitId, string patientId)
        {
            if (!DevelopmentProgressMode.Enabled) return;
            var view = visits.dialogue;
            view.SetApiMode(false); view.SetNextAvailable(false); view.SetEndAvailable(false);
            view.SetConnectionLabel("개발 진행 미리보기 · AI 상담/평가 없음");
            view.ShowLine("개발 안내", "개발 스킵으로 이 내담자의 70/70 클리어와 엔딩을 미리 볼 수 있습니다.");
            view.SetStatus("별도 개발 저장 사용 · 실제 평가·코인 지급 없음");
            if (Progress.State.Phase == ProgressPhase.Presenting || Progress.State.Phase == ProgressPhase.AwaitingDeparture)
                visits.MarkConsultationEnded(visitId);
        }

        public bool CanSkipDevelopmentPatient => DevelopmentProgressMode.Enabled && Progress?.State.DevelopmentMode == true
            && Progress.State.Phase == ProgressPhase.Consulting && visits != null
            && visits.State == PatientVisitController.VisitState.Consulting && RoomControlsAvailable;

        public bool SkipDevelopmentPatient()
        {
            if (!CanSkipDevelopmentPatient) return false;
            try
            {
                var state = Progress.State; var patient = state.Current;
                var receipt = Progress.SkipDevelopmentVisit(new RoundIdentity { ProgressId = state.ProgressId,
                    PatientId = patient.PatientId, CaseId = patient.CaseId, RoundId = patient.RoundId }, PhotoForPatient?.Invoke(patient.PatientId));
                // Only the durable, marked development receipt enables presentation/departure.
                visits.MarkConsultationEnded(visits.CurrentSessionId);
                visits.dialogue.SetStatus("개발 스킵 저장 완료 · 환자 70 / 상담자 70 · 실제 AI 평가 아님");
                LastError = ""; ResultCommitted?.Invoke(receipt); return true;
            }
            catch (Exception e) { Fail(e); return false; }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!DevelopmentProgressMode.Enabled) return;
            GUI.Box(new Rect(12, 12, 360, 105), "DEV PREVIEW | separate save | no AI / coins");
            bool previous = GUI.enabled;
            GUI.enabled = previous && CanSkipDevelopmentPatient;
            if (GUI.Button(new Rect(24, 42, 336, 34), "Skip patient (70 / 70)")) SkipDevelopmentPatient();
            GUI.enabled = previous;
            GUI.Label(new Rect(24, 80, 336, 24), Progress?.State.Phase == ProgressPhase.Finished
                ? "5 / 5 - final ending unlocked" : "Play stopped: disable via Gamsamo > Development");
        }
#endif
        public ConfirmedResult PendingResult => Progress?.State.PendingPresentation;
        public bool CompletePresentation(string key)
        {
            try { Progress.CompletePresentation(key); LastError = ""; return true; }
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
            settlementDescriptions.Clear();
            var runs = GameProgressSession.SavedRuns();
            foreach (var run in runs)
            foreach (var receipt in run.Economy.Ledger?.settlements ?? new List<Gamsamo.Economy.SessionSettlement>())
                settlementDescriptions[run.ProgressId + "/" + receipt.patientId + "/" + receipt.caseId + "/" + receipt.sessionId] =
                    "기본 보상 " + receipt.baseCoins + " C · 첫 클리어 보너스 " + receipt.bonusCoins + " C\n이번 지급 " + (receipt.baseCoins + receipt.bonusCoins) + " C\n" + (receipt.cleared ? "클리어 완료" : "재상담이 필요합니다");
            // Previous runs are read-only; browsing never changes the active progress or A's active case.
            foreach (var run in runs)
            foreach (var patient in run.Patients)
            {
                if (string.IsNullOrEmpty(patient.AiSnapshot)) continue;
                var state = AiProtocol.Object(AiProtocol.Parse(patient.AiSnapshot), "state");
                var definition = visits.patients.FirstOrDefault(p => p.patientId == patient.PatientId);
                if (definition == null) continue;
                var labels = AiProtocol.Array(AiProtocol.Patient(patient.PatientId), "rubric").OfType<JObject>()
                    .ToDictionary(item => AiProtocol.Text(item, "id"), item => AiProtocol.Text(item, "label"));
                foreach (JObject session in AiProtocol.Array(state, "sessions"))
                {
                    var single = AiProtocol.Copy(state); single["sessions"] = new JArray(session.DeepClone());
                    visits.room.SetArchivedRecord(run.ProgressId + "/" + patient.PatientId + "/" + (string)session["id"], new MedicalRecordData {
                        gameProgressId = run.ProgressId, caseId = patient.CaseId, sessionId = (string)session["id"],
                        registrationNumber = patient.PatientId, patientName = definition.displayName,
                        recordedDate = PatientAiAdapter.RecordedLocalDate((string)session["started_at"]), consultationNotes = "저장된 상담 회차: " + (string)session["id"],
                        consultationTranscript = AiRecordFormatter.Transcript(single), consultationResult = AiRecordFormatter.Results(single, labels) });
                }
            }
        }
        public bool ReturnToStartMenu()
        {
            if (visits != null && visits.room != null && visits.room.IsOverlayBlocking) return false;
            // CanRetry also covers a missing connection setting with no pending request/save.
            // That recoverable error must not trap a durably saved consultation in the room.
            if (ai != null && ai.IsBusy) { LastError = "요청·자동 저장을 완료한 후 돌아가세요."; return false; }
            try
            {
                if (ai != null && ai.IsAiVisit)
                {
                    var snapshot = PatientAiAdapter.CreateCaseRepository().Store(ai.ConversationKey).Load();
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
        private void OnDestroy() { if (roomStatus != null) Destroy(roomStatus.gameObject); visits?.room?.pcDesktopView?.BindEconomy(null); if (visits?.room != null) visits.room.CommittedSettlementForRecord = null; if (visits != null) visits.PatientArrived -= DevelopmentArrived; if (ai != null) ai.StateChanged -= OnAiChanged; }
    }
}
