using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Gamsamo.Progression
{
    [Serializable] public sealed class ProgressProfile
    {
        public string Name = "", Introduction = "";
        public void Validate()
        {
            if (Name == null || Introduction == null || Name.Length > 40 || Introduction.Length > 500
                || Name.Any(char.IsControl) || Introduction.Any(c => char.IsControl(c) && c != '\n' && c != '\r'))
                throw new ArgumentException("이름은 40자, 소개는 500자 이내로 입력해 주세요. 이름에는 줄바꿈을 사용할 수 없습니다.");
        }
    }
    // B owns these contracts. A supplies committed evaluations; C calculates; D displays receipts.
    public enum ProgressPhase { Waiting, Consulting, Evaluating, AwaitingSettlement, Presenting, AwaitingDeparture, Finished }
    [Serializable] public sealed class RoundIdentity
    {
        public string ProgressId, PatientId, CaseId, RoundId;
        [JsonIgnore] public string Key => JsonConvert.SerializeObject(new[] { ProgressId, PatientId, CaseId, RoundId });
        [JsonIgnore] public string FirstClearKey => JsonConvert.SerializeObject(new[] { ProgressId, PatientId });
        public bool IsValid => new[] { ProgressId, PatientId, CaseId, RoundId }.All(x => !string.IsNullOrWhiteSpace(x));
    }
    [Serializable] public sealed class SavedEvaluation
    {
        public RoundIdentity Identity;
        public int PatientScore, CounselorScore;
        public bool AiSaveCompleted;
        public string Phase;
    }
    [Serializable] public sealed class EconomyState
    {
        public long Balance;
        public Gamsamo.Economy.EconomyState Ledger;
        public Dictionary<string, long> HighestRewards = new Dictionary<string, long>();
        public List<string> FirstClearBonuses = new List<string>();
        public List<string> OwnedItems = new List<string>();
        public Dictionary<string, string> EquippedItems = new Dictionary<string, string>();
    }
    [Serializable] public sealed class RewardCalculation
    {
        public string SettlementKey;
        public long Granted;
        public EconomyState Economy;
    }
    public interface IRewardCalculator
    {
        RewardCalculation Calculate(SavedEvaluation evaluation, EconomyState previous, bool firstClear);
    }
    [Serializable] public sealed class ConfirmedResult
    {
        public RoundIdentity Identity;
        public bool Cleared, FirstClear;
        public long Granted;
        public string PhotoId;
    }
    [Serializable] public sealed class PatientProgress
    {
        public string PatientId, CaseId, RoundId, AiSnapshot;
        public string PendingAiOperation, PreviousRoundId;
        public bool Cleared, NeedsRevisit;
    }
    [Serializable] public sealed class GameProgress
    {
        public int Version = 1, Revision;
        public string ProgressId;
        public ProgressProfile Profile = new ProgressProfile();
        public List<PatientProgress> Patients = new List<PatientProgress>();
        public int CurrentIndex;
        public ProgressPhase Phase;
        public EconomyState Economy = new EconomyState();
        public Dictionary<string, ConfirmedResult> Settlements = new Dictionary<string, ConfirmedResult>();
        public List<string> Photos = new List<string>();
        public List<string> PlayedEndings = new List<string>();
        public bool FinalEndingUnlocked, FinalEndingPlayed;
        public ConfirmedResult PendingPresentation;
        public string PresentationStage = "clear", FinalPresentationStage = "credits";
        [JsonIgnore] public int ClearedCount => Patients.Where(p => p.Cleared).Select(p => p.PatientId).Distinct().Count();
        [JsonIgnore] public PatientProgress Current => CurrentIndex < Patients.Count ? Patients[CurrentIndex] : null;
        public static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        public void Validate()
        {
            if (Profile == null) throw new InvalidOperationException("프로필 저장 형식이 손상되었습니다.");
            Profile.Validate();
            if (Version != 1 || string.IsNullOrWhiteSpace(ProgressId) || Revision < 0 || Patients == null || Patients.Count != 5
                || Patients.Any(p => p == null || string.IsNullOrWhiteSpace(p.PatientId))
                || Patients.Select(p => p.PatientId).Distinct().Count() != 5 || CurrentIndex < 0 || CurrentIndex > Patients.Count
                || Economy == null || Economy.Balance < 0 || Settlements == null || Photos == null || PlayedEndings == null
                || Economy.HighestRewards == null || Economy.FirstClearBonuses == null || Economy.OwnedItems == null || Economy.EquippedItems == null
                || (Economy.Ledger != null && (!Economy.Ledger.IsValid() || Economy.Ledger.runId != ProgressId || Economy.Ledger.balance != Economy.Balance))
                || Economy.HighestRewards.Values.Any(value => value < 0)
                || !Enum.IsDefined(typeof(ProgressPhase), Phase)
                || FinalEndingUnlocked != (ClearedCount == Patients.Count) || (FinalEndingPlayed && !FinalEndingUnlocked)
                || (Phase == ProgressPhase.Finished && CurrentIndex != Patients.Count)
                || Patients.Take(CurrentIndex).Any(p => !p.Cleared)
                || (Phase == ProgressPhase.Presenting && PendingPresentation == null))
                throw new InvalidOperationException("진행 저장의 형식·상태가 손상되었습니다. 원본과 백업을 보존하고 복구해 주세요.");
            if (Settlements.Any(pair => pair.Value?.Identity == null || !pair.Value.Identity.IsValid
                || pair.Key != pair.Value.Identity.Key || pair.Value.Identity.ProgressId != ProgressId || pair.Value.Granted < 0
                || !Patients.Any(p => p.PatientId == pair.Value.Identity.PatientId)
                || (pair.Value.FirstClear && !pair.Value.Cleared))
                || Photos.Distinct().Count() != Photos.Count || PlayedEndings.Distinct().Count() != PlayedEndings.Count
                || PlayedEndings.Any(id => !Patients.Any(p => p.PatientId == id && p.Cleared))
                || Patients.Any(p => p.Cleared && p.NeedsRevisit)
                || (CurrentIndex == Patients.Count && Phase != ProgressPhase.Finished)
                || (PendingPresentation != null && (PendingPresentation.Identity == null || Current == null
                    || PendingPresentation.Identity.PatientId != Current.PatientId || PendingPresentation.Identity.CaseId != Current.CaseId
                    || PendingPresentation.Identity.RoundId != Current.RoundId
                    || !Settlements.TryGetValue(PendingPresentation.Identity.Key, out var stored)
                    || JsonConvert.SerializeObject(stored) != JsonConvert.SerializeObject(PendingPresentation)))
                || (Phase == ProgressPhase.AwaitingDeparture && PendingPresentation == null)
                || (Phase == ProgressPhase.Presenting && (!PendingPresentation.Cleared || (PresentationStage != "clear" && PresentationStage != "ending"))))
                throw new InvalidOperationException("정산·재방문·엔딩 상태가 일치하지 않습니다. 원본을 보존해 주세요.");
        }
    }
    public interface IProgressStore
    {
        GameProgress Load();
        void Save(GameProgress candidate, int expectedRevision);
    }
}
