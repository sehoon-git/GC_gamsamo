using System;

namespace Gamsamo.Economy
{
    /// <summary>No payable default. B supplies the approved game configuration when available.</summary>
    [Serializable]
    public sealed class RewardPolicy
    {
        public bool approved;
        public float coinsPerScorePoint;
        public int firstClearBonus;

        internal bool CanCalculate =>
            approved && !float.IsNaN(coinsPerScorePoint) && !float.IsInfinity(coinsPerScorePoint) &&
            coinsPerScorePoint >= 0 && firstClearBonus >= 0;
    }

    public enum RewardStatus
    {
        Proposed,
        AlreadySettled,
        EvaluationPending,
        ProgressionUnconfirmed,
        InvalidState,
        InvalidEvaluation,
        ConflictingEvaluation,
        PolicyUnavailable,
        AmountOverflow
    }

    /// <summary>Only a proposal. D reads from the state B returns after durable commit.</summary>
    public sealed class RewardPlan
    {
        public RewardStatus Status { get; }
        public EconomyState ProposedState { get; }
        public int BaseCoins { get; }
        public int BonusCoins { get; }

        internal RewardPlan(RewardStatus status, EconomyState proposed = null, int baseCoins = 0, int bonusCoins = 0)
        {
            Status = status;
            ProposedState = proposed;
            BaseCoins = baseCoins;
            BonusCoins = bonusCoins;
        }
    }

    public static class RewardSettlement
    {
        /// <summary>
        /// Called by B with A's validated, saved integer scores and clear decision after B has matched
        /// the active run, patient, case and session. Never derives a clear decision or writes a save.
        /// </summary>
        public static RewardPlan Plan(EconomyState committed, RewardPolicy policy, string runId, string patientId,
            string caseId, string sessionId, int patientScore, int counselorScore, bool cleared,
            bool aiEvaluationSaved, bool progressionConfirmed)
        {
            if (!aiEvaluationSaved) return new RewardPlan(RewardStatus.EvaluationPending);
            if (!progressionConfirmed) return new RewardPlan(RewardStatus.ProgressionUnconfirmed);
            if (committed == null || !committed.IsValid()) return new RewardPlan(RewardStatus.InvalidState);
            if (runId != committed.runId || string.IsNullOrWhiteSpace(patientId) || string.IsNullOrWhiteSpace(caseId) ||
                string.IsNullOrWhiteSpace(sessionId) || !EconomyState.ScoreInRange(patientScore) || !EconomyState.ScoreInRange(counselorScore))
                return new RewardPlan(RewardStatus.InvalidEvaluation);

            SessionSettlement prior = committed.FindSettlement(patientId, caseId, sessionId);
            if (prior != null)
            {
                bool matches = prior.patientScore == patientScore && prior.counselorScore == counselorScore && prior.cleared == cleared;
                return new RewardPlan(matches ? RewardStatus.AlreadySettled : RewardStatus.ConflictingEvaluation);
            }

            if (policy == null || !policy.CanCalculate) return new RewardPlan(RewardStatus.PolicyUnavailable);
            // Designers enter decimal coin rates. Keep binary float approximation out of floor(10 × 0.7).
            int combined = patientScore + counselorScore;
            if (combined > 0 && (double)policy.coinsPerScorePoint > int.MaxValue) return new RewardPlan(RewardStatus.AmountOverflow);
            decimal threshold = combined == 0 ? 0 : decimal.Floor(combined * (decimal)policy.coinsPerScorePoint);
            if (threshold > int.MaxValue) return new RewardPlan(RewardStatus.AmountOverflow);
            int baseCoins = Math.Max(0, (int)threshold - committed.HighestPaidBaseFor(patientId));
            bool firstClear = cleared && !committed.HasFirstClearBonusFor(patientId);
            int bonus = firstClear ? policy.firstClearBonus : 0;
            long gain = (long)baseCoins + bonus;
            if (committed.totalEarned + gain > int.MaxValue) return new RewardPlan(RewardStatus.AmountOverflow);

            var proposal = committed.DeepCopy();
            proposal.settlements.Add(new SessionSettlement
            {
                patientId = patientId, caseId = caseId, sessionId = sessionId, patientScore = patientScore,
                counselorScore = counselorScore, cleared = cleared, baseThreshold = (int)threshold,
                firstClear = firstClear, baseCoins = baseCoins, bonusCoins = bonus
            });
            proposal.balance += (int)gain;
            proposal.totalEarned += (int)gain;
            return new RewardPlan(RewardStatus.Proposed, proposal, baseCoins, bonus);
        }
    }
}
