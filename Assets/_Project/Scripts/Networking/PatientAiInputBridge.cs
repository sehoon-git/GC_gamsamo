using System;
using Gamsamo.Dialogue;
using UnityEngine;

namespace Gamsamo.Networking
{
    /// <summary>Presentation only: delegates input, retries and persistence to the PR24 adapter.</summary>
    [DisallowMultipleComponent]
    public sealed class PatientAiInputBridge : MonoBehaviour
    {
        private PatientAiAdapter adapter;
        private PatientVisitController visits;
        private PatientDialogueView view;
        private bool bound;
        private string feedback = "";

        private void Start() => Bind();
        private void OnEnable() { if (bound) Refresh(); }

        private void Bind()
        {
            if (bound) return;
            adapter = GetComponent<PatientAiAdapter>();
            visits = GetComponent<PatientVisitController>();
            view = visits == null ? null : visits.dialogue;
            if (adapter == null || visits == null || view == null) return;
            adapter.StateChanged += Refresh;
            adapter.ErrorOccurred += ShowFeedback;
            adapter.PatientLineCommitted += ClearFeedback;
            visits.StateChanged += VisitChanged;
            view.SendRequested += Submit;
            view.RetryRequested += Retry;
            view.HistoryRequested += OpenRecord;
            if (view.replyInput != null) view.replyInput.onValueChanged.AddListener(DraftChanged);
            bound = true;
            Refresh();
        }

        private void VisitChanged(PatientVisitController.VisitState state)
        {
            if (state == PatientVisitController.VisitState.Entering || state == PatientVisitController.VisitState.Waiting)
                feedback = "";
            Refresh();
        }

        private void Refresh()
        {
            if (view == null || adapter == null || visits == null) return;
            bool supported = adapter.IsAiVisit && visits.IsConsultationActive;
            view.SetApiMode(supported);
            view.SetConnectionLabel(supported ? (adapter.IsMock ? "모의 AI · 연결 시연" : visits.CurrentPatient.displayName + " AI 상담") : "임시 대화 시연 · AI 미연결");
            if (!supported) return;
            view.SetComposeState(adapter.CanEditDraft, adapter.CanSubmit,
                adapter.CurrentPhase == "active" && !adapter.EndRequested,
                adapter.CanRetry, visits.room != null && !string.IsNullOrEmpty(adapter.LastPatientText));
            if (view.Draft != adapter.Draft) view.SetDraft(adapter.Draft);
            // Error messages are already sanitized by the adapter; never show raw transport bodies or settings.
            string message = string.IsNullOrEmpty(adapter.LastError) ? feedback : adapter.LastError;
            if (!string.IsNullOrEmpty(message)) view.SetStatus(message);
        }

        private void DraftChanged(string text)
        {
            if (adapter != null && !adapter.UpdateDraft(text)) Refresh();
        }

        private void Submit(string text)
        {
            if (adapter == null || !string.IsNullOrEmpty(Input.compositionString)) return;
            feedback = "";
            adapter.SubmitText(text);
            Refresh();
        }

        private void Retry()
        {
            if (adapter == null) return;
            feedback = "";
            adapter.Retry();
            Refresh();
        }

        private void OpenRecord()
        {
            if (visits != null && visits.room != null && adapter != null && adapter.IsAiVisit)
                visits.room.OpenRecord();
        }

        private void ShowFeedback(string message) { feedback = message ?? ""; Refresh(); }
        private void ClearFeedback(string _) { feedback = ""; }

        private void OnDestroy()
        {
            if (!bound) return;
            adapter.StateChanged -= Refresh;
            adapter.ErrorOccurred -= ShowFeedback;
            adapter.PatientLineCommitted -= ClearFeedback;
            visits.StateChanged -= VisitChanged;
            view.SendRequested -= Submit;
            view.RetryRequested -= Retry;
            view.HistoryRequested -= OpenRecord;
            if (view.replyInput != null) view.replyInput.onValueChanged.RemoveListener(DraftChanged);
        }
    }
}
