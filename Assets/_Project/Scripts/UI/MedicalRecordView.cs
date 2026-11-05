using System;
using System.Collections.Generic;
using Gamsamo.Patients;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    [DisallowMultipleComponent]
    public sealed class MedicalRecordView : MonoBehaviour
    {
        public TMP_Text registrationNumber;
        public TMP_Text patientName;
        public TMP_Text gender;
        public TMP_Text age;
        public TMP_Text occupation;
        public TMP_Text recordedDate;
        public TMP_Text visitReason;
        public TMP_Text medicalHistory;
        public TMP_Text medication;
        public TMP_Text consultationNotes;
        public TMP_Text specialNotes;
        public TMP_Text counselor;

        public enum RecordSection { Summary, Transcript, Result, Settlement }
        public enum InformationState { Unknown, NotApplicable, Undisclosed, Disclosed }
        public event Action<int> RecordNavigationRequested;
        public RecordSection Section { get; private set; }
        public int DetailPage => consultationNotes != null ? consultationNotes.pageToDisplay : 1;
        public int DetailPageCount => consultationNotes != null ? Mathf.Max(1, consultationNotes.textInfo.pageCount) : 1;
        private MedicalRecordData current;
        private RectTransform detailControls;
        private Button summaryTab, transcriptTab, resultTab, settlementTab, previousPage, nextPage;
        private TMP_Text heading;
        private bool isArchive;
        private int round;
        public string Heading => (current?.patientName ?? "내담자") + " · " + (isArchive ? "회차별 보관본" : "현재 사례 합본")
            + "\n" + (round > 0 ? "상담 " + round + "회차 · " : "회차 정보 확인 전 · ") + (string.IsNullOrEmpty(current?.recordedDate) ? "날짜 미확인" : current.recordedDate);
        public void SetRecordHeading(bool archive, int roundNumber)
        { isArchive = archive; round = Mathf.Max(0, roundNumber); if (heading != null) heading.text = Heading; }
        private Button previousRecord, nextRecord;
        private TMP_Text pageLabel, recordLabel;
        private int recordIndex, recordCount;
        private readonly Dictionary<string, string> structuredFields = new Dictionary<string, string>(StringComparer.Ordinal);
        private string settlementText = "";
        private string contextGame, contextCase;

        /// <summary>B calls before ShowRecord when restoring/switching a run or case.</summary>
        public void BeginRecordContext(string gameId, string caseId)
        {
            if (contextGame == gameId && contextCase == caseId) return;
            contextGame = gameId; contextCase = caseId;
            structuredFields.Clear(); settlementText = "";
        }

        /// <summary>Display projection supplied by A/B. Hidden payloads are discarded, not cached.</summary>
        public bool SetInformationState(string recordId, string field, InformationState state, string disclosedValue = null)
        {
            if (current == null || string.IsNullOrEmpty(recordId) || current.registrationNumber != recordId) return false;
            TMP_Text label = FieldLabel(field);
            if (label == null || !Enum.IsDefined(typeof(InformationState), state)) return false;
            string value = state == InformationState.Unknown ? "미확인"
                : state == InformationState.NotApplicable ? "해당 없음"
                : state == InformationState.Undisclosed ? "미공개"
                : "공개됨" + (string.IsNullOrWhiteSpace(disclosedValue) ? "" : " · " + disclosedValue);
            structuredFields[field] = value;
            SetText(label, value);
            return true;
        }

        /// <summary>C/B supply an already committed settlement description, never a provisional calculation.</summary>
        public bool SetCommittedSettlement(string recordId, bool committed, string text)
        {
            if (!committed || current == null || current.registrationNumber != recordId) return false;
            settlementText = text ?? "";
            ShowDetail(false); return true;
        }

        private TMP_Text FieldLabel(string field)
        {
            switch (field)
            {
                case "gender": return gender;
                case "age": return age;
                case "occupation": return occupation;
                case "visitReason": return visitReason;
                case "medicalHistory": return medicalHistory;
                case "medication": return medication;
                case "specialNotes": return specialNotes;
                default: return null;
            }
        }

        public void ShowRecord(MedicalRecordData data)
        {
            bool changedPatient = current == null || current.registrationNumber != data?.registrationNumber;
            bool resultArrived = string.IsNullOrEmpty(current?.consultationResult) && !string.IsNullOrEmpty(data?.consultationResult);
            current = data;
            if (changedPatient) { structuredFields.Clear(); settlementText = ""; }
            if (changedPatient) Section = string.IsNullOrEmpty(data?.consultationTranscript) ? RecordSection.Summary : RecordSection.Transcript;
            if (resultArrived) Section = RecordSection.Result;
            SetText(registrationNumber, data?.registrationNumber);
            SetText(patientName, data?.patientName);
            SetText(gender, string.IsNullOrWhiteSpace(data?.gender) ? "미확인" : data.gender);
            SetText(age, string.IsNullOrWhiteSpace(data?.age) ? "미확인" : data.age);
            SetText(occupation, string.IsNullOrWhiteSpace(data?.occupation) ? "미확인" : data.occupation);
            SetText(recordedDate, data?.recordedDate);
            SetText(visitReason, string.IsNullOrWhiteSpace(data?.visitReason) ? "미확인" : data.visitReason);
            SetText(medicalHistory, string.IsNullOrWhiteSpace(data?.medicalHistory) ? "미확인" : data.medicalHistory);
            SetText(medication, string.IsNullOrWhiteSpace(data?.medication) ? "미확인" : data.medication);
            SetText(specialNotes, string.IsNullOrWhiteSpace(data?.specialNotes) ? "미확인" : data.specialNotes);
            SetText(counselor, data?.counselor);
            foreach (var field in structuredFields) SetText(FieldLabel(field.Key), field.Value);
            if (Application.isPlaying) EnsureDetailsControls();
            ShowDetail(changedPatient || resultArrived);
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            EnsureDetailsControls();
            ShowDetail(false);
        }

        // The same paper contains short notes, the complete transcript and the evaluation.
        // Pagination changes only presentation; it never truncates or rewrites the stored text.
        public void SelectSection(RecordSection section)
        {
            Section = section;
            ShowDetail(true);
        }

        public void ChangePage(int direction)
        {
            if (consultationNotes == null) return;
            consultationNotes.ForceMeshUpdate(true);
            consultationNotes.pageToDisplay = Mathf.Clamp(DetailPage + direction, 1, DetailPageCount);
            RefreshControls();
        }

        public void SetRecordNavigation(int index, int count)
        {
            recordIndex = index;
            recordCount = count;
            RefreshControls();
        }

        private void ShowDetail(bool resetPage)
        {
            if (consultationNotes == null) return;
            int oldPage = resetPage ? 1 : DetailPage;
            string text = Section == RecordSection.Transcript ? current?.consultationTranscript
                : Section == RecordSection.Result ? current?.consultationResult : Section == RecordSection.Settlement ? settlementText : current?.consultationNotes;
            if (Section == RecordSection.Result && !string.IsNullOrEmpty(settlementText)) text += "\n\n정산 내역\n" + settlementText;
            if (string.IsNullOrWhiteSpace(text)) text = Section == RecordSection.Settlement ? "저장이 확정된 정산 내역이 아직 없습니다." : Section == RecordSection.Result ? "상담 결과가 아직 없습니다."
                : Section == RecordSection.Transcript ? "저장된 대화가 아직 없습니다." : "상담에서 확인한 내용을 기록합니다.";
            SetText(consultationNotes, text);
            consultationNotes.overflowMode = TextOverflowModes.Page;
            consultationNotes.maxVisibleCharacters = int.MaxValue;
            consultationNotes.ForceMeshUpdate(true);
            consultationNotes.pageToDisplay = Mathf.Clamp(oldPage, 1, DetailPageCount);
            RefreshControls();
        }

        public void EnsureDetailsControls()
        {
            if (detailControls != null || consultationNotes == null || consultationNotes.font == null) return;
            detailControls = Rect("ConsultationRecordControls", transform, 0, 0, 1024, 1536);
            heading = Text("RecordHeading", Heading, 296, 884, 592, 60);
            heading.fontSize = 26; heading.alignment = TextAlignmentOptions.TopLeft;
            summaryTab = Button("SummaryTab", "메모", 296, 952, 140, 40, () => SelectSection(RecordSection.Summary));
            transcriptTab = Button("TranscriptTab", "원문", 446, 952, 140, 40, () => SelectSection(RecordSection.Transcript));
            resultTab = Button("ResultTab", "평가", 596, 952, 140, 40, () => SelectSection(RecordSection.Result));
            settlementTab = Button("SettlementTab", "정산", 746, 952, 140, 40, () => SelectSection(RecordSection.Settlement));
            Place(consultationNotes.rectTransform, 296, 1004, 592, 154);
            consultationNotes.alignment = TextAlignmentOptions.TopLeft;
            consultationNotes.enableAutoSizing = false;
            consultationNotes.fontSize = 22;
            previousPage = Button("PreviousDetailPage", "이전 쪽", 296, 1172, 160, 36, () => ChangePage(-1));
            pageLabel = Text("DetailPage", "", 466, 1172, 252, 36);
            nextPage = Button("NextDetailPage", "다음 쪽", 728, 1172, 160, 36, () => ChangePage(1));
            previousRecord = Button("PreviousPatientRecord", "이전 서류", 296, 1416, 180, 42, () => RecordNavigationRequested?.Invoke(-1));
            recordLabel = Text("RecordIndex", "", 486, 1416, 212, 42);
            nextRecord = Button("NextPatientRecord", "다음 서류", 708, 1416, 180, 42, () => RecordNavigationRequested?.Invoke(1));
            RefreshControls();
        }

        private void RefreshControls()
        {
            if (detailControls == null) return;
            summaryTab.interactable = Section != RecordSection.Summary;
            transcriptTab.interactable = Section != RecordSection.Transcript;
            resultTab.interactable = Section != RecordSection.Result;
            settlementTab.interactable = Section != RecordSection.Settlement;
            heading.text = Heading;
            previousPage.interactable = DetailPage > 1;
            nextPage.interactable = DetailPage < DetailPageCount;
            pageLabel.text = DetailPage + " / " + DetailPageCount;
            previousRecord.interactable = recordIndex > 0;
            nextRecord.interactable = recordIndex + 1 < recordCount;
            recordLabel.text = recordCount > 0 ? "서류 " + (recordIndex + 1) + " / " + recordCount : "";
        }

        private Button Button(string name, string caption, float x, float y, float width, float height, UnityEngine.Events.UnityAction clicked)
        {
            RectTransform rect = Rect(name, detailControls, x, y, width, height);
            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color32(229, 208, 178, 255);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(clicked);
            var label = Text(name + "Label", caption, 0, 0, width, height, rect);
            label.raycastTarget = false;
            return button;
        }

        private TMP_Text Text(string name, string value, float x, float y, float width, float height, Transform parent = null)
        {
            var text = Rect(name, parent != null ? parent : detailControls, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = consultationNotes.font;
            text.fontSize = 22;
            text.color = new Color32(69, 47, 38, 255);
            text.alignment = TextAlignmentOptions.Center;
            text.richText = false;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            Place(rect, x, y, width, height);
            return rect;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void SetText(TMP_Text target, string value)
        {
            if (target == null)
                return;

            // Narrative and player-controlled content must never become TMP markup.
            target.richText = false;
            target.text = string.IsNullOrWhiteSpace(value) ? "—" : value;
        }
    }
}
