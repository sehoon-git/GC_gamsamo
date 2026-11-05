using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Gamsamo.UI.Presentation;
using UnityEngine;

namespace Gamsamo.UI.UiNext
{
    // UI projections and request correlation only. No persistence, rewards or unlock decisions.
    public sealed class UiFeaturePorts : MonoBehaviour
    {
        public const int NameLimit = 24, IntroductionLimit = 240;
        public string ContextId { get; private set; } = "";
        public ProfileDisplay Profile { get; private set; }
        public ProgressDisplay Progress { get; private set; }
        public IReadOnlyList<EndingListItem> Endings { get; private set; } = Array.Empty<EndingListItem>();
        public bool EndingsKnown { get; private set; }
        public bool ProfileSaving => pendingProfile != null;
        public bool CanSaveProfile => ProfileSaveRequested != null && Profile != null && !string.IsNullOrEmpty(ContextId);
        public bool CanRequestReplay => EndingReplayRequested != null;
        public event Action Changed;
        public event Action<string> ProfileQueryRequested;
        public event Action<ProfileSaveRequest> ProfileSaveRequested;
        public event Action<bool, string> ProfileSaveFinished;
        public event Action<string> EndingListRequested;
        public event Action<EndingReplayRequest> EndingReplayRequested;
        public event Action<string, EndingReplayGrant, string> EndingReplayFinished;
        public event Action<RecordSelection> RecordInformationRequested;
        public event Action<RecordSelection, IReadOnlyList<RecordFieldDisplay>> RecordInformationReceived;
        private ProfileSaveRequest pendingProfile;
        private EndingReplayRequest pendingReplay;
        private RecordSelection selectedRecord;

        public void SetContext(string contextId)
        {
            if (ContextId == (contextId ?? "")) return;
            ContextId = contextId ?? ""; pendingProfile = null; pendingReplay = null; selectedRecord = null;
            Profile = null; Progress = null; EndingsKnown = false; Endings = Array.Empty<EndingListItem>(); Changed?.Invoke();
        }
        public void QueryProfile() { if (Profile == null) ProfileQueryRequested?.Invoke(ContextId); }
        public void QueryEndings() => EndingListRequested?.Invoke(ContextId);
        public bool PublishProfile(string contextId, ProfileDisplay profile)
        {
            if (contextId != ContextId || profile == null) return false;
            Profile = new ProfileDisplay(profile.Name, profile.Introduction, profile.Affiliation); Changed?.Invoke(); return true;
        }
        public bool PublishProgress(string contextId, ProgressDisplay progress)
        {
            if (contextId != ContextId || progress == null || progress.Completed < 0 || progress.Total < progress.Completed) return false;
            Progress = progress; Changed?.Invoke(); return true;
        }
        public bool SaveProfile(string name, string introduction, out string message)
        {
            if (ProfileSaving) { message = "저장 중입니다."; return false; }
            if (!ValidateProfile(name, introduction, out message)) return false;
            if (!CanSaveProfile) { message = "프로필 저장 기능 연결을 기다리고 있습니다."; return false; }
            var request = new ProfileSaveRequest(Guid.NewGuid().ToString("N"), ContextId, name.Trim(), introduction ?? "");
            pendingProfile = request; Changed?.Invoke();
            try { ProfileSaveRequested.Invoke(request); }
            catch (Exception) { CompleteProfileSave(request.RequestId, false, null, "프로필을 저장하지 못했습니다. 입력은 유지됩니다."); }
            return true;
        }
        public bool CompleteProfileSave(string requestId, bool saved, ProfileDisplay committed, string safeError = null)
        {
            if (pendingProfile == null || pendingProfile.RequestId != requestId || pendingProfile.ContextId != ContextId) return false;
            // Success without the committed snapshot is not a successful UI operation.
            saved = saved && committed != null;
            if (saved) Profile = new ProfileDisplay(committed.Name, committed.Introduction, committed.Affiliation);
            pendingProfile = null; Changed?.Invoke();
            ProfileSaveFinished?.Invoke(saved, saved ? "프로필을 저장했습니다." : safeError ?? "저장 실패 · 입력을 유지했습니다. 다시 저장해 주세요."); return true;
        }
        public static bool ValidateProfile(string name, string introduction, out string message)
        {
            if (string.IsNullOrWhiteSpace(name)) { message = "이름을 입력해 주세요."; return false; }
            if (TextLength(name.Trim()) > NameLimit) { message = "이름은 " + NameLimit + "자까지 입력할 수 있습니다."; return false; }
            if (TextLength(introduction) > IntroductionLimit) { message = "소개는 " + IntroductionLimit + "자까지 입력할 수 있습니다."; return false; }
            if (name.Any(char.IsControl) || (introduction ?? "").Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
            { message = "사용할 수 없는 제어 문자가 포함되어 있습니다."; return false; }
            message = ""; return true;
        }
        public static int TextLength(string value)
        {
            // Unity Mono's StringInfo counts decomposed Hangul jamo separately. Normalize only for counting.
            value = value ?? "";
            try { return new StringInfo(value.Normalize(NormalizationForm.FormC)).LengthInTextElements; }
            catch (ArgumentException) { return value.Length; } // An in-progress IME/surrogate must not break the editor.
        }
        public bool PublishEndings(string contextId, IReadOnlyList<EndingListItem> items)
        {
            if (contextId != ContextId) return false;
            Endings = (items ?? Array.Empty<EndingListItem>()).Where(x => x != null).GroupBy(x => x.PatientId).Select(g => g.First()).ToArray();
            EndingsKnown = true; Changed?.Invoke(); return true;
        }
        public bool RequestReplay(string patientId)
        {
            var entry = Endings.FirstOrDefault(x => x.PatientId == patientId);
            if (string.IsNullOrWhiteSpace(ContextId) || pendingReplay != null || !EndingsKnown || !CanRequestReplay || entry == null || !entry.Unlocked || !entry.Approved || !entry.ContentReady) return false;
            var request = new EndingReplayRequest(Guid.NewGuid().ToString("N"), ContextId, patientId);
            pendingReplay = request;
            try { EndingReplayRequested.Invoke(request); }
            catch(Exception) { CompleteReplay(request.RequestId, null, "후일담을 불러오지 못했습니다."); }
            return true;
        }
        public void CancelReplayRequest() { pendingReplay = null; }
        public bool CompleteReplay(string requestId, EndingReplayGrant grant, string safeError = null)
        {
            if (pendingReplay == null || pendingReplay.RequestId != requestId || pendingReplay.ContextId != ContextId) return false;
            string patient = pendingReplay.PatientId; pendingReplay = null;
            var listed = Endings.FirstOrDefault(x => x.PatientId == patient);
            if (listed == null || !listed.Unlocked || !listed.Approved || !listed.ContentReady || grant == null || grant.ContextId != ContextId
                || grant.PatientId != patient || !grant.Unlocked || grant.Catalog?.FindApproved(patient) == null) grant = null;
            EndingReplayFinished?.Invoke(patient, grant, safeError ?? (grant == null ? "재생할 수 있는 승인된 후일담이 없습니다." : "")); return true;
        }
        public void SelectRecord(RecordSelection selection)
        { selectedRecord = selection?.Copy(); if (selection != null) RecordInformationRequested?.Invoke(selection.Copy()); }
        public bool PublishRecordInformation(RecordSelection selection, IReadOnlyList<RecordFieldDisplay> fields)
        {
            if (selection == null || selectedRecord == null || !selectedRecord.Same(selection)) return false;
            // Only disclosed values cross the UI port, even if a producer accidentally supplies a hidden payload.
            var safe = (fields ?? Array.Empty<RecordFieldDisplay>()).Where(x => x != null)
                .Select(x => new RecordFieldDisplay(x.Field, x.State, x.State == MedicalRecordView.InformationState.Disclosed ? x.Value : null)).ToArray();
            RecordInformationReceived?.Invoke(selection, safe); return true;
        }
    }
    public enum UiFailureKind { Unspecified, Connection, Save }

    public sealed class ProfileDisplay
    {
        public string Name { get; } public string Introduction { get; } public string Affiliation { get; }
        public ProfileDisplay(string name, string introduction, string affiliation = "") { Name=name??""; Introduction=introduction??""; Affiliation=affiliation??""; }
    }
    public sealed class ProgressDisplay
    {
        public string Patient { get; } public string State { get; } public string Promotion { get; }
        public int Completed { get; } public int Total { get; }
        public ProgressDisplay(string patient,string state,string promotion,int completed,int total) { Patient=patient;State=state;Promotion=promotion;Completed=completed;Total=total; }
    }
    public sealed class ProfileSaveRequest
    {
        public string RequestId { get; } public string ContextId { get; } public string Name { get; } public string Introduction { get; }
        public ProfileSaveRequest(string requestId,string contextId,string name,string introduction) { RequestId=requestId;ContextId=contextId;Name=name;Introduction=introduction; }
    }
    public sealed class EndingListItem
    {
        public string PatientId { get; } public string Name { get; } public bool Unlocked { get; } public bool Approved { get; } public bool ContentReady { get; }
        public EndingListItem(string patientId,string name,bool unlocked,bool approved,bool contentReady) { PatientId=patientId;Name=name;Unlocked=unlocked;Approved=approved;ContentReady=contentReady; }
    }
    public sealed class EndingReplayRequest
    {
        public string RequestId { get; } public string ContextId { get; } public string PatientId { get; }
        public EndingReplayRequest(string requestId,string contextId,string patientId) { RequestId=requestId;ContextId=contextId;PatientId=patientId; }
    }
    public sealed class EndingReplayGrant
    {
        public string ContextId, PatientId; public bool Unlocked, PhotoUnlocked; public EndingContentCatalog Catalog;
    }
    public sealed class RecordSelection
    {
        public string GameId, PatientId, CaseId, SessionId; public bool IsArchive; public int Round;
        public RecordSelection Copy() => (RecordSelection)MemberwiseClone();
        public bool Same(RecordSelection other) => other != null && GameId==other.GameId && PatientId==other.PatientId && CaseId==other.CaseId && SessionId==other.SessionId && IsArchive==other.IsArchive;
    }
    public sealed class RecordFieldDisplay
    {
        public string Field { get; } public MedicalRecordView.InformationState State { get; } public string Value { get; }
        public RecordFieldDisplay(string field,MedicalRecordView.InformationState state,string value=null) { Field=field;State=state;Value=state==MedicalRecordView.InformationState.Disclosed?value:null; }
    }
}
