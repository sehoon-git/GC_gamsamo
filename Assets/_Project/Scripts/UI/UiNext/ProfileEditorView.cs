using Gamsamo.UI.Presentation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.UiNext
{
    /// <summary>Owns a transient draft. Only the integration's committed reply changes ProfileView.</summary>
    public sealed class ProfileEditorView : PresentationModal
    {
        public UiFeaturePorts ports;
        public ProfileView profile;
        public Button editButton, saveButton, cancelButton;
        public TMP_InputField nameInput, introductionInput;
        public TMP_Text feedback, counts;
        string context;
        public string DraftName => nameInput != null ? nameInput.text : "";
        public string DraftIntroduction => introductionInput != null ? introductionInput.text : "";
        void Awake()
        {
            if (panel != null) panel.SetActive(false);
            editButton?.onClick.AddListener(BeginEdit);
            saveButton?.onClick.AddListener(Save);
            cancelButton?.onClick.AddListener(Cancel);
            nameInput?.onValueChanged.AddListener(DraftChanged);
            introductionInput?.onValueChanged.AddListener(DraftChanged);
        }
        void OnEnable()
        {
            if (ports == null) return;
            ports.Changed += Refresh; ports.ProfileSaveFinished += Saved; context = ports.ContextId; Refresh();
        }
        public void BeginEdit()
        {
            if (ports == null || IsOpen) return;
            ports.QueryProfile();
            if (!OpenModal()) return;
            nameInput.SetTextWithoutNotify(ports.Profile?.Name ?? "");
            introductionInput.SetTextWithoutNotify(ports.Profile?.Introduction ?? "");
            Set(feedback, ports.CanSaveProfile ? "이름과 소개를 수정한 뒤 저장해 주세요." : "프로필 저장 연결 준비 중 · 지금은 저장할 수 없습니다.");
            Refresh(); nameInput.Select();
        }
        public void Save()
        {
            if (!IsOpen || ports == null || ports.ProfileSaving) return;
            Set(feedback, "저장 중 · 입력을 보관하고 있습니다.");
            if (!ports.SaveProfile(DraftName, DraftIntroduction, out string error)) Set(feedback, error);
            Refresh();
        }
        public void Cancel()
        {
            if (!IsOpen || (ports != null && ports.ProfileSaving)) return;
            CloseModal();
            nameInput.SetTextWithoutNotify(""); introductionInput.SetTextWithoutNotify("");
        }
        void Saved(bool success, string message)
        {
            Set(feedback, message);
            if (success && IsOpen) CloseModal();
            Refresh();
        }
        void DraftChanged(string _) { Refresh(); }
        public void Refresh()
        {
            if (ports == null) return;
            if (context != ports.ContextId)
            {
                context = ports.ContextId; CloseModal();
                nameInput?.SetTextWithoutNotify(""); introductionInput?.SetTextWithoutNotify("");
                profile?.SetProfile("", "", ""); profile?.ClearProgress();
            }
            if (ports.Profile != null) profile?.SetProfile(ports.Profile.Name, ports.Profile.Affiliation, ports.Profile.Introduction, profile.Avatar);
            if (ports.Progress != null)
            {
                var p = ports.Progress;
                profile?.SetProgress(p.Promotion, p.Patient, p.State, p.Completed, p.Total);
            }
            bool busy = ports.ProfileSaving;
            if (nameInput != null) nameInput.interactable = !busy;
            if (introductionInput != null) introductionInput.interactable = !busy;
            if (saveButton != null) saveButton.interactable = !busy && ports.CanSaveProfile;
            if (cancelButton != null) cancelButton.interactable = !busy;
            Set(counts, "이름 " + UiFeaturePorts.TextLength(DraftName) + " / " + UiFeaturePorts.NameLimit
                + "     소개 " + UiFeaturePorts.TextLength(DraftIntroduction) + " / " + UiFeaturePorts.IntroductionLimit);
        }
        void Update() { if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) Cancel(); }
        protected override void OnDisable()
        {
            if (ports != null) { ports.Changed -= Refresh; ports.ProfileSaveFinished -= Saved; }
            base.OnDisable();
        }
        static void Set(TMP_Text text, string value) { if (text != null) { text.richText = false; text.text = value; } }
    }
}
