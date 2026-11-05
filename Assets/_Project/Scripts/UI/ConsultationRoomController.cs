using System.Collections;
using System.Collections.Generic;
using Gamsamo.Patients;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Owns the room's record/PC overlays and their shared background blur.</summary>
    [DisallowMultipleComponent]
    public sealed class ConsultationRoomController : MonoBehaviour
    {
        public RawImage background;
        public Texture backgroundTexture;
        public Shader blurShader;
        public ConsultationBackgroundBlur backgroundBlur;
        public CanvasGroup modal;
        public RectTransform recordPanel;
        public Button openRecordButton;
        public Button closeRecordButton;
        public Button backdropButton;
        public MedicalRecordView recordView;
        public UiNext.UiFeaturePorts uiFeaturePorts;
        public MedicalRecordDragHandler recordDrag;
        public MedicalRecordData record = MedicalRecordData.CreateSample();

        public CanvasGroup pcModal;
        public RectTransform pcPanel;
        public Button openPcButton;
        public Button closePcButton;
        public Button pcBackdropButton;
        public PcDesktopView pcDesktopView;
        public Button windowBlindButton;

        [SerializeField, Min(0f)] private float transitionDuration = 0.2f;

        public bool IsRecordOpen { get; private set; }
        public bool IsPcOpen { get; private set; }
        // The patient input remains blocked until the room overlay has finished fading out.
        public bool IsOverlayBlocking => activeModal != ModalKind.None || inputBlocks > 0;
        private int inputBlocks;
        public bool HasExternalInputBlock => inputBlocks > 0;
        public System.IDisposable AcquireInputBlock()
        { inputBlocks++; return new InputLease(this); }
        private sealed class InputLease : System.IDisposable
        {
            private ConsultationRoomController owner;
            public InputLease(ConsultationRoomController owner) { this.owner = owner; }
            public void Dispose() { if (owner == null) return; owner.inputBlocks--; owner = null; }
        }
        public bool CanInteractWithRoom => initialized && isActiveAndEnabled
            && !IsOverlayBlocking && transition == null;

        private enum ModalKind { None, Record, Pc }

        private Coroutine transition;
        private float visibleAmount;
        private bool initialized;
        private GameObject previousSelection;
        private Button returnButton;
        private ModalKind activeModal;
        private CosmeticAppearanceController subscribedAppearance;
        private readonly List<MedicalRecordData> patientRecords = new List<MedicalRecordData>();
        private int selectedRecord;
        public System.Func<MedicalRecordData, string> CommittedSettlementForRecord;
        private readonly Dictionary<string, MedicalRecordData> archivedRecords = new Dictionary<string, MedicalRecordData>();
        public void ClearArchivedRecords()
        {
            foreach (var archived in archivedRecords.Values) patientRecords.Remove(archived);
            archivedRecords.Clear();
        }
        public void SetArchivedRecord(string key, MedicalRecordData data)
        {
            if (archivedRecords.TryGetValue(key, out var old)) patientRecords.Remove(old);
            archivedRecords[key] = data;
            patientRecords.RemoveAll(item => item.registrationNumber == "SAMPLE-001");
            patientRecords.Add(data);
            RefreshSelectedRecord();
        }

        private CanvasGroup ActiveGroup => activeModal == ModalKind.Record ? modal
            : activeModal == ModalKind.Pc ? pcModal : null;
        private RectTransform ActivePanel => activeModal == ModalKind.Record ? recordPanel
            : activeModal == ModalKind.Pc ? pcPanel : null;

        private void Awake()
        {
            if (backgroundBlur != null)
                backgroundBlur.Configure(background, backgroundTexture, blurShader);
            else if (background != null)
                background.texture = backgroundTexture;

            if (openRecordButton != null)
            {
                // A fully transparent hotspot must still participate in GraphicRaycaster input.
                if (openRecordButton.targetGraphic != null)
                    openRecordButton.targetGraphic.canvasRenderer.cullTransparentMesh = false;
                openRecordButton.onClick.AddListener(OpenRecord);
            }
            if (closeRecordButton != null)
            {
                // Preserve installed references while the record is now closed from outside or with Escape.
                closeRecordButton.interactable = false;
                closeRecordButton.gameObject.SetActive(false);
            }
            if (backdropButton != null)
                backdropButton.onClick.AddListener(CloseRecordFromBackdrop);
            if (openPcButton != null)
            {
                if (openPcButton.targetGraphic != null)
                    openPcButton.targetGraphic.canvasRenderer.cullTransparentMesh = false;
                openPcButton.onClick.AddListener(OpenPc);
            }
            if (closePcButton != null)
                closePcButton.onClick.AddListener(ClosePc);
            if (pcBackdropButton != null)
                pcBackdropButton.onClick.AddListener(ClosePcTopmost);
            if (windowBlindButton != null)
            {
                if (windowBlindButton.targetGraphic != null)
                    windowBlindButton.targetGraphic.canvasRenderer.cullTransparentMesh = false;
                windowBlindButton.onClick.AddListener(CycleWindowBlinds);
            }

            if (recordView != null)
                recordView.RecordNavigationRequested += NavigateRecord;
            if (uiFeaturePorts != null) uiFeaturePorts.RecordInformationReceived += ApplyRecordInformation;
            SetRecord(record);

            initialized = true;
            ResetClosed();
        }

        private void OnEnable()
        {
            subscribedAppearance = GetComponent<CosmeticAppearanceController>();
            if (subscribedAppearance != null)
                subscribedAppearance.Changed += RefreshWindowBlindInteraction;
            if (initialized)
                ResetClosed();
        }

        private void Update()
        {
            if (inputBlocks > 0 || Presentation.PresentationModal.LastClosedFrame == Time.frameCount) return; // A nested presentation owns Escape until it closes.
            if (!Input.GetKeyDown(KeyCode.Escape))
                return;
            if (IsPcOpen)
                ClosePcTopmost();
            else if (IsRecordOpen)
                CloseRecord();
        }

        public void SetRecord(MedicalRecordData data)
        {
            record = data;
            if (data == null) return;
            int index = patientRecords.FindIndex(item => item.registrationNumber == data.registrationNumber && !archivedRecords.ContainsValue(item));
            if (index < 0)
            {
                // The design preview is not a real patient's consultation archive.
                patientRecords.RemoveAll(item => item.registrationNumber == "SAMPLE-001");
                patientRecords.Add(data);
                index = patientRecords.Count - 1;
            }
            else patientRecords[index] = data;
            if (!IsRecordOpen) selectedRecord = index;
            RefreshSelectedRecord();
        }

        private void NavigateRecord(int direction)
        {
            if (!IsRecordOpen || patientRecords.Count == 0) return;
            selectedRecord = Mathf.Clamp(selectedRecord + direction, 0, patientRecords.Count - 1);
            RefreshSelectedRecord();
        }

        private void RefreshSelectedRecord()
        {
            if (recordView == null || patientRecords.Count == 0) return;
            selectedRecord = Mathf.Clamp(selectedRecord, 0, patientRecords.Count - 1);
            var selected = patientRecords[selectedRecord];
            bool archive = archivedRecords.ContainsValue(selected);
            recordView.BeginRecordContext(selected.gameProgressId, selected.caseId + "/" + selected.sessionId + "/" + archive);
            recordView.ShowRecord(selected);
            recordView.SetCommittedSettlement(selected.registrationNumber, true, CommittedSettlementForRecord?.Invoke(selected));
            recordView.SetRecordNavigation(selectedRecord, patientRecords.Count);
            recordView.SetRecordHeading(archive, 0);
            uiFeaturePorts?.SelectRecord(new UiNext.RecordSelection { GameId = selected.gameProgressId,
                PatientId = selected.registrationNumber, CaseId = selected.caseId, SessionId = selected.sessionId, IsArchive = archive });
        }

        private void ApplyRecordInformation(UiNext.RecordSelection selected, IReadOnlyList<UiNext.RecordFieldDisplay> fields)
        {
            if (recordView == null || patientRecords.Count == 0) return;
            var shown = patientRecords[Mathf.Clamp(selectedRecord, 0, patientRecords.Count - 1)];
            if (shown.gameProgressId != selected.GameId || shown.registrationNumber != selected.PatientId
                || shown.caseId != selected.CaseId || shown.sessionId != selected.SessionId
                || archivedRecords.ContainsValue(shown) != selected.IsArchive) return;
            recordView.SetRecordHeading(selected.IsArchive, selected.Round);
            foreach (var field in fields) recordView.SetInformationState(selected.PatientId, field.Field, field.State, field.Value);
        }

        /// <summary>Cycles the selected room window through half, down, and up while no modal is visible.</summary>
        public void CycleWindowBlinds()
        {
            // activeModal remains set until fade-out ends, even after IsPcOpen/IsRecordOpen become false.
            if (!CanInteractWithRoom || subscribedAppearance == null || !subscribedAppearance.CanControlBlinds)
                return;
            subscribedAppearance.SetBlindState((subscribedAppearance.BlindState + 1) % 3);
        }

        public void OpenRecord()
        {
            if (inputBlocks > 0 || !isActiveAndEnabled || modal == null || IsRecordOpen)
                return;
            RefreshSelectedRecord();
            if (recordDrag != null)
                recordDrag.ResetPosition();
            OpenModal(ModalKind.Record, openRecordButton, null);
        }

        public void CloseRecord()
        {
            if (inputBlocks > 0 || !isActiveAndEnabled || modal == null || !IsRecordOpen)
                return;

            CloseModal();
        }

        private void CloseRecordFromBackdrop()
        {
            if (recordDrag != null && recordDrag.BlocksBackdropClick)
                return;
            CloseRecord();
        }

        public void OpenPc()
        {
            if (inputBlocks > 0 || !isActiveAndEnabled || pcModal == null || IsPcOpen)
                return;
            if (pcDesktopView != null)
                pcDesktopView.ResetDesktop();
            OpenModal(ModalKind.Pc, openPcButton, closePcButton);
        }

        /// <summary>Closes the whole PC, including its shop/app window.</summary>
        public void ClosePc()
        {
            if (inputBlocks > 0 || !isActiveAndEnabled || pcModal == null || !IsPcOpen)
                return;
            CloseModal();
        }

        private void ClosePcTopmost()
        {
            if (inputBlocks > 0 || !IsPcOpen)
                return;
            if (pcDesktopView != null && pcDesktopView.CloseActiveWindow())
                return;
            ClosePc();
        }

        private void OpenModal(ModalKind kind, Button opener, Button closeButton)
        {
            StopTransition();
            // Capture room focus once, not the close button of an already visible overlay.
            if (activeModal == ModalKind.None)
            {
                previousSelection = EventSystem.current != null
                    ? EventSystem.current.currentSelectedGameObject : null;
                returnButton = opener;
            }
            if (activeModal != ModalKind.None && activeModal != kind)
            {
                HideGroup(ActiveGroup);
                if (activeModal == ModalKind.Pc && pcDesktopView != null)
                    pcDesktopView.ResetDesktop();
            }

            activeModal = kind;
            IsRecordOpen = kind == ModalKind.Record;
            IsPcOpen = kind == ModalKind.Pc;
            CanvasGroup group = ActiveGroup;
            group.gameObject.SetActive(true);
            group.blocksRaycasts = true;
            group.interactable = true;
            SetRoomHotspots(false);
            ApplyVisibility(visibleAmount);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(closeButton != null ? closeButton.gameObject : null);
            StartTransition(1f);
        }

        private void CloseModal()
        {
            IsRecordOpen = false;
            IsPcOpen = false;
            // Room hotspots stay disabled until the fade-out is completely finished.
            CanvasGroup group = ActiveGroup;
            if (group != null)
            {
                group.blocksRaycasts = true;
                group.interactable = false;
            }
            StartTransition(0f);
        }

        private void StartTransition(float target)
        {
            StopTransition();

            if (transitionDuration <= 0f)
            {
                ApplyVisibility(target);
                FinishTransition(target);
                return;
            }

            transition = StartCoroutine(AnimateVisibility(target));
        }

        private IEnumerator AnimateVisibility(float target)
        {
            float start = visibleAmount;
            float elapsed = 0f;
            while (elapsed < transitionDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / transitionDuration));
                ApplyVisibility(Mathf.Lerp(start, target, t));
                yield return null;
            }

            ApplyVisibility(target);
            transition = null;
            FinishTransition(target);
        }

        private void ApplyVisibility(float amount)
        {
            visibleAmount = amount;
            CanvasGroup group = ActiveGroup;
            if (group != null)
                group.alpha = amount;
            RectTransform panel = ActivePanel;
            if (panel != null)
                panel.localScale = Vector3.one * Mathf.Lerp(0.98f, 1f, amount);
            if (backgroundBlur != null)
                backgroundBlur.SetAmount(amount);
        }

        private void FinishTransition(float target)
        {
            if (target > 0f)
                return;

            HideGroup(modal);
            HideGroup(pcModal);
            if (pcDesktopView != null)
                pcDesktopView.ResetDesktop();
            activeModal = ModalKind.None;
            SetRoomHotspots(true);

            if (EventSystem.current == null)
                return;

            GameObject next = previousSelection;
            Selectable selectable = next != null ? next.GetComponent<Selectable>() : null;
            if (next == null || !next.activeInHierarchy || (selectable != null && !selectable.IsInteractable()))
                next = returnButton != null ? returnButton.gameObject : null;
            EventSystem.current.SetSelectedGameObject(next);
            previousSelection = null;
            returnButton = null;
        }

        private void ResetClosed()
        {
            IsRecordOpen = false;
            IsPcOpen = false;
            ApplyVisibility(0f);
            HideGroup(modal);
            HideGroup(pcModal);
            if (pcDesktopView != null)
                pcDesktopView.ResetDesktop();
            activeModal = ModalKind.None;
            previousSelection = null;
            returnButton = null;
            SetRoomHotspots(true);
        }

        private static void HideGroup(CanvasGroup group)
        {
            if (group != null)
            {
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
                group.gameObject.SetActive(false);
            }
        }

        private void SetRoomHotspots(bool interactable)
        {
            if (openRecordButton != null)
                openRecordButton.interactable = interactable;
            if (openPcButton != null)
                openPcButton.interactable = interactable;
            RefreshWindowBlindInteraction();
        }

        private void RefreshWindowBlindInteraction()
        {
            if (windowBlindButton != null)
                windowBlindButton.interactable = CanInteractWithRoom && subscribedAppearance != null
                    && subscribedAppearance.CanControlBlinds;
        }

        private void StopTransition()
        {
            if (transition != null)
            {
                StopCoroutine(transition);
                transition = null;
            }
        }

        private void OnDisable()
        {
            if (subscribedAppearance != null)
                subscribedAppearance.Changed -= RefreshWindowBlindInteraction;
            subscribedAppearance = null;
            StopTransition();
            if (initialized)
                ResetClosed();
        }

        private void OnDestroy()
        {
            if (uiFeaturePorts != null) uiFeaturePorts.RecordInformationReceived -= ApplyRecordInformation;
            if (recordView != null)
                recordView.RecordNavigationRequested -= NavigateRecord;
            if (openRecordButton != null)
                openRecordButton.onClick.RemoveListener(OpenRecord);
            if (backdropButton != null)
                backdropButton.onClick.RemoveListener(CloseRecordFromBackdrop);
            if (openPcButton != null)
                openPcButton.onClick.RemoveListener(OpenPc);
            if (closePcButton != null)
                closePcButton.onClick.RemoveListener(ClosePc);
            if (pcBackdropButton != null)
                pcBackdropButton.onClick.RemoveListener(ClosePcTopmost);
            if (windowBlindButton != null)
                windowBlindButton.onClick.RemoveListener(CycleWindowBlinds);
        }
    }
}
