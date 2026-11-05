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
        private bool replayOpening;

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
                    if (modalLease != null || visits.room == null || (current != photo && visits.room.IsOverlayBlocking
                        && !(current == ending && replayOpening && visits.room.IsPcOpen))) return false;
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
            runtime.PhotoForPatient = id => id + "_smile";
        }
        public EndingAvailability[] GetEndingAvailability()
        {
            var state = runtime?.Progress?.State;
            if (state == null) return Array.Empty<EndingAvailability>();
            return state.Patients.Select(p => {
                var entry = content?.patients?.FirstOrDefault(e => e != null && e.patientId == p.PatientId);
                bool ready = entry != null && entry.approved && !string.IsNullOrWhiteSpace(entry.description);
                return new EndingAvailability { PatientId = p.PatientId, PhotoId = p.PatientId + "_smile",
                    Title = entry?.title, Unlocked = p.Cleared, Approved = entry?.approved == true,
                    PhotoPresent = entry?.photo != null, StoryPresent = !string.IsNullOrWhiteSpace(entry?.description),
                    Status = !p.Cleared ? "잠김" : ready ? "다시 보기" : "후일담 준비 중" };
            }).ToArray();
        }
        public bool TryReplayEnding(string patientId, out string error)
        {
            error = "현재 다시 보기를 시작할 수 없습니다.";
            var state = runtime?.Progress?.State;
            if (state == null || ending == null || ending.IsOpen || ending.AwaitingSave || state.Phase == ProgressPhase.Presenting
                || (state.FinalEndingUnlocked && !state.FinalEndingPlayed)) return false;
            var available = GetEndingAvailability().FirstOrDefault(e => e.PatientId == patientId);
            if (available == null || !available.Unlocked) return false;
            if (!available.Ready) { error = "후일담 준비 중"; return false; }
            var receipt = state.Settlements.Values.FirstOrDefault(r => r.Cleared && r.Identity.PatientId == patientId);
            if (receipt == null) return false;
            var id = receipt.Identity;
            if (!ending.RestoreCommittedClear(state.ProgressId, patientId, id.CaseId, id.RoundId, true, true,
                visits.patients.First(p => p.patientId == patientId).displayName, 0, state.Photos.Contains(patientId + "_smile"), "completed")) return false;
            replayOpening = true;
            try { ending.Replay(); }
            finally { replayOpening = false; }
            if (!ending.IsOpen || !ending.IsReplay) return false;
            error = null; return true;
        }
        public bool TryReplayFinalEnding(out string error)
        {
            error = "최종 엔딩을 다시 볼 수 없습니다.";
            var state = runtime?.Progress?.State;
            if (state == null || !state.FinalEndingPlayed || ending == null || ending.IsOpen || ending.AwaitingSave) return false;
            if (content == null || !content.creditsApproved || string.IsNullOrWhiteSpace(content.credits)) { error = "크레딧 준비 중"; return false; }
            if (!ending.RestoreFinalEnding(state.ProgressId, true, true, "completed")) return false;
            SetCreditsPhotos(state);
            replayOpening = true;
            try { ending.Replay(); } finally { replayOpening = false; }
            if (!ending.IsOpen || !ending.IsReplay) return false;
            error = null; return true;
        }
        private void SetCreditsPhotos(GameProgress state)
        {
            var photos = content?.patients?.Where(p => p != null && p.approved && p.photo != null && state.Photos.Contains(p.photoId))
                .Select(p => new PhotoGalleryEntry { photoId = p.photoId, title = p.title, description = p.description, thumbnail = p.photo, fullSize = p.photo }).ToArray();
            ending?.SetCreditsPhotos(state.ProgressId, photos ?? Array.Empty<PhotoGalleryEntry>());
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
                string name = visits.patients.FirstOrDefault(p => p.patientId == state.Current?.PatientId)?.displayName;
                pc?.profileView?.SetProgress("승급 기준 준비 중", name, StatusText(state), state.ClearedCount, state.Patients.Count);
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
            {
                ending.RestoreFinalEnding(state.ProgressId, true, true, state.FinalPresentationStage);
                SetCreditsPhotos(state);
            }
        }
        private static string StatusText(GameProgress state) => state.Phase == ProgressPhase.Finished ? "모든 내담자의 상담을 마쳤습니다"
            : state.Current?.NeedsRevisit == true ? "재상담 대기" : state.Phase == ProgressPhase.Waiting ? "방문 대기" : "상담 진행 중";
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
