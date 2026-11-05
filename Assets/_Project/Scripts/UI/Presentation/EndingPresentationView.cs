using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    /// <summary>Renders only B's committed results. Playback acknowledgement is also owned by B.</summary>
    public sealed class EndingPresentationView : PresentationModal
    {
        public EndingContentCatalog content;
        public TMP_Text titleText, bodyText, rewardText, photoNotice, feedbackText;
        public Image photo;
        public Button continueButton, skipButton, replayButton, retryButton;
        public RectTransform creditsBody;
        public GameObject creditsViewport;
        public TMP_Text creditsText;
        public Image[] finalPhotos;
        public TMP_Text[] finalCaptions;
        // game, patient (empty for final), case, session, requested presentation stage.
        public event Action<string, string, string, string, string> PlaybackSaveRequested;
        public string Stage { get; private set; } = "";
        public string PendingStage { get; private set; } = "";
        public bool IsReplay { get; private set; }
        public bool AwaitingSave { get; private set; }
        public string Identity => identity;
        private string gameId, patientId, caseId, sessionId, identity = "", displayName;
        private int awardedCoins;
        private bool photoUnlocked;
        private bool final;
        private float creditElapsed;
        private Vector2 creditsStart;

        private void Awake()
        {
            if (continueButton != null) continueButton.onClick.AddListener(Continue);
            if (skipButton != null) skipButton.onClick.AddListener(Skip);
            if (replayButton != null) replayButton.onClick.AddListener(Replay);
            if (retryButton != null) retryButton.onClick.AddListener(RetryPlaybackSave);
            if (panel != null) panel.SetActive(false);
            if (creditsBody != null) creditsStart = creditsBody.anchoredPosition;
        }

        /// <summary>No evaluation is accepted here. B calls after A→B→C→B persistence succeeds.</summary>
        public bool RestoreCommittedClear(string game, string patient, string caseValue, string session,
            bool gameSaveCommitted, bool cleared, string name, int coinsAwarded, bool photoWasUnlocked,
            string savedStage = "clear")
        {
            if (!gameSaveCommitted || !cleared || coinsAwarded < 0 || !CompleteIds(game, patient, caseValue, session)
                || !ValidStage(savedStage, false) || IsOpen || AwaitingSave) return false;
            if (identity == Key(game, patient, caseValue, session) && (Stage == "completed" || Stage == "skipped")
                && savedStage != "completed" && savedStage != "skipped") return false;
            StoreContext(game, patient, caseValue, session, false);
            displayName = name ?? ""; awardedCoins = coinsAwarded; photoUnlocked = photoWasUnlocked;
            Stage = savedStage;
            return RestoreStage();
        }

        /// <summary>B supplies its persisted final-unlock flag. D never counts patients or infers unlock.</summary>
        public bool RestoreFinalEnding(string game, bool gameSaveCommitted, bool finalUnlocked, string savedStage = "credits")
        {
            if (string.IsNullOrWhiteSpace(game) || !gameSaveCommitted || !finalUnlocked
                || !ValidStage(savedStage, true) || IsOpen || AwaitingSave) return false;
            if (identity == Key(game, "", "", "") && (Stage == "completed" || Stage == "skipped")
                && savedStage != "completed" && savedStage != "skipped") return false;
            StoreContext(game, "", "", "", true);
            Stage = savedStage;
            return RestoreStage();
        }

        private void StoreContext(string game, string patient, string caseValue, string session, bool isFinal)
        {
            gameId = game; patientId = patient; caseId = caseValue; sessionId = session; final = isFinal;
            identity = Key(game, patient, caseValue, session);
            IsReplay = false; PendingStage = ""; creditElapsed = 0;
            if (finalPhotos != null) foreach (var item in finalPhotos) if (item != null) { item.sprite = null; item.enabled = false; }
            if (finalCaptions != null) foreach (var item in finalCaptions) GuidebookView.Set(item, "");
            if (creditsBody != null) creditsBody.anchoredPosition = creditsStart;
            GuidebookView.Set(feedbackText, "");
        }

        private bool RestoreStage()
        {
            if (Stage == "completed" || Stage == "skipped") return true; // Restored finished playback stays closed.
            if (PlaybackSaveRequested == null || !OpenModal()) return false;
            Render();
            if (Stage == "clear") PresentationAudio.Instance?.PlayClearSound();
            return true;
        }

        public void Continue()
        {
            if (!IsOpen || AwaitingSave || !string.IsNullOrEmpty(PendingStage)) return;
            if (!final && (Stage == "ending" || IsReplay) && bodyText != null && bodyText.pageToDisplay < bodyText.textInfo.pageCount)
            { bodyText.pageToDisplay++; return; }
            if (IsReplay) { FinishPresentation(); return; }
            Request(Stage == "clear" ? "ending" : "completed");
        }
        public void Skip()
        {
            if (!IsOpen || AwaitingSave || !string.IsNullOrEmpty(PendingStage)) return;
            if (IsReplay) { FinishPresentation(); return; }
            Request("skipped");
        }
        public void Replay()
        {
            if (string.IsNullOrEmpty(identity) || AwaitingSave || IsOpen
                || (Stage != "completed" && Stage != "skipped") || !OpenModal()) return;
            IsReplay = true; creditElapsed = 0;
            Render(); // No save, grant, evaluation or economy callbacks on replay.
        }
        private void Request(string target)
        {
            if (PlaybackSaveRequested == null) return;
            PendingStage = target; AwaitingSave = true;
            GuidebookView.Set(feedbackText, "재생 상태를 저장하고 있어요");
            RefreshButtons();
            PlaybackSaveRequested.Invoke(gameId, patientId, caseId, sessionId, target);
        }
        public void RetryPlaybackSave()
        {
            if (!IsOpen || AwaitingSave || string.IsNullOrEmpty(PendingStage)) return;
            Request(PendingStage);
        }
        public bool AcknowledgePlaybackSaved(string game, string patient, string caseValue, string session,
            string stage, bool committed, string error = null)
        {
            if (!IsOpen || !AwaitingSave || Key(game, patient, caseValue, session) != identity || stage != PendingStage) return false;
            AwaitingSave = false;
            if (!committed)
            {
                GuidebookView.Set(feedbackText, error ?? "재생 상태를 저장하지 못했습니다. 재시도로 이어가세요.");
                RefreshButtons(); return true;
            }
            Stage = stage; PendingStage = "";
            GuidebookView.Set(feedbackText, "");
            if (stage == "completed" || stage == "skipped") FinishPresentation();
            else Render();
            return true;
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Skip();
            // Gentle top-to-bottom credits. Playback time never represents save or AI progress.
            if (final && creditsBody != null && !AwaitingSave)
            {
                creditElapsed += Time.unscaledDeltaTime;
                creditsBody.anchoredPosition = creditsStart + Vector2.down * Mathf.Min(creditElapsed * 20f, creditsBody.rect.height + 340f);
            }
        }
        private void Render()
        {
            bool clear = !IsReplay && !final && Stage == "clear";
            if (!clear) PresentationAudio.Instance?.PlayEndingMusic();
            EndingContent entry = content != null ? content.FindApproved(patientId) : null;
            GuidebookView.Set(titleText, clear ? displayName + " 님의 상담을 마쳤어요" : final ? "함께 만든 이야기" : entry != null ? entry.title : "후일담");
            GuidebookView.Set(bodyText, clear ? "함께 나눈 이야기와 상세 결과는 책상 위 서류에 보관되어 있습니다."
                : final ? FinalCreditsText() : entry != null ? entry.description : "후일담 콘텐츠가 아직 준비되지 않았습니다. 계속하기로 돌아갈 수 있어요.");
            GuidebookView.Set(rewardText, clear ? "이번 획득 " + awardedCoins + " C" : "");
            GuidebookView.Set(photoNotice, clear && photoUnlocked ? "PC 사진첩에 사진이 추가되었습니다." : "");
            if (photo != null) { photo.sprite = !clear && !final && photoUnlocked && entry != null ? entry.photo : null; photo.enabled = photo.sprite != null; photo.preserveAspect = true; }
            if (bodyText != null)
            {
                bool withPhoto = photo != null && photo.enabled;
                bodyText.gameObject.SetActive(!final);
                bodyText.rectTransform.anchoredPosition = new Vector2(270, withPhoto ? -410 : -150);
                bodyText.rectTransform.sizeDelta = new Vector2(620, withPhoto ? 220 : 310);
                bodyText.pageToDisplay = 1;
                bodyText.ForceMeshUpdate();
            }
            if (creditsViewport != null) creditsViewport.SetActive(final);
            if (final)
            {
                GuidebookView.Set(creditsText, FinalCreditsText());
                if (creditsText != null && creditsBody != null)
                {
                    creditsBody.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(500, creditsText.preferredHeight));
                    creditsStart = new Vector2(0, creditsBody.rect.height);
                    creditsBody.anchoredPosition = creditsStart; creditElapsed = 0;
                }
            }
            RefreshButtons();
        }
        private string FinalCreditsText()
        {
            if (content == null || !content.creditsApproved || string.IsNullOrWhiteSpace(content.credits))
                return "최종 엔딩이 해금되었습니다.\n정식 후일담 사진과 제작진 크레딧은 준비 중입니다.";
            return content.credits;
        }

        /// <summary>B supplies only saved, unlocked photos and verified captions for this run.</summary>
        public void SetCreditsPhotos(string game, IReadOnlyList<PhotoGalleryEntry> unlockedPhotos)
        {
            if (game != gameId || !final) return;
            for (int index = 0; index < 6; index++)
            {
                var entry = unlockedPhotos != null && index < unlockedPhotos.Count ? unlockedPhotos[index] : null;
                if (finalPhotos != null && index < finalPhotos.Length && finalPhotos[index] != null)
                {
                    finalPhotos[index].sprite = entry?.thumbnail;
                    finalPhotos[index].enabled = entry?.thumbnail != null;
                }
                if (finalCaptions != null && index < finalCaptions.Length)
                    GuidebookView.Set(finalCaptions[index], entry?.title);
            }
        }
        private void RefreshButtons()
        {
            bool ready = !AwaitingSave && string.IsNullOrEmpty(PendingStage);
            if (continueButton != null) { continueButton.interactable = ready; continueButton.gameObject.SetActive(ready); }
            if (skipButton != null) { skipButton.interactable = ready; skipButton.gameObject.SetActive(ready); }
            if (retryButton != null) { retryButton.gameObject.SetActive(!AwaitingSave && !string.IsNullOrEmpty(PendingStage)); retryButton.interactable = !AwaitingSave; }
            if (replayButton != null) replayButton.gameObject.SetActive(false); // The host's replay entry calls Replay after completion.
        }
        private static bool CompleteIds(params string[] ids)
        { foreach (string id in ids) if (string.IsNullOrWhiteSpace(id)) return false; return true; }
        private static bool ValidStage(string stage, bool final) => stage == "completed" || stage == "skipped" || stage == (final ? "credits" : "clear") || (!final && stage == "ending");
        private static string Key(params string[] values)
        { string result = ""; foreach (string value in values) result += (value ?? "").Length + ":" + value; return result; }
        private void FinishPresentation() { CloseModal(); PresentationAudio.Instance?.RestoreRoomMusic(); }
        protected override void OnDisable()
        {
            base.OnDisable();
            // B owns any outstanding write. Restore its committed snapshot when the view is recreated.
            AwaitingSave = false; PendingStage = "";
        }
    }
}
