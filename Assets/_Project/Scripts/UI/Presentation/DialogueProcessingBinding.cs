using Gamsamo.Dialogue;
using Gamsamo.Networking;
using UnityEngine;
using UnityEngine.UI;
using System;

namespace Gamsamo.UI.Presentation
{
    /// <summary>Read-only projection of A's existing adapter. Never submits, ends or retries requests.</summary>
    public sealed class DialogueProcessingBinding : MonoBehaviour
    {
        public PatientAiAdapter adapter;
        public PatientDialogueView view;
        public Button gameResultRetryButton;
        public event Action GameResultRetryRequested;
        private PatientAiAdapter subscribed;
        private bool gameResultPending;
        private string gameSaveError;
        private UiNext.UiFailureKind failureKind;
        /// <summary>Integration may classify A's current error from authoritative operation metadata.</summary>
        public bool SetAiFailureKind(AiConversationKey key, UiNext.UiFailureKind kind)
        {
            var status = adapter != null ? adapter.ServiceStatus : null;
            if (key == null || status?.Key == null || status.Phase != AiServicePhase.Error
                || key.SettlementId != status.Key.SettlementId) return false;
            failureKind = kind; Refresh(); return true;
        }

        public void Bind(PatientAiAdapter source, PatientDialogueView target)
        {
            if (view != null) view.SetPresentationBlock(this, false);
            Unsubscribe(); adapter = source; view = target;
            if (isActiveAndEnabled) Subscribe();
            Refresh();
        }
        private void Awake() { if (gameResultRetryButton != null) gameResultRetryButton.onClick.AddListener(RetryGameResult); }
        private void OnEnable() { Subscribe(); Refresh(); }
        private void Subscribe()
        {
            if (subscribed != null || adapter == null) return;
            subscribed = adapter; subscribed.StateChanged += OnAiStateChanged;
            Refresh();
        }
        private void OnAiStateChanged() { failureKind = UiNext.UiFailureKind.Unspecified; Refresh(); }
        private void Unsubscribe()
        { if (subscribed != null) subscribed.StateChanged -= OnAiStateChanged; subscribed = null; }
        private void OnDisable() { Unsubscribe(); if (view != null) view.SetPresentationBlock(this, false); }

        /// <summary>B brackets the entire game-result commit; A's evaluation event is not that commit.</summary>
        public void BeginGameResultSave() { gameResultPending = true; gameSaveError = null; Refresh(); }
        public void FailGameResultSave(string safeMessage)
        { gameResultPending = true; gameSaveError = "저장 실패 · " + (safeMessage ?? "결과를 저장하지 못했습니다."); Refresh(); }
        public void CompleteGameResultSave()
        { gameResultPending = false; gameSaveError = null; if (view != null) view.SetProcessingState(DialogueProcessingState.Saved); Refresh(); }
        public void RetryGameResult()
        {
            if (!gameResultPending || gameSaveError == null || GameResultRetryRequested == null) return;
            gameSaveError = null; Refresh(); GameResultRetryRequested.Invoke();
        }

        public void Refresh()
        {
            if (view == null) return;
            view.SetPresentationBlock(this, gameResultPending);
            if (gameResultRetryButton != null)
            {
                gameResultRetryButton.gameObject.SetActive(gameResultPending && gameSaveError != null);
                gameResultRetryButton.interactable = gameResultPending && gameSaveError != null && GameResultRetryRequested != null;
            }
            if (gameResultPending)
            {
                view.SetProcessingState(gameSaveError == null ? DialogueProcessingState.SavingResult : DialogueProcessingState.Error, gameSaveError);
                return;
            }
            if (adapter == null || !adapter.IsAiVisit) return;
            var status = adapter.ServiceStatus;
            if (status.Phase != AiServicePhase.Error) failureKind = UiNext.UiFailureKind.Unspecified;
            switch (status.Phase)
            {
                case AiServicePhase.PreparingOpening: view.SetProcessingState(DialogueProcessingState.PreparingOpening); break;
                case AiServicePhase.GeneratingReply: view.SetProcessingState(DialogueProcessingState.WaitingForReply); break;
                case AiServicePhase.Saving: view.SetProcessingState(DialogueProcessingState.SavingTranscript); break;
                case AiServicePhase.Closing: view.SetProcessingState(DialogueProcessingState.EndingRequested); break;
                case AiServicePhase.Evaluating: view.SetProcessingState(DialogueProcessingState.WaitingForEvaluation); break;
                case AiServicePhase.SavingEvaluation: view.SetProcessingState(DialogueProcessingState.SavingResult); break;
                case AiServicePhase.Completed: view.SetProcessingState(DialogueProcessingState.Saved); break;
                case AiServicePhase.Error:
                    // A currently exposes one Error phase for network and file errors. Do not guess by message/elapsed time.
                    view.SetProcessingState(DialogueProcessingState.Error, failureKind == UiNext.UiFailureKind.Connection
                        ? "연결 실패 · " + status.Error : failureKind == UiNext.UiFailureKind.Save
                        ? "저장 실패 · " + status.Error : "연결·저장 확인 필요 · " + status.Error);
                    break;
                default: failureKind = UiNext.UiFailureKind.Unspecified; view.SetProcessingState(DialogueProcessingState.Ready); break;
            }
        }
    }
}
