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
        public Sprite albumPaper;
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
        private float creditElapsed, creditTravel;
        private Vector2 creditsStart;
        private EndingContent album;
        private EndingAlbumSpread spread;
        private int momentIndex;
        private float readingElapsed;
        private TMP_Text continueLabel, skipLabel;
        private int MomentCount
        {
            get
            {
                int authored = album?.moments?.Length ?? 0;
                if (authored == 0) return album == null ? 0 : 1;
                return album.moments[authored - 1]?.photo == album.photo ? authored : authored + 1;
            }
        }
        private bool ShowingAlbum => album != null && (IsReplay || Stage == "ending");
        private bool LastMoment => momentIndex + 1 == MomentCount;

        private void Awake()
        {
            if (continueButton != null) continueButton.onClick.AddListener(Continue);
            if (skipButton != null) skipButton.onClick.AddListener(Skip);
            if (replayButton != null) replayButton.onClick.AddListener(Replay);
            if (retryButton != null) retryButton.onClick.AddListener(RetryPlaybackSave);
            if (panel != null) panel.SetActive(false);
            if (continueButton != null) continueLabel = continueButton.GetComponentInChildren<TMP_Text>(true);
            if (skipButton != null) skipLabel = skipButton.GetComponentInChildren<TMP_Text>(true);
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
            var entry = content != null ? content.FindApproved(patientId) : null;
            if (photoUnlocked && entry?.photo != null && photo != null && bodyText != null) album = entry;
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
            album = null; momentIndex = 0; readingElapsed = 0;
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
            if (!final && (Stage == "ending" || IsReplay) && HasMoreText())
            { bodyText.pageToDisplay++; readingElapsed = 0; RefreshButtons(); return; }
            if (ShowingAlbum && !LastMoment)
            {
                momentIndex++;
                RenderMoment();
                return;
            }
            if (IsReplay) { FinishPresentation(); return; }
            Request(Stage == "clear" ? "ending" : "completed");
        }
        public void Skip()
        {
            if (!IsOpen || AwaitingSave || !string.IsNullOrEmpty(PendingStage)) return;
            if (album != null)
            {
                if (ShowingAlbum && LastMoment) { Continue(); return; }
                momentIndex = MomentCount - 1;
                if (ShowingAlbum) RenderMoment();
                else Request("ending");
                return;
            }
            if (IsReplay) { FinishPresentation(); return; }
            Request("skipped");
        }
        public void Replay()
        {
            if (string.IsNullOrEmpty(identity) || AwaitingSave || IsOpen
                || (Stage != "completed" && Stage != "skipped") || !OpenModal()) return;
            IsReplay = true; creditElapsed = 0; momentIndex = 0;
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
            if (Input.GetKeyDown(KeyCode.Escape)) { Skip(); return; }
            AdvanceReading(Time.unscaledDeltaTime);
            if (final && creditTravel > 0 && !AwaitingSave && string.IsNullOrEmpty(PendingStage))
            {
                creditElapsed += Time.unscaledDeltaTime;
                float distance = Mathf.Clamp((creditElapsed - 4f) * 22f, 0, creditTravel);
                creditsBody.anchoredPosition = creditsStart + Vector2.up * distance;
            }
        }
        private void AdvanceReading(float seconds)
        {
            if (album == null || !IsOpen || AwaitingSave || !string.IsNullOrEmpty(PendingStage)) return;
            if (ShowingAlbum && LastMoment) return;
            readingElapsed += seconds;
            float duration = ShowingAlbum ? Mathf.Max(8f, 3f + bodyText.text.Length * 0.13f) : 4f;
            if (readingElapsed >= duration) Continue();
        }
        private void Render()
        {
            bool clear = !IsReplay && !final && Stage == "clear";
            if (!clear) PresentationAudio.Instance?.PlayEndingMusic();
            readingElapsed = 0;
            spread?.Hide();
            EndingContent entry = content != null ? content.FindApproved(patientId) : null;
            GuidebookView.Set(titleText, clear ? displayName + " 님의 상담을 마쳤어요" : final ? "함께 만든 이야기" : entry != null ? entry.title : "후일담");
            GuidebookView.Set(bodyText, clear ? "함께 나눈 이야기와 상세 결과는 책상 위 서류에 보관되어 있습니다."
                : final ? FinalCreditsText() : entry != null ? entry.description : "후일담 콘텐츠가 아직 준비되지 않았습니다. 계속하기로 돌아갈 수 있어요.");
            GuidebookView.Set(rewardText, clear ? "이번 획득 " + awardedCoins + " C" : "");
            GuidebookView.Set(photoNotice, clear ? (photoUnlocked ? "PC 사진첩에 사진이 추가되었습니다." : "웃는 사진과 후일담은 준비 중입니다. 클리어 기록은 저장되었습니다.") : "");
            if (photo != null) { photo.sprite = null; photo.enabled = false; }
            if (bodyText != null)
            {
                bodyText.gameObject.SetActive(!final);
                bodyText.rectTransform.anchoredPosition = new Vector2(270, -150);
                bodyText.rectTransform.sizeDelta = new Vector2(620, 310);
                bodyText.pageToDisplay = 1;
                bodyText.ForceMeshUpdate();
            }
            if (creditsViewport != null) creditsViewport.SetActive(final);
            if (final)
            {
                GuidebookView.Set(creditsText, FinalCreditsText());
                if (creditsText != null && creditsBody != null && creditsViewport != null)
                {
                    var window = (RectTransform)creditsViewport.transform;
                    float height = creditsText.GetPreferredValues(creditsText.text, window.rect.width, Mathf.Infinity).y;
                    creditsText.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                    creditsBody.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                    creditTravel = Mathf.Max(0f, height - window.rect.height);
                    creditsStart = new Vector2(0, -Mathf.Max(0f, window.rect.height - height) * 0.5f);
                    creditsBody.anchoredPosition = creditsStart; creditElapsed = 0;
                }
            }
            if (ShowingAlbum) RenderMoment();
            else RefreshButtons();
        }
        private void RenderMoment()
        {
            var moments = album.moments;
            EndingMoment moment = moments != null && momentIndex < moments.Length ? moments[momentIndex] : null;
            if (spread == null) spread = new EndingAlbumSpread(photo, bodyText, albumPaper);
            string title = moment?.title ?? displayName + "의 웃는 얼굴";
            string story = moment?.description ?? "";
            spread.Show(LastMoment ? album.photo : moment?.photo, title, story, momentIndex + 1, MomentCount);
            readingElapsed = 0;
            if (momentIndex > 0) PresentationAudio.Instance?.PlayPageSound();
            RefreshButtons();
        }
        private string FinalCreditsText()
        {
            if (content == null || !content.creditsApproved || string.IsNullOrWhiteSpace(content.credits))
                return "다섯 사람의 상담을 마쳤습니다.\n함께해 주셔서 감사합니다.";
            return content.credits;
        }

        /// <summary>B supplies only saved, unlocked photos and verified captions for this run.</summary>
        public void SetCreditsPhotos(string game, IReadOnlyList<PhotoGalleryEntry> unlockedPhotos)
        {
            if (game != gameId || !final) return;
            int slots = finalPhotos?.Length ?? 0;
            int count = Mathf.Min(unlockedPhotos?.Count ?? 0, slots);
            for (int index = 0; index < slots; index++)
            {
                var entry = index < count ? unlockedPhotos[index] : null;
                bool left = index % 2 == 0;
                int columnCount = left ? (count + 1) / 2 : count / 2;
                float x = left ? 38 : 918, y = 128 + (3 - columnCount) * 78 + index / 2 * 156;
                var image = finalPhotos[index];
                if (image != null)
                {
                    image.sprite = entry?.thumbnail;
                    image.enabled = image.sprite != null;
                    image.preserveAspect = true;
                    image.rectTransform.anchoredPosition = new Vector2(x, -y);
                    image.rectTransform.sizeDelta = new Vector2(204, 98);
                }
                if (finalCaptions == null || index >= finalCaptions.Length || finalCaptions[index] == null) continue;
                var caption = finalCaptions[index];
                GuidebookView.Set(caption, entry?.title);
                caption.rectTransform.anchoredPosition = new Vector2(x, -(y + 101));
                caption.rectTransform.sizeDelta = new Vector2(204, 45);
                caption.enableAutoSizing = true;
                caption.fontSizeMin = 16; caption.fontSizeMax = 20;
            }
        }
        private bool HasMoreText() => bodyText != null && !string.IsNullOrEmpty(bodyText.text)
            && bodyText.pageToDisplay < bodyText.textInfo.pageCount;
        private void RefreshButtons()
        {
            bool ready = !AwaitingSave && string.IsNullOrEmpty(PendingStage);
            string caption = "계속하기";
            if (ShowingAlbum)
                caption = HasMoreText() ? "다음 쪽" : LastMoment ? "앨범 닫기" : "다음 사진";
            else if (album != null && Stage == "clear") caption = "앨범 열기";
            GuidebookView.Set(continueLabel, caption);
            GuidebookView.Set(skipLabel, album != null ? "마지막 사진" : "건너뛰기");
            if (continueButton != null) { continueButton.interactable = ready; continueButton.gameObject.SetActive(ready); }
            if (skipButton != null) { skipButton.interactable = ready; skipButton.gameObject.SetActive(ready && !(ShowingAlbum && LastMoment)); }
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
