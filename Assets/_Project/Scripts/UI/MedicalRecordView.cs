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

        public enum RecordSection { Summary, Transcript, Result }
        public enum InformationState { Unknown, NotApplicable, Undisclosed, Disclosed }
        public event Action<int> RecordNavigationRequested;
        public RecordSection Section { get; private set; }
        public int DetailPage => consultationNotes != null ? consultationNotes.pageToDisplay : 1;
        public int DetailPageCount => consultationNotes != null ? Mathf.Max(1, consultationNotes.textInfo.pageCount) : 1;
        private MedicalRecordData current;
        private RectTransform detailControls;
        private Button summaryTab, transcriptTab, resultTab, previousPage, nextPage;
        private Button previousRecord, nextRecord;
        private TMP_Text pageLabel, recordLabel;
        private static readonly Color32 Ink = new Color32(62, 48, 40, 255);
        private static readonly Color32 FadedInk = new Color32(137, 123, 110, 255);
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
            SetText(gender, data?.gender);
            SetText(age, data?.age);
            SetText(occupation, data?.occupation);
            SetText(recordedDate, data?.recordedDate);
            SetText(visitReason, data?.visitReason);
            SetText(medicalHistory, data?.medicalHistory);
            SetText(medication, data?.medication);
            SetText(specialNotes, data?.specialNotes);
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
                : Section == RecordSection.Result ? current?.consultationResult : current?.consultationNotes;
            if (Section == RecordSection.Result && !string.IsNullOrEmpty(settlementText)) text += "\n\n정산 내역\n" + settlementText;
            if (string.IsNullOrWhiteSpace(text)) text = Section == RecordSection.Result ? "상담 결과가 아직 없습니다."
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
            // The printed notes value cell is x278..906, y874..1220 on every 1024x1536 sheet.
            // Soften just its ruling underneath the text; preserve the form borders and left label.
            var writingArea = Rect("WritingArea", transform, 280, 876, 624, 342);
            writingArea.SetAsFirstSibling();
            var paper = writingArea.gameObject.AddComponent<Image>();
            paper.color = new Color32(250, 243, 237, 206);
            paper.raycastTarget = false;
            var rule = Rect("IndexRule", detailControls, 280, 934, 624, 2).gameObject.AddComponent<Image>();
            rule.color = new Color32(151, 136, 119, 255);
            rule.raycastTarget = false;
            summaryTab = IndexTab("SummaryTab", "상담 메모", 296, RecordSection.Summary);
            transcriptTab = IndexTab("TranscriptTab", "대화 원문", 496, RecordSection.Transcript);
            resultTab = IndexTab("ResultTab", "상담 결과", 696, RecordSection.Result);
            Place(consultationNotes.rectTransform, 296, 946, 592, 216);
            consultationNotes.alignment = TextAlignmentOptions.TopLeft;
            consultationNotes.enableAutoSizing = false;
            consultationNotes.fontSize = 25;
            consultationNotes.color = Ink;
            previousPage = InkButton("PreviousDetailPage", "‹ 이전 쪽", 296, 1166, 146, 44, () => ChangePage(-1));
            pageLabel = Text("DetailPage", "", 450, 1166, 284, 44);
            nextPage = InkButton("NextDetailPage", "다음 쪽 ›", 742, 1166, 146, 44, () => ChangePage(1));
            // Bind document navigation to the title band. Its footer has only 46px of paper.
            recordLabel = Text("RecordIndex", "", 114, 248, 230, 48);
            recordLabel.alignment = TextAlignmentOptions.MidlineLeft;
            previousRecord = InkButton("PreviousPatientRecord", "‹ 이전 서류", 650, 248, 124, 48,
                () => RecordNavigationRequested?.Invoke(-1));
            nextRecord = InkButton("NextPatientRecord", "다음 서류 ›", 782, 248, 124, 48,
                () => RecordNavigationRequested?.Invoke(1));
            RefreshControls();
        }

        private void RefreshControls()
        {
            if (detailControls == null) return;
            SetTab(summaryTab, Section == RecordSection.Summary);
            SetTab(transcriptTab, Section == RecordSection.Transcript);
            SetTab(resultTab, Section == RecordSection.Result);
            bool paginated = DetailPageCount > 1;
            previousPage.interactable = DetailPage > 1;
            nextPage.interactable = DetailPage < DetailPageCount;
            previousPage.gameObject.SetActive(paginated);
            nextPage.gameObject.SetActive(paginated);
            pageLabel.gameObject.SetActive(paginated);
            pageLabel.text = DetailPage + " / " + DetailPageCount;
            previousRecord.interactable = recordIndex > 0;
            nextRecord.interactable = recordIndex + 1 < recordCount;
            previousRecord.gameObject.SetActive(recordCount > 1);
            nextRecord.gameObject.SetActive(recordCount > 1);
            recordLabel.text = recordCount > 0 ? "서류 " + (recordIndex + 1) + " / " + recordCount : "";
        }

        private Button IndexTab(string name, string caption, float x, RecordSection section)
        {
            const float width = 192, height = 48;
            RectTransform rect = Rect(name, detailControls, x, 888, width, height);
            var tab = rect.gameObject.AddComponent<RecordIndexTabGraphic>();
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = tab;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors;
            colors.disabledColor = Color.white; // The active paper tab supplies its own colour.
            colors.pressedColor = new Color32(224, 208, 186, 255);
            colors.fadeDuration = 0f;
            button.colors = colors;
            button.onClick.AddListener(() => SelectSection(section));
            var label = Text(name + "Label", caption, 0, 2, width, height - 2, rect);
            label.fontSize = 24;
            return button;
        }

        private static void SetTab(Button button, bool selected)
        {
            button.interactable = !selected;
            ((RecordIndexTabGraphic)button.targetGraphic).SetSelected(selected);
            button.GetComponentInChildren<TMP_Text>(true).fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
        }

        private Button InkButton(string name, string caption, float x, float y, float width, float height,
            UnityEngine.Events.UnityAction clicked)
        {
            var rect = Rect(name, detailControls, x, y, width, height);
            var hitArea = rect.gameObject.AddComponent<Image>();
            hitArea.color = Color.clear;
            var label = Text(name + "Label", caption, 0, 0, width, height, rect);
            label.color = Color.white;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = label;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var colors = button.colors;
            colors.normalColor = Ink;
            colors.highlightedColor = colors.selectedColor = new Color32(123, 71, 39, 255);
            colors.pressedColor = new Color32(84, 44, 23, 255);
            colors.disabledColor = FadedInk;
            colors.fadeDuration = 0f;
            button.colors = colors;
            button.onClick.AddListener(clicked);
            return button;
        }

        private TMP_Text Text(string name, string value, float x, float y, float width, float height, Transform parent = null)
        {
            var text = Rect(name, parent != null ? parent : detailControls, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
            text.font = consultationNotes.font;
            text.fontSize = 22;
            text.color = Ink;
            text.textWrappingMode = TextWrappingModes.NoWrap;
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
