using System.Collections;
using Gamsamo.Patients;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Owns the record modal presentation, without changing consultation or server state.</summary>
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
        public MedicalRecordData record = MedicalRecordData.CreateSample();

        [SerializeField, Min(0f)] private float transitionDuration = 0.2f;

        public bool IsRecordOpen { get; private set; }

        private Coroutine transition;
        private float visibleAmount;
        private bool initialized;
        private GameObject previousSelection;

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
                closeRecordButton.onClick.AddListener(CloseRecord);
            if (backdropButton != null)
                backdropButton.onClick.AddListener(CloseRecord);

            if (recordView != null)
                recordView.ShowRecord(record);

            initialized = true;
            ResetClosed();
        }

        private void OnEnable()
        {
            if (initialized)
                ResetClosed();
        }

        private void Update()
        {
            if (IsRecordOpen && Input.GetKeyDown(KeyCode.Escape))
                CloseRecord();
        }

        public void SetRecord(MedicalRecordData data)
        {
            record = data;
            if (recordView != null)
                recordView.ShowRecord(record);
        }

        public void OpenRecord()
        {
            if (!isActiveAndEnabled || modal == null || IsRecordOpen)
                return;

            // Keep the original focus when reversing an in-progress close animation.
            if (visibleAmount <= 0f)
                previousSelection = EventSystem.current != null
                    ? EventSystem.current.currentSelectedGameObject
                    : null;

            IsRecordOpen = true;
            modal.gameObject.SetActive(true);
            modal.blocksRaycasts = true;
            modal.interactable = true;
            if (openRecordButton != null)
                openRecordButton.interactable = false;
            if (recordView != null)
                recordView.ShowRecord(record);
            if (closeRecordButton != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(closeRecordButton.gameObject);

            StartTransition(1f);
        }

        public void CloseRecord()
        {
            if (!isActiveAndEnabled || modal == null || !IsRecordOpen)
                return;

            IsRecordOpen = false;
            // Continue swallowing background clicks until the fade-out has completed.
            modal.blocksRaycasts = true;
            modal.interactable = false;
            StartTransition(0f);
        }

        private void StartTransition(float target)
        {
            if (transition != null)
            {
                StopCoroutine(transition);
                transition = null;
            }

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
            if (modal != null)
                modal.alpha = amount;
            if (recordPanel != null)
                recordPanel.localScale = Vector3.one * Mathf.Lerp(0.98f, 1f, amount);
            if (backgroundBlur != null)
                backgroundBlur.SetAmount(amount);
        }

        private void FinishTransition(float target)
        {
            if (target > 0f)
                return;

            if (modal != null)
            {
                modal.blocksRaycasts = false;
                modal.interactable = false;
                modal.gameObject.SetActive(false);
            }
            if (openRecordButton != null)
                openRecordButton.interactable = true;

            if (EventSystem.current == null)
                return;

            GameObject next = previousSelection;
            if (next == null || !next.activeInHierarchy)
                next = openRecordButton != null ? openRecordButton.gameObject : null;
            EventSystem.current.SetSelectedGameObject(next);
        }

        private void ResetClosed()
        {
            IsRecordOpen = false;
            ApplyVisibility(0f);
            if (modal != null)
            {
                modal.blocksRaycasts = false;
                modal.interactable = false;
                modal.gameObject.SetActive(false);
            }
            if (openRecordButton != null)
                openRecordButton.interactable = true;
        }

        private void OnDisable()
        {
            if (transition != null)
            {
                StopCoroutine(transition);
                transition = null;
            }
            if (initialized)
                ResetClosed();
        }

        private void OnDestroy()
        {
            if (openRecordButton != null)
                openRecordButton.onClick.RemoveListener(OpenRecord);
            if (closeRecordButton != null)
                closeRecordButton.onClick.RemoveListener(CloseRecord);
            if (backdropButton != null)
                backdropButton.onClick.RemoveListener(CloseRecord);
        }
    }
}
