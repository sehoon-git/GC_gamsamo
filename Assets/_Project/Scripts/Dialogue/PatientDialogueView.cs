using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Dialogue
{
    /// <summary>Draws one speaker's line. It never admits or completes a patient on its own.</summary>
    [DisallowMultipleComponent]
    public sealed class PatientDialogueView : MonoBehaviour
    {
        public CanvasGroup root;
        public TMP_Text speakerText;
        public TMP_Text bodyText;
        // This may live beside the door, outside the dialogue's CanvasGroup.
        public TMP_Text statusText;
        public Button nextButton;
        public Button endButton;
        public GameObject confirmationPanel;
        public Button confirmEndButton;
        public Button cancelEndButton;
        [Min(1f)] public float charactersPerSecond = 32f;

        public event Action NextRequested;
        public event Action EndRequested;
        public event Action ConfirmEndRequested;
        public event Action CancelEndRequested;

        public bool IsTyping => typing != null;
        public bool IsVisible => visible && !suspended;
        public bool IsConfirming { get; private set; }
        public string DisplayedText => bodyText != null ? bodyText.text : string.Empty;

        private Coroutine typing;
        private bool visible;
        private bool suspended;
        private bool allowNext;
        private bool bound;

        private void Awake() => BindButtons();

        public void ShowLine(string speaker, string text)
        {
            BindButtons();
            StopTyping();
            visible = true;
            ShowConfirmation(false);
            if (speakerText != null)
            {
                speakerText.richText = false;
                speakerText.text = speaker ?? string.Empty;
            }
            if (bodyText != null)
            {
                bodyText.richText = false;
                bodyText.text = text ?? string.Empty;
                bodyText.maxVisibleCharacters = 0;
                bodyText.ForceMeshUpdate();
                if (bodyText.textInfo.characterCount > 0 && isActiveAndEnabled)
                    typing = StartCoroutine(TypeLine());
                else
                    bodyText.maxVisibleCharacters = int.MaxValue;
            }
            RefreshVisibility();
        }

        public void SetStatus(string text)
        {
            if (statusText == null) return;
            statusText.richText = false;
            statusText.text = text ?? string.Empty;
        }

        public void SetNextAvailable(bool available)
        {
            allowNext = available;
            RefreshVisibility();
        }

        public void SetSuspended(bool value)
        {
            if (suspended == value) return;
            suspended = value;
            RefreshVisibility();
        }

        public void ShowConfirmation(bool show)
        {
            IsConfirming = show && visible;
            RefreshVisibility();
        }

        public void RevealAll()
        {
            StopTyping();
            if (bodyText != null) bodyText.maxVisibleCharacters = int.MaxValue;
            RefreshVisibility();
        }

        public void Hide()
        {
            StopTyping();
            visible = false;
            IsConfirming = false;
            RefreshVisibility();
        }

        private IEnumerator TypeLine()
        {
            int total = bodyText.textInfo.characterCount;
            float shown = 0f;
            // Defer once so the Coroutine handle is always assigned before completion.
            yield return null;
            while (shown < total)
            {
                if (!suspended && !IsConfirming)
                {
                    shown += Time.unscaledDeltaTime * Mathf.Max(1f, charactersPerSecond);
                    bodyText.maxVisibleCharacters = Mathf.Min(total, Mathf.FloorToInt(shown));
                }
                yield return null;
            }
            bodyText.maxVisibleCharacters = int.MaxValue;
            typing = null;
            RefreshVisibility();
        }

        private void RefreshVisibility()
        {
            bool shown = visible && !suspended;
            if (root != null)
            {
                root.alpha = shown ? 1f : 0f;
                root.interactable = shown && !IsConfirming;
                root.blocksRaycasts = shown;
            }
            if (confirmationPanel != null)
                confirmationPanel.SetActive(shown && IsConfirming);
            if (nextButton != null) nextButton.interactable = shown && !IsConfirming && (allowNext || IsTyping);
            if (endButton != null) endButton.interactable = shown && !IsConfirming;
        }

        private void BindButtons()
        {
            if (bound) return;
            if (nextButton != null) nextButton.onClick.AddListener(NotifyNext);
            if (endButton != null) endButton.onClick.AddListener(NotifyEnd);
            if (confirmEndButton != null) confirmEndButton.onClick.AddListener(NotifyConfirm);
            if (cancelEndButton != null) cancelEndButton.onClick.AddListener(NotifyCancel);
            bound = true;
        }

        private void NotifyNext() { if (IsVisible && !IsConfirming) NextRequested?.Invoke(); }
        private void NotifyEnd() { if (IsVisible && !IsConfirming) EndRequested?.Invoke(); }
        private void NotifyConfirm() { if (IsVisible && IsConfirming) ConfirmEndRequested?.Invoke(); }
        private void NotifyCancel() { if (IsVisible && IsConfirming) CancelEndRequested?.Invoke(); }

        private void StopTyping()
        {
            if (typing != null) StopCoroutine(typing);
            typing = null;
        }

        private void OnDisable() => StopTyping();

        private void OnDestroy()
        {
            if (nextButton != null) nextButton.onClick.RemoveListener(NotifyNext);
            if (endButton != null) endButton.onClick.RemoveListener(NotifyEnd);
            if (confirmEndButton != null) confirmEndButton.onClick.RemoveListener(NotifyConfirm);
            if (cancelEndButton != null) cancelEndButton.onClick.RemoveListener(NotifyCancel);
        }
    }
}
