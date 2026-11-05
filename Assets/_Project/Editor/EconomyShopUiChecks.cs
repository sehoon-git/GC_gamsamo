using System;
using Gamsamo.Economy;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gamsamo.Editor
{
    /// <summary>Play Mode test using temporary UI objects and an isolated, Editor-only save.</summary>
    public static class EconomyShopUiChecks
    {
        private const string Pending = "Gamsamo.EconomyShopUiChecks.pending";
        private const string First = "wallpaper.test-a", Second = "wallpaper.test-b";

        [InitializeOnLoadMethod]
        private static void Subscribe()
        {
            EditorApplication.playModeStateChanged -= OnModeChanged;
            EditorApplication.playModeStateChanged += OnModeChanged;
        }

        // Batch invocation: -batchmode -nographics -executeMethod Gamsamo.Editor.EconomyShopUiChecks.Run
        // Omit -quit: this check exits after Play Mode has started and the assertions finish.
        [MenuItem("Gamsamo/Run Economy Shop UI Checks")]
        public static void Run()
        {
            if (Application.isPlaying) { Execute(); return; }
            SessionState.SetBool(Pending, true);
            EditorApplication.isPlaying = true;
        }

        private static void OnModeChanged(PlayModeStateChange mode)
        {
            if (mode != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.EraseBool(Pending);
            EditorApplication.update -= ExecuteOnNextUpdate;
            EditorApplication.update += ExecuteOnNextUpdate;
        }

        private static void ExecuteOnNextUpdate()
        {
            EditorApplication.update -= ExecuteOnNextUpdate;
            int exitCode = 0;
            try { Execute(); }
            catch (Exception ex) { Debug.LogException(ex); exitCode = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(exitCode);
            else EditorApplication.isPlaying = false;
        }

        private static void Execute()
        {
            var root = new GameObject("EconomyShopUiChecks", typeof(RectTransform));
            root.SetActive(false);
            var catalog = ScriptableObject.CreateInstance<CosmeticCatalog>();
            Sprite original = MakeSprite(Texture2D.grayTexture);
            Sprite first = MakeSprite(Texture2D.whiteTexture);
            Sprite second = MakeSprite(Texture2D.blackTexture);
            try
            {
                catalog.items = new[] {
                    new CosmeticItem { id = First, target = "wallpaper", artwork = first },
                    new CosmeticItem { id = Second, target = "wallpaper", artwork = second }
                };
                catalog.coinPrices = new[] { new CosmeticCoinPrice { itemId = First, approved = true, coins = 3 } };
                var desktop = root.AddComponent<PcDesktopView>();
                desktop.wallpaper = Child("Wallpaper", root).AddComponent<Image>();
                desktop.defaultWallpaper = desktop.wallpaper.sprite = original;
                desktop.shopSprites = new[] { original, original, original, original, original, original };
                desktop.shopWindow = Child("Shop", root);
                desktop.shopWindow.SetActive(false);
                var appearance = root.AddComponent<CosmeticAppearanceController>();
                appearance.catalog = catalog;
                appearance.desktop = desktop;
                var shop = desktop.shopView = desktop.shopWindow.AddComponent<CosmeticShopView>();
                shop.catalog = catalog;
                shop.appearance = appearance;
                shop.restoreCategoryButton = Child("Footer", desktop.shopWindow).AddComponent<Button>();
                shop.itemButtons = new[] { Child("A", desktop.shopWindow).AddComponent<Button>(),
                    Child("B", desktop.shopWindow).AddComponent<Button>() };
                desktop.balanceText = Child("Balance", desktop.shopWindow).AddComponent<TextMeshProUGUI>();
                desktop.balanceText.enabled = false; // Validate the displayed value independently of the project's font atlas.
                var priceLabel = Child("Price", desktop.shopWindow).AddComponent<TextMeshProUGUI>();
                priceLabel.enabled = false;
                shop.priceLabels = new TMP_Text[] { priceLabel };
                root.SetActive(true);
                desktop.OpenShop();
                desktop.SelectTab(4);
                shop.itemButtons[0].onClick.Invoke();
                Require(priceLabel.text == "3 C" && !shop.ItemActionButton.interactable,
                    "approved price is visible before a wallet is connected");
                desktop.CloseShop();
                var store = new TestStore();
                var wallet = new EconomyWallet("ui-test", store, store);
                Require(wallet.Reload() == EconomyStatus.Success, "load committed wallet");
                desktop.BindEconomy(wallet);
                desktop.OpenShop();
                desktop.SelectTab(4);
                shop.itemButtons[1].onClick.Invoke();
                Require(desktop.wallpaper.sprite == second && !shop.ItemActionButton.interactable, "preview unpriced item");
                shop.restoreCategoryButton.onClick.Invoke();
                Require(desktop.wallpaper.sprite == original, "cancel restores original");

                shop.itemButtons[0].onClick.Invoke();
                shop.ItemActionButton.onClick.Invoke();
                Require(!wallet.Owns(First) && wallet.Balance == 8, "purchase requires confirmation");
                store.TestPrice = 4;
                shop.restoreCategoryButton.onClick.Invoke();
                Require(!wallet.Owns(First) && wallet.Balance == 8, "price changes require a new confirmation");
                store.TestPrice = 3;
                shop.itemButtons[0].onClick.Invoke();
                shop.ItemActionButton.onClick.Invoke();
                store.FailWrite = true;
                shop.restoreCategoryButton.onClick.Invoke();
                Require(!wallet.Owns(First) && wallet.Balance == 8 && desktop.wallpaper.sprite == original,
                    "failed save restores the artwork and leaves both money and ownership intact");
                store.FailWrite = false;
                shop.itemButtons[0].onClick.Invoke();
                shop.ItemActionButton.onClick.Invoke();
                shop.restoreCategoryButton.onClick.Invoke();
                shop.restoreCategoryButton.onClick.Invoke();
                Require(wallet.Owns(First) && wallet.Balance == 5 && store.Writes == 1 &&
                    desktop.balanceText.text == "5" && desktop.wallpaper.sprite == original,
                    "double confirmation charges once and buying does not equip");
                shop.itemButtons[0].onClick.Invoke();
                shop.ItemActionButton.onClick.Invoke();
                Require(wallet.GetEquipped("wallpaper") == First && desktop.wallpaper.sprite == first, "equip owned item");
                shop.itemButtons[1].onClick.Invoke();
                desktop.CloseShop();
                Require(desktop.wallpaper.sprite == first, "closing shop restores equipped wallpaper");
                var restored = new EconomyWallet("ui-test", store, store);
                Require(restored.Reload() == EconomyStatus.Success, "reload serialized snapshot");
                desktop.BindEconomy(restored);
                Require(desktop.TryGetWalletTotals(out int balance, out int earned) && balance == 5 && earned == 8 &&
                    restored.Owns(First) && desktop.wallpaper.sprite == first, "rebind restores balance, earnings and artwork");
                Require(desktop.TryGetSettledReward("test", "test", "test", out int baseCoins, out int bonusCoins)
                    && baseCoins == 8 && bonusCoins == 0 && !desktop.TryGetSettledReward("test", "test", "other", out _, out _),
                    "only a stored session has a receipt");
                desktop.OpenShop();
                desktop.SelectTab(4);
                shop.itemButtons[0].onClick.Invoke();
                shop.ItemActionButton.onClick.Invoke();
                Require(restored.GetEquipped("wallpaper") == null && desktop.wallpaper.sprite == original, "unequip");
                desktop.BindEconomy(null);
                Require(!desktop.TryGetWalletTotals(out _, out _) && desktop.balanceText.text == "—", "unbound display");
                Debug.Log("PASS EconomyShopUiChecks (preview, confirmation, failure, double-click, equip, restore, unequip)");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(original);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private static GameObject Child(string name, GameObject parent)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent.transform, false);
            return child;
        }

        private static Sprite MakeSprite(Texture2D texture) =>
            Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.one * 0.5f);

        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Economy UI check failed: " + description);
        }

        private sealed class TestStore : IEconomyStore, IEconomyCatalog
        {
            public bool FailWrite;
            public int TestPrice = 3;
            public int Writes;
            private string saved;
            public TestStore()
            {
                var state = EconomyState.NewRun("ui-test");
                state.balance = state.totalEarned = 8;
                state.settlements.Add(new SessionSettlement { patientId = "test", caseId = "test", sessionId = "test",
                    patientScore = 4, counselorScore = 4, baseThreshold = 8, baseCoins = 8 });
                saved = JsonUtility.ToJson(state);
            }
            public bool TryGetItem(string id, out string target) { target = "wallpaper"; return id == First || id == Second; }
            public bool TryGetApprovedPrice(string id, out int price) { price = TestPrice; return id == First; }
            public bool TryReadEconomy(string runId, out EconomyState state, out long revision)
            {
                state = JsonUtility.FromJson<EconomyState>(saved);
                revision = Writes;
                return state.runId == runId;
            }
            public StoreWriteResult TryCommitShopChange(string runId, long revision, EconomyState state)
            {
                if (FailWrite) return StoreWriteResult.Failed;
                if (revision != Writes || state.runId != runId) return StoreWriteResult.Conflict;
                saved = JsonUtility.ToJson(state);
                Writes++;
                return StoreWriteResult.Committed;
            }
        }
    }
}
