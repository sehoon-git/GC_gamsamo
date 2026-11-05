using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.Patients;
using Gamsamo.Dialogue;
using TMPro;
using Gamsamo.UI;
using Gamsamo.UI.UiNext;
using Gamsamo.UI.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.Editor.UiNext
{
    // Editor-only fixtures. No production save, economy, AI or catalog is written.
    public static class UiNextChecks
    {
        static readonly StringBuilder report=new StringBuilder();
        static int passed;
        static void Check(bool condition,string name)
        {report.AppendLine((condition?"PASS ":"FAIL ")+name);if(!condition)throw new InvalidOperationException(name);passed++;}
        public static IEnumerator Run(ConsultationRoomController room)
        {
            report.Clear();passed=0;
            var watermark=Watermark(room);
            try { UiNextPreview.AssertIsolated(); Check(true,"Preview has no production services or visit bootstrap after Start"); var steps=Profile(room); while(steps.MoveNext()) yield return steps.Current; Endings(room); Records(room); yield return null; Status(room); report.AppendLine("TOTAL "+passed+" passed; Editor-only mock callbacks, no actual save/server calls."); }
            finally {UnityEngine.Object.Destroy(watermark);Directory.CreateDirectory("artifacts/ui-next");File.WriteAllText("artifacts/ui-next/ui-checks.txt",report.ToString());}
            Debug.Log("UiNext checks: "+passed+" passed.");
        }
        static IEnumerator Profile(ConsultationRoomController room)
        {
            var bindings=room.GetComponentInChildren<UiNextBindings>(true);var editor=bindings.profileEditor;var ports=bindings.ports;
            room.OpenPc();room.pcDesktopView.ResetDesktop();yield return null;
            Check(RaycastHits(room.pcDesktopView.endingsButton.gameObject,room.pcDesktopView.endingsButton),"Desktop ending shortcut receives pointer raycast");
            room.pcDesktopView.OpenProfile();yield return null;
            Check(RaycastHits(editor.editButton.gameObject,editor.editButton),"Profile edit button receives pointer raycast");
            editor.BeginEdit();
            Check(editor.IsOpen && !editor.saveButton.interactable,"Unconnected editor is visible but cannot save");
            editor.Cancel();ports.SetContext("UI-TEST-RUN");ports.PublishProfile(ports.ContextId,new ProfileDisplay("기존 이름","기존 소개","UI 검사"));
            ports.PublishProgress(ports.ContextId,new ProgressDisplay("서윤","재상담 대기","승급 기준 준비 중",0,5));
            int requests=0;ProfileSaveRequest request=null;
            Action<ProfileSaveRequest> save=r=>{requests++;request=r;};ports.ProfileSaveRequested+=save;
            editor.BeginEdit();editor.nameInput.text="수정 이름";editor.introductionInput.text="초안 첫 줄\n두 번째 줄";
            yield return null;
            room.ClosePc();room.OpenRecord();Check(room.IsPcOpen&&!room.IsRecordOpen,"Profile modal blocks direct background PC close/record open");
            Check(RaycastHits(editor.panel,room.closePcButton),"Fullscreen profile blocker catches background close-button pointer");
            UiNextPreview.Capture("profile-blocker-check",1280,720);
            editor.Save();editor.Save();
            Check(requests==1 && ports.ProfileSaving&&!editor.saveButton.interactable&&!editor.cancelButton.interactable,"Pending profile save blocks repeated save/cancel");
            Check(ports.Profile.Name=="기존 이름","Profile is unchanged before committed response");
            Check(!ports.CompleteProfileSave("stale",true,new ProfileDisplay("잘못된 이름","")),"Wrong request ID ignored");
            ports.CompleteProfileSave(request.RequestId,false,null,"검사용 저장 실패");
            Check(editor.IsOpen&&editor.DraftName=="수정 이름"&&editor.DraftIntroduction.Contains("두 번째 줄")&&editor.feedback.text=="검사용 저장 실패","Failed save preserves exact draft and error");
            editor.Save();ports.CompleteProfileSave(request.RequestId,true,new ProfileDisplay("확정 이름","확정 소개","UI 검사"));
            Check(!editor.IsOpen&&!room.HasExternalInputBlock&&editor.profile.nameText.text=="확정 이름","Only successful committed reply updates screen and releases lock");
            editor.BeginEdit();editor.nameInput.text="취소할 이름";editor.Cancel();Check(ports.Profile.Name=="확정 이름","Cancel discards draft without saving");
            Check(!UiFeaturePorts.ValidateProfile("  ","",out _),"Blank name rejected");
            Check(!UiFeaturePorts.ValidateProfile(new string('가',25),"",out _),"25-character Korean name rejected");
            Check(!UiFeaturePorts.ValidateProfile("이름",new string('나',241),out _),"241-character introduction rejected");
            Check(UiFeaturePorts.ValidateProfile(new string('가',24),new string('나',240),out _),"Boundary 24/240 Korean input accepted");
            Check(UiFeaturePorts.TextLength("가") == 1,"Composed Hangul grapheme counted as one character");
            editor.BeginEdit();editor.nameInput.text=new string('가',25);editor.Save();Check(!ports.ProfileSaving&&editor.feedback.text.Contains("24"),"Validation shown before dispatch");editor.Cancel();
            editor.BeginEdit();editor.nameInput.text="이전 회차";editor.Save();string old=request.RequestId;
            ports.SetContext("UI-TEST-OTHER");Check(!ports.CompleteProfileSave(old,true,new ProfileDisplay("이전 회차",""))&&!editor.IsOpen,"Run switch invalidates pending save and closes draft");
            ports.ProfileSaveRequested-=save;
            ports.SetContext("UI-TEST-RUN");ports.PublishProfile(ports.ContextId,new ProfileDisplay("김상담",string.Concat(Enumerable.Repeat("긴 한글 소개를 확인합니다. ",12)),"상담실"));
            ports.PublishProgress(ports.ContextId,new ProgressDisplay("서윤","재상담 대기","승급 기준 준비 중",0,5));
            UiNextPreview.Capture("profile-after",1280,720);UiNextPreview.Capture("profile-after-fhd",1920,1080);
            ports.ProfileSaveRequested+=save;editor.BeginEdit();editor.nameInput.text=new string('가',24);editor.introductionInput.text=new string('한',240);
            UiNextPreview.Capture("profile-edit-hd",1280,720);UiNextPreview.Capture("profile-edit-fhd",1920,1080);editor.Cancel();ports.ProfileSaveRequested-=save;
        }
        static void Endings(ConsultationRoomController room)
        {
            var pc=room.pcDesktopView;var ports=room.uiFeaturePorts;pc.OpenEndings();
            var list=pc.endingListView;int requested=0,playbackWrites=0;
            var catalog=ScriptableObject.CreateInstance<EndingContentCatalog>();
            catalog.patients=new[]{new EndingContent{patientId="seoyun",approved=true,title="UI 검사 전용 후일담",description="이 문장은 화면 검사용입니다. 정식 후일담이 아닙니다."}};
            try
            {
                Action<EndingReplayRequest> replay=r=>{requested++;ports.CompleteReplay(r.RequestId,new EndingReplayGrant{ContextId=r.ContextId,PatientId=r.PatientId,Unlocked=true,Catalog=catalog});};
                ports.EndingReplayRequested+=replay;
                ports.PublishEndings(ports.ContextId,new[]{new EndingListItem("seoyun","서윤",true,true,true),new EndingListItem("dohyeon","도현",false,true,true),
                    new EndingListItem("taejun","태준",true,false,true),new EndingListItem("yeonwoo","연우",true,true,false),new EndingListItem("harin","하린",false,false,false)});
                Check(list.playButtons[0].interactable&&!list.playButtons[1].interactable&&!list.playButtons[2].interactable&&!list.playButtons[3].interactable,"Unlocked, locked, unapproved and unprepared replay access guards");
                Check(!ports.RequestReplay("dohyeon")&&!ports.RequestReplay("taejun")&&!ports.RequestReplay("yeonwoo")&&requested==0,"Direct calls cannot bypass replay access guards");
                Action<string,string,string,string,string> write=(a,b,c,d,e)=>playbackWrites++;
                list.player.PlaybackSaveRequested+=write;
                UiNextPreview.Capture("endings-after",1280,720);
                list.Play("seoyun");Check(list.player.IsOpen&&list.player.IsReplay&&room.HasExternalInputBlock,"Approved replay uses existing player and input lease");
                room.ClosePc();Check(room.IsPcOpen,"Replay blocks background PC close");
                list.player.Skip();Check(!list.player.IsOpen&&pc.IsEndingsOpen&&!room.HasExternalInputBlock,"Skip returns to PC list and restores input");
                list.Play("seoyun");list.player.Continue();Check(playbackWrites==0&&requested==2,"Replay and skip invoke no persistence/grant callback");
                list.player.PlaybackSaveRequested-=write;ports.EndingReplayRequested-=replay;
                Check(!list.player.TryOpenApprovedReplay("UI-TEST-RUN","dohyeon",true,false,catalog),"Catalog without approved content cannot play");
                ports.PublishEndings(ports.ContextId,Array.Empty<EndingListItem>());
                Check(list.stateLabels.All(x=>x.text=="해금 상태 확인 전"),"Missing persisted entries are unknown, not invented locks/unlocks");
            }
            finally {UnityEngine.Object.Destroy(catalog);}
            pc.CloseActiveWindow();room.ClosePc();
        }
        static void Records(ConsultationRoomController room)
        {
            var record=new MedicalRecordData { gameProgressId="UI-TEST-RUN",registrationNumber="seoyun",patientName="서윤",caseId="case",sessionId="round-2",recordedDate="2026-11-06",
                consultationTranscript=string.Concat(Enumerable.Repeat("[UI 검사] 상담사: 긴 한글 대화 원문을 확인합니다.\n",40)),consultationResult=string.Concat(Enumerable.Repeat("[UI 검사] 평가 근거를 페이지로 나누어 확인합니다.\n",35))};
            room.SetRecord(record);room.OpenRecord();var view=room.recordView;view.SetRecordHeading(false,2);
            Check(view.Heading.Contains("현재 사례 합본")&&view.Heading.Contains("상담 2회차")&&view.Heading.Contains("2026-11-06"),"Aggregate heading includes patient, supplied round and date");
            var selection=new RecordSelection { GameId=record.gameProgressId,PatientId="seoyun",CaseId="case",SessionId="round-2",Round=2 };
            var ports=room.uiFeaturePorts;ports.SelectRecord(selection);selection.SessionId="mutated";
            var answer=new RecordSelection { GameId=record.gameProgressId,PatientId="seoyun",CaseId="case",SessionId="round-2",Round=2 };
            Check(ports.PublishRecordInformation(answer,new[]{new RecordFieldDisplay("occupation",MedicalRecordView.InformationState.Undisclosed,"누출 금지 원본")}),"Selection is copied before asynchronous response");
            Check(view.occupation.text=="미공개"&&!view.occupation.text.Contains("누출"),"Undisclosed payload is discarded");
            var stale=answer.Copy();stale.SessionId="other";Check(!ports.PublishRecordInformation(stale,Array.Empty<RecordFieldDisplay>()),"Stale round response cannot change selected record");
            foreach(var state in new[]{MedicalRecordView.InformationState.Unknown,MedicalRecordView.InformationState.NotApplicable,MedicalRecordView.InformationState.Disclosed})
                Check(view.SetInformationState("seoyun","age",state,"공개 값"),"Information state accepted: "+state);
            view.SetCommittedSettlement("seoyun",true,"[UI 검사] 확정 정산 표시");view.SelectSection(MedicalRecordView.RecordSection.Settlement);
            Check(view.consultationNotes.text.Contains("확정 정산"),"Dedicated settlement tab displays committed receipt");
            view.SelectSection(MedicalRecordView.RecordSection.Result);Check(view.DetailPageCount>1,"Long Korean evaluation paginates without truncation");
            view.ChangePage(1);Check(view.DetailPage==2,"Long evaluation second page is reachable");
            view.SetRecordHeading(true,2);Check(view.Heading.Contains("회차별 보관본"),"Archive and aggregate headings are distinct");
            UiNextPreview.Capture("record-after",1280,720);UiNextPreview.Capture("record-after-fhd",1920,1080);room.CloseRecord();
        }
        public static void RunExisting()
        { Gamsamo.Editor.Presentation.PresentationChecks.Run(); Gamsamo.Editor.PatientAiInputChecks.Run(); }
        static GameObject Watermark(ConsultationRoomController room)
        {
            var root=new GameObject("UI Test Watermark",typeof(RectTransform));root.transform.SetParent(room.GetComponentInChildren<UiNextBindings>().transform,false);
            var rect=(RectTransform)root.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,0);rect.anchoredPosition=new Vector2(20,12);rect.sizeDelta=new Vector2(1500,34);
            var text=root.AddComponent<TextMeshProUGUI>();text.font=room.pcDesktopView.profileView.nameText.font;text.fontSize=22;text.color=Color.white;text.raycastTarget=false;
            text.text="UI 검사 화면 · 모의 응답 / 실제 저장 없음";return root;
        }
        static void Status(ConsultationRoomController room)
        {
            var details=room.GetComponentInChildren<UiNextBindings>().statusDetails;var dialogue=details.dialogue;
            dialogue.ShowLine("UI 검사", "마지막 상담 대화");dialogue.SetApiMode(true);dialogue.SetComposeState(true,true,true,false,true);dialogue.SetDraft("보내지 않은 초안\n둘째 줄");
            foreach(var state in new[]{DialogueProcessingState.PreparingOpening,DialogueProcessingState.WaitingForReply,DialogueProcessingState.SavingTranscript,DialogueProcessingState.WaitingForEvaluation,DialogueProcessingState.SavingResult})
            {
                dialogue.SetProcessingState(state);Check(dialogue.DisplayedText=="마지막 상담 대화"&&dialogue.Draft=="보내지 않은 초안\n둘째 줄","Waiting state preserves dialogue/draft: "+state);
            }
            dialogue.SetProcessingState(DialogueProcessingState.Error,string.Concat(Enumerable.Repeat("[UI 검사] 저장하지 못했습니다. 입력 내용은 보존됩니다.\n",25)));
            details.Open();Check(details.IsOpen&&room.HasExternalInputBlock,"Long status opens an input-blocking details modal");
            Check(details.body.text.Contains("저장하지 못했습니다"),"Actual error copied to scrollable status, not patient speech");
            UiNextPreview.Capture("status-details-hd",1280,720);details.Close();
            Check(!room.HasExternalInputBlock&&dialogue.Draft.Contains("둘째 줄"),"Closing status restores input and preserves draft");
            dialogue.SetProcessingState(DialogueProcessingState.Ready);Check(!DialogueProcessingText.IsWaiting(dialogue.ProcessingState),"Completed wait indicator removed");
            dialogue.Hide();
        }
        static bool RaycastHits(GameObject panel,Button behind)
        {
            Canvas.ForceUpdateCanvases();var rect=(RectTransform)behind.transform;var point=RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(rect.rect.center));
            var results=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){position=point},results);
            Debug.Log("UiNext raycast: "+point+" "+string.Join(",",results.Select(r=>r.gameObject.name+"["+r.sortingOrder+","+r.module.sortOrderPriority+"]")));
            return results.Count>0&&(results[0].gameObject==panel||results[0].gameObject.transform.IsChildOf(panel.transform));
        }
    }
}
