using System;
using System.IO;
using System.Reflection;
using System.Text;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using Gamsamo.Patients;
using Gamsamo.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Exercises the real input bridge and PR24 session in isolation, without a server or player-save access.</summary>
    public static class PatientAiInputChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Field(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name) => target.GetType().GetMethod(name, Private).Invoke(target, null);
        private static void Property(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);
        private static JObject Example(string name) => AiProtocol.Parse(File.ReadAllText("contracts/" + name + ".example.json", Encoding.UTF8));

        [MenuItem("Gamsamo/AI/Run Input Bridge Checks")]
        public static void Run()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Run isolated input checks outside Play mode.");
            foreach (string file in Directory.GetFiles(Path.Combine(Application.streamingAssetsPath, "GameData", "Patients"), "*.json"))
                AiProtocol.RegisterPatient(AiProtocol.Parse(File.ReadAllText(file)));
            int passed = 0;
            var report = new StringBuilder();
            void Check(bool value, string label)
            {
                report.AppendLine((value ? "PASS " : "FAIL ") + label);
                if (!value) throw new InvalidOperationException(label);
                passed++;
            }
            string temporary = Path.Combine(Path.GetTempPath(), "GamsamoInputChecks_" + Guid.NewGuid().ToString("N"));
            var host = new GameObject("IsolatedInputBridgeChecks", typeof(RectTransform));
            host.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var visits = host.AddComponent<PatientVisitController>();
                var view = host.AddComponent<PatientDialogueView>();
                var room = host.AddComponent<ConsultationRoomController>();
                visits.dialogue = view;
                visits.room = room;
                view.root = host.AddComponent<CanvasGroup>();
                var portraitObject = new GameObject("Portrait", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
                portraitObject.transform.SetParent(host.transform, false);
                visits.portraitRoot = portraitObject.GetComponent<RectTransform>();
                visits.portraitGroup = portraitObject.GetComponent<CanvasGroup>();
                visits.portraitImage = portraitObject.GetComponent<Image>();
                var inputObject = new GameObject("ReplyInput", typeof(RectTransform));
                inputObject.transform.SetParent(host.transform, false);
                view.replyInput = inputObject.AddComponent<TMP_InputField>();
                view.replyRoot = inputObject;
                var sendObject = new GameObject("Send", typeof(RectTransform));
                sendObject.transform.SetParent(host.transform, false);
                view.sendButton = sendObject.AddComponent<Button>();
                var endObject = new GameObject("End", typeof(RectTransform), typeof(Image));
                endObject.transform.SetParent(host.transform, false);
                view.endButton = endObject.AddComponent<Button>();
                view.endButton.targetGraphic = endObject.GetComponent<Image>();
                var nextObject = new GameObject("Next", typeof(RectTransform));
                nextObject.transform.SetParent(host.transform, false);
                view.nextButton = nextObject.AddComponent<Button>();
                // The Edit-mode fixture deliberately does not call the visit's scene-only Awake.
                // Wire its public click handler here; the Play checks cover the serialized scene binding.
                view.DismissRequested += () => visits.DismissPatient();
                view.NextRequested += visits.NextDemoLine;
                int departureClicks = 0;
                view.DismissRequested += () => departureClicks++;
                Field(view, "bound", false);
                Call(view, "BindButtons");
                view.EnsureDepartureControl();
                var departureButton = view.dismissButton;
                view.EnsureDepartureControl();
                Check(departureButton != null && view.dismissButton == departureButton,
                    "Creating the departure control twice reuses one button");
                Field(view, "visible", true);
                Property(visits, "State", PatientVisitController.VisitState.Consulting);
                Property(visits, "CurrentPatient", new PatientVisitDefinition { patientId = "seoyun", displayName = "서윤" });
                Property(visits, "CurrentSessionId", "local_check");
                var adapter = host.AddComponent<PatientAiAdapter>();
                Call(adapter, "Awake");
                var snapshot = AiSaveStore.NewSnapshot();
                snapshot["state"] = Example("start.response")["state"].DeepClone();
                var store = new AiSaveStore(Path.Combine(temporary, "input"));
                store.Save(snapshot, -1);
                var session = new AiSession(store);
                Field(adapter, "session", session);
                Field(adapter, "committedState", session.State);
                Field(adapter, "localVisitId", "local_check");
                Field(adapter, "activePatientId", "seoyun");
                Field(adapter, "patientName", "서윤");
                Field(adapter, "healthReady", true);
                // Hold a scheduled operation before ContinueFlow, so no transport is created by this test.
                adapter.StateChanged += () => { if (session.PendingRequest != null) Field(adapter, "networkBusy", true); };
                var bridge = host.AddComponent<PatientAiInputBridge>();
                Call(bridge, "Bind");
                Check(adapter.CanSubmit && view.sendButton.interactable, "The real bridge enables sending after a committed first utterance");
                Check(!visits.DismissPatient() && !adapter.FinishVisit() && !departureButton.gameObject.activeSelf,
                    "An active consultation cannot be dismissed before evaluation is saved");
                string input = "  한글 질문과 공감\n두 번째 줄 <b>원문</b>  ";
                view.replyInput.text = input;
                Check(adapter.Draft == input, "Input changes reach UpdateDraft without trimming whitespace or markup");
                view.sendButton.onClick.Invoke();
                Check(session.PendingRequest != null && AiProtocol.Text(AiProtocol.Object(session.PendingRequest, "body"), "text") == input,
                    "The real send button creates the adapter's durable verbatim turn request");
                string pending = AiProtocol.Encode(session.PendingRequest);
                view.sendButton.onClick.Invoke();
                Check(AiProtocol.Encode(session.PendingRequest) == pending && !view.sendButton.interactable,
                    "Repeated send clicks cannot create a duplicate request");
                Check(JToken.DeepEquals(new AiSession(store).PendingRequest, session.PendingRequest), "Pending input survives reloading the PR24 save store");
                Check(adapter.RequestEnd() && view.IsConfirming, "Ending during an in-flight request asks for confirmation");
                Check(adapter.CancelEnd() && !session.EndRequested, "Cancelling end leaves the consultation active");
                Check(adapter.RequestEnd() && adapter.ConfirmEnd(), "Confirmed end is accepted while the response is pending");
                Check(session.EndRequested && AiProtocol.Encode(session.PendingRequest) == pending,
                    "End intent is durable without replacing the pending utterance");
                Check(!visits.DismissPatient() && !adapter.FinishVisit(),
                    "A confirmed end with a pending response cannot dismiss the patient");

                var evaluated = AiSaveStore.NewSnapshot();
                evaluated["state"] = Example("evaluate.response")["state"].DeepClone();
                evaluated["end_requested"] = true;
                var resultStore = new AiSaveStore(Path.Combine(temporary, "result"));
                resultStore.Save(evaluated, -1);
                var completedSession = new AiSession(resultStore);
                session = completedSession;
                Field(adapter, "session", completedSession);
                Field(adapter, "committedState", completedSession.State);
                Field(adapter, "networkBusy", false);
                Field(adapter, "draftDirty", false);
                Field(adapter, "renderedRevision", -1);
                var bodyObject = new GameObject("PatientText", typeof(RectTransform));
                bodyObject.transform.SetParent(host.transform, false);
                view.bodyText = bodyObject.AddComponent<TextMeshProUGUI>();
                view.bodyText.text = "직전 환자 대사";
                Call(adapter, "RenderCommitted");
                Check(view.DisplayedText == "직전 환자 대사", "Committing evaluation does not replace patient dialogue with a result popup");
                Check(room.record.consultationTranscript == AiRecordFormatter.Transcript(completedSession.State),
                    "The desk record receives every committed utterance and closing notice");
                Check(!string.IsNullOrWhiteSpace(room.record.consultationResult) && room.record.consultationResult.Contains("환자 변화"),
                    "The desk record receives saved evaluation scores and reasons");
                Check(typeof(PatientAiAdapter).GetMethod("ShowResult", Private) == null, "No result-dialogue renderer remains in the adapter");
                Check(visits.IsAwaitingDeparture && adapter.CanFinishVisit && departureButton.gameObject.activeSelf
                    && departureButton.interactable && !view.endButton.gameObject.activeSelf,
                    "A saved evaluation exposes the explicit departure button and replaces the end action");
                for (int frame = 0; frame < 3; frame++) Call(adapter, "Update");
                view.RevealAll();
                visits.NextDemoLine();
                Check(visits.State == PatientVisitController.VisitState.Consulting && !completedSession.VisitCompleted,
                    "Evaluated state remains seated across Update and next-page input");
                Check(!visits.MarkConsultationEnded("wrong-session"), "A foreign session cannot mark this visit ready for departure");
                var modalField = typeof(ConsultationRoomController).GetField("activeModal", Private);
                modalField.SetValue(room, Enum.Parse(modalField.FieldType, "Record"));
                Call(visits, "RefreshInput");
                Check(!visits.DismissPatient() && !adapter.FinishVisit() && !departureButton.interactable,
                    "Reading the record blocks departure without losing the evaluated visit");
                modalField.SetValue(room, Enum.Parse(modalField.FieldType, "None"));
                Call(visits, "RefreshInput");
                Field(adapter, "networkBusy", true);
                Call(adapter, "Publish");
                Check(!adapter.CanFinishVisit && !visits.DismissPatient(), "Pending work blocks explicit departure");
                Field(adapter, "networkBusy", false);
                Property(adapter, "LastError", "isolated saved-result error");
                Call(adapter, "Publish");
                Check(!adapter.CanFinishVisit && !visits.DismissPatient(), "An unresolved save or transport error blocks departure");
                Property(adapter, "LastError", "");
                Call(adapter, "Publish");
                // Exercise the same persisted result being restored after reopening the game.
                session = completedSession = new AiSession(resultStore);
                Field(adapter, "session", completedSession);
                Field(adapter, "committedState", completedSession.State);
                Field(adapter, "renderedRevision", -1);
                Call(adapter, "RenderCommitted");
                Call(adapter, "Update");
                Check(visits.State == PatientVisitController.VisitState.Consulting && !completedSession.VisitCompleted
                    && departureButton.interactable, "Restoring an evaluated save waits for an explicit departure click");
                departureButton.onClick.Invoke();
                Check(visits.State == PatientVisitController.VisitState.Leaving && string.IsNullOrEmpty(visits.CurrentSessionId),
                    "The departure button invokes the adapter and invalidates the local visit once");
                departureButton.onClick.Invoke();
                Check(!adapter.FinishVisit() && !visits.DismissPatient() && visits.CompletedCount == 0 && departureClicks == 1,
                    "Repeated departure clicks cannot start another departure or count another patient");
                Check(new AiSession(resultStore).VisitCompleted && !string.IsNullOrWhiteSpace(room.record.consultationResult),
                    "Explicit departure durably completes the visit and retains its desk record");
                // Return only this isolated fixture to active state for the existing multi-patient input checks.
                Property(visits, "State", PatientVisitController.VisitState.Consulting);
                Property(visits, "CurrentSessionId", "local_check");
                Property(visits, "IsAwaitingDeparture", false);
                Field(adapter, "localVisitId", "local_check");
                Field(adapter, "healthReady", true);
                Field(view, "visible", true);
                view.SetDepartureAvailable(false);
                CheckRecordPages(host.transform, Check);
                foreach (string id in new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" })
                {
                    string name = AiProtocol.Text(AiProtocol.Patient(id), "name");
                    Property(visits, "CurrentPatient", new PatientVisitDefinition { patientId = id, displayName = name });
                    var patientSnapshot = AiSaveStore.NewSnapshot(id);
                    patientSnapshot["state"] = Example(id == "seoyun" ? "start.response" : id + "-start.response")["state"].DeepClone();
                    var patientStore = new AiSaveStore(Path.Combine(temporary, id));
                    patientStore.Save(patientSnapshot, -1);
                    session = new AiSession(patientStore, id);
                    Field(adapter, "session", session);
                    Field(adapter, "committedState", session.State);
                    Field(adapter, "activePatientId", id);
                    Field(adapter, "patientName", name);
                    Field(adapter, "draftDirty", false);
                    Field(adapter, "networkBusy", false);
                    Call(bridge, "Refresh");
                    Check(adapter.IsAiVisit && view.replyRoot.activeSelf && adapter.CanSubmit, id + " has a usable input panel");
                    Check(adapter.IsSeoyunVisit == (id == "seoyun"), id + " preserves the legacy Seoyun flag");
                    view.replyInput.text = name + "에게 보내는 초안";
                    Check(adapter.Draft == view.replyInput.text, id + " has its own draft");
                    Call(adapter, "FlushDraft");
                }
                Property(visits, "CurrentPatient", new PatientVisitDefinition { patientId = "minseok", displayName = "민석" });
                Call(bridge, "Refresh");
                Check(!adapter.IsAiVisit && !view.replyRoot.activeSelf, "Minseok keeps the local demo without an AI input panel");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
                Directory.CreateDirectory("artifacts/ui");
                File.WriteAllText("artifacts/ui/patient-ai-input-checks.txt", report.ToString(), new UTF8Encoding(false));
            }
            Debug.Log("Patient AI input checks: " + passed + " passed. No server request or player save was used.");
        }

        private static void CheckRecordPages(Transform parent, Action<bool, string> check)
        {
            Font source = AssetDatabase.LoadAssetAtPath<Font>("Assets/_Project/Fonts/NotoSansCJKkr-Regular.otf");
            check(source != null, "An isolated record test can load the project's Korean font source");
            TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(source);
            font.hideFlags = HideFlags.HideAndDontSave;
            font.isMultiAtlasTexturesEnabled = true;
            var paper = new GameObject("IsolatedRecordPages", typeof(RectTransform), typeof(Canvas));
            paper.hideFlags = HideFlags.HideAndDontSave;
            paper.transform.SetParent(parent, false);
            try
            {
                var record = paper.AddComponent<MedicalRecordView>();
                TMP_Text Text(string name)
                {
                    var node = new GameObject(name, typeof(RectTransform));
                    node.transform.SetParent(paper.transform, false);
                    var text = node.AddComponent<TextMeshProUGUI>();
                    text.font = font;
                    text.fontSize = 22;
                    return text;
                }
                record.consultationNotes = Text("DetailBody");
                record.patientName = Text("PatientName");
                record.EnsureDetailsControls();
                var controls = paper.transform.Find("ConsultationRecordControls");
                check(controls != null && controls.Find("TranscriptTab") != null && controls.Find("ResultTab") != null,
                    "Record detail tabs are built from the isolated font and panel");
                int childCount = paper.transform.childCount;
                record.EnsureDetailsControls();
                check(paper.transform.childCount == childCount, "Building record controls twice does not duplicate them");
                Button Button(string name) => controls.Find(name).GetComponent<Button>();
                var transcript = new StringBuilder("  한글 원문 <b>이것은 태그가 아닙니다</b>  \n");
                for (int i = 0; i < 80; i++) transcript.Append("상담 원문 ").Append(i + 1).AppendLine(": 줄바꿈과 공백을 그대로 보존합니다.");
                var seoyun = new MedicalRecordData
                {
                    registrationNumber = "seoyun", patientName = "서윤", consultationNotes = "짧은 상담 메모",
                    consultationTranscript = transcript.ToString(), consultationResult = "환자 변화 45 / 100\n상담자 대응 60 / 100\n한글 평가 <b>인용 원문</b>"
                };
                record.ShowRecord(seoyun);
                Button("TranscriptTab").onClick.Invoke();
                Canvas.ForceUpdateCanvases();
                record.consultationNotes.ForceMeshUpdate(true);
                check(record.Section == MedicalRecordView.RecordSection.Transcript && record.DetailPageCount > 1,
                    "A long Korean transcript uses multiple pages on the real paper layout");
                check(record.consultationNotes.text == seoyun.consultationTranscript && !record.consultationNotes.richText,
                    "Record pages preserve literal markup, Korean text, whitespace and newlines");
                check(record.DetailPage == 1 && !Button("PreviousDetailPage").interactable && Button("NextDetailPage").interactable,
                    "The first transcript page has correct navigation bounds");
                Button("NextDetailPage").onClick.Invoke();
                check(record.DetailPage == 2, "The paper's next-page button advances the transcript");
                record.ChangePage(int.MaxValue / 2);
                int lastPage = record.DetailPage;
                check(lastPage == record.DetailPageCount && !Button("NextDetailPage").interactable,
                    "Page navigation stops at the actual final transcript page");
                record.ShowRecord(seoyun);
                check(record.DetailPage == lastPage, "Refreshing the same document restores its final page");
                Button("ResultTab").onClick.Invoke();
                check(record.DetailPage == 1 && record.consultationNotes.text == seoyun.consultationResult && !record.consultationNotes.richText,
                    "The result tab shows the exact saved evaluation as plain Korean text");

                var room = paper.AddComponent<ConsultationRoomController>();
                room.recordView = record;
                Call(room, "Awake");
                room.SetRecord(seoyun);
                var dohyeon = new MedicalRecordData
                {
                    registrationNumber = "dohyeon", patientName = "도현", consultationNotes = "임시 대화 시연",
                    consultationTranscript = "도현의 별도 원문", consultationResult = ""
                };
                room.SetRecord(dohyeon);
                Property(room, "IsRecordOpen", true);
                Button("PreviousPatientRecord").onClick.Invoke();
                Button("TranscriptTab").onClick.Invoke();
                check(record.patientName.text == "서윤" && record.consultationNotes.text == seoyun.consultationTranscript,
                    "Previous-document navigation retains the departed patient's complete transcript");
                Button("ResultTab").onClick.Invoke();
                check(record.consultationNotes.text == seoyun.consultationResult && room.record == dohyeon,
                    "Reading a previous evaluation does not change the room's current patient record");
                var updatedDohyeon = new MedicalRecordData
                {
                    registrationNumber = "dohyeon", patientName = "도현", consultationNotes = "변경된 임시 메모",
                    consultationTranscript = "도현의 갱신된 별도 원문", consultationResult = ""
                };
                room.SetRecord(updatedDohyeon);
                check(record.patientName.text == "서윤" && record.consultationNotes.text == seoyun.consultationResult,
                    "New current-patient data does not replace a previously selected document");
                Button("NextPatientRecord").onClick.Invoke();
                Button("TranscriptTab").onClick.Invoke();
                check(record.patientName.text == "도현" && record.consultationNotes.text == updatedDohyeon.consultationTranscript,
                    "Next-document navigation reads the updated patient without mixing records");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(paper);
                var material = font.material;
                var atlases = font.atlasTextures;
                UnityEngine.Object.DestroyImmediate(font);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (atlases != null) foreach (var atlas in atlases)
                    if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
            }
        }
    }
}
