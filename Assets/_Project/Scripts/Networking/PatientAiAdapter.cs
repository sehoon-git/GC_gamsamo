using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.Dialogue;
using Gamsamo.Patients;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Gamsamo.Networking
{
    /// <summary>
    /// Patient-specific external, stateless API adapter. Input widgets belong to the game UI and call SubmitText.
    /// A true return means accepted locally; committed events fire only after durable state + transcript storage.
    /// Credentials are loaded from a local file, never serialized into this component.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PatientAiAdapter : MonoBehaviour
    {
        public event Action StateChanged;
        public event Action<string> PatientLineCommitted;
        public event Action<string> CounselorNoticeCommitted;
        public event Action<string> EvaluationCommitted;
        public event Action<AiSavedEvaluation> SavedEvaluationAvailable;
        public event Action<string> ErrorOccurred;

        public static string ConnectionFile => Path.Combine(Application.persistentDataPath, "ai-connection.json");
        private static AiPatientCatalog patientCatalog;
        public static AiPatientCatalog PatientCatalog => patientCatalog ?? (patientCatalog = new AiPatientCatalog(Path.Combine(Application.streamingAssetsPath, "GameData")));
        public static AiCaseRepository CreateCaseRepository() => new AiCaseRepository(Path.Combine(Application.persistentDataPath, "ai-saves"), PatientCatalog);
        public AiServiceStatus ServiceStatus => new AiServiceStatus(session, LastError, networkBusy);
        public AiConversationKey ConversationKey => session == null ? null : AiConversationKey.FromState(session.State);
        public AiCaseRecord ReadCommittedRecord() => session == null ? null : new AiCaseRecord(session.State);
        public bool TryGetSavedEvaluation(out AiSavedEvaluation evaluation)
        {
            evaluation = null;
            return session != null && session.TryGetSavedEvaluation(out evaluation);
        }
        public static string SaveDirectory => SaveDirectoryFor("seoyun"); // Compatibility for the original client.
        public static string SaveDirectoryFor(string patientId)
        {
            AiProtocol.Require(UsesPatient(patientId), "지원하지 않는 환자의 저장 경로입니다.");
            return Path.Combine(Application.persistentDataPath, "ai-saves", patientId);
        }
        public string PatientId => activePatientId;
        public bool IsAiVisit => HasActiveVisit;
        public string CurrentPhase => session == null ? "uninitialized" : session.Phase;
        public string CaseId => session == null ? "" : session.CaseId;
        public string SessionId => committedState == null ? "" : AiProtocol.Id(AiProtocol.Current(committedState), "id");
        public int Revision => committedState == null ? 0 : AiProtocol.Number(committedState, "revision");
        public string Draft => draftDirty ? draftBuffer : session == null ? "" : session.Draft;
        public string LastError { get; private set; } = "";
        public string LastPatientText { get; private set; } = "";
        public string EvaluationJson => committedState != null && AiProtocol.Current(committedState)["evaluation"] is JObject value
            ? AiProtocol.Encode(value) : "";
        public bool IsBusy => networkBusy || (session != null && (session.HasPendingSave || session.PendingRequest != null));
        public bool EndRequested => session != null && session.EndRequested;
        public bool IsMock => inferenceMode == "mock";
        public bool IsSeoyunVisit => HasActiveVisit && activePatientId == "seoyun"; // Legacy meaning; use IsAiVisit for the full roster.
        public bool CanRetry => HasActiveVisit && !networkBusy && (!string.IsNullOrEmpty(LastError) || (session != null && session.HasPendingSave));
        public bool CanEditDraft => CanInteract && healthReady && session != null && !IsBusy && !EndRequested
            && CurrentPhase == "active" && string.IsNullOrEmpty(LastError)
            && AiProtocol.Current(committedState)["opening"] is JObject;
        public bool CanSubmit => CanEditDraft && !View.IsTyping && !View.HasMorePages;
        public bool CanFinishVisit => CanInteract && visits.IsAwaitingDeparture && !IsBusy
            && CurrentPhase == "evaluated" && string.IsNullOrEmpty(LastError);
        public bool CanRevisit => CanFinishVisit && !AiProtocol.Flag(AiProtocol.Object(AiProtocol.Current(committedState), "evaluation"), "cleared");
        public bool CanChangeConversation => !networkBusy && !draftDirty && (session == null || (!session.HasPendingSave && session.PendingRequest == null));
        public IReadOnlyList<string> ConfirmedFacts => committedState == null ? new string[0]
            : AiProtocol.Array(committedState, "revealed_fact_ids").Select(v => (string)v)
                .Where(factTexts.ContainsKey).Select(id => factTexts[id]).ToArray();

        private PatientVisitController visits;
        private AiSession session;
        private JObject committedState;
        private AiConnection connection;
        private UnityWebRequest activeRequest;
        private bool networkBusy;
        private bool healthReady;
        private string inferenceMode = "";
        private string localVisitId = "";
        private string activePatientId = "";
        private string patientName = "";
        private int generation;
        private int renderedRevision = -1;
        private string draftBuffer = "";
        private bool draftDirty;
        private float draftSaveAt;
        private bool previousCanSubmit;
        private bool previousCanEdit;
        private bool previousCanFinish;
        private bool previousCanRevisit;
        private readonly Dictionary<string, string> factTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> criterionLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        private PatientDialogueView View => visits == null ? null : visits.dialogue;
        private bool HasActiveVisit => isActiveAndEnabled && visits != null && visits.isActiveAndEnabled
            && visits.State == PatientVisitController.VisitState.Consulting && CanonicalPatientId(visits.CurrentPatientId) == activePatientId
            && !string.IsNullOrEmpty(localVisitId) && visits.CurrentSessionId == localVisitId;
        private bool CanInteract => HasActiveVisit && View != null && View.IsVisible && !View.IsConfirming
            && (visits.room == null || !visits.room.IsOverlayBlocking);

        private void Awake()
        {
            visits = GetComponent<PatientVisitController>();
            if (visits == null) { enabled = false; return; }
            visits.UsesExternalDialogue = UsesPatient;
            visits.ExternalConfirmEnd = ConfirmFromView;
            visits.ExternalFinishVisit = FinishVisit;
            visits.PatientArrived += Arrived;
            visits.StateChanged += VisitChanged;
        }
        private static bool UsesPatient(string patientId) => PatientCatalog.Contains(patientId);
        private static string CanonicalPatientId(string patientId) => UsesPatient(patientId) ? PatientCatalog.Get(patientId).Id : patientId;
        private void OnEnable()
        {
            if (visits != null && visits.State == PatientVisitController.VisitState.Consulting && UsesPatient(visits.CurrentPatientId))
                Arrived(visits.CurrentSessionId, visits.CurrentPatientId);
        }
        private void Arrived(string visitId, string patientId)
        {
            if (!UsesPatient(patientId)) return;
            patientId = CanonicalPatientId(patientId);
            FlushDraft();
            // Never discard a response whose state/transcript write failed when another visit arrives.
            if (session != null && session.HasPendingSave) { Fail("이전 상담의 자동 저장을 먼저 복구해 주세요."); return; }
            AbortNetwork();
            if (activePatientId != patientId)
            {
                BindSession(null); committedState = null; draftDirty = false; draftBuffer = "";
                factTexts.Clear(); criterionLabels.Clear(); LastPatientText = "";
            }
            activePatientId = patientId;
            patientName = visits.CurrentPatient.displayName;
            localVisitId = visitId;
            renderedRevision = -1;
            LastError = "";
            View.ShowLine(patientName, "");
            if (!InitializeSession()) return;
            RenderCommitted();
            ContinueFlow();
        }
        private bool InitializeSession()
        {
            try
            {
                if (factTexts.Count == 0)
                {
                    PatientCatalog.Register(activePatientId);
                    var patient = AiProtocol.Patient(activePatientId);
                    AiProtocol.Require(AiProtocol.Id(patient, "id") == activePatientId, "환자 데이터 ID 오류");
                    AiProtocol.RegisterPatient(patient);
                    patientName = AiProtocol.Text(patient, "name");
                    foreach (JObject fact in AiProtocol.Array(patient, "private_facts")) factTexts.Add(AiProtocol.Text(fact, "id"), AiProtocol.Text(fact, "text"));
                    foreach (JObject criterion in AiProtocol.Array(patient, "rubric")) criterionLabels.Add(AiProtocol.Text(criterion, "id"), AiProtocol.Text(criterion, "label"));
                }
                if (session == null) BindSession(new AiSession(new AiSaveStore(SaveDirectoryFor(activePatientId)), activePatientId));
                committedState = session.State;
                return true;
            }
            catch (Exception)
            {
                factTexts.Clear(); criterionLabels.Clear();
                Fail("환자 데이터 또는 자동 저장을 읽을 수 없습니다. 기존 기록을 보존한 채 파일·버전·쓰기 권한을 확인해 주세요.");
                return false;
            }
        }
        private void BindSession(AiSession value)
        {
            if (session != null) session.PersistenceChanged -= Publish;
            session = value;
            if (session != null) session.PersistenceChanged += Publish;
        }

        /// <summary>B calls before PatientArrived, with IDs already persisted in its game progress intent.</summary>
        public bool StartNewCase(AiConversationKey key) => SelectCase(key, () => CreateCaseRepository().StartNewCase(key));
        public bool ResumeSession(AiConversationKey key) => SelectCase(key, () => CreateCaseRepository().Resume(key));
        public bool StartRevisit(AiConversationKey previous, string newSessionId) =>
            SelectCase(previous, () => CreateCaseRepository().StartRevisit(previous, newSessionId));

        private bool SelectCase(AiConversationKey key, Func<AiSession> load)
        {
            if (key == null || networkBusy || (session != null && session.HasPendingSave)) return false;
            if (HasActiveVisit && activePatientId != key.PatientId) return false;
            FlushDraft();
            if (draftDirty || (session != null && session.HasPendingSave)) return false;
            // A saved in-flight body may only be resumed in the same case; never abandon it through a UI switch.
            if (session != null && session.PendingRequest != null && !key.SameCase(AiConversationKey.FromState(session.State))) return false;
            try
            {
                var selected = load();
                AbortNetwork();
                BindSession(selected);
                activePatientId = key.PatientId;
                factTexts.Clear(); criterionLabels.Clear();
                committedState = session.State; renderedRevision = -1;
                draftDirty = false; draftBuffer = session.Draft; LastError = ""; LastPatientText = ""; inferenceMode = "";
                if (HasActiveVisit && InitializeSession()) { RenderCommitted(); ContinueFlow(); }
                else Publish();
                return true;
            }
            catch (Exception) { Fail("진행·환자·사례·회차 ID 또는 자동 저장을 확인해 주세요. 기존 기록은 보존됩니다."); return false; }
        }
        private void VisitChanged(PatientVisitController.VisitState value)
        {
            if (value == PatientVisitController.VisitState.Consulting) return;
            FlushDraft();
            AbortNetwork();
            localVisitId = "";
            Publish();
        }
        private void Update()
        {
            if (draftDirty && Time.unscaledTime >= draftSaveAt && !IsBusy) FlushDraft();
            // Evaluation (including a restored result) only enables an explicit departure action.
            // Neither a frame update nor paging through dialogue may dismiss the patient.
            bool canSubmit = CanSubmit, canEdit = CanEditDraft;
            bool canFinish = CanFinishVisit, canRevisit = CanRevisit;
            if (canSubmit != previousCanSubmit || canEdit != previousCanEdit || canFinish != previousCanFinish || canRevisit != previousCanRevisit)
            {
                previousCanSubmit = canSubmit; previousCanEdit = canEdit;
                previousCanFinish = canFinish; previousCanRevisit = canRevisit;
                Publish();
            }
        }

        /// <summary>Preserves text verbatim. UI must suppress submission while IME composition is active.</summary>
        public bool SubmitText(string text)
        {
            if (!CanSubmit) return false;
            if (string.IsNullOrWhiteSpace(text)) { Emit(ErrorOccurred, "상담 내용을 입력해 주세요."); return false; }
            if (!Schedule("turn", text)) return false;
            draftDirty = false; // The submitted draft is already inside the durable pending request.
            ContinueFlow();
            return true;
        }
        /// <summary>Debounces draft-only disk saves by 0.75s; SubmitText saves the complete request synchronously.</summary>
        public bool UpdateDraft(string text)
        {
            if (!CanEditDraft) return false;
            draftBuffer = text ?? "";
            draftDirty = draftBuffer != session.Draft;
            draftSaveAt = Time.unscaledTime + 0.75f;
            Emit(StateChanged);
            return true;
        }
        private void FlushDraft()
        {
            if (!draftDirty || session == null || session.PendingRequest != null || session.HasPendingSave || session.EndRequested) return;
            try { session.SaveDraft(draftBuffer); draftDirty = false; }
            catch (Exception) { Fail("초안을 자동 저장하지 못했습니다. 공간·쓰기 권한을 확인한 뒤 재시도해 주세요."); }
        }
        public bool Retry()
        {
            if (!CanRetry) return false;
            LastError = "";
            healthReady = false;
            connection = null; // A corrected local address/key takes effect without replaying an accepted turn.
            if (session == null && !InitializeSession()) return false;
            try { session.RetrySave(); }
            catch (Exception) { Fail("자동 저장을 다시 완료하지 못했습니다. 받은 응답은 메모리에 보관되어 있습니다."); return false; }
            if (draftDirty && session.Draft == draftBuffer) draftDirty = false;
            RenderCommitted();
            ContinueFlow();
            return true;
        }
        public bool RequestEnd()
        {
            if (!CanInteract || session == null || EndRequested || CurrentPhase != "active") return false;
            visits.RequestEndConsultation();
            Publish();
            return View.IsConfirming;
        }
        public bool CancelEnd()
        {
            if (!HasActiveVisit || View == null || !View.IsConfirming || EndRequested) return false;
            visits.CancelEndConsultation();
            Publish();
            return true;
        }
        public bool ConfirmEnd()
        {
            if (!HasActiveVisit || View == null || !View.IsConfirming || (visits.room != null && visits.room.IsOverlayBlocking)
                || session == null || EndRequested || CurrentPhase != "active") return false;
            FlushDraft();
            try { session.RequestEnd(); }
            catch (Exception) { View.ShowConfirmation(false); Fail("종료 의도를 저장하지 못했습니다. 재시도로 저장을 먼저 완료해 주세요."); return false; }
            View.ShowConfirmation(false);
            Publish();
            ContinueFlow();
            return true;
        }
        private bool ConfirmFromView(string visitId)
        {
            if (visitId != localVisitId) return true;
            ConfirmEnd();
            return true; // Saving an evaluation and explicitly dismissing its patient are separate actions.
        }
        public bool Evaluate()
        {
            if (!HasActiveVisit || IsBusy || CurrentPhase != "pending_evaluation" || !string.IsNullOrEmpty(LastError)) return false;
            if (!Schedule("evaluate")) return false;
            ContinueFlow(); return true;
        }
        public bool Revisit()
        {
            // Scoped games allocate/persist the new session ID in B's intent, then call StartRevisit.
            if (session != null && AiProtocol.ProgressId(session.State) != "legacy") return false;
            if (!CanRevisit || !Schedule("revisit")) return false;
            ContinueFlow(); return true;
        }
        public bool FinishVisit()
        {
            if (!CanFinishVisit) return false;
            try { session.CompleteVisit(); }
            catch (Exception) { Fail("상담 완료 상태를 저장하지 못했습니다. 재시도해 주세요."); return false; }
            return visits.CompleteConsultation(localVisitId);
        }
        /// <summary>Starts a separate case after evaluation. The caller owns its confirmation UI; old snapshots are retained.</summary>
        public bool NewExperiment()
        {
            if (session != null && AiProtocol.ProgressId(session.State) != "legacy") return false;
            if (!CanFinishVisit) return false;
            try { session.NewExperiment(); }
            catch (Exception) { Fail("새 실험을 저장하지 못했습니다. 기존 기록은 보존됩니다."); return false; }
            draftDirty = false;
            renderedRevision = -1;
            RenderCommitted(); ContinueFlow(); return true;
        }
        private bool Schedule(string action, string text = null)
        {
            try { session.Schedule(action, text); Publish(); return true; }
            catch (Exception) { Fail("요청을 자동 저장하지 못했습니다. 공간·쓰기 권한을 확인한 뒤 재시도해 주세요."); return false; }
        }
        private void ContinueFlow()
        {
            if (!HasActiveVisit || networkBusy || session == null || session.HasPendingSave || !string.IsNullOrEmpty(LastError)) { Publish(); return; }
            // Reading/finishing a durably evaluated visit does not require a live server.
            if (CurrentPhase == "evaluated" && session.PendingRequest == null) { Publish(); return; }
            if (!healthReady)
            {
                try { connection = AiConnection.Load(ConnectionFile); }
                catch (Exception) { Fail("AI 연결 설정을 확인해 주세요. 연결 JSON을 가져온 뒤 재시도할 수 있습니다."); return; }
                networkBusy = true; Publish();
                StartCoroutine(CheckHealth(generation, localVisitId));
                return;
            }
            if (session.PendingRequest == null)
            {
                string action = session.NextAction;
                if (action != null && !Schedule(action)) return;
            }
            var pending = session.PendingRequest;
            if (pending == null) { Publish(); return; }
            networkBusy = true; Publish();
            StartCoroutine(Send(pending, generation, localVisitId));
        }
        private bool IsCurrent(int token, string visitId) => token == generation && HasActiveVisit && localVisitId == visitId;
        private IEnumerator CheckHealth(int token, string visitId)
        {
            using (var request = UnityWebRequest.Get(connection.BaseUrl + "/health"))
            {
                ConfigureRequest(request); activeRequest = request;
                yield return request.SendWebRequest();
                if (!IsCurrent(token, visitId)) yield break;
                activeRequest = null; networkBusy = false;
                if (request.result != UnityWebRequest.Result.Success) { Fail(NetworkError(request)); yield break; }
                try
                {
                    var health = AiProtocol.Parse(request.downloadHandler.text);
                    inferenceMode = AiProtocol.ValidateHealth(health, activePatientId);
                    if (AiProtocol.ProgressId(session.State) != "legacy")
                        AiProtocol.Require(health["capabilities"] is JArray capabilities && capabilities.Any(v => (string)v == "game_progress_scope"),
                            "게임 진행별 저장을 지원하는 서버가 필요합니다.");
                    healthReady = true;
                }
                catch (Exception) { Fail("서버의 준비 상태·데이터 버전이 일치하지 않습니다. 서버 설정을 확인해 주세요."); }
            }
            if (healthReady) ContinueFlow();
        }
        private IEnumerator Send(JObject pending, int token, string visitId)
        {
            string action = AiProtocol.Text(pending, "action");
            using (var request = new UnityWebRequest(connection.BaseUrl + "/v1/dialogue/" + action, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(AiProtocol.Encode(AiProtocol.Object(pending, "body"))));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                ConfigureRequest(request); activeRequest = request;
                yield return request.SendWebRequest();
                if (!IsCurrent(token, visitId)) yield break;
                activeRequest = null; networkBusy = false;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (action == "turn" && ErrorCode(request) == "context_overflow")
                    {
                        try { session.ReturnRejectedInputToDraft(); }
                        catch (Exception) { Fail("입력 복원 내용을 저장하지 못했습니다. 재시도해 주세요."); yield break; }
                        LastError = "";
                        Emit(ErrorOccurred, "입력이 문맥 한도를 넘었습니다. 초안을 줄여 다시 보내 주세요.");
                        ContinueFlow(); yield break;
                    }
                    Fail(NetworkError(request)); yield break;
                }
                try { session.Receive(pending, AiProtocol.Parse(request.downloadHandler.text), inferenceMode); }
                catch (Exception)
                {
                    Fail(session.HasPendingSave ? "응답은 받았지만 자동 저장을 완료하지 못했습니다. 재시도하면 저장부터 진행합니다."
                        : "응답 형식·회차·요청이 일치하지 않습니다. 확정하지 않은 요청을 다시 시도할 수 있습니다.");
                    yield break;
                }
            }
            LastError = "";
            draftDirty = false;
            RenderCommitted();
            ContinueFlow();
        }
        private void ConfigureRequest(UnityWebRequest request)
        {
            request.timeout = 180;
            request.redirectLimit = 0; // Never forward the Bearer credential to a redirected host.
            if (!string.IsNullOrEmpty(connection.ApiKey)) request.SetRequestHeader("Authorization", "Bearer " + connection.ApiKey);
        }
        private static string ErrorCode(UnityWebRequest request)
        {
            try { return (string)AiProtocol.Parse(request.downloadHandler.text)["detail"]?["code"] ?? ""; }
            catch (Exception) { return ""; }
        }
        private static string NetworkError(UnityWebRequest request)
        {
            // Proxy HTML and raw transport errors may contain addresses: never expose them or log the request body/key.
            if (request.responseCode == 401 || request.responseCode == 403) return "접근 키가 허용되지 않았습니다. 로컬 연결 설정을 수정한 뒤 재시도해 주세요.";
            if (request.responseCode == 503) return "AI 서버가 아직 준비되지 않았습니다. 준비 후 같은 요청을 재시도해 주세요.";
            if (request.responseCode == 409) return "회차 또는 데이터 버전이 서버와 일치하지 않습니다. 저장 기록을 보존한 채 서버 버전을 확인해 주세요.";
            return "AI 서버 연결·응답을 완료하지 못했습니다. 연결 설정을 확인한 뒤 같은 요청을 재시도해 주세요.";
        }
        private void RenderCommitted()
        {
            if (session == null || !HasActiveVisit) return;
            committedState = session.State;
            int revision = AiProtocol.Number(committedState, "revision");
            if (revision == renderedRevision) { Publish(); return; }
            renderedRevision = revision;
            var current = AiProtocol.Current(committedState);
            var turns = AiProtocol.Array(current, "turns");
            var speech = turns.Count > 0 ? (JObject)turns.Last : current["opening"] as JObject;
            LastPatientText = speech == null ? "" : AiProtocol.Text(speech, "patient_text");
            if (CurrentPhase == "active" || CurrentPhase == "closing")
            {
                if (CurrentPhase == "active" && visits.IsAwaitingDeparture) visits.ClearConsultationEnded(localVisitId);
                bool confirming = View.IsConfirming && !EndRequested;
                View.ShowLine(patientName, LastPatientText);
                if (confirming) View.ShowConfirmation(true);
                if (speech != null) Emit(PatientLineCommitted, LastPatientText);
            }
            else if (CurrentPhase == "pending_evaluation")
            {
                View.ShowLine("상담 안내", AiProtocol.ClosingText);
                Emit(CounselorNoticeCommitted, AiProtocol.ClosingText);
            }
            else if (CurrentPhase == "evaluated")
            {
                // A restored evaluation must not leave an empty speech bubble or invent new dialogue.
                if (string.IsNullOrEmpty(View.DisplayedText)) View.ShowLine(patientName, LastPatientText);
            }
            UpdateRecord();
            if (CurrentPhase == "evaluated")
            {
                visits.MarkConsultationEnded(localVisitId);
                Emit(EvaluationCommitted, EvaluationJson);
                if (session.TryGetSavedEvaluation(out var evaluation)) Emit(SavedEvaluationAvailable, evaluation);
            }
            Publish();
        }
        private void UpdateRecord()
        {
            if (visits.room == null) return;
            visits.room.SetRecord(new MedicalRecordData
            {
                registrationNumber = activePatientId, patientName = this.patientName,
                recordedDate = RecordedLocalDate(AiProtocol.Text(AiProtocol.Current(committedState), "started_at")),
                visitReason = "상담에서 확인한 내용",
                medicalHistory = "아직 확인하지 않음", medication = "아직 확인하지 않음",
                consultationNotes = CurrentPhase == "evaluated" ? "상담이 종료되었습니다. 전체 상담 기록과 평가 결과를 확인해 주세요."
                    : string.IsNullOrEmpty(LastPatientText) ? "첫 이야기를 기다리고 있습니다." : "상담 진행 중 · 전체 대화는 대화 원문 탭에서 확인할 수 있습니다.",
                consultationTranscript = AiRecordFormatter.Transcript(committedState),
                consultationResult = AiRecordFormatter.Results(committedState, criterionLabels),
                specialNotes = ConfirmedFacts.Count == 0 ? "아직 공개된 정보 없음" : string.Join("\n\n", ConfirmedFacts),
                counselor = visits.room.record == null ? "" : visits.room.record.counselor
            });
        }
        private static string RecordedLocalDate(string startedAt)
        {
            if (DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                return timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string previousDate = startedAt == null ? "" : startedAt.Split('T')[0];
            return DateTime.TryParseExact(previousDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? previousDate : "";
        }
        private void Publish()
        {
            if (HasActiveVisit && View != null)
            {
                View.SetEndAvailable(session != null && CurrentPhase == "active" && !EndRequested);
                View.SetNextAvailable(false);
                View.SetDepartureAvailable(CanFinishVisit);
                string status;
                switch (ServiceStatus.Phase)
                {
                    case AiServicePhase.Error: status = "연결·자동 저장 확인 필요 · 재시도"; break;
                    case AiServicePhase.SavingEvaluation: status = "평가 결과를 저장하고 있어요"; break;
                    case AiServicePhase.Saving: status = "상담 기록을 저장하고 있어요"; break;
                    case AiServicePhase.PreparingOpening: status = "첫 이야기를 준비하고 있어요"; break;
                    case AiServicePhase.Evaluating: status = "상담을 마쳤어요 · 평가 중"; break;
                    case AiServicePhase.Closing: status = "응답 저장 후 상담을 마칠게요"; break;
                    case AiServicePhase.GeneratingReply: status = patientName + "의 응답을 기다리고 있어요"; break;
                    case AiServicePhase.Completed: status = "상담 저장 완료 · 기록부를 확인한 뒤 환자 내보내기를 눌러 주세요"; break;
                    default: status = networkBusy ? "AI 연결을 확인하고 있어요" : "답변을 자유롭게 적어 주세요"; break;
                }
                View.SetStatus(status);
            }
            Emit(StateChanged);
        }
        private void Fail(string message)
        {
            LastError = message;
            Publish();
            Emit(ErrorOccurred, message);
        }
        private static void Emit(Action listeners)
        {
            if (listeners == null) return;
            foreach (Action listener in listeners.GetInvocationList())
                try { listener(); } catch (Exception) { Debug.LogError("AI UI event listener failed. Saved conversation state was preserved."); }
        }
        private static void Emit(Action<string> listeners, string value)
        {
            if (listeners == null) return;
            foreach (Action<string> listener in listeners.GetInvocationList())
                try { listener(value); } catch (Exception) { Debug.LogError("AI UI event listener failed. Saved conversation state was preserved."); }
        }
        private static void Emit(Action<AiSavedEvaluation> listeners, AiSavedEvaluation value)
        {
            if (listeners == null) return;
            foreach (Action<AiSavedEvaluation> listener in listeners.GetInvocationList())
                try { listener(value); } catch (Exception) { Debug.LogError("AI evaluation listener failed. Saved result is available for retry."); }
        }
        private void AbortNetwork()
        {
            generation++;
            if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
            StopAllCoroutines();
            networkBusy = healthReady = false;
        }
        private void OnApplicationPause(bool paused) { if (paused) FlushDraft(); }
        private void OnApplicationQuit() => FlushDraft();
        private void OnDisable() { FlushDraft(); AbortNetwork(); }
        private void OnDestroy()
        {
            if (session != null) session.PersistenceChanged -= Publish;
            if (visits == null) return;
            if (visits.UsesExternalDialogue == UsesPatient) visits.UsesExternalDialogue = null;
            if (visits.ExternalConfirmEnd == ConfirmFromView) visits.ExternalConfirmEnd = null;
            if (visits.ExternalFinishVisit == FinishVisit) visits.ExternalFinishVisit = null;
            visits.PatientArrived -= Arrived;
            visits.StateChanged -= VisitChanged;
        }
    }

    /// <summary>Adds the shared patient adapter without rewriting existing serialized scene/prefab references.</summary>
    public static class PatientAiBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneLoaded += SceneLoaded;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AttachLoaded() => AttachAdapters();
        private static void SceneLoaded(Scene scene, LoadSceneMode mode) => AttachAdapters();
        public static void AttachAdapters()
        {
            foreach (var visits in UnityEngine.Object.FindObjectsByType<PatientVisitController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (visits.GetComponent<PatientAiAdapter>() == null) visits.gameObject.AddComponent<PatientAiAdapter>();
                if (visits.GetComponent<PatientAiInputBridge>() == null) visits.gameObject.AddComponent<PatientAiInputBridge>();
            }
        }
    }
}
