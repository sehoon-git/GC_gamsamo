using System;
using System.Collections;
using System.Collections.Generic;
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
        public event Action<string> ErrorOccurred;

        public static string ConnectionFile => Path.Combine(Application.persistentDataPath, "ai-connection.json");
        public static string SaveDirectory => SaveDirectoryFor("seoyun"); // Compatibility for the original client.
        public static string SaveDirectoryFor(string patientId)
        {
            AiProtocol.Patient(patientId); // Validate before constructing a path.
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
        public bool CanFinishVisit => CanInteract && !IsBusy && CurrentPhase == "evaluated" && string.IsNullOrEmpty(LastError);
        public bool CanRevisit => CanFinishVisit && !AiProtocol.Flag(AiProtocol.Object(AiProtocol.Current(committedState), "evaluation"), "cleared");
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
        private bool showingClosing;
        private bool resultVisible;
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
            && visits.State == PatientVisitController.VisitState.Consulting && visits.CurrentPatientId == activePatientId
            && !string.IsNullOrEmpty(localVisitId) && visits.CurrentSessionId == localVisitId;
        private bool CanInteract => HasActiveVisit && View != null && View.IsVisible && !View.IsConfirming
            && (visits.room == null || !visits.room.IsOverlayBlocking);

        private void Awake()
        {
            visits = GetComponent<PatientVisitController>();
            if (visits == null) { enabled = false; return; }
            visits.UsesExternalDialogue = UsesPatient;
            visits.ExternalConfirmEnd = ConfirmFromView;
            visits.ExternalNextRequested += AdvanceCommittedView;
            visits.PatientArrived += Arrived;
            visits.StateChanged += VisitChanged;
        }
        private static bool UsesPatient(string patientId) => new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" }.Contains(patientId);
        private void OnEnable()
        {
            if (visits != null && visits.State == PatientVisitController.VisitState.Consulting && UsesPatient(visits.CurrentPatientId))
                Arrived(visits.CurrentSessionId, visits.CurrentPatientId);
        }
        private void Arrived(string visitId, string patientId)
        {
            if (!UsesPatient(patientId)) return;
            FlushDraft();
            AbortNetwork();
            if (activePatientId != patientId)
            {
                session = null; committedState = null; draftDirty = false; draftBuffer = "";
                factTexts.Clear(); criterionLabels.Clear(); LastPatientText = "";
            }
            activePatientId = patientId;
            patientName = visits.CurrentPatient.displayName;
            localVisitId = visitId;
            renderedRevision = -1;
            showingClosing = resultVisible = false;
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
                    var patient = AiProtocol.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "GameData", "Patients", activePatientId + ".json")));
                    AiProtocol.Require(AiProtocol.Id(patient, "id") == activePatientId, "환자 데이터 ID 오류");
                    AiProtocol.RegisterPatient(patient);
                    patientName = AiProtocol.Text(patient, "name");
                    foreach (JObject fact in AiProtocol.Array(patient, "private_facts")) factTexts.Add(AiProtocol.Text(fact, "id"), AiProtocol.Text(fact, "text"));
                    foreach (JObject criterion in AiProtocol.Array(patient, "rubric")) criterionLabels.Add(AiProtocol.Text(criterion, "id"), AiProtocol.Text(criterion, "label"));
                }
                if (session == null) session = new AiSession(new AiSaveStore(SaveDirectoryFor(activePatientId)), activePatientId);
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
            bool canSubmit = CanSubmit, canEdit = CanEditDraft;
            bool canFinish = CanFinishVisit, canRevisit = CanRevisit;
            if (canSubmit != previousCanSubmit || canEdit != previousCanEdit || canFinish != previousCanFinish || canRevisit != previousCanRevisit)
            {
                previousCanSubmit = canSubmit; previousCanEdit = canEdit;
                previousCanFinish = canFinish; previousCanRevisit = canRevisit;
                Emit(StateChanged);
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
            return true; // AI visits may only leave after evaluation has been saved.
        }
        public bool Evaluate()
        {
            if (!HasActiveVisit || IsBusy || CurrentPhase != "pending_evaluation" || !string.IsNullOrEmpty(LastError)) return false;
            if (!Schedule("evaluate")) return false;
            ContinueFlow(); return true;
        }
        public bool Revisit()
        {
            if (!CanRevisit || !Schedule("revisit")) return false;
            showingClosing = resultVisible = false;
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
            if (!CanFinishVisit) return false;
            try { session.NewExperiment(); }
            catch (Exception) { Fail("새 실험을 저장하지 못했습니다. 기존 기록은 보존됩니다."); return false; }
            draftDirty = showingClosing = resultVisible = false;
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
                try { session.Receive(pending, AiProtocol.Parse(request.downloadHandler.text)); }
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
                bool confirming = View.IsConfirming && !EndRequested;
                showingClosing = resultVisible = false;
                View.ShowLine(patientName, LastPatientText);
                if (confirming) View.ShowConfirmation(true);
                if (speech != null) Emit(PatientLineCommitted, LastPatientText);
            }
            else if (CurrentPhase == "pending_evaluation")
            {
                showingClosing = true; resultVisible = false;
                View.ShowLine("상담자", AiProtocol.ClosingText);
                Emit(CounselorNoticeCommitted, AiProtocol.ClosingText);
            }
            else if (CurrentPhase == "evaluated")
            {
                // Keep the closing notice visible until Next, even if mock evaluation returned immediately.
                if (!showingClosing) ShowResult();
                Emit(EvaluationCommitted, EvaluationJson);
            }
            UpdateRecord();
            Publish();
        }
        private void ShowResult()
        {
            if (CurrentPhase != "evaluated") return;
            var evaluation = AiProtocol.Object(AiProtocol.Current(committedState), "evaluation");
            var text = new StringBuilder();
            text.Append("환자 변화 ").Append(AiProtocol.Number(evaluation, "patient_score")).Append(" / 100\n")
                .Append("상담자 대응 ").Append(AiProtocol.Number(evaluation, "counselor_score")).Append(" / 100\n")
                .Append(AiProtocol.Flag(evaluation, "cleared") ? "두 기준을 충족했습니다." : "재상담에서 이야기를 이어갈 수 있습니다.");
            foreach (JObject score in AiProtocol.Array(evaluation, "scores"))
            {
                string id = AiProtocol.Text(score, "criterion_id");
                text.Append("\n\n").Append(criterionLabels.TryGetValue(id, out var label) ? label : "평가 항목")
                    .Append(" · ").Append(AiProtocol.Number(score, "score")).Append(" / 25\n").Append((string)score["explanation"]);
                foreach (JObject evidence in AiProtocol.Array(score, "evidence"))
                    text.Append("\n").Append(AiProtocol.Flag(evidence, "counter") ? "반대 근거: “" : "근거: “")
                        .Append(AiProtocol.Text(evidence, "quote")).Append("”");
            }
            showingClosing = false; resultVisible = true;
            View.ShowLine("상담 결과", text.ToString());
            View.SetNextAvailable(true);
        }
        private void AdvanceCommittedView()
        {
            if (!CanInteract || CurrentPhase != "evaluated" || IsBusy) return;
            if (showingClosing || !resultVisible) ShowResult();
            else FinishVisit();
            Publish();
        }
        private void UpdateRecord()
        {
            if (visits.room == null) return;
            visits.room.SetRecord(new MedicalRecordData
            {
                registrationNumber = activePatientId, patientName = this.patientName,
                recordedDate = AiProtocol.Text(AiProtocol.Current(committedState), "started_at").Split('T')[0],
                visitReason = "상담에서 확인한 내용",
                medicalHistory = "아직 확인하지 않음", medication = "아직 확인하지 않음",
                consultationNotes = string.IsNullOrEmpty(LastPatientText) ? "첫 이야기를 기다리고 있습니다." : LastPatientText,
                specialNotes = ConfirmedFacts.Count == 0 ? "아직 공개된 정보 없음" : string.Join("\n\n", ConfirmedFacts),
                counselor = visits.room.record == null ? "" : visits.room.record.counselor
            });
        }
        private void Publish()
        {
            if (HasActiveVisit && View != null)
            {
                View.SetEndAvailable(session != null && CurrentPhase == "active" && !EndRequested);
                View.SetNextAvailable(CurrentPhase == "evaluated" && !networkBusy && string.IsNullOrEmpty(LastError));
                string status;
                if (!string.IsNullOrEmpty(LastError)) status = "연결·자동 저장 확인 필요 · 재시도";
                else if (session != null && session.HasPendingSave) status = "자동 저장을 기다리고 있어요";
                else if (CurrentPhase == "evaluated") status = "평가 저장 완료 · 다음으로 결과 확인";
                else if (CurrentPhase == "pending_evaluation") status = "상담을 마쳤어요 · 평가 중";
                else if (EndRequested) status = "응답 저장 후 상담을 마칠게요";
                else if (networkBusy) status = patientName + "의 응답을 기다리고 있어요";
                else status = IsMock ? "모의 AI · 입력 UI 연결 대기" : patientName + " 상담 중";
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
            if (visits == null) return;
            if (visits.UsesExternalDialogue == UsesPatient) visits.UsesExternalDialogue = null;
            if (visits.ExternalConfirmEnd == ConfirmFromView) visits.ExternalConfirmEnd = null;
            visits.ExternalNextRequested -= AdvanceCommittedView;
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
                if (visits.GetComponent<PatientAiAdapter>() == null) visits.gameObject.AddComponent<PatientAiAdapter>();
        }
    }
}
