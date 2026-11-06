using System;
using System.IO;
using Gamsamo.Economy;
using Gamsamo.Progression;
using Gamsamo.Save;
using Newtonsoft.Json.Linq;
using Ledger = Gamsamo.Economy.EconomyState;

static class StartingFundsChecks
{
    static readonly string[] Roster = { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" };
    static int checks;
    static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS starting coins: " + name);
    }
    sealed class Catalog : IEconomyCatalog
    {
        public bool TryGetItem(string item, out string category)
        { category = "window"; return item == "window.oak"; }
        public bool TryGetApprovedPrice(string item, out int price)
        { price = 50; return item == "window.oak"; }
    }
    static EconomyWallet Wallet(ProgressService service)
    {
        var wallet = new EconomyWallet(service.State.ProgressId, new EconomyIntegration(service), new Catalog());
        Check(wallet.Reload() == EconomyStatus.Success, "wallet reads valid saved ledger");
        return wallet;
    }
    public static void Restore(string path)
    {
        var service = new ProgressService(new GameProgressStore(path));
        var wallet = Wallet(service);
        Check(wallet.Balance == 390 && wallet.TotalEarned == 440, "restore preserves grant, purchase and 240 reward");
        Check(wallet.Owns("window.oak") && wallet.GetEquipped("window") == "window.oak", "restore preserves ownership and equipment");
        Check(service.State.Economy.Ledger.startingCoins == 200 && service.State.Economy.Ledger.settlements.Count == 1,
            "starting grant is separate from consultation rewards");
    }
    public static void Run(string root)
    {
        checks = 0;
        string path = Path.Combine(root, "starting-coins.json");
        var store = new GameProgressStore(path);
        var service = new ProgressService(store);
        service.Create(Roster);
        Check(service.State.Economy.Balance == 200 && store.Load().Economy.Ledger.startingCoins == 200,
            "new game durably starts with 200");
        Check(service.State.Settlements.Count == 0 && service.State.Economy.Ledger.settlements.Count == 0,
            "grant does not create a fake consultation");
        var wallet = Wallet(service);
        store.BeforeReplace = () => { throw new IOException("test purchase write failure"); };
        Check(wallet.Purchase("window.oak", 50) == EconomyStatus.SaveFailed && wallet.Balance == 200
            && !wallet.Owns("window.oak") && store.Load().Economy.Balance == 200, "failed purchase preserves starting funds");
        store.BeforeReplace = null;
        Check(wallet.Purchase("window.oak", 50) == EconomyStatus.Success && wallet.Balance == 150,
            "customization purchase works before any consultation");
        Check(wallet.Equip("window.oak") == EconomyStatus.Success, "initial purchase can be equipped");
        service = new ProgressService(store);
        wallet = Wallet(service);
        Check(wallet.Balance == 150 && wallet.GetEquipped("window") == "window.oak", "reload does not refill starting funds");
        Check(wallet.Purchase("window.oak", 50) == EconomyStatus.AlreadyOwned && wallet.Balance == 150,
            "duplicate purchase does not debit again");
        service.BeginVisit();
        service.CaptureAi("seoyun", "test-case", "test-round", "test-only", "evaluated");
        var evaluation = new SavedEvaluation {
            Identity = new RoundIdentity { ProgressId = service.State.ProgressId, PatientId = "seoyun", CaseId = "test-case", RoundId = "test-round" },
            AiSaveCompleted = true, Phase = "evaluated", PatientScore = 70, CounselorScore = 70
        };
        var bridge = new EconomyIntegration(service);
        var result = service.Settle(evaluation, bridge, "seoyun_smile");
        service.Settle(evaluation, bridge, "seoyun_smile");
        Check(result.Granted == 240 && service.State.Economy.Balance == 390, "existing reward policy and duplicate protection remain");
        Restore(path);
        string prior = File.ReadAllText(path);
        var second = new ProgressService(new GameProgressStore(Path.Combine(root, "another-start.json")));
        second.Create(Roster);
        Check(second.State.ProgressId != service.State.ProgressId && second.State.Economy.Balance == 200
            && File.ReadAllText(path) == prior, "another new game has its own 200 and preserves old run");

        // Genuine pre-grant shapes: missing field, existing zero/earned ledger, and no ledger.
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var legacy = second.State;
            legacy.Economy.Ledger = kind == 2 ? null : Ledger.NewRun(legacy.ProgressId);
            legacy.Economy.Balance = 0;
            if (kind == 1)
            {
                var ledger = RewardSettlement.Plan(legacy.Economy.Ledger, RewardPolicy.ForRelease(), legacy.ProgressId,
                    "seoyun", "old-case", "old-round", 40, 40, false, true, true).ProposedState;
                ledger.balance -= 50;
                ledger.ownedItemIds.Add("window.oak");
                ledger.purchases.Add(new PurchaseReceipt { itemId = "window.oak", price = 50 });
                legacy.Economy.Ledger = ledger;
                legacy.Economy.Balance = 30;
            }
            var json = JObject.FromObject(legacy);
            (json["Economy"]["Ledger"] as JObject)?.Property("startingCoins")?.Remove();
            string oldPath = Path.Combine(root, "legacy-" + kind + ".json");
            File.WriteAllText(oldPath, json.ToString());
            string bytes = File.ReadAllText(oldPath);
            var old = new ProgressService(new GameProgressStore(oldPath));
            var oldWallet = Wallet(old);
            Check(oldWallet.Balance == (kind == 1 ? 30 : 0) && oldWallet.TotalEarned == (kind == 1 ? 80 : 0)
                && File.ReadAllText(oldPath) == bytes, "legacy variant " + kind + " is not topped up or rewritten");
        }
        var broken = service.State.Economy.Ledger.DeepCopy();
        broken.startingCoins = -1;
        Check(!broken.IsValid(), "negative grant rejected");
        broken = service.State.Economy.Ledger.DeepCopy();
        broken.startingCoins = 0;
        Check(!broken.IsValid(), "unaccounted 200 rejected");
        string failedPath = Path.Combine(root, "failed-start.json");
        var failedStore = new GameProgressStore(failedPath) { BeforeReplace = () => { throw new IOException("test create failure"); } };
        var failed = new ProgressService(failedStore);
        bool rejected = false;
        try { failed.Create(Roster); } catch (IOException) { rejected = true; }
        Check(rejected && failed.State == null && !File.Exists(failedPath), "failed create publishes neither state nor funds");
        failedStore.BeforeReplace = null;
        failed.Create(Roster);
        Check(failed.State.Economy.Balance == 200, "retry after failed create grants only once");
        Console.WriteLine("PASS " + checks + " starting coin checks");
        Console.WriteLine("STARTING_COINS_FIXTURE " + path);
    }
}
