using System;
using Gamsamo.Economy;

namespace Gamsamo.Progression
{
    // C owns the ledger and rules; B persists this slice in its whole-game transaction.
    public sealed class EconomyIntegration : IRewardCalculator, IEconomyStore
    {
        private readonly ProgressService progress;
        public EconomyIntegration(ProgressService progress) { this.progress = progress; }
        public static Gamsamo.Economy.EconomyState Ledger(EconomyState state, string run)
        {
            if (state.Ledger != null) return state.Ledger.DeepCopy();
            if (state.Balance != 0 || state.HighestRewards.Count != 0 || state.OwnedItems.Count != 0)
                throw new InvalidOperationException("이전 경제 저장의 명시적 이관이 필요합니다. 원본을 보존해 주세요.");
            return Gamsamo.Economy.EconomyState.NewRun(run);
        }
        public RewardCalculation Calculate(SavedEvaluation evaluation, EconomyState previous, bool firstClear)
        {
            var id = evaluation.Identity;
            var plan = RewardSettlement.Plan(Ledger(previous, id.ProgressId), RewardPolicy.ForRelease(),
                id.ProgressId, id.PatientId, id.CaseId, id.RoundId, evaluation.PatientScore, evaluation.CounselorScore,
                evaluation.PatientScore >= 70 && evaluation.CounselorScore >= 70, evaluation.AiSaveCompleted, true);
            if (plan.Status != RewardStatus.Proposed) throw new InvalidOperationException("보상 정산 확인 필요: " + plan.Status);
            previous.Ledger = plan.ProposedState;
            previous.Balance = plan.ProposedState.balance;
            return new RewardCalculation { SettlementKey = id.Key, Granted = plan.BaseCoins + plan.BonusCoins, Economy = previous };
        }
        public bool TryReadEconomy(string runId, out Gamsamo.Economy.EconomyState committed, out long revision)
        {
            committed = null; revision = -1;
            var state = progress.State;
            if (state == null || state.ProgressId != runId) return false;
            try { committed = Ledger(state.Economy, runId); revision = state.Revision; return committed.IsValid(); }
            catch { return false; }
        }
        public StoreWriteResult TryCommitShopChange(string runId, long expectedRevision, Gamsamo.Economy.EconomyState proposed)
        {
            var state = progress.State;
            if (state.ProgressId != runId || state.Revision != expectedRevision) return StoreWriteResult.Conflict;
            if (proposed == null || proposed.runId != runId || !proposed.IsValid()) return StoreWriteResult.Failed;
            try
            {
                state.Economy.Ledger = proposed.DeepCopy(); state.Economy.Balance = proposed.balance;
                progress.CommitEconomy(state.Economy, state.Revision);
                return StoreWriteResult.Committed;
            }
            catch { return StoreWriteResult.Failed; }
        }
    }
}
