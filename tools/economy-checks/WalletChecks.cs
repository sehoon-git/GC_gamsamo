using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Gamsamo.Economy;

public static class WalletChecks
{
    // A disk-backed test double for B's whole-game snapshot. Never compiled into the game.
    public sealed class Game
    {
        public EconomyState economy;
        public long revision;
        public int patientScore;
        public bool cleared;
        public string nextPatient;
        public List<string> photos = new List<string>();
    }

    private sealed class Store : IEconomyStore
    {
        private readonly string file;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(Game));
        public bool fail, race, throwOnWrite;
        public Action beforeCommit;
        public int writes;

        public Store(string path) { file = path; }

        public Game Load()
        {
            using (var stream = File.OpenRead(file)) return (Game)serializer.Deserialize(stream);
        }

        public void Save(Game game)
        {
            using (var stream = File.Create(file)) serializer.Serialize(stream, game);
        }

        public bool TryReadEconomy(string runId, out EconomyState snapshot, out long revision)
        {
            Game game = Load();
            snapshot = game.economy;
            revision = game.revision;
            return snapshot.runId == runId;
        }

        public StoreWriteResult TryCommitShopChange(string runId, long revision, EconomyState proposed)
        {
            beforeCommit?.Invoke();
            Game game = Load();
            if (race)
            {
                // A different B operation advances the same whole-game save.
                game.photos.Add("other-photo");
                ++game.revision;
                Save(game);
                race = false;
            }
            if (game.revision != revision || game.economy.runId != runId) return StoreWriteResult.Conflict;
            if (throwOnWrite) throw new IOException("Test storage failure");
            if (fail) return StoreWriteResult.Failed;
            game.economy = proposed;
            ++game.revision;
            Save(game);
            ++writes;
            return StoreWriteResult.Committed;
        }
    }

    private sealed class Catalog : IEconomyCatalog
    {
        public bool TryGetItem(string id, out string category)
        {
            category = "door";
            return id == "door.oak" || id == "door.sage" || id == "door.white" || id == "door.pending";
        }

        public bool TryGetApprovedPrice(string id, out int price)
        {
            price = id == "door.white" ? 99 : 30;
            return TryGetItem(id, out _) && id != "door.pending";
        }
    }

    private static int count;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        ++count;
    }

    public static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected --exercise or --resume followed by a test save path.");
        if (args[0] == "--resume")
        {
            var wallet = new EconomyWallet("run-1", new Store(args[1]), new Catalog());
            Check(wallet.Reload() == EconomyStatus.Success, "Load on a fresh process");
            Check(wallet.Balance == 20 && wallet.TotalEarned == 80, "Balance and earned on restart");
            Check(wallet.Owns("door.oak") && wallet.Owns("door.sage") && wallet.GetEquipped("door") == "door.sage",
                "Inventory and last equipment on restart");
            Check(wallet.TryGetSettledReward("p1", "case1", "visit1", out int paid, out _) && paid == 80,
                "Paid receipt still available on restart");
            Console.WriteLine("Fresh-process restore checks passed: " + count);
        }
        else if (args[0] == "--exercise")
        {
            RewardChecks.Run();
            Exercise(args[1]);
            Console.WriteLine("Wallet checks passed: " + count);
        }
        else throw new ArgumentException("Unknown test mode.");
        return 0;
    }

    private static void Exercise(string file)
    {
        const string run = "run-1";
        var policy = RewardPolicy.ForRelease();
        var store = new Store(file);
        var catalog = new Catalog();
        var wallet = new EconomyWallet(run, store, catalog);
        Check(wallet.Reload() == EconomyStatus.Unavailable && !wallet.IsReady, "Missing save is unavailable");
        var fresh = EconomyState.NewRun(run);
        store.Save(new Game { economy = fresh, patientScore = 40, nextPatient = "p2" });
        Check(wallet.Reload() == EconomyStatus.Success && wallet.Balance == 0, "Load new run");
        var reward = RewardSettlement.Plan(fresh, policy, run, "p1", "case1", "visit1", 40, 40, false, true, true);
        Check(reward.ProposedState != null && wallet.Balance == 0, "Proposed award stays invisible");
        Check(!wallet.TryGetSettledReward("p1", "case1", "visit1", out _, out _), "Pending reward cannot be displayed");
        var game = store.Load();
        game.economy = reward.ProposedState;
        game.photos.Add("photo1");
        ++game.revision;
        store.Save(game);
        Check(wallet.Reload() == EconomyStatus.Success && wallet.Balance == 80 && wallet.TotalEarned == 80, "Publish only after B saves");
        Check(wallet.TryGetSettledReward("p1", "case1", "visit1", out int paidBase, out int bonus) && paidBase == 80 && bonus == 0,
            "D reads only a committed session receipt");
        Check(wallet.Purchase("missing") == EconomyStatus.InvalidItem, "Unknown ID");
        Check(wallet.Purchase("door.pending") == EconomyStatus.PricePending, "Unapproved price");
        Check(wallet.Purchase("door.oak", 25) == EconomyStatus.PriceChanged, "Reject price different from confirmed quote");
        Check(wallet.Purchase("door.white") == EconomyStatus.InsufficientFunds, "Insufficient balance");
        Check(wallet.Equip("door.oak") == EconomyStatus.NotOwned, "Only owned items can be equipped");
        Check(store.writes == 0, "Rejected operations never save");
        store.fail = true;
        Check(wallet.Purchase("door.oak") == EconomyStatus.SaveFailed, "Purchase failure");
        Check(wallet.Balance == 80 && !wallet.Owns("door.oak") && store.Load().economy.balance == 80, "Debit and purchase rolled back together");
        store.fail = false;
        store.race = true;
        Check(wallet.Purchase("door.oak") == EconomyStatus.Conflict && !wallet.Owns("door.oak"), "Stale revision is rejected");
        Check(store.Load().photos.Contains("other-photo") && wallet.Balance == 80, "Concurrent B save preserved");
        store.beforeCommit = () =>
        {
            Check(wallet.Balance == 80 && !wallet.Owns("door.oak"), "No provisional UI update");
            Check(wallet.Purchase("door.oak") == EconomyStatus.Busy, "Nested repeated click rejected");
        };
        Check(wallet.Purchase("door.oak") == EconomyStatus.Success, "Purchase committed");
        store.beforeCommit = null;
        Check(wallet.Balance == 50 && wallet.Owns("door.oak") && wallet.TotalEarned == 80 && wallet.GetEquipped("door") == null, "Acquire without auto-equip");
        Check(wallet.Purchase("door.oak") == EconomyStatus.AlreadyOwned && store.writes == 1, "No second debit");
        Check(wallet.Equip("door.oak") == EconomyStatus.Success && wallet.Equip("door.oak") == EconomyStatus.NoChange, "Equip once");
        Check(wallet.Purchase("door.sage") == EconomyStatus.Success && wallet.Equip("door.sage") == EconomyStatus.Success, "Replace equipped item in category");
        store.throwOnWrite = true;
        Check(wallet.Unequip("door") == EconomyStatus.SaveFailed && wallet.GetEquipped("door") == "door.sage", "Failed unequip keeps appearance");
        var restarted = new EconomyWallet(run, new Store(file), catalog);
        Check(restarted.Reload() == EconomyStatus.Success && restarted.Balance == 20 && restarted.TotalEarned == 80, "Reload persisted balance and earnings");
        Check(restarted.Owns("door.oak") && restarted.Owns("door.sage") && restarted.GetEquipped("door") == "door.sage", "Reload durable inventory and equipment");
        var restartCheckpoint = store.Load();
        Check(restarted.Unequip("door") == EconomyStatus.Success && restarted.Unequip("door") == EconomyStatus.NoChange, "Unequip is idempotent");
        Check(wallet.Reload() == EconomyStatus.Success && wallet.GetEquipped("door") == null && wallet.Owns("door.sage"), "Clear equipment but keep ownership");
        game = store.Load();
        Check(game.patientScore == 40 && !game.cleared && game.nextPatient == "p2" && game.photos.Count == 2, "Shop preserves scores, clear, patient and photos");
        Check(game.economy.settlements.Count == 1, "Shop preserves consultation history");
        Check(game.economy.purchases.Count == 2 && game.economy.purchases[0].price == 30 && game.economy.purchases[1].itemId == "door.sage", "Persist exact amounts paid and purchase history");
        var replay = RewardSettlement.Plan(game.economy, policy, run, "p1", "case1", "visit1", 40, 40, false, true, true);
        Check(replay.ProposedState == null && replay.BaseCoins == 0 && replay.BonusCoins == 0, "Replay after restart does not pay again");
        game.economy = EconomyState.NewRun("different-run");
        store.Save(game);
        Check(wallet.Purchase("door.oak") == EconomyStatus.Unavailable && !wallet.IsReady, "Stale wallet cannot change another run");
        store.Save(restartCheckpoint);
    }
}
