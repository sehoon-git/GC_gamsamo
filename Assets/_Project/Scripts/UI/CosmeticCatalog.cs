using System;
using System.Collections.Generic;
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
        // An unset price is not a free item; this catalog does not store a numeric price.
        public bool hasPrice;
    }

    public sealed class CosmeticCatalog : ScriptableObject
    {
        public CosmeticItem[] items = Array.Empty<CosmeticItem>();

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
