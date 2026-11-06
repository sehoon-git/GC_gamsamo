using System;
using System.Collections.Generic;

namespace Gamsamo.Economy
{
    [Serializable]
    public sealed class PurchaseReceipt
    {
        public string itemId;
        // Actual price paid at purchase. Later catalog changes never rewrite this receipt.
        public int price;
    }

    [Serializable]
    public sealed class EquippedItem
    {
        public string categoryId;
        public string itemId;
    }

    [Serializable]
    public sealed class SessionSettlement
    {
        // The containing economy slice supplies the fourth part of the key, runId.
        public string patientId;
        public string caseId;
        public string sessionId;
        public int patientScore;
        public int counselorScore;
        public bool cleared;
        public int baseThreshold;
        public int baseCoins;
        public int bonusCoins;
        public bool firstClear;

        internal SessionSettlement Copy() => (SessionSettlement)MemberwiseClone();
    }

    /// <summary>B serializes this slice with progression in the same game-save transaction.</summary>
    [Serializable]
    public sealed class EconomyState
    {
        public const int NewGameStartingCoins = 200;
        public string runId;
        // Missing in older saves means zero. Loading never awards a new starting grant.
        public int startingCoins;
        public int balance;
        public int totalEarned;
        public List<SessionSettlement> settlements = new List<SessionSettlement>();
        public List<PurchaseReceipt> purchases = new List<PurchaseReceipt>();
        public List<string> ownedItemIds = new List<string>();
        public List<EquippedItem> equippedItems = new List<EquippedItem>();

        public static EconomyState NewRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("A game run ID is required.", nameof(runId));
            return new EconomyState { runId = runId };
        }

        // Only the new-progress transaction calls this; legacy empty ledgers use NewRun.
        public static EconomyState NewGame(string runId)
        {
            var state = NewRun(runId);
            state.startingCoins = NewGameStartingCoins;
            state.balance = state.totalEarned = state.startingCoins;
            return state;
        }

        public EconomyState DeepCopy()
        {
            return new EconomyState
            {
                runId = runId,
                startingCoins = startingCoins,
                balance = balance,
                totalEarned = totalEarned,
                settlements = settlements.ConvertAll(record => record.Copy()),
                purchases = purchases.ConvertAll(record => new PurchaseReceipt { itemId = record.itemId, price = record.price }),
                ownedItemIds = new List<string>(ownedItemIds),
                equippedItems = equippedItems.ConvertAll(item => new EquippedItem { categoryId = item.categoryId, itemId = item.itemId })
            };
        }

        public SessionSettlement FindSettlement(string patientId, string caseId, string sessionId)
        {
            return settlements.Find(record => record.patientId == patientId && record.caseId == caseId && record.sessionId == sessionId);
        }

        public int HighestPaidBaseFor(string patientId)
        {
            int highest = 0;
            foreach (var record in settlements)
                if (record.patientId == patientId) highest = Math.Max(highest, record.baseThreshold);
            return highest;
        }

        public bool HasFirstClearBonusFor(string patientId) => settlements.Exists(record => record.patientId == patientId && record.firstClear);

        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(runId) || startingCoins < 0 || balance < 0 || balance > totalEarned ||
                settlements == null || purchases == null || ownedItemIds == null || equippedItems == null) return false;
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in ownedItemIds)
                if (string.IsNullOrWhiteSpace(item) || !owned.Add(item)) return false;
            var purchased = new HashSet<string>(StringComparer.Ordinal);
            long spent = 0;
            foreach (PurchaseReceipt receipt in purchases)
            {
                if (receipt == null || receipt.price <= 0 || !owned.Contains(receipt.itemId) || !purchased.Add(receipt.itemId)) return false;
                spent += receipt.price;
            }
            if (purchased.Count != owned.Count || totalEarned - spent != balance) return false;
            var categories = new HashSet<string>(StringComparer.Ordinal);
            var equipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (EquippedItem item in equippedItems)
                if (item == null || string.IsNullOrWhiteSpace(item.categoryId) || !owned.Contains(item.itemId) ||
                    !categories.Add(item.categoryId) || !equipped.Add(item.itemId)) return false;

            var keys = new HashSet<Tuple<string, string, string>>();
            var highest = new Dictionary<string, int>(StringComparer.Ordinal);
            var clearedPatients = new HashSet<string>(StringComparer.Ordinal);
            long earned = startingCoins;
            foreach (var record in settlements)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.patientId) || string.IsNullOrWhiteSpace(record.caseId) ||
                    string.IsNullOrWhiteSpace(record.sessionId) || !keys.Add(Tuple.Create(record.patientId, record.caseId, record.sessionId)) ||
                    !ScoreInRange(record.patientScore) || !ScoreInRange(record.counselorScore) || record.baseThreshold < 0 ||
                    record.bonusCoins < 0) return false;

                highest.TryGetValue(record.patientId, out int previous);
                bool firstClear = record.cleared && clearedPatients.Add(record.patientId);
                if (record.firstClear != firstClear || (!firstClear && record.bonusCoins != 0) ||
                    record.baseCoins != Math.Max(0, record.baseThreshold - previous)) return false;
                highest[record.patientId] = Math.Max(previous, record.baseThreshold);
                earned += (long)record.baseCoins + record.bonusCoins;
            }
            return earned == totalEarned;
        }

        // These are the two committed integer totals validated by AiProtocol.ValidateEvaluation.
        internal static bool ScoreInRange(int score) => score >= 0 && score <= 100;
    }
}
