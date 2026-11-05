using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gamsamo.UI;
using UnityEditor;
using UnityEngine;

namespace Gamsamo.Editor
{
    /// <summary>Validates the supplied catalog and builds references without inventing prices or ownership.</summary>
    public static class CosmeticCatalogImporter
    {
        public const string CatalogPath = "Assets/_Project/Settings/CosmeticCatalog.asset";
        const string ArtRoot = "Assets/_Project/Art/UI/CounselingPack/cosmetics/";
        const string ShopRoot = "Assets/_Project/Art/UI/CosmeticShop/";
        static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper" };
        static readonly string[] Themes = { "oak", "walnut", "sage", "lavender", "terracotta", "midnight", "brass", "white" };

        public static CosmeticCatalog Build()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Rebuild the cosmetic catalog outside Play mode.");
            string json = File.ReadAllText(ArtRoot + "catalog.json");
            CatalogData data = JsonUtility.FromJson<CatalogData>(json);
            ValidateCatalog(data, json);
            ValidateBlindManifest(data);
            ValidateShopLayout(data);

            // Complete all source validation before changing import settings or the existing asset.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string path in Directory.GetFiles(ArtRoot, "*.png", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(ShopRoot, "*.png", SearchOption.AllDirectories)))
                ImportSprite(path.Replace('\\', '/'));

            CosmeticItem[] items = data.items.Select(CreateItem).ToArray();
            CosmeticCatalog catalog = AssetDatabase.LoadAssetAtPath<CosmeticCatalog>(CatalogPath);
            if (catalog == null)
            {
                if (File.Exists(CatalogPath))
                    throw new InvalidOperationException("An unreadable or different asset already exists at " + CatalogPath);
                if (!AssetDatabase.IsValidFolder("Assets/_Project/Settings"))
                    AssetDatabase.CreateFolder("Assets/_Project", "Settings");
                catalog = ScriptableObject.CreateInstance<CosmeticCatalog>();
                catalog.items = items;
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            else
            {
                catalog.items = items;
                EditorUtility.SetDirty(catalog);
            }
            AssetDatabase.SaveAssetIfDirty(catalog);
            return catalog;
        }

        static void ValidateCatalog(CatalogData data, string json)
        {
            Require(data != null && data.items != null && data.items.Length == 40, "Catalog must contain exactly 40 items.");
            Require(data.currency == "KRW", "Keep the supplied KRW currency; no conversion is defined.");
            Require(data.categories != null && data.categories.Select(entry => entry.id).OrderBy(id => id)
                .SequenceEqual(Targets.OrderBy(id => id)), "Catalog categories must match the five supported targets.");
            Require(data.themes != null && data.themes.Select(entry => entry.id).OrderBy(id => id)
                .SequenceEqual(Themes.OrderBy(id => id)), "Catalog must contain the eight supplied themes.");
            ValidateExplicitNullPrices(json);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemData item in data.items)
            {
                Require(item != null && !string.IsNullOrEmpty(item.id) && ids.Add(item.id), "Every cosmetic ID must be present and unique.");
                Require(Targets.Contains(item.category) && Themes.Contains(item.theme), "Unknown category or theme for " + item.id);
                Require(item.id == item.category + "." + item.theme, "Category/theme ID mismatch for " + item.id);
                Require(!string.IsNullOrWhiteSpace(item.name_ko) && !string.IsNullOrWhiteSpace(item.card_label), "Missing display name for " + item.id);
                Require(item.images != null, "Missing image paths for " + item.id);
                ResolvePng(item.thumbnail);
                if (item.category == "window")
                {
                    Require(item.blinds != null && item.blinds.shop_preview_state == "half"
                        && item.blinds.closed_blocks_weather, "Missing window blind configuration for " + item.id);
                    ValidateWeather(item.blinds.@base, item.id + " base");
                    ValidateStates(item.blinds.states, item.id);
                    Require(item.blinds.overlays != null, "Missing blind overlays for " + item.id);
                    ResolvePng(item.blinds.overlays.up);
                    ResolvePng(item.blinds.overlays.half);
                    ResolvePng(item.blinds.overlays.down);
                    Require(item.images.clear == item.blinds.states.half.clear
                        && item.images.cloudy == item.blinds.states.half.cloudy
                        && item.images.rain == item.blinds.states.half.rain, "Window preview must use the half-down states for " + item.id);
                }
                else if (item.category == "wallpaper") ResolvePng(item.images.wallpaper);
                else
                {
                    ResolvePng(item.images.sprite);
                    if (item.category == "clipboard") ResolvePng(item.images.detail);
                }
            }
            foreach (string target in Targets)
                Require(data.items.Count(item => item.category == target) == 8, "Each category must have eight items: " + target);
        }

        static void ValidateBlindManifest(CatalogData catalog)
        {
            var manifest = JsonUtility.FromJson<BlindManifest>(File.ReadAllText(ArtRoot + "window/blind-states.json"));
            Require(manifest?.themes?.Length == 8 && manifest.shop_preview_state == "half", "Blind manifest must contain eight themes.");
            Require(manifest.weather != null && manifest.weather.SequenceEqual(new[] { "clear", "cloudy", "rain" }), "Unexpected blind weather order.");
            var seen = new HashSet<string>();
            foreach (BlindTheme theme in manifest.themes)
            {
                Require(theme != null && seen.Add(theme.id), "Duplicate or missing blind manifest theme.");
                ItemData item = catalog.items.SingleOrDefault(candidate => candidate.id == theme.product_id);
                Require(item != null && item.category == "window" && item.theme == theme.id, "Blind manifest product mismatch.");
                Require(theme.@base != null && theme.overlay != null && theme.composed != null, "Incomplete blind manifest for " + theme.id);
                MatchPaths(item.blinds.@base, theme.@base, theme.id);
                Require(item.blinds.overlays.up == "window/" + theme.overlay.up
                    && item.blinds.overlays.half == "window/" + theme.overlay.half
                    && item.blinds.overlays.down == "window/" + theme.overlay.down, "Blind overlay manifest mismatch for " + theme.id);
                MatchPaths(item.blinds.states.up, theme.composed.up, theme.id);
                MatchPaths(item.blinds.states.half, theme.composed.half, theme.id);
                MatchPaths(item.blinds.states.down, theme.composed.down, theme.id);
            }
        }

        static void MatchPaths(WeatherPaths catalog, WeatherPaths manifest, string label)
        {
            Require(catalog != null && manifest != null && catalog.clear == "window/" + manifest.clear
                && catalog.cloudy == "window/" + manifest.cloudy && catalog.rain == "window/" + manifest.rain,
                "Catalog and blind manifest disagree for " + label);
        }

        static void ValidateShopLayout(CatalogData catalog)
        {
            var shop = JsonUtility.FromJson<ShopLayout>(File.ReadAllText(ShopRoot + "layout.json"));
            Require(shop?.tabs?.Length == 5, "Shop layout must contain five tabs.");
            Require(Path.GetFullPath(Path.Combine(ShopRoot, shop.catalog ?? ""))
                == Path.GetFullPath(ArtRoot + "catalog.json"), "Shop catalog reference does not point to the imported catalog.");
            var seen = new HashSet<string>();
            foreach (ShopTab tab in shop.tabs)
            {
                Require(tab != null && Targets.Contains(tab.id) && seen.Add(tab.id), "Unexpected or repeated shop tab.");
                string[] expected = catalog.items.Where(item => item.category == tab.id).Select(item => item.id).ToArray();
                Require(tab.products != null && tab.products.SequenceEqual(expected), "Shop product order differs from catalog for " + tab.id);
                ResolvePng(tab.active_window_png, ShopRoot);
                ResolvePng(tab.empty_window_png, ShopRoot);
            }
        }

        static void ValidateStates(BlindStates states, string label)
        {
            Require(states != null, "Missing nine blind states for " + label);
            ValidateWeather(states.up, label + " up");
            ValidateWeather(states.half, label + " half");
            ValidateWeather(states.down, label + " down");
        }

        static void ValidateWeather(WeatherPaths paths, string label)
        {
            Require(paths != null, "Missing weather paths for " + label);
            ResolvePng(paths.clear);
            ResolvePng(paths.cloudy);
            ResolvePng(paths.rain);
        }

        // JsonUtility cannot represent nullable numeric fields. Inspect JSON tokens instead of
        // deserializing null to a numeric default and accidentally treating an unset price as free.
        static void ValidateExplicitNullPrices(string json)
        {
            const string pattern = @"""(?:\\.|[^""\\])*""|true|false|null|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|[{}\[\]:,]";
            string[] tokens = Regex.Matches(json, pattern).Cast<Match>().Select(match => match.Value).ToArray();
            int start = -1;
            for (int i = 0; i + 2 < tokens.Length; i++)
                if (tokens[i] == "\"items\"" && tokens[i + 1] == ":" && tokens[i + 2] == "[") { start = i + 3; break; }
            Require(start >= 0, "Missing catalog items array.");
            int depth = 0, arrays = 1, prices = 0, items = 0;
            for (int i = start; i < tokens.Length && arrays > 0; i++)
            {
                string token = tokens[i];
                if (token == "[") arrays++;
                else if (token == "]") arrays--;
                else if (token == "{") { depth++; if (depth == 1) prices = 0; }
                else if (token == "}")
                {
                    if (depth == 1) { Require(prices == 1, "Each item must explicitly contain one null price_krw."); items++; }
                    depth--;
                }
                else if (depth == 1 && token == "\"price_krw\"" && i + 2 < tokens.Length && tokens[i + 1] == ":")
                {
                    Require(tokens[i + 2] == "null", "Item prices are not set. Do not import a numeric price or convert currency implicitly.");
                    prices++;
                }
            }
            Require(items == 40, "Exactly 40 explicit null prices are required.");
        }

        static CosmeticItem CreateItem(ItemData source)
        {
            var item = new CosmeticItem
            {
                id = source.id, target = source.category, theme = source.theme,
                displayName = source.name_ko, cardLabel = source.card_label,
                thumbnail = Sprite(source.thumbnail), hasPrice = false
            };
            if (source.category == "window")
            {
                item.artwork = Sprite(source.blinds.states.half.clear);
                item.weatherSprites = WeatherSprites(source.blinds.@base);
                item.blindSprites = WeatherSprites(source.blinds.states.up)
                    .Concat(WeatherSprites(source.blinds.states.half)).Concat(WeatherSprites(source.blinds.states.down)).ToArray();
                item.blindOverlays = new[] { Sprite(source.blinds.overlays.up), Sprite(source.blinds.overlays.half), Sprite(source.blinds.overlays.down) };
            }
            else if (source.category == "wallpaper") item.artwork = Sprite(source.images.wallpaper);
            else
            {
                item.artwork = Sprite(source.images.sprite);
                if (source.category == "clipboard") item.detailArtwork = Sprite(source.images.detail);
            }
            return item;
        }

        static Sprite[] WeatherSprites(WeatherPaths paths) => new[] { Sprite(paths.clear), Sprite(paths.cloudy), Sprite(paths.rain) };

        static Sprite Sprite(string relative)
        {
            string path = ResolvePng(relative);
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("Sprite did not import: " + path);
            return sprite;
        }

        static string ResolvePng(string relative, string root = ArtRoot)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "Missing or rooted image path.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(root, relative));
            Require(fullPath.StartsWith(fullRoot, StringComparison.Ordinal) && Path.GetExtension(fullPath) == ".png"
                && File.Exists(fullPath), "Missing or out-of-folder PNG: " + relative);
            return (root + relative).Replace('\\', '/');
        }

        static void ImportSprite(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Texture importer is missing: " + path);
            bool changed = importer.textureType != TextureImporterType.Sprite
                || importer.spriteImportMode != SpriteImportMode.Single || importer.spritePixelsPerUnit != 100
                || !importer.alphaIsTransparency || importer.mipmapEnabled || importer.npotScale != TextureImporterNPOTScale.None
                || importer.wrapMode != TextureWrapMode.Clamp || importer.filterMode != FilterMode.Bilinear
                || importer.maxTextureSize != 2048 || importer.textureCompression != TextureImporterCompression.Uncompressed;
            if (!changed) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

#pragma warning disable CS0649 // Fields are populated from the supplied JSON, not from C# assignments.
        [Serializable] sealed class NamedEntry { public string id; }
        [Serializable] sealed class CatalogData
        {
            public string currency;
            public NamedEntry[] themes, categories;
            public ItemData[] items;
        }
        [Serializable] sealed class ItemData
        {
            public string id, theme, category, name_ko, card_label, thumbnail;
            public ImagePaths images;
            public BlindData blinds;
        }
        [Serializable] class WeatherPaths { public string clear, cloudy, rain; }
        [Serializable] sealed class ImagePaths { public string clear, cloudy, rain, sprite, detail, wallpaper; }
        [Serializable] sealed class BlindOverlays { public string up, half, down; }
        [Serializable] sealed class BlindStates { public WeatherPaths up, half, down; }
        [Serializable] sealed class BlindData
        {
            public string shop_preview_state;
            public WeatherPaths @base;
            public BlindOverlays overlays;
            public BlindStates states;
            public bool closed_blocks_weather;
        }
        [Serializable] sealed class BlindManifest { public string[] weather; public string shop_preview_state; public BlindTheme[] themes; }
        [Serializable] sealed class BlindTheme
        {
            public string id, product_id;
            public WeatherPaths @base;
            public BlindOverlays overlay;
            public BlindStates composed;
        }
        [Serializable] sealed class ShopLayout { public string catalog; public ShopTab[] tabs; }
        [Serializable] sealed class ShopTab { public string id, active_window_png, empty_window_png; public string[] products; }
#pragma warning restore CS0649
    }
}
