using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Gamsamo.Progression
{
    public sealed class ProgressService
    {
        private readonly IProgressStore store;
        private GameProgress state;
        public GameProgress State => GameProgress.Copy(state);
        public ProgressService(IProgressStore store) { this.store = store; state = store.Load(); }
        public void Create(IEnumerable<string> roster)
        {
            if (state != null) throw new InvalidOperationException("A new progress requires a new store.");
            var next = new GameProgress { ProgressId = Guid.NewGuid().ToString("N"), Phase = ProgressPhase.Waiting,
                Patients = roster.Select(id => new PatientProgress { PatientId = id }).ToList() };
            next.Validate(); store.Save(next, -1); state = next;
        }
        private void Commit(GameProgress next)
        {
            next.Revision = state.Revision + 1; next.Validate();
            store.Save(next, state.Revision); state = next;
        }
        public void BeginVisit()
        {
            if (state.Phase != ProgressPhase.Waiting || state.Current == null) throw new InvalidOperationException("방문 대기 상태가 아닙니다.");
            var next = State; next.Phase = ProgressPhase.Consulting; Commit(next);
        }
        public void PrepareAiOperation()
        {
            var next = State;
            var patient = next.Current;
            if (patient == null || !string.IsNullOrEmpty(patient.PendingAiOperation)) return;
            if (string.IsNullOrEmpty(patient.CaseId))
            {
                patient.CaseId = Guid.NewGuid().ToString("N"); patient.RoundId = Guid.NewGuid().ToString("N");
                patient.PendingAiOperation = "start";
            }
            else if (patient.NeedsRevisit && next.Phase == ProgressPhase.Waiting)
            {
                patient.PreviousRoundId = patient.RoundId; patient.RoundId = Guid.NewGuid().ToString("N");
                patient.PendingAiOperation = "revisit";
            }
            else return;
            Commit(next);
        }
        public void CompleteAiOperation(string roundId)
        {
            if (state.Current?.RoundId != roundId || string.IsNullOrEmpty(state.Current.PendingAiOperation)) return;
            var next = State; next.Current.PendingAiOperation = null; next.Current.PreviousRoundId = null;
            next.Current.NeedsRevisit = false; Commit(next);
        }
        public void SavePresentationStage(string key, string stage)
        {
            if (state.PendingPresentation?.Identity.Key != key || state.Phase != ProgressPhase.Presenting)
                throw new InvalidOperationException("표시 중인 회차가 다릅니다.");
            if (stage != "ending" && stage != "completed" && stage != "skipped") throw new InvalidOperationException("잘못된 재생 상태입니다.");
            var next = State; next.PresentationStage = stage;
            if (stage == "completed" || stage == "skipped")
            {
                next.Phase = ProgressPhase.AwaitingDeparture;
                if (!next.PlayedEndings.Contains(next.Current.PatientId)) next.PlayedEndings.Add(next.Current.PatientId);
            }
            Commit(next);
        }
        // Snapshot is A's existing serialized AiSaveStore format, never an independently invented AI DTO.
        public void CaptureAi(string patientId, string caseId, string roundId, string snapshot, string phase)
        {
            if (!new[] { "active", "closing", "pending_evaluation", "evaluated" }.Contains(phase))
                throw new InvalidOperationException("알 수 없는 상담 상태입니다.");
            if (state.Current == null || state.Current.PatientId != patientId || string.IsNullOrEmpty(caseId) || string.IsNullOrEmpty(roundId))
                throw new InvalidOperationException("현재 환자·사례·회차가 일치하지 않습니다.");
            var next = State;
            if (!string.IsNullOrEmpty(next.Current.CaseId) && next.Current.CaseId != caseId) throw new InvalidOperationException("사례를 바꿀 수 없습니다.");
            if (!string.IsNullOrEmpty(next.Current.RoundId) && next.Current.RoundId != roundId)
            {
                if (!next.Current.NeedsRevisit || next.Current.Cleared || state.Phase == ProgressPhase.Presenting || state.Phase == ProgressPhase.AwaitingDeparture)
                    throw new InvalidOperationException("다음 문 입장으로만 재상담 회차를 시작할 수 있습니다.");
                next.Current.NeedsRevisit = false;
            }
            next.Current.CaseId = caseId; next.Current.RoundId = roundId; next.Current.AiSnapshot = snapshot;
            if (state.Phase != ProgressPhase.Presenting && state.Phase != ProgressPhase.AwaitingDeparture)
                next.Phase = phase == "evaluated" ? ProgressPhase.AwaitingSettlement
                    : phase == "pending_evaluation" || phase == "closing" ? ProgressPhase.Evaluating : ProgressPhase.Consulting;
            if (JsonConvert.SerializeObject(next) != JsonConvert.SerializeObject(state)) Commit(next);
        }
        public ConfirmedResult Settle(SavedEvaluation evaluation, IRewardCalculator calculator, string photoId = null)
        {
            var id = evaluation?.Identity;
            if (id == null || !id.IsValid || id.ProgressId != state.ProgressId || !evaluation.AiSaveCompleted || evaluation.Phase != "evaluated"
                || evaluation.PatientScore < 0 || evaluation.PatientScore > 100 || evaluation.CounselorScore < 0 || evaluation.CounselorScore > 100)
                throw new InvalidOperationException("저장 완료된 유효 평가가 필요합니다.");
            if (state.Settlements.TryGetValue(id.Key, out var existing)) return GameProgress.Copy(existing);
            var patient = state.Current;
            if (patient == null || patient.PatientId != id.PatientId || patient.CaseId != id.CaseId || patient.RoundId != id.RoundId
                || state.Phase != ProgressPhase.AwaitingSettlement) throw new InvalidOperationException("현재 정산 회차와 다릅니다.");
            if (calculator == null) throw new InvalidOperationException("보상 계산 연결 대기: C의 IRewardCalculator가 필요합니다.");
            bool cleared = evaluation.PatientScore >= 70 && evaluation.CounselorScore >= 70;
            bool first = cleared && !patient.Cleared;
            var reward = calculator.Calculate(GameProgress.Copy(evaluation), GameProgress.Copy(state.Economy), first);
            if (reward == null || reward.SettlementKey != id.Key || reward.Economy == null || reward.Granted < 0
                || reward.Economy.Balance != checked(state.Economy.Balance + reward.Granted))
                throw new InvalidOperationException("보상 결과의 식별자·잔액이 일치하지 않습니다.");
            var next = State;
            var receipt = new ConfirmedResult { Identity = GameProgress.Copy(id), Cleared = cleared, FirstClear = first,
                Granted = reward.Granted, PhotoId = cleared ? photoId : null };
            next.Economy = GameProgress.Copy(reward.Economy);
            next.Current.Cleared = cleared; next.Current.NeedsRevisit = !cleared;
            next.Settlements.Add(id.Key, receipt);
            if (cleared && !string.IsNullOrEmpty(photoId) && !next.Photos.Contains(photoId)) next.Photos.Add(photoId);
            next.FinalEndingUnlocked = next.ClearedCount == next.Patients.Count;
            next.PendingPresentation = receipt;
            next.PresentationStage = "clear";
            next.Phase = cleared ? ProgressPhase.Presenting : ProgressPhase.AwaitingDeparture;
            Commit(next); return GameProgress.Copy(receipt);
        }
        public void CompletePresentation(string settlementKey)
        {
            if (state.PendingPresentation?.Identity.Key != settlementKey) throw new InvalidOperationException("표시 회차가 다릅니다.");
            if (state.Phase != ProgressPhase.Presenting) return;
            var next = State; next.Phase = ProgressPhase.AwaitingDeparture;
            if (!next.PlayedEndings.Contains(next.Current.PatientId)) next.PlayedEndings.Add(next.Current.PatientId);
            Commit(next);
        }
        public void Depart()
        {
            if (state.Phase != ProgressPhase.AwaitingDeparture) throw new InvalidOperationException("평가·정산·표시 완료가 필요합니다.");
            var next = State;
            if (next.Current.Cleared) next.CurrentIndex++;
            next.PendingPresentation = null;
            next.Phase = next.Current == null ? ProgressPhase.Finished : ProgressPhase.Waiting; Commit(next);
        }
        public void CommitEconomy(EconomyState economy, int expectedRevision)
        {
            if (expectedRevision != state.Revision)
                throw new InvalidOperationException("경제 상태를 다시 읽은 뒤 시도하세요.");
            var next = State; next.Economy = GameProgress.Copy(economy); Commit(next);
        }
        public void CompleteFinalEnding()
        {
            if (!state.FinalEndingUnlocked) throw new InvalidOperationException("최종 엔딩이 잠겨 있습니다.");
            if (state.FinalEndingPlayed) return;
            var next = State; next.FinalEndingPlayed = true; Commit(next);
        }
        // No proposed 2/4 thresholds are installed by default.
        public string Certificate(IReadOnlyDictionary<int, string> approvedThresholds) => approvedThresholds == null ? ""
            : approvedThresholds.Where(p => p.Key <= state.ClearedCount).OrderByDescending(p => p.Key).Select(p => p.Value).FirstOrDefault() ?? "";
    }
}
