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
            subscribed = adapter; subscribed.StateChanged += Refresh;
            Refresh();
        }
        private void Unsubscribe()
        { if (subscribed != null) subscribed.StateChanged -= Refresh; subscribed = null; }
        private void OnDisable() { Unsubscribe(); if (view != null) view.SetPresentationBlock(this, false); }

        /// <summary>B brackets the entire game-result commit; A's evaluation event is not that commit.</summary>
        public void BeginGameResultSave() { gameResultPending = true; gameSaveError = null; Refresh(); }
        public void FailGameResultSave(string safeMessage)
        { gameResultPending = true; gameSaveError = safeMessage ?? "결과를 저장하지 못했습니다."; Refresh(); }
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
            if (!string.IsNullOrEmpty(adapter.LastError)) view.SetProcessingState(DialogueProcessingState.Error, adapter.LastError);
            else if (adapter.IsBusy && view.ProcessingState == DialogueProcessingState.SavingTranscript)
                view.SetProcessingState(adapter.CurrentPhase == "pending_evaluation" ? DialogueProcessingState.SavingResult : DialogueProcessingState.SavingTranscript);
            else if (adapter.CurrentPhase == "evaluated") view.SetProcessingState(DialogueProcessingState.Saved);
            else if (adapter.CurrentPhase == "pending_evaluation") view.SetProcessingState(DialogueProcessingState.WaitingForEvaluation);
            else if (adapter.EndRequested) view.SetProcessingState(DialogueProcessingState.EndingRequested);
            else if (adapter.IsBusy) view.SetProcessingState(string.IsNullOrEmpty(adapter.LastPatientText) ? DialogueProcessingState.PreparingOpening : DialogueProcessingState.WaitingForReply);
            else view.SetProcessingState(DialogueProcessingState.Ready);
        }
    }
}
