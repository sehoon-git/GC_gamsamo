using System;
using System.Collections.Generic;
using Gamsamo.Economy;
using UnityEngine;

namespace Gamsamo.UI
{
    [Serializable]
    public sealed class CosmeticItem
    {
        public string id;
        public string target;
        public string theme;
        public string displayName;
        public string cardLabel;
        public Sprite thumbnail;
        public Sprite artwork;
        public Sprite detailArtwork;
        // Base window artwork: clear, cloudy, rain.
        public Sprite[] weatherSprites = Array.Empty<Sprite>();
        // Composed windows: up(clear, cloudy, rain), half(...), down(...).
        public Sprite[] blindSprites = Array.Empty<Sprite>();
        // Separate overlays: up, half, down. Do not layer these over composed blindSprites.
        public Sprite[] blindOverlays = Array.Empty<Sprite>();
        // Legacy art-source (KRW) metadata. Purchase code reads the approved coin table only.
        public bool hasPrice;
    }

    [Serializable]
    public sealed class CosmeticCoinPrice
    {
        public string itemId;
        public bool approved;
        public int coins = -1;
    }

    public sealed class CosmeticCatalog : ScriptableObject, IEconomyCatalog
    {
        public CosmeticItem[] items = Array.Empty<CosmeticItem>();
        // Maintained separately from imported artwork. Reimporting items preserves this table.
        public CosmeticCoinPrice[] coinPrices = Array.Empty<CosmeticCoinPrice>();

        public bool TryGetItem(string itemId, out string categoryId)
        {
            categoryId = null;
            if (string.IsNullOrWhiteSpace(itemId) || items == null) return false;
            CosmeticItem found = null;
            foreach (CosmeticItem item in items)
            {
                if (item == null || item.id != itemId) continue;
                if (found != null) return false;
                found = item;
            }
            if (found == null || string.IsNullOrWhiteSpace(found.target)) return false;
            categoryId = found.target;
            return true;
        }

        public bool TryGetApprovedPrice(string itemId, out int price)
        {
            price = 0;
            if (coinPrices == null || !TryGetItem(itemId, out _)) return false;
            CosmeticCoinPrice found = null;
            foreach (CosmeticCoinPrice row in coinPrices)
            {
                if (row == null || row.itemId != itemId) continue;
                if (found != null) return false;
                found = row;
            }
            if (found == null || !found.approved || found.coins <= 0) return false;
            price = found.coins;
            return true;
        }

        public CosmeticItem Find(string id)
        {
            if (string.IsNullOrEmpty(id) || items == null) return null;
            foreach (CosmeticItem item in items)
                if (item != null && string.Equals(item.id, id, StringComparison.Ordinal)) return item;
            return null;
        }

        /// <summary>Returns the source catalog order for one category without changing the shared catalog.</summary>
        public CosmeticItem[] GetItems(string target)
        {
            if (string.IsNullOrEmpty(target) || items == null) return Array.Empty<CosmeticItem>();
            var result = new List<CosmeticItem>();
            foreach (CosmeticItem item in items)
                if (item != null && string.Equals(item.target, target, StringComparison.Ordinal)) result.Add(item);
            return result.ToArray();
        }
    }
}
