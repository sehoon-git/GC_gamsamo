using System;
using System.Collections;
using System.Collections.Generic;
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
        // Connection/progress/error feedback stays inside the dialogue panel.
        public TMP_Text statusText;
        public Button nextButton;
        public Button endButton;
        public Button dismissButton;
        public GameObject confirmationPanel;
        public Button confirmEndButton;
        public Button cancelEndButton;
        public GameObject replyRoot;
        public TMP_InputField replyInput;
        public Button sendButton;
        public Button retryButton;
        public Button historyButton;
        public Button previousPageButton;
        public TMP_Text connectionText;
        public TMP_Text pageText;
        [Min(1f)] public float charactersPerSecond = 32f;

        public event Action NextRequested;
        public event Action EndRequested;
        public event Action DismissRequested;
        public event Action ConfirmEndRequested;
        public event Action CancelEndRequested;
        public event Action<string> SendRequested;
        public event Action RetryRequested;
        public event Action HistoryRequested;

        public bool IsTyping => typing != null;
        public bool IsVisible => visible && !suspended && !presentationBlocked;
        public bool IsConfirming { get; private set; }
        public string DisplayedText => bodyText != null ? bodyText.text : string.Empty;
        public string Draft => replyInput != null ? replyInput.text : string.Empty;
        public int CurrentPage => bodyText != null ? bodyText.pageToDisplay : 1;
        public int PageCount => bodyText != null ? Mathf.Max(1, bodyText.textInfo.pageCount) : 1;
        public bool HasMorePages => CurrentPage < PageCount;

        private Coroutine typing;
        private bool visible;
        private bool suspended;
        private bool allowNext;
        private bool bound;
        private bool apiMode;
        private bool canEdit;
        private bool canSend;
        private bool canEnd = true;
        private bool canDepart;
        private bool canRetry;
        private bool canReadHistory;
        private Button boundDismissButton;
        private string nextCaption = "계속 듣기";
        private readonly HashSet<object> presentationBlockOwners = new HashSet<object>();
        private bool presentationBlocked => presentationBlockOwners.Count > 0;
        private float processingElapsed;
        private string processingMessage = "";
        private bool animateProcessing;
        public DialogueProcessingState ProcessingState { get; private set; }

        private void Awake() => BindButtons();

        private void Update()
        {
            if (animateProcessing) { processingElapsed += Time.unscaledDeltaTime; RefreshProcessingLabel(); }
            // Multiline Enter never submits while the Korean IME is composing.
            if (replyInput != null && replyInput.isFocused && string.IsNullOrEmpty(Input.compositionString)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) NotifySend();
        }

        public void ShowLine(string speaker, string text)
        {
            BindButtons();
            StopTyping();
            visible = true;
            if (speakerText != null)
            {
                speakerText.richText = false;
                speakerText.text = speaker ?? string.Empty;
            }
            if (bodyText != null)
            {
                bodyText.richText = false;
                bodyText.text = text ?? string.Empty;
                bodyText.overflowMode = TextOverflowModes.Page;
                bodyText.pageToDisplay = 1;
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
            // Compatibility with A's existing captions. New integrations should send a typed state.
            // Unknown captions remain ordinary feedback; elapsed time never becomes an error.
            text = text ?? string.Empty;
            if (text.EndsWith("의 응답을 기다리고 있어요", StringComparison.Ordinal))
                SetProcessingState(string.IsNullOrEmpty(DisplayedText) ? DialogueProcessingState.PreparingOpening : DialogueProcessingState.WaitingForReply);
            else if (text == "자동 저장을 기다리고 있어요") SetProcessingState(DialogueProcessingState.SavingTranscript);
            else if (text == "상담을 마쳤어요 · 평가 중") SetProcessingState(DialogueProcessingState.WaitingForEvaluation);
            else if (text == "응답 저장 후 상담을 마칠게요") SetProcessingState(DialogueProcessingState.EndingRequested);
            else if (text.StartsWith("상담 저장 완료", StringComparison.Ordinal)) SetProcessingState(DialogueProcessingState.Saved, text);
            else if (canRetry || text == "연결·자동 저장 확인 필요 · 재시도") SetProcessingState(DialogueProcessingState.Error, text);
            else SetProcessingState(DialogueProcessingState.Ready, text);
        }

        /// <summary>A supplies operation/error states; B supplies SavingResult until its own commit succeeds.</summary>
        public void SetProcessingState(DialogueProcessingState state, string message = null)
        {
            if (ProcessingState != state) processingElapsed = 0;
            ProcessingState = state;
            processingMessage = message ?? DialogueProcessingText.Label(state);
            animateProcessing = DialogueProcessingText.IsWaiting(state);
            RefreshProcessingLabel();
        }

        private void RefreshProcessingLabel()
        {
            if (statusText == null) return;
            float elapsed = processingElapsed;
            statusText.richText = false;
            statusText.text = processingMessage + (animateProcessing ? new string('.', 1 + (int)(elapsed * 2) % 3) : "");
            if (animateProcessing && elapsed >= 20f) statusText.text += "  잠시 더 기다려 주세요";
        }

        /// <summary>A D modal blocks only presentation input; draft, line and service state are untouched.</summary>
        public void SetPresentationBlocked(bool blocked)
        {
            SetPresentationBlock(this, blocked);
        }

        public void SetPresentationBlock(object owner, bool blocked)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (blocked) presentationBlockOwners.Add(owner); else presentationBlockOwners.Remove(owner);
            RefreshVisibility();
        }

        public void SetNextAvailable(bool available)
        {
            allowNext = available;
            RefreshVisibility();
        }

        public void SetEndAvailable(bool available)
        {
            canEnd = available;
            RefreshVisibility();
        }

        /// <summary>The visit owner enables departure only after the consultation is safely finished.</summary>
        public void SetDepartureAvailable(bool available)
        {
            canDepart = available;
            BindButtons();
            RefreshVisibility();
        }

        /// <summary>Adds the departure control to older scenes without replacing their serialized UI.</summary>
        public void EnsureDepartureControl()
        {
            if (dismissButton != null || endButton == null) return;
            var buttonObject = Instantiate(endButton.gameObject, endButton.transform.parent, false);
            buttonObject.name = "DismissPatientButton";
            dismissButton = buttonObject.GetComponent<Button>();
            // A copied UnityEvent can include persistent end-consultation listeners as well as runtime ones.
            dismissButton.onClick = new Button.ButtonClickedEvent();
            TMP_Text label = dismissButton.GetComponentInChildren<TMP_Text>(true);
            if (label != null) { label.richText = false; label.text = "환자 내보내기"; }
            buttonObject.SetActive(false);
        }

        /// <summary>Retains the PR24 adapter/controller paging API.</summary>
        public bool AdvancePage() => !IsTyping && HasMorePages && AdvancePageOrReveal();

        public void SetNextAction(bool available, string caption)
        {
            nextCaption = caption ?? "계속 듣기";
            SetNextAvailable(available);
        }

        public void SetApiMode(bool value)
        {
            apiMode = value;
            if (!value) { canEdit = canSend = canRetry = canReadHistory = false; canEnd = true; }
            RefreshVisibility();
        }

        public void SetComposeState(bool editable, bool sendable, bool endable, bool retryable, bool historyAvailable)
        {
            canEdit = editable;
            canSend = sendable;
            canEnd = endable;
            canRetry = retryable;
            canReadHistory = historyAvailable;
            RefreshVisibility();
        }

        public void SetConnectionLabel(string value)
        {
            if (connectionText != null) connectionText.text = value ?? string.Empty;
        }

        public void SetDraft(string value)
        {
            if (replyInput != null) replyInput.SetTextWithoutNotify(value ?? string.Empty);
        }

        /// <summary>Paging only changes presentation; the complete line remains in the transcript.</summary>
        public bool AdvancePageOrReveal()
        {
            if (IsTyping) { RevealAll(); return true; }
            if (!HasMorePages) return false;
            bodyText.pageToDisplay++;
            BeginPage();
            return true;
        }

        private void BeginPage()
        {
            StopTyping();
            bodyText.ForceMeshUpdate();
            int first = bodyText.textInfo.pageInfo[Mathf.Clamp(CurrentPage - 1, 0, PageCount - 1)].firstCharacterIndex;
            bodyText.maxVisibleCharacters = first;
            if (isActiveAndEnabled) typing = StartCoroutine(TypeLine());
            RefreshVisibility();
        }

        private void PreviousPage()
        {
            if (!IsVisible || IsConfirming || bodyText == null || CurrentPage <= 1) return;
            StopTyping();
            bodyText.pageToDisplay--;
            bodyText.maxVisibleCharacters = int.MaxValue;
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
            canDepart = false;
            RefreshVisibility();
        }

        private IEnumerator TypeLine()
        {
            int page = Mathf.Clamp(CurrentPage - 1, 0, PageCount - 1);
            int total = bodyText.textInfo.characterCount == 0 ? 0 : bodyText.textInfo.pageInfo[page].lastCharacterIndex + 1;
            float shown = bodyText.textInfo.characterCount == 0 ? 0 : bodyText.textInfo.pageInfo[page].firstCharacterIndex;
            // Defer once so the Coroutine handle is always assigned before completion.
            yield return null;
            while (shown < total)
            {
                if (!suspended && !presentationBlocked && !IsConfirming)
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
                root.interactable = shown && !IsConfirming && !presentationBlocked;
                root.blocksRaycasts = shown;
            }
            if (confirmationPanel != null)
                confirmationPanel.SetActive(shown && IsConfirming);
            bool interactive = shown && !IsConfirming && !presentationBlocked;
            if (nextButton != null)
            {
                nextButton.interactable = interactive && ((allowNext && !canDepart) || IsTyping || HasMorePages);
                TMP_Text label = nextButton.GetComponentInChildren<TMP_Text>();
                if (label != null) label.text = IsTyping ? "바로 보기" : HasMorePages ? "다음 쪽" : nextCaption;
            }
            if (previousPageButton != null) previousPageButton.interactable = interactive && CurrentPage > 1;
            if (pageText != null) pageText.text = PageCount > 1 ? CurrentPage + " / " + PageCount : string.Empty;
            if (endButton != null)
            {
                endButton.gameObject.SetActive(!canDepart);
                endButton.interactable = interactive && canEnd && !canDepart;
            }
            if (dismissButton != null)
            {
                dismissButton.gameObject.SetActive(canDepart);
                dismissButton.interactable = interactive && canDepart;
            }
            if (replyRoot != null) replyRoot.SetActive(apiMode);
            if (replyInput != null) replyInput.interactable = interactive && apiMode && canEdit && !canDepart;
            if (sendButton != null) sendButton.interactable = interactive && apiMode && canSend && !canDepart;
            if (retryButton != null) { retryButton.gameObject.SetActive(apiMode && canRetry); retryButton.interactable = interactive && canRetry; }
            if (historyButton != null) { historyButton.gameObject.SetActive(apiMode); historyButton.interactable = interactive && canReadHistory; }
        }

        private void BindButtons()
        {
            EnsureDepartureControl();
            if (boundDismissButton != dismissButton)
            {
                if (boundDismissButton != null) boundDismissButton.onClick.RemoveListener(NotifyDismiss);
                boundDismissButton = dismissButton;
                if (boundDismissButton != null) boundDismissButton.onClick.AddListener(NotifyDismiss);
            }
            if (bound) return;
            if (nextButton != null) nextButton.onClick.AddListener(NotifyNext);
            if (endButton != null) endButton.onClick.AddListener(NotifyEnd);
            if (confirmEndButton != null) confirmEndButton.onClick.AddListener(NotifyConfirm);
            if (cancelEndButton != null) cancelEndButton.onClick.AddListener(NotifyCancel);
            if (sendButton != null) sendButton.onClick.AddListener(NotifySend);
            if (retryButton != null) retryButton.onClick.AddListener(NotifyRetry);
            if (historyButton != null) historyButton.onClick.AddListener(NotifyHistory);
            if (previousPageButton != null) previousPageButton.onClick.AddListener(PreviousPage);
            bound = true;
        }

        private void NotifyNext() { if (IsVisible && !IsConfirming && (!canDepart || IsTyping || HasMorePages)) NextRequested?.Invoke(); }
        private void NotifyEnd() { if (IsVisible && !IsConfirming && canEnd && !canDepart) EndRequested?.Invoke(); }
        private void NotifyDismiss() { if (isActiveAndEnabled && IsVisible && !IsConfirming && canDepart) DismissRequested?.Invoke(); }
        private void NotifyConfirm() { if (IsVisible && IsConfirming) ConfirmEndRequested?.Invoke(); }
        private void NotifyCancel() { if (IsVisible && IsConfirming) CancelEndRequested?.Invoke(); }
        private void NotifySend()
        {
            if (IsVisible && !IsConfirming && apiMode && canSend && !canDepart && string.IsNullOrEmpty(Input.compositionString)
                && !string.IsNullOrWhiteSpace(Draft)) SendRequested?.Invoke(Draft);
        }
        private void NotifyRetry() { if (IsVisible && !IsConfirming && canRetry) RetryRequested?.Invoke(); }
        private void NotifyHistory() { if (IsVisible && !IsConfirming && canReadHistory) HistoryRequested?.Invoke(); }

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
            if (boundDismissButton != null) boundDismissButton.onClick.RemoveListener(NotifyDismiss);
            if (confirmEndButton != null) confirmEndButton.onClick.RemoveListener(NotifyConfirm);
            if (cancelEndButton != null) cancelEndButton.onClick.RemoveListener(NotifyCancel);
            if (sendButton != null) sendButton.onClick.RemoveListener(NotifySend);
            if (retryButton != null) retryButton.onClick.RemoveListener(NotifyRetry);
            if (historyButton != null) historyButton.onClick.RemoveListener(NotifyHistory);
            if (previousPageButton != null) previousPageButton.onClick.RemoveListener(PreviousPage);
        }
    }
}

namespace Gamsamo.Dialogue
{
    // Presentation states, not AI/save DTOs. A/B retain ownership of the underlying operation.
    public enum DialogueProcessingState
    {
        Ready, PreparingOpening, WaitingForReply, SavingTranscript, WaitingForEvaluation,
        SavingResult, EndingRequested, Saved, Error
    }

    public static class DialogueProcessingText
    {
        public static bool IsWaiting(DialogueProcessingState state) => state != DialogueProcessingState.Ready
            && state != DialogueProcessingState.Saved && state != DialogueProcessingState.Error;

        public static string Label(DialogueProcessingState state)
        {
            switch (state)
            {
                case DialogueProcessingState.PreparingOpening: return "첫 이야기를 준비하고 있어요";
                case DialogueProcessingState.WaitingForReply: return "답변을 기다리고 있어요";
                case DialogueProcessingState.SavingTranscript: return "상담 기록을 저장하고 있어요";
                case DialogueProcessingState.WaitingForEvaluation: return "상담 결과를 정리하고 있어요";
                case DialogueProcessingState.SavingResult: return "결과를 저장하고 있어요";
                case DialogueProcessingState.EndingRequested: return "진행 중인 응답과 기록 저장 후 상담을 마칠게요";
                case DialogueProcessingState.Saved: return "상담 기록 저장 완료 · 상세 결과는 서류에서 확인해 주세요";
                case DialogueProcessingState.Error: return "처리를 완료하지 못했어요. 재시도로 이어갈 수 있어요";
                default: return "답변을 자유롭게 적어 주세요";
            }
        }
    }
}
