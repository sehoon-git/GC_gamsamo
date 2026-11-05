using System;
using System.Collections.Generic;
using Gamsamo.UI;
using UnityEditor;
using UnityEngine;

namespace Gamsamo.Editor
{
    public static class CosmeticPriceChecks
    {
        // Read-only check. Deliberately does not rebuild the catalog, a prefab, or a scene.
        [MenuItem("Gamsamo/Check Cosmetic Coin Prices")]
        public static void Run()
        {
            CosmeticCatalog catalog = AssetDatabase.LoadAssetAtPath<CosmeticCatalog>(CosmeticCatalogImporter.CatalogPath);
            if (catalog == null || catalog.items == null || catalog.items.Length != 48)
                throw new InvalidOperationException("Expected the 48-item cosmetic catalog at " + CosmeticCatalogImporter.CatalogPath);
            string[] pending = Validate(catalog);
            CheckPriceLookup();
            if (pending.Length > 0)
                Debug.LogWarning(pending.Length + " cosmetic coin prices await approval: " + string.Join(", ", pending));
            else
                Debug.Log("All 48 cosmetic items have an approved coin price.");
        }

        // Missing or malformed rows are data errors. An explicit pending row is valid but not for sale.
        public static string[] Validate(CosmeticCatalog catalog)
        {
            var errors = new List<string>();
            var pending = new List<string>();
            var productIds = new HashSet<string>(StringComparer.Ordinal);
            var priceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CosmeticItem item in catalog.items ?? Array.Empty<CosmeticItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id) || string.IsNullOrWhiteSpace(item.target))
                    errors.Add("Missing product ID or category.");
                else if (!productIds.Add(item.id))
                    errors.Add("Duplicate product ID: " + item.id);
            }
            foreach (CosmeticCoinPrice row in catalog.coinPrices ?? Array.Empty<CosmeticCoinPrice>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.itemId) || !productIds.Contains(row.itemId))
                {
                    errors.Add("Coin price has an unknown product ID: " + row?.itemId);
                    continue;
                }
                if (!priceIds.Add(row.itemId))
                    errors.Add("Duplicate coin price: " + row.itemId);
                else if (!row.approved)
                    pending.Add(row.itemId);
                else if (!catalog.TryGetApprovedPrice(row.itemId, out _))
                    errors.Add("Approved coin price must identify one product and have a positive amount: " + row.itemId);
            }
            foreach (string id in productIds)
                if (!priceIds.Contains(id)) errors.Add("Missing coin price row: " + id);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            return pending.ToArray();
        }

        // Exercises explicit approval and ambiguous keys without changing the production asset.
        public static void CheckPriceLookup()
        {
            var catalog = ScriptableObject.CreateInstance<CosmeticCatalog>();
            try
            {
                var item = new CosmeticItem { id = "test.item", target = "test" };
                var row = new CosmeticCoinPrice { itemId = item.id, coins = 12 };
                catalog.items = new[] { item };
                catalog.coinPrices = new[] { row };
                Check(Validate(catalog).Length == 1 && !catalog.TryGetApprovedPrice(item.id, out _), "A draft price is not approved.");
                row.approved = true;
                Check(Validate(catalog).Length == 0 && catalog.TryGetApprovedPrice(item.id, out int amount) && amount == 12,
                    "Only an approved coin amount can be quoted.");
                catalog.coinPrices = Array.Empty<CosmeticCoinPrice>();
                CheckRejected(catalog, item.id, "Missing coin price row");
                catalog.coinPrices = new[] { row, row };
                CheckRejected(catalog, item.id, "Duplicate coin price");
                catalog.coinPrices = new[] { row };
                row.itemId = "test.unknown";
                CheckRejected(catalog, row.itemId, "unknown product ID");
                row.itemId = item.id;
                row.coins = 0;
                CheckRejected(catalog, item.id, "positive amount");
                row.coins = 12;
                catalog.items = new[] { item, item };
                Check(!catalog.TryGetItem(item.id, out _), "Duplicate product IDs cannot be used for inventory.");
                CheckRejected(catalog, item.id, "Duplicate product ID");
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); }
        }

        static void CheckRejected(CosmeticCatalog catalog, string id, string expectedError)
        {
            Check(!catalog.TryGetApprovedPrice(id, out _), "Unexpected approved price: " + id);
            try { Validate(catalog); }
            catch (InvalidOperationException exception) when (exception.Message.Contains(expectedError)) { return; }
            throw new InvalidOperationException("Expected price validation error: " + expectedError);
        }

        static void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
        }
    }
}
