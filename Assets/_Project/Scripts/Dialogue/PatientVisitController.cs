using System;
using System.Collections;
using System.Collections.Generic;
using Gamsamo.Patients;
using Gamsamo.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Dialogue
{
    /// <summary>Owns the local six-person queue. Door clicks cannot replace an unfinished visit.</summary>
    [DisallowMultipleComponent]
    public sealed class PatientVisitController : MonoBehaviour
    {
        public enum VisitState { Waiting, Entering, Consulting, Leaving, Finished }
        public const int RequiredPatientCount = 6;

        public ConsultationRoomController room;
        public Button doorButton;
        public Image portraitImage;
        public CanvasGroup portraitGroup;
        public RectTransform portraitRoot;
        public PatientDialogueView dialogue;
        public PatientVisitDefinition[] patients;
        public Vector2 entryOffset = new Vector2(-500f, 0f);
        [Min(0f)] public float entryDuration = 0.65f;
        [Min(0f)] public float exitDuration = 0.3f;
        public bool useDemoDialogue = true;

        /// <summary>Future dialogue adapters receive session ID first, patient ID second.</summary>
        public event Action<string, string> PatientArrived;
        public event Action<VisitState> StateChanged;

        // A runtime adapter opts in per patient. The remaining local demos keep their own rules.
        public Func<string, bool> UsesExternalDialogue;
        public Func<string, bool> ExternalConfirmEnd;
        public Action ExternalNextRequested;

        public VisitState State { get; private set; } = VisitState.Waiting;
        public int CompletedCount { get; private set; }
        public int AdmittedCount => nextPatientIndex;
        public string CurrentSessionId { get; private set; } = string.Empty;
        public PatientVisitDefinition CurrentPatient { get; private set; }
        public string CurrentPatientId => CurrentPatient != null ? CurrentPatient.patientId : string.Empty;
        public bool IsConsultationActive => State == VisitState.Entering || State == VisitState.Consulting || State == VisitState.Leaving;

        private int nextPatientIndex;
        private int demoLineIndex;
        private Vector2 seatedPosition;
        private Coroutine transition;
        private bool initialized;
        private bool validConfiguration;
        private MedicalRecordData startingRecord;
        private bool RoomBlocked => room != null && room.IsOverlayBlocking;

        private void Awake()
        {
            seatedPosition = portraitRoot != null ? portraitRoot.anchoredPosition : Vector2.zero;
            startingRecord = room != null ? room.record : null;
            validConfiguration = ValidateConfiguration(out string reason);
            if (!validConfiguration) Debug.LogError("Patient visits could not start: " + reason, this);
            if (doorButton != null)
            {
                if (doorButton.targetGraphic != null) doorButton.targetGraphic.canvasRenderer.cullTransparentMesh = false;
                doorButton.onClick.AddListener(OnDoorClicked);
            }
            if (dialogue != null)
            {
                dialogue.NextRequested += NextDemoLine;
                dialogue.EndRequested += RequestEndConsultation;
                dialogue.ConfirmEndRequested += ConfirmEndConsultation;
                dialogue.CancelEndRequested += CancelEndConsultation;
                dialogue.Hide();
            }
            SetPortraitAlpha(0f);
            if (portraitImage != null) portraitImage.raycastTarget = false;
            initialized = true;
            UpdateStatus();
            RefreshInput();
        }

        private void OnEnable()
        {
            if (!initialized) return;
            if (State == VisitState.Entering) transition = StartCoroutine(EnterPatient());
            else if (State == VisitState.Leaving) transition = StartCoroutine(LeavePatient());
            else if (State == VisitState.Consulting && dialogue != null) dialogue.RevealAll();
            RefreshInput();
        }

        private void Update()
        {
            RefreshInput();
            if (!RoomBlocked && dialogue != null && dialogue.IsConfirming && Input.GetKeyDown(KeyCode.Escape))
                CancelEndConsultation();
        }

        public bool ValidateConfiguration(out string reason)
        {
            if (doorButton == null || portraitImage == null || portraitGroup == null || portraitRoot == null || dialogue == null)
            {
                reason = "Door, portrait, or dialogue UI reference is missing.";
                return false;
            }
            if (patients == null || patients.Length != RequiredPatientCount)
            {
                reason = "Exactly six patients are required.";
                return false;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (PatientVisitDefinition patient in patients)
            {
                if (patient == null || string.IsNullOrWhiteSpace(patient.patientId) || string.IsNullOrWhiteSpace(patient.displayName)
                    || patient.portrait == null || !ids.Add(patient.patientId))
                {
                    reason = "Each patient needs a unique ID, display name, and portrait.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        private void OnDoorClicked() => TryAdmitNextPatient();

        public bool TryAdmitNextPatient()
        {
            if (!isActiveAndEnabled || !validConfiguration || RoomBlocked || State != VisitState.Waiting)
                return false;
            if (nextPatientIndex >= patients.Length)
            {
                SetState(VisitState.Finished);
                return false;
            }

            // Reserve the slot and lock the door synchronously, before any animation yields.
            CurrentPatient = patients[nextPatientIndex++];
            CurrentSessionId = Guid.NewGuid().ToString("N");
            demoLineIndex = 0;
            portraitImage.sprite = CurrentPatient.portrait;
            portraitImage.preserveAspect = true;
            dialogue.Hide();
            SetState(VisitState.Entering);
            SetPatientRecord();
            transition = StartCoroutine(EnterPatient());
            return true;
        }

        private IEnumerator EnterPatient()
        {
            float elapsed = 0f;
            portraitRoot.anchoredPosition = seatedPosition + entryOffset;
            SetPortraitAlpha(0f);
            yield return null;
            while (elapsed < entryDuration)
            {
                if (!RoomBlocked)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / Mathf.Max(entryDuration, 0.001f)));
                    portraitRoot.anchoredPosition = Vector2.Lerp(seatedPosition + entryOffset, seatedPosition, t);
                    SetPortraitAlpha(t);
                }
                yield return null;
            }
            portraitRoot.anchoredPosition = seatedPosition;
            SetPortraitAlpha(1f);
            transition = null;
            SetState(VisitState.Consulting);
            if (useDemoDialogue && !UsesExternalDialogueNow()) ShowDemoLine();
            else
            {
                dialogue.ShowLine(CurrentPatient.displayName, string.Empty);
                dialogue.SetNextAvailable(false);
            }
            PatientArrived?.Invoke(CurrentSessionId, CurrentPatientId);
        }

        private void ShowDemoLine()
        {
            dialogue.SetEndAvailable(true);
            string[] lines = CurrentPatient.demoLines;
            string line = lines != null && demoLineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[demoLineIndex])
                ? lines[demoLineIndex] : "안녕하세요. 오늘은 제 이야기를 조금 나눠 보고 싶어요.";
            dialogue.ShowLine(CurrentPatient.displayName, line);
            dialogue.SetNextAvailable(lines != null && demoLineIndex + 1 < lines.Length);
        }

        public void NextDemoLine()
        {
            if (!CanUseDialogue()) return;
            if (dialogue.IsTyping)
            {
                dialogue.RevealAll();
                return;
            }
            if (dialogue.AdvancePage()) return;
            if (UsesExternalDialogueNow())
            {
                ExternalNextRequested?.Invoke();
                return;
            }
            if (!useDemoDialogue || CurrentPatient.demoLines == null || demoLineIndex + 1 >= CurrentPatient.demoLines.Length)
                return;
            demoLineIndex++;
            ShowDemoLine();
        }

        /// <summary>LLM integration seam. Late replies from departed/replaced visits are rejected.</summary>
        public bool PresentPatientLine(string sessionId, string text)
        {
            if (!isActiveAndEnabled || State != VisitState.Consulting || string.IsNullOrEmpty(sessionId)
                || sessionId != CurrentSessionId || string.IsNullOrWhiteSpace(text)) return false;
            dialogue.ShowLine(CurrentPatient.displayName, text);
            dialogue.SetNextAvailable(false);
            return true;
        }

        public void RequestEndConsultation()
        {
            if (CanUseDialogue()) dialogue.ShowConfirmation(true);
        }

        public void CancelEndConsultation()
        {
            if (dialogue != null) dialogue.ShowConfirmation(false);
        }

        public void ConfirmEndConsultation()
        {
            if (!isActiveAndEnabled || RoomBlocked || State != VisitState.Consulting || dialogue == null || !dialogue.IsConfirming) return;
            if (UsesExternalDialogueNow() && ExternalConfirmEnd != null && ExternalConfirmEnd(CurrentSessionId)) return;
            CompleteConsultation(CurrentSessionId);
        }

        private bool UsesExternalDialogueNow() => UsesExternalDialogue != null && UsesExternalDialogue(CurrentPatientId);

        /// <summary>Ends only this local visit. It does not grant a clear, reward, or saved progress.</summary>
        public bool CompleteConsultation(string sessionId)
        {
            if (!isActiveAndEnabled || RoomBlocked || State != VisitState.Consulting
                || string.IsNullOrEmpty(sessionId) || sessionId != CurrentSessionId) return false;
            // Invalidate pending external replies as soon as leaving begins.
            CurrentSessionId = string.Empty;
            dialogue.Hide();
            SetState(VisitState.Leaving);
            transition = StartCoroutine(LeavePatient());
            return true;
        }

        private IEnumerator LeavePatient()
        {
            float elapsed = 0f;
            float initialAlpha = portraitGroup.alpha;
            Vector2 start = portraitRoot.anchoredPosition;
            yield return null;
            while (elapsed < exitDuration)
            {
                if (!RoomBlocked)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / Mathf.Max(exitDuration, 0.001f));
                    SetPortraitAlpha(Mathf.Lerp(initialAlpha, 0f, t));
                    portraitRoot.anchoredPosition = Vector2.Lerp(start, seatedPosition + new Vector2(-70f, 0f), t);
                }
                yield return null;
            }
            SetPortraitAlpha(0f);
            portraitRoot.anchoredPosition = seatedPosition;
            portraitImage.sprite = null;
            CurrentPatient = null;
            CompletedCount++;
            transition = null;
            SetState(nextPatientIndex >= patients.Length ? VisitState.Finished : VisitState.Waiting);
        }

        private bool CanUseDialogue()
        {
            return isActiveAndEnabled && !RoomBlocked && State == VisitState.Consulting && dialogue != null && !dialogue.IsConfirming;
        }

        private void SetPatientRecord()
        {
            if (room == null) return;
            room.SetRecord(new MedicalRecordData
            {
                registrationNumber = CurrentPatient.patientId,
                patientName = CurrentPatient.displayName,
                recordedDate = DateTime.Now.ToString("yyyy.MM.dd"),
                visitReason = "아직 확인하지 않음",
                medicalHistory = "아직 확인하지 않음",
                medication = "아직 확인하지 않음",
                consultationNotes = "상담을 시작했습니다.",
                specialNotes = string.Empty,
                counselor = room.record != null ? room.record.counselor : string.Empty
            });
        }

        private void SetState(VisitState value)
        {
            State = value;
            UpdateStatus();
            RefreshInput();
            StateChanged?.Invoke(State);
        }

        private void UpdateStatus()
        {
            if (dialogue == null) return;
            if (!validConfiguration) dialogue.SetStatus("내담자 정보를 확인해 주세요.");
            else if (State == VisitState.Waiting) dialogue.SetStatus("문을 눌러 내담자를 맞이하세요");
            else if (State == VisitState.Finished) dialogue.SetStatus("오늘의 상담을 모두 마쳤습니다");
            else if (State == VisitState.Entering) dialogue.SetStatus("내담자가 들어오고 있어요");
            else if (State == VisitState.Leaving) dialogue.SetStatus("다음 만남을 준비하고 있어요");
            else dialogue.SetStatus(string.Empty);
        }

        private void RefreshInput()
        {
            bool blocked = RoomBlocked;
            if (doorButton != null) doorButton.interactable = isActiveAndEnabled && validConfiguration && !blocked && State == VisitState.Waiting;
            if (dialogue != null) dialogue.SetSuspended(!isActiveAndEnabled || blocked);
        }

        private void SetPortraitAlpha(float value)
        {
            if (portraitGroup == null) return;
            portraitGroup.alpha = value;
            portraitGroup.interactable = false;
            portraitGroup.blocksRaycasts = false;
        }

        private void OnDisable()
        {
            if (transition != null) StopCoroutine(transition);
            transition = null;
            if (doorButton != null) doorButton.interactable = false;
            if (dialogue != null) dialogue.SetSuspended(true);
        }

#if UNITY_EDITOR
        /// <summary>Editor-only verification reset. Never a player-facing progression or save operation.</summary>
        public void ResetPreview()
        {
            if (transition != null) StopCoroutine(transition);
            transition = null;
            CurrentSessionId = string.Empty;
            CurrentPatient = null;
            CompletedCount = nextPatientIndex = demoLineIndex = 0;
            if (dialogue != null) dialogue.Hide();
            if (portraitRoot != null) portraitRoot.anchoredPosition = seatedPosition;
            if (portraitImage != null) portraitImage.sprite = null;
            SetPortraitAlpha(0f);
            if (room != null && startingRecord != null) room.SetRecord(startingRecord);
            SetState(VisitState.Waiting);
        }
#endif

        private void OnDestroy()
        {
            if (doorButton != null) doorButton.onClick.RemoveListener(OnDoorClicked);
            if (dialogue == null) return;
            dialogue.NextRequested -= NextDemoLine;
            dialogue.EndRequested -= RequestEndConsultation;
            dialogue.ConfirmEndRequested -= ConfirmEndConsultation;
            dialogue.CancelEndRequested -= CancelEndConsultation;
        }
    }
}
