using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Gamsamo.Dialogue;
using Gamsamo.Patients;
using Gamsamo.UI;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor.Presentation
{
    /// <summary>Isolated D component tests. No AI requests, player saves, scenes or economy are used.</summary>
    public static class PresentationChecks
    {
        private static readonly List<UnityEngine.Object> Objects = new List<UnityEngine.Object>();
        private static readonly StringBuilder Report = new StringBuilder();
        private static int passed;
        private static void Check(bool value, string description)
        {
            Report.AppendLine((value ? "PASS " : "FAIL ") + description);
            if (!value) throw new InvalidOperationException(description);
            passed++;
        }
        private static void Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        private static T Track<T>(T value) where T : UnityEngine.Object { Objects.Add(value); return value; }

        [MenuItem("Gamsamo/Presentation/Run Isolated D Checks")]
        public static void Run()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Run isolated checks in Edit mode.");
            Report.Clear(); passed = 0;
            try
            {
                CheckDialogueAndGuide(); CheckEnding(); CheckAuthoredAlbum(); CheckPhotoRecordProfile();
                Report.AppendLine("TOTAL " + passed + " passed; no AI/server or player-save calls.");
                Debug.Log("D presentation checks: " + passed + " passed.");
            }
            finally
            {
                for (int i = Objects.Count - 1; i >= 0; i--) if (Objects[i] != null) UnityEngine.Object.DestroyImmediate(Objects[i]);
                Objects.Clear();
                Directory.CreateDirectory("artifacts/d-presentation");
                File.WriteAllText("artifacts/d-presentation/edit-checks.txt", Report.ToString());
            }
        }

        public static void RunAndBuild()
        {
            Run();
            Gamsamo.Editor.PatientAiInputChecks.Run();
            PresentationPrefabBuilder.BuildMissing();
        }

        private static PatientDialogueView Dialogue()
        {
            var root = Track(PresentationPrefabBuilder.CanvasRoot("DEV-D-Dialogue"));
            var view = root.AddComponent<PatientDialogueView>();
            view.root = root.AddComponent<CanvasGroup>();
            view.bodyText = PresentationPrefabBuilder.Label("Body", root.transform, "마지막으로 저장된 대화", 0, 0, 900, 200, 25);
            view.statusText = PresentationPrefabBuilder.Label("Status", root.transform, "", 0, 220, 1200, 80, 24);
            view.replyRoot = new GameObject("Reply", typeof(RectTransform)); view.replyRoot.transform.SetParent(root.transform, false);
            view.replyInput = view.replyRoot.AddComponent<TMP_InputField>();
            view.sendButton = PresentationPrefabBuilder.Button("Send", root.transform, "전송", 0, 330, 180, 70);
            view.endButton = PresentationPrefabBuilder.Button("End", root.transform, "종료", 200, 330, 180, 70);
            root.SetActive(true); Call(view, "Awake"); Set(view, "visible", true);
            view.SetApiMode(true); view.SetComposeState(true, true, true, false, true);
            view.SetDraft("  보내지 않은 초안\n두 번째 줄  ");
            return view;
        }

        private static void CheckDialogueAndGuide()
        {
            var view = Dialogue();
            string draft = view.Draft, line = view.DisplayedText;
            foreach (DialogueProcessingState state in Enum.GetValues(typeof(DialogueProcessingState)))
            {
                view.SetProcessingState(state);
                Check(view.DisplayedText == line && view.Draft == draft, state + " preserves dialogue and draft");
                Check(!view.statusText.richText && (state == DialogueProcessingState.Ready
                    ? string.IsNullOrEmpty(view.statusText.text) : view.statusText.text.Length > 0),
                    state + " keeps idle feedback empty and operation feedback visible");
            }
            view.SetProcessingState(DialogueProcessingState.WaitingForEvaluation);
            Set(view, "processingElapsed", 60f); Call(view, "RefreshProcessingLabel");
            Check(view.ProcessingState == DialogueProcessingState.WaitingForEvaluation && !view.statusText.text.Contains("0점"), "Long evaluation is still waiting, not an error or score");
            view.SetStatus("서윤의 응답을 기다리고 있어요");
            Check(view.ProcessingState == DialogueProcessingState.WaitingForReply, "Existing A response caption remains compatible");
            view.bodyText.text = ""; view.SetStatus("서윤의 응답을 기다리고 있어요");
            Check(view.ProcessingState == DialogueProcessingState.PreparingOpening, "Empty first line is opening preparation, not a fabricated utterance");
            view.bodyText.text = line;
            view.SetComposeState(false, false, false, true, true); view.SetStatus("저장 오류 · 재시도");
            Check(view.ProcessingState == DialogueProcessingState.Error, "Real retryable error is distinguished");
            view.SetComposeState(true, true, true, false, true); view.SetProcessingState(DialogueProcessingState.Ready);
            Check(!view.statusText.text.Contains("기다") && view.DisplayedText == line, "Ready clears waiting feedback without replacing the line");
            var binding = view.gameObject.AddComponent<DialogueProcessingBinding>(); binding.view = view;
            binding.BeginGameResultSave();
            Check(view.ProcessingState == DialogueProcessingState.SavingResult && view.DisplayedText == line, "B game-result commit is distinct from A evaluation completion");
            Check(!view.IsVisible, "Result save also blocks dialogue send/end/departure entry points");
            binding.FailGameResultSave("게임 저장 재시도 필요");
            Check(view.ProcessingState == DialogueProcessingState.Error, "B save failure is an error, not a clear result");
            int gameRetries = 0; binding.GameResultRetryRequested += () => gameRetries++;
            binding.RetryGameResult(); binding.RetryGameResult();
            Check(gameRetries == 1 && view.ProcessingState == DialogueProcessingState.SavingResult, "Game-save retry calls B once and never resubmits an AI turn");
            object anotherModal = new object(); view.SetPresentationBlock(anotherModal, true);
            binding.CompleteGameResultSave();
            Check(!view.IsVisible, "Completing one operation cannot release another modal's input block");
            view.SetPresentationBlock(anotherModal, false);
            Check(view.IsVisible && view.ProcessingState == DialogueProcessingState.Saved, "Successful game commit clears error and releases its own block");

            var guideRoot = Track(PresentationPrefabBuilder.CreateGuidebook()); guideRoot.SetActive(true);
            var guide = guideRoot.GetComponent<GuidebookView>(); Call(guide, "Awake"); guide.dialogue = view;
            Check(!guide.TryOpen(), "Unconnected modal refuses to open behind an unblocked room");
            bool locked = false; int releases = 0, sends = 0, ends = 0, retries = 0;
            guide.BindModalGate(() => { if (locked) return false; locked = true; return true; }, () => { locked = false; releases++; });
            view.SendRequested += _ => sends++; view.EndRequested += () => ends++; view.RetryRequested += () => retries++;
            Check(guide.TryOpen() && locked && !view.IsVisible, "Guide acquires B's lock and blocks dialogue input");
            view.sendButton.onClick.Invoke(); view.endButton.onClick.Invoke();
            Check(sends == 0 && ends == 0 && retries == 0, "Guide cannot send, end or retry behind itself");
            guide.Previous(); Check(guide.Page == 0 && !guide.previousButton.interactable, "First page has no previous page");
            for (int page = 0; page < guide.PageCount; page++)
            {
                guide.GoTo(page); guide.bodyText.ForceMeshUpdate(true);
                Check(guide.bodyText.textInfo.pageCount <= 1, "Guide page " + (page + 1) + " fits its authored content area");
            }
            for (int i = 0; i < 10; i++) guide.Next();
            Check(guide.Page == 5 && !guide.nextButton.interactable && guide.pageText.text == "6 / 6", "Last page is bounded and numbered");
            guide.Close(); guide.Close();
            Check(!locked && releases == 1 && view.IsVisible && view.Draft == draft && view.DisplayedText == line, "Closing restores original consultation without duplicate release");
            guide.TryOpen(); guide.Close();
            Check(sends == 0 && retries == 0, "Reopening the guide sends no request");
            view.SendRequested += _ => view.SetComposeState(false, false, true, false, true);
            view.sendButton.onClick.Invoke(); view.sendButton.onClick.Invoke();
            Check(sends == 1 && view.Draft == draft, "Service input lock prevents repeated sends and preserves draft");
        }

        private static void CheckEnding()
        {
            var root = Track(PresentationPrefabBuilder.CreateEnding()); root.SetActive(true);
            var view = root.GetComponent<EndingPresentationView>(); Call(view, "Awake");
            bool locked = false; int saves = 0;
            view.BindModalGate(() => { if (locked) return false; locked = true; return true; }, () => locked = false);
            view.PlaybackSaveRequested += (game, patient, caseId, session, stage) => saves++;
            Check(!view.RestoreCommittedClear("g", "p", "c", "s", false, true, "개발용", 7, true), "Uncommitted game result cannot open clear UI");
            Check(!view.RestoreCommittedClear("g", "p", "c", "s", true, false, "개발용", 7, true), "Uncleared result cannot open clear UI");
            Check(!view.RestoreCommittedClear("g", "p", "", "s", true, true, "개발용", 7, true), "Complete settlement identity is required");
            Check(view.RestoreCommittedClear("g", "p", "c", "s", true, true, "개발용", 7, true), "B committed clear opens presentation");
            Check(view.rewardText.text.Contains("7 C") && view.photoNotice.text.Contains("사진첩") && !view.bodyText.text.Contains("점수"), "Clear shows supplied award/photo notice, clear summary omits numerical grades");
            view.Continue(); view.Continue(); view.Skip();
            Check(saves == 1 && view.AwaitingSave && view.Stage == "clear", "Playback waits for one B save; repeated clicks do not advance");
            Check(!view.AcknowledgePlaybackSaved("wrong", "p", "c", "s", "ending", true), "Late result for another game is rejected");
            view.AcknowledgePlaybackSaved("g", "p", "c", "s", "ending", false);
            Check(view.IsOpen && view.Stage == "clear" && view.retryButton.gameObject.activeSelf, "Failed playback save stays open at committed stage");
            view.RetryPlaybackSave();
            Check(saves == 2 && view.PendingStage == "ending", "Retry repeats only the same playback transition");
            view.AcknowledgePlaybackSaved("g", "p", "c", "s", "ending", true);
            Check(view.Stage == "ending" && view.bodyText.text.Contains("준비되지") && view.IsOpen, "Missing ending content is honest and skippable");
            view.Skip(); view.AcknowledgePlaybackSaved("g", "p", "c", "s", "skipped", true);
            Check(!view.IsOpen && !locked && view.Stage == "skipped", "Skip closes only after playback state commit");
            int beforeReplay = saves; view.Replay(); view.Skip();
            Check(saves == beforeReplay && !view.IsOpen, "Replay/skip emit no save, reward or photo-grant operation");
            Check(!view.RestoreCommittedClear("g", "p", "c", "s", true, true, "개발용", 7, true), "Duplicate old clear cannot reopen after acknowledged completion");

            var restoredRoot = Track(PresentationPrefabBuilder.CreateEnding()); restoredRoot.SetActive(true);
            var restored = restoredRoot.GetComponent<EndingPresentationView>(); Call(restored, "Awake");
            restored.BindModalGate(() => true, () => { }); restored.PlaybackSaveRequested += (g, p, c, s, stage) => { };
            Check(restored.RestoreCommittedClear("g", "p", "c", "s", true, true, "개발용", 7, true, "completed") && !restored.IsOpen, "Restored completed ending stays closed");
            restored.Replay(); Check(restored.IsOpen && restored.IsReplay, "Restored ending can be replayed"); restored.Skip();
            Check(!restored.RestoreFinalEnding("g", true, false), "D cannot unlock final ending itself");
            Check(restored.RestoreFinalEnding("g", true, true), "Only B's committed unlock opens final credits");
            restored.Skip(); restored.AcknowledgePlaybackSaved("g", "", "", "", "skipped", true);
            Check(!restored.RestoreFinalEnding("g", true, true), "Stale final-unlock notification cannot reopen completed credits");
            var content = Track(ScriptableObject.CreateInstance<EndingContentCatalog>());
            var texture = Track(new Texture2D(2, 2));
            var sprite = Track(Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero));
            content.patients = new[] { new EndingContent { patientId = "p", approved = true, photo = sprite } };
            restored.content = content;
            restored.RestoreCommittedClear("other-game", "p", "c", "s", true, true, "개발용", 0, false, "ending");
            Check(!restored.photo.enabled, "Approved content alone cannot reveal a photo B has not unlocked");
            restored.Skip(); restored.AcknowledgePlaybackSaved("other-game", "p", "c", "s", "skipped", true);
        }

        private static void CheckAuthoredAlbum()
        {
            // Exercise the shipped prefab as well as the generated missing-content UI above.
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(PresentationPrefabBuilder.Folder + "/ClearEnding.prefab");
            var root = Track(UnityEngine.Object.Instantiate(template));
            var view = root.GetComponent<EndingPresentationView>();
            Call(view, "Awake");
            var texture = Track(new Texture2D(16, 12));
            var storyPhoto = Track(Sprite.Create(texture, new Rect(0, 0, 16, 9), Vector2.one * 0.5f));
            var smilePhoto = Track(Sprite.Create(texture, new Rect(0, 0, 8, 12), Vector2.one * 0.5f));
            var catalog = Track(ScriptableObject.CreateInstance<EndingContentCatalog>());
            var album = new EndingContent { patientId = "album", approved = true, title = "다시 보는 날들",
                photo = smilePhoto, moments = new EndingMoment[5] };
            for (int i = 0; i < album.moments.Length; i++)
                album.moments[i] = new EndingMoment { title = "기억 " + i, description = "그날의 이야기 " + i, photo = storyPhoto };
            catalog.patients = new[] { album };
            view.content = catalog;
            int saves = 0;
            view.BindModalGate(() => true, () => { });
            view.PlaybackSaveRequested += (g, p, c, s, stage) => saves++;
            var tick = typeof(EndingPresentationView).GetMethod("AdvanceReading", BindingFlags.Instance | BindingFlags.NonPublic);
            void ReadFor(float seconds) => tick.Invoke(view, new object[] { seconds });

            Check(view.RestoreCommittedClear("g", "album", "c", "s", true, true, "서윤", 5, true), "Shipped ending prefab opens after committed clear");
            ReadFor(2f);
            Check(saves == 0 && view.Stage == "clear", "Clear summary allows reading time");
            ReadFor(3f); ReadFor(100f);
            Check(saves == 1 && view.PendingStage == "ending" && !view.photo.enabled, "Timed opening waits for one committed stage change");
            view.AcknowledgePlaybackSaved("g", "album", "c", "s", "ending", true);
            var spread = view.photo.transform.parent.Find("EndingAlbumSpread");
            var folio = spread.Find("MomentNumber").GetComponent<TMP_Text>();
            var signature = spread.Find("ClosingThanks").GetComponent<TMP_Text>();
            Check(view.titleText.text == "" && view.photo.sprite == storyPhoto && view.bodyText.text == album.moments[0].description
                && view.photo.rectTransform.anchoredPosition.x + view.photo.rectTransform.rect.width < view.bodyText.rectTransform.anchoredPosition.x,
                "Album pairs the uncropped first photo with its description on the right");
            ReadFor(1f);
            Check(folio.text == "장면 1 / 6", "First album photo remains available to read");
            for (int i = 1; i < album.moments.Length; i++)
            {
                if (i % 2 == 0) ReadFor(30f); else view.Continue();
                Check(view.bodyText.text == album.moments[i].description && signature.text == "" && saves == 1,
                    "Timed and manual page turns show authored moment " + (i + 1) + " without a save");
            }
            ReadFor(30f); ReadFor(100f);
            Check(folio.text == "장면 6 / 6" && view.photo.sprite == smilePhoto && signature.text == "상담해주셔서 감사합니다"
                && view.IsOpen && !view.AwaitingSave, "Final smile and exact thanks stay visible until dismissed");
            view.Continue(); view.AcknowledgePlaybackSaved("g", "album", "c", "s", "completed", false);
            ReadFor(100f);
            Check(view.IsOpen && view.photo.sprite == smilePhoto && saves == 2 && view.retryButton.gameObject.activeSelf,
                "Failed completion holds the final photo and retry button");
            view.RetryPlaybackSave(); view.AcknowledgePlaybackSaved("g", "album", "c", "s", "completed", true);
            Check(!view.IsOpen && saves == 3, "Completion acknowledgement releases the album");
            view.Replay(); view.Skip();
            Check(folio.text == "장면 6 / 6" && view.photo.sprite == smilePhoto && saves == 3,
                "Replay can jump to the final photo without another save");
            view.Continue();
            Check(!view.IsOpen && saves == 3, "Replay closes without granting rewards or changing playback state");

            var granted = new PhotoGalleryEntry[5];
            for (int i = 0; i < granted.Length; i++)
                granted[i] = new PhotoGalleryEntry { title = "환자 " + (i + 1) + "의 웃는 얼굴", thumbnail = smilePhoto };
            Check(view.RestoreFinalEnding("g", true, true), "Final ending opens after the personal album releases its modal");
            view.SetCreditsPhotos("another-game", granted);
            Check(!view.finalPhotos[0].enabled, "Final credits reject photos from another save");
            view.SetCreditsPhotos("g", granted);
            for (int i = 0; i < granted.Length; i++)
                Check(view.finalPhotos[i].enabled && view.finalPhotos[i].preserveAspect
                    && view.finalCaptions[i].text == "", "Final credits show saved portrait without its album title " + (i + 1));
            Check(!view.finalPhotos[5].enabled && view.finalCaptions[5].text == "", "Five saved photos leave no extra visible slot");
            Check(view.creditsBody.anchoredPosition.y <= 0
                && -view.creditsBody.anchoredPosition.y < ((RectTransform)view.creditsViewport.transform).rect.height,
                "Short credits are visible immediately between the photo columns");
            view.Continue(); view.AcknowledgePlaybackSaved("g", "", "", "", "completed", true);
            Check(!view.IsOpen && saves == 4, "Final credits close after their separate committed completion");
        }

        private static void CheckPhotoRecordProfile()
        {
            var host = Track(PresentationPrefabBuilder.CanvasRoot("DEV-D-DataViews")); host.SetActive(true);
            var gallery = host.AddComponent<PhotoGalleryView>();
            gallery.thumbnails = new Image[6]; gallery.titles = new TMP_Text[6];
            for (int i = 0; i < 6; i++)
            {
                gallery.thumbnails[i] = PresentationPrefabBuilder.Image("Photo" + i, host.transform, 0, 0, 100, 100);
                gallery.titles[i] = PresentationPrefabBuilder.Label("Title" + i, host.transform, "", 0, 0, 200, 40, 20);
            }
            var texture = Track(new Texture2D(2, 2));
            var sprite = Track(Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero));
            var catalog = new[] { new PhotoGalleryEntry { photoId = "photo-1", title = "<b>원문 제목</b>", description = "설명", thumbnail = sprite },
                new PhotoGalleryEntry { photoId = "locked-photo", title = "미해금 사진", thumbnail = sprite } };
            gallery.SetUnlockedPhotos("g", new[] { "photo-1", "photo-1", "missing" }, catalog);
            Check(gallery.DisplayedPhotoCount == 1 && !gallery.titles[0].richText, "Saved photo IDs are filtered/deduplicated without granting locked photos");
            gallery.SetUnlockedPhotos("g", new[] { "photo-1" }, catalog);
            Check(gallery.DisplayedPhotoCount == 1, "Repeated restore replaces gallery instead of appending duplicate photos");
            gallery.SetUnlockedPhotos("new-game", Array.Empty<string>(), catalog);
            Check(gallery.DisplayedPhotoCount == 0, "Another run cannot retain previously unlocked photos");

            var viewerRoot = Track(PresentationPrefabBuilder.CreatePhotoViewer()); viewerRoot.SetActive(true);
            var viewer = viewerRoot.GetComponent<PhotoViewer>(); Call(viewer, "Awake");
            viewer.BindModalGate(() => true, () => { });
            var longDescription = new StringBuilder();
            for (int i = 0; i < 35; i++) longDescription.AppendLine("개발용 긴 사진 설명 " + i);
            viewer.ShowGrantedPhoto(new PhotoGalleryEntry { thumbnail = sprite, description = longDescription.ToString() });
            Check(viewer.PageCount > 1 && !viewer.previousButton.interactable, "Long photo descriptions start on a bounded first page");
            for (int i = 0; i < 100; i++) viewer.Next();
            Check(viewer.Page == viewer.PageCount && !viewer.nextButton.interactable, "Photo description can be read through its last page");
            viewer.Close();

            var record = host.AddComponent<MedicalRecordView>();
            record.age = PresentationPrefabBuilder.Label("Age", host.transform, "", 0, 0, 500, 60, 20);
            record.consultationNotes = PresentationPrefabBuilder.Label("Record", host.transform, "", 0, 80, 800, 500, 20);
            var data = new MedicalRecordData { registrationNumber = "p", age = "미확인", consultationResult = "저장된 평가" };
            record.ShowRecord(data);
            record.SetInformationState("p", "age", MedicalRecordView.InformationState.Undisclosed, "SECRET");
            Check(record.age.text == "미공개", "Undisclosed payload is never rendered");
            record.ShowRecord(data); Check(record.age.text == "미공개", "Record refresh keeps disclosure projection");
            record.SetInformationState("p", "age", MedicalRecordView.InformationState.NotApplicable);
            Check(record.age.text == "해당 없음", "Not applicable is distinct from unknown");
            record.SetInformationState("p", "age", MedicalRecordView.InformationState.Unknown);
            Check(record.age.text == "미확인", "Unknown remains explicitly unknown");
            record.SetInformationState("p", "age", MedicalRecordView.InformationState.Disclosed, "제공된 값");
            Check(record.age.text == "공개됨 · 제공된 값", "Only disclosed values are displayed");
            Check(!record.SetInformationState("other", "age", MedicalRecordView.InformationState.Disclosed, "WRONG"), "Late field update for another record is rejected");
            Check(!record.SetCommittedSettlement("p", false, "PROVISIONAL"), "Provisional settlement is rejected");
            record.SetCommittedSettlement("p", true, "확정 7 C"); record.SelectSection(MedicalRecordView.RecordSection.Result);
            Check(record.consultationNotes.text.Contains("저장된 평가") && record.consultationNotes.text.Contains("확정 7 C"), "Detailed committed settlement stays with evaluation in the paper");
            record.ShowRecord(new MedicalRecordData { registrationNumber = "other", age = "미확인" });
            Check(record.age.text == "미확인" && !record.consultationNotes.text.Contains("확정 7 C"), "Switching records clears old disclosure/settlement display");

            var profile = host.AddComponent<ProfileView>();
            profile.nameText = PresentationPrefabBuilder.Label("Name", host.transform, "", 0, 0, 800, 60, 20);
            profile.affiliationText = PresentationPrefabBuilder.Label("Affiliation", host.transform, "", 0, 60, 800, 60, 20);
            profile.introText = PresentationPrefabBuilder.Label("Intro", host.transform, "", 0, 120, 800, 60, 20);
            profile.SetProfile("한이수", "상담실", "말이 잘 나오지 않는 날엔 천천히 시작해요.");
            Check(profile.nameText.text == "한이수" && profile.affiliationText.text == "상담실"
                && profile.introText.text == "말이 잘 나오지 않는 날엔 천천히 시작해요."
                && profile.transform.Find("CommittedProgress") == null, "Profile preserves supplied identity and introduction without a progress footer");
        }
    }
}
