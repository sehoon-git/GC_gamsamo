using System;
using System.Linq;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using Gamsamo.UI;
using Gamsamo.UI.Presentation;
using UnityEngine;

namespace Gamsamo.Progression
{
    public sealed class GamePresentationIntegration : MonoBehaviour
    {
        public GameObject guidePrefab, endingPrefab, photoPrefab, processingPrefab, audioPrefab;
        public EndingContentCatalog content;
        private GameProgressRuntime runtime;
        private PatientVisitController visits;
        private EndingPresentationView ending;
        private DialogueProcessingBinding processing;
        private IDisposable modalLease;
        private int displayedRevision = -1;

        public void Bind(GameProgressRuntime source, PatientVisitController controller, PatientAiAdapter ai)
        {
            runtime = source; visits = controller;
            var guide = Spawn<GuidebookView>(guidePrefab);
            ending = Spawn<EndingPresentationView>(endingPrefab);
            var photo = Spawn<PhotoViewer>(photoPrefab);
            processing = Spawn<DialogueProcessingBinding>(processingPrefab);
            foreach (var modal in new PresentationModal[] { guide, ending, photo })
            {
                if (modal == null) continue;
                modal.dialogue = visits.dialogue;
                var current = modal;
                modal.BindModalGate(() =>
                {
                    if (modalLease != null || visits.room == null || (current != photo && visits.room.IsOverlayBlocking)) return false;
                    modalLease = visits.room.AcquireInputBlock(); return true;
                }, () => { modalLease?.Dispose(); modalLease = null; });
            }
            if (ending != null) { ending.content = content; ending.PlaybackSaveRequested += SavePlayback; }
            if (processing != null)
            {
                processing.Bind(ai, visits.dialogue);
                processing.GameResultRetryRequested += runtime.RetryProgress;
            }
            if (visits.room?.pcDesktopView?.photoView != null) visits.room.pcDesktopView.photoView.viewer = photo;
            var audio = Spawn<PresentationAudio>(audioPrefab);
            if (audio != null) { audio.content = content; audio.PlayRoomMusic(); }
            // Stable entitlement exists even while the approved image has not yet been delivered.
            runtime.PhotoForPatient = id => content?.FindApproved(id)?.photoId ?? id + "_smile";
        }
        private T Spawn<T>(GameObject prefab) where T : Component => prefab == null ? null : Instantiate(prefab).GetComponent<T>();
        public void BeginResultSave() => processing?.BeginGameResultSave();
        public void CompleteResultSave() => processing?.CompleteGameResultSave();
        public void FailResultSave(string message) => processing?.FailGameResultSave(message);
        private void Update()
        {
            var state = runtime?.Progress?.State;
            if (state == null) return;
            if (state.Revision != displayedRevision)
            {
                displayedRevision = state.Revision;
                var pc = visits.room?.pcDesktopView;
                var photos = content?.patients?.Where(p => p != null && p.approved && p.photo != null)
                    .Select(p => new PhotoGalleryEntry { photoId = p.photoId, title = p.title, description = p.description, thumbnail = p.photo, fullSize = p.photo }).ToArray();
                if (pc?.photoView != null)
                {
                    pc.photoView.EmptyStateText = photos == null || photos.Length == 0 ? "웃는 사진은 준비 중입니다. 클리어 기록은 안전하게 저장됩니다." : null;
                    pc.photoView.SetUnlockedPhotos(state.ProgressId, state.Photos, photos);
                }
            }
            if (ending == null || ending.IsOpen || ending.AwaitingSave) return;
            var receipt = state.PendingPresentation;
            if (state.Phase == ProgressPhase.Presenting && receipt != null)
            {
                var id = receipt.Identity;
                var entry = content?.FindApproved(id.PatientId);
                ending.RestoreCommittedClear(id.ProgressId, id.PatientId, id.CaseId, id.RoundId, true, receipt.Cleared,
                    visits.patients.First(p => p.patientId == id.PatientId).displayName, checked((int)receipt.Granted),
                    entry?.photo != null && state.Photos.Contains(entry.photoId), state.PresentationStage);
            }
            else if (state.Phase == ProgressPhase.Finished && state.FinalEndingUnlocked && !state.FinalEndingPlayed)
                ending.RestoreFinalEnding(state.ProgressId, true, true, state.FinalPresentationStage);
        }
        private void SavePlayback(string game, string patient, string caseId, string session, string stage)
        {
            bool saved = false; string error = null;
            try
            {
                if (game != runtime.Progress.State.ProgressId) throw new InvalidOperationException("다른 진행의 재생 요청입니다.");
                if (string.IsNullOrEmpty(patient)) runtime.Progress.CompleteFinalEnding();
                else runtime.Progress.SavePresentationStage(new RoundIdentity { ProgressId = game, PatientId = patient, CaseId = caseId, RoundId = session }.Key, stage);
                saved = true;
            }
            catch (Exception) { error = "재생 상태를 저장하지 못했습니다. 다시 시도해 주세요."; }
            ending.AcknowledgePlaybackSaved(game, patient, caseId, session, stage, saved, error);
        }
        private void OnDestroy() { modalLease?.Dispose(); }
    }
}
