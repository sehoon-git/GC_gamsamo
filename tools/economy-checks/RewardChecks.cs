using System;
using Gamsamo.Economy;

public static class RewardChecks
{
    private static int checks;
    // Test fixture only. Neither the weighting nor the 100C bonus is a production default.
    private static RewardPolicy Example() => new RewardPolicy { approved = true, coinsPerScorePoint = 1f, firstClearBonus = 100 };

    private static RewardPlan Eval(EconomyState state, string session, int patient, int counselor, bool cleared = false,
        string patientId = "patient", string caseId = "case")
    {
        return RewardSettlement.Plan(state, Example(), state.runId, patientId, caseId, session, patient, counselor, cleared, true, true);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Reward check: " + message);
        ++checks;
    }

    private static EconomyState Award(RewardPlan plan, int baseCoins, int bonus, int balance)
    {
        Check(plan.Status == RewardStatus.Proposed && plan.BaseCoins == baseCoins && plan.BonusCoins == bonus, "proposed award");
        Check(plan.ProposedState != null && plan.ProposedState.balance == balance && plan.ProposedState.IsValid(), "proposed slice");
        return plan.ProposedState;
    }

    private static void NoWrite(RewardPlan plan, RewardStatus status)
    {
        Check(plan.Status == status && plan.ProposedState == null && plan.BaseCoins == 0 && plan.BonusCoins == 0, status.ToString());
    }

    public static void Run()
    {
        checks = 0;
        var fresh = EconomyState.NewRun("run");
        var first = Award(Eval(fresh, "s1", 40, 40), 80, 0, 80);
        Check(fresh.balance == 0 && fresh.settlements.Count == 0, "proposal leaves committed state untouched");
        var second = Award(Eval(first, "s2", 55, 55), 30, 0, 110);
        var third = Award(Eval(second, "s3", 70, 70, true), 30, 100, 240);
        NoWrite(Eval(third, "s3", 70, 70, true), RewardStatus.AlreadySettled);
        NoWrite(Eval(third, "s3", 71, 70, true), RewardStatus.ConflictingEvaluation);
        var same = Award(Eval(third, "s4", 70, 70, true), 0, 0, 240);
        var down = Award(Eval(same, "s5", 62, 50), 0, 0, 240);
        Check(down.settlements.Count == 5 && down.HighestPaidBaseFor("patient") == 140 && down.HasFirstClearBonusFor("patient"), "patient history");

        // Same raw session ID in a new case is a distinct settlement; a patient's high-water mark spans cases.
        var newCase = Award(Eval(third, "s3", 71, 70, true, caseId: "case-2"), 1, 0, 241);
        var other = Award(Eval(newCase, "s3", 70, 70, true, patientId: "other"), 140, 100, 481);
        Check(other.HasFirstClearBonusFor("other") && other.HighestPaidBaseFor("other") == 140, "patients scoped independently");
        var anotherRun = Award(Eval(EconomyState.NewRun("run-2"), "s3", 70, 70, true), 140, 100, 240);
        Check(anotherRun.settlements.Count == 1, "fresh run has its own ledger");
        var high = Award(Eval(fresh, "high", 90, 60), 150, 0, 150);
        Award(Eval(high, "clear", 70, 70, true), 0, 100, 250);

        // A durable save is B's responsibility; dropping a proposal reproduces retry after failed save.
        Award(Eval(second, "s3", 70, 70, true), 30, 100, 240);
        Check(second.balance == 110 && !second.HasFirstClearBonusFor("patient"), "rejected write leaves first-clear available");
        NoWrite(RewardSettlement.Plan(fresh, Example(), "run", "p", "c", "s", 70, 70, true, false, true), RewardStatus.EvaluationPending);
        NoWrite(RewardSettlement.Plan(fresh, Example(), "run", "p", "c", "s", 70, 70, true, true, false), RewardStatus.ProgressionUnconfirmed);
        NoWrite(RewardSettlement.Plan(fresh, Example(), "wrong-run", "p", "c", "s", 70, 70, true, true, true), RewardStatus.InvalidEvaluation);
        NoWrite(Eval(fresh, " ", 0, 0), RewardStatus.InvalidEvaluation);
        NoWrite(Eval(fresh, "bad", -1, 0), RewardStatus.InvalidEvaluation);
        NoWrite(Eval(fresh, "bad", 100, 101), RewardStatus.InvalidEvaluation);
        NoWrite(RewardSettlement.Plan(fresh, new RewardPolicy(), "run", "p", "c", "s", 40, 40, false, true, true), RewardStatus.PolicyUnavailable);
        var policy = Example();
        policy.coinsPerScorePoint = 0.7f;
        Award(RewardSettlement.Plan(fresh, policy, "run", "p", "c", "s", 5, 5, false, true, true), 7, 0, 7);
        policy.coinsPerScorePoint = float.MaxValue;
        NoWrite(RewardSettlement.Plan(fresh, policy, "run", "p", "c", "s", 5, 5, false, true, true), RewardStatus.AmountOverflow);
        policy.coinsPerScorePoint = float.NaN;
        NoWrite(RewardSettlement.Plan(fresh, policy, "run", "p", "c", "s", 5, 5, false, true, true), RewardStatus.PolicyUnavailable);

        var damaged = third.DeepCopy();
        damaged.settlements.Add(damaged.settlements[0]);
        Check(!damaged.IsValid() && third.IsValid(), "duplicate stored ID rejected and clone isolated");
        damaged = third.DeepCopy();
        damaged.settlements[0].baseCoins++;
        NoWrite(Eval(damaged, "new", 85, 85, true), RewardStatus.InvalidState);
        // Restoration validates debit, ownership and the historical price as one purchase.
        var paid = third.DeepCopy();
        paid.ownedItemIds.Add("oak-door");
        paid.balance -= 9;
        Check(!paid.IsValid(), "incomplete purchase without receipt rejected");
        paid.purchases.Add(new PurchaseReceipt { itemId = "oak-door", price = 9 });
        paid.equippedItems.Add(new EquippedItem { categoryId = "door", itemId = "oak-door" });
        Check(paid.IsValid(), "confirmed debit agrees with ownership and receipt");
        var broken = paid.DeepCopy();
        broken.purchases[0].price++;
        Check(!broken.IsValid() && paid.IsValid(), "receipt and balance must match; clones do not alias receipts");
        broken = paid.DeepCopy();
        broken.purchases.Add(new PurchaseReceipt { itemId = "oak-door", price = 9 });
        broken.balance -= 9;
        Check(!broken.IsValid(), "duplicate purchase receipt rejected");
        broken = paid.DeepCopy();
        broken.ownedItemIds.Clear();
        Check(!broken.IsValid(), "debit without ownership rejected");
        broken = paid.DeepCopy();
        broken.purchases[0].price = 0;
        broken.balance = broken.totalEarned;
        Check(!broken.IsValid(), "unapproved zero-price receipt rejected");
        var afterShopping = Award(Eval(paid, "after-shopping", 71, 70, true), 1, 0, 232);
        Check(afterShopping.FindSettlement("patient", "case", "s3").cleared && afterShopping.totalEarned == 241,
            "buying only changes available coins; reward history survives");
        // Delimiter-containing IDs must never collapse different tuple components.
        var a = Award(Eval(fresh, "z", 1, 1, caseId: "x/y"), 2, 0, 2);
        Award(Eval(a, "y/z", 2, 2, caseId: "x"), 2, 0, 4);
        Console.WriteLine("Reward checks passed: " + checks);
    }
}
