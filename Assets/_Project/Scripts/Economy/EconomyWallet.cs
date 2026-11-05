using System;
using System.Threading;

namespace Gamsamo.Economy
{
    public interface IEconomyCatalog
    {
        bool TryGetItem(string itemId, out string categoryId);
        bool TryGetApprovedPrice(string itemId, out int price);
    }

    public enum StoreWriteResult { Committed, Conflict, Failed }

    /// <summary>B implements this on its whole-game save. Read and compare-and-swap use the
    /// active run and the revision of the entire save, including non-economic changes.</summary>
    public interface IEconomyStore
    {
        bool TryReadEconomy(string runId, out EconomyState committed, out long revision);

        // Replace only the economy slice in the latest complete save. Return Committed only
        // after durable write; Conflict and Failed must leave the original save intact.
        StoreWriteResult TryCommitShopChange(string runId, long expectedRevision, EconomyState proposed);
    }

    public enum EconomyStatus
    {
        Success, Unavailable, InvalidState, InvalidItem, PricePending, InsufficientFunds,
        AlreadyOwned, NotOwned, NoChange, Busy, Conflict, SaveFailed, PriceChanged
    }

    /// <summary>One wallet per active run. B calls Reload after loading or committing
    /// a consultation. UI observes only snapshots that B has already committed.</summary>
    public sealed class EconomyWallet
    {
        private readonly string runId;
        private readonly IEconomyStore store;
        private readonly IEconomyCatalog catalog;
        private EconomyState confirmed;
        private int busy;

        public EconomyWallet(string runId, IEconomyStore store, IEconomyCatalog catalog)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("A run ID is required.", nameof(runId));
            this.runId = runId;
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public event Action Changed;
        public bool IsReady => confirmed != null;
        public int Balance => confirmed?.balance ?? 0;
        public int TotalEarned => confirmed?.totalEarned ?? 0;

        public bool Owns(string itemId) => itemId != null && confirmed != null && confirmed.ownedItemIds.Contains(itemId);

        public string GetEquipped(string categoryId)
        {
            if (categoryId == null || confirmed == null) return null;
            return confirmed.equippedItems.Find(item => item.categoryId == categoryId)?.itemId;
        }

        public bool TryGetSettledReward(string patientId, string caseId, string sessionId, out int baseCoins, out int bonusCoins)
        {
            var record = confirmed?.FindSettlement(patientId, caseId, sessionId);
            baseCoins = record?.baseCoins ?? 0;
            bonusCoins = record?.bonusCoins ?? 0;
            return record != null;
        }

        public bool TryGetApprovedPrice(string itemId, out int price)
        {
            price = 0;
            return !string.IsNullOrWhiteSpace(itemId) && catalog.TryGetApprovedPrice(itemId, out price) && price > 0;
        }

        // Invoke on the game/UI thread. Also blocks nested operations from Changed callbacks.
        public EconomyStatus Reload()
        {
            if (Interlocked.Exchange(ref busy, 1) != 0) return EconomyStatus.Busy;
            try
            {
                EconomyStatus status = Read(out EconomyState snapshot, out _);
                Publish(snapshot);
                return status;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        public EconomyStatus Purchase(string itemId)
        {
            return Purchase(itemId, null);
        }

        // A UI confirmation supplies the price the player saw. Reject a changed price.
        public EconomyStatus Purchase(string itemId, int? quotedPrice)
        {
            return Change(next =>
            {
                if (!catalog.TryGetItem(itemId, out _)) return EconomyStatus.InvalidItem;
                if (next.ownedItemIds.Contains(itemId)) return EconomyStatus.AlreadyOwned;
                if (!TryGetApprovedPrice(itemId, out int price)) return EconomyStatus.PricePending;
                if (quotedPrice.HasValue && price != quotedPrice.Value) return EconomyStatus.PriceChanged;
                if (next.balance < price) return EconomyStatus.InsufficientFunds;
                next.balance -= price;
                next.ownedItemIds.Add(itemId);
                next.purchases.Add(new PurchaseReceipt { itemId = itemId, price = price });
                return EconomyStatus.Success;
            });
        }

        public EconomyStatus Equip(string itemId)
        {
            return Change(next =>
            {
                if (!catalog.TryGetItem(itemId, out string category)) return EconomyStatus.InvalidItem;
                if (!next.ownedItemIds.Contains(itemId)) return EconomyStatus.NotOwned;
                var equipped = next.equippedItems.Find(item => item.categoryId == category);
                if (equipped != null && equipped.itemId == itemId) return EconomyStatus.NoChange;
                if (equipped == null) next.equippedItems.Add(new EquippedItem { categoryId = category, itemId = itemId });
                else equipped.itemId = itemId;
                return EconomyStatus.Success;
            });
        }

        public EconomyStatus Unequip(string categoryId)
        {
            return Change(next =>
            {
                return next.equippedItems.RemoveAll(item => item.categoryId == categoryId) == 0
                    ? EconomyStatus.NoChange : EconomyStatus.Success;
            });
        }

        private EconomyStatus Change(Func<EconomyState, EconomyStatus> apply)
        {
            if (Interlocked.Exchange(ref busy, 1) != 0) return EconomyStatus.Busy;
            try
            {
                EconomyStatus status = Read(out EconomyState previous, out long revision);
                if (status != EconomyStatus.Success)
                {
                    Publish(null);
                    return status;
                }
                EconomyState next = previous.DeepCopy();
                status = apply(next);
                if (status != EconomyStatus.Success)
                {
                    Publish(previous);
                    return status;
                }

                StoreWriteResult write;
                try { write = store.TryCommitShopChange(runId, revision, next.DeepCopy()); }
                catch (Exception) { write = StoreWriteResult.Failed; }
                if (write == StoreWriteResult.Committed)
                {
                    Publish(next);
                    return EconomyStatus.Success;
                }

                // A different writer may have committed (including a new run) or an
                // acknowledgement may have been lost. Show only a fresh, verified read.
                Read(out EconomyState durable, out _);
                Publish(durable);
                return write == StoreWriteResult.Conflict ? EconomyStatus.Conflict : EconomyStatus.SaveFailed;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        private EconomyStatus Read(out EconomyState snapshot, out long revision)
        {
            snapshot = null;
            revision = -1;
            EconomyState state;
            try
            {
                if (!store.TryReadEconomy(runId, out state, out revision)) return EconomyStatus.Unavailable;
            }
            catch (Exception) { return EconomyStatus.Unavailable; }
            if (state == null || revision < 0 || state.runId != runId || !state.IsValid()) return EconomyStatus.InvalidState;
            foreach (var slot in state.equippedItems)
                if (catalog.TryGetItem(slot.itemId, out string category) && category != slot.categoryId)
                    return EconomyStatus.InvalidState;
            snapshot = state.DeepCopy();
            return EconomyStatus.Success;
        }

        private void Publish(EconomyState snapshot)
        {
            confirmed = snapshot;
            Changed?.Invoke();
        }
    }
}
