using System;
using System.IO;
using System.Linq;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Upgrades the existing consultation scene and prefab using the supplied modular artwork.</summary>
    public static class CounselingAssetPackInstaller
    {
        private const string Pack = "Assets/_Project/Art/UI/CounselingPack/";
        private const string PcArt = "Assets/_Project/Art/UI/PcDesktop/";
        private const string PrefabPath = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";

        [MenuItem("Gamsamo/Apply Counseling Asset Pack")]
        public static void Apply()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before applying assets.");
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == PrefabPath)
                throw new InvalidOperationException("Close the consultation prefab stage before applying assets.");
            var scene = SceneManager.GetSceneByPath(ConsultationRoomSceneBuilder.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ConsultationRoomSceneBuilder.ScenePath, OpenSceneMode.Additive);
            if (scene.isDirty) throw new InvalidOperationException("Save the consultation scene before applying assets.");

            AssetDatabase.Refresh();
            foreach (var path in Directory.GetFiles(Pack, "*.png", SearchOption.AllDirectories))
                ImportSprite(path.Replace('\\', '/'));
            ImportSprite(PcArt + "pc-frame.png");
            PcDesktopSceneInstaller.Install();
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Project/Fonts/NotoSansKR SDF.asset");
            if (font == null) throw new InvalidOperationException("The Korean TMP font is missing.");
            var cosmetics = CosmeticCatalogImporter.Build();
            var room = scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<ConsultationRoomController>(true)).Single();
            GameObject prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Upgrade(room, font, cosmetics);
                Upgrade(prefab.GetComponent<ConsultationRoomController>(), font, cosmetics);
                PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                // Save only this project's font after creating any newly needed Korean glyphs.
                AssetDatabase.SaveAssetIfDirty(font);
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
            Debug.Log("Counseling asset pack applied to scene and prefab, including 48 cosmetic previews, room wallpaper and window blinds.");
        }

        private static void Upgrade(ConsultationRoomController room, TMP_FontAsset font, CosmeticCatalog cosmetics)
        {
            if (room == null || room.pcDesktopView == null)
                throw new InvalidOperationException("The installed consultation PC is required.");
            RoomAssetPackInstaller.Apply(room.gameObject);
            var view = room.pcDesktopView;
            var desktop = (RectTransform)view.transform;
            var background = desktop.GetComponent<Image>();
            background.sprite = null;
            background.color = Color.clear;
            background.canvasRenderer.cullTransparentMesh = false;
            background.raycastTarget = true;
            var wallpaperRect = Child(desktop, "Wallpaper");
            Place(wallpaperRect, 7, 46, 1506, 603);
            wallpaperRect.SetSiblingIndex(0);
            view.wallpaper = GetOrAdd<Image>(wallpaperRect.gameObject);
            view.defaultWallpaper = Sprite(PcArt + "pc-wallpaper.png");
            if (view.wallpaper.sprite == null) view.wallpaper.sprite = view.defaultWallpaper;
            view.wallpaper.raycastTarget = false;
            var frame = Child(desktop, "DesktopFrame");
            Place(frame, 0, 0, 1520, 657);
            frame.SetSiblingIndex(1);
            var frameImage = GetOrAdd<Image>(frame.gameObject);
            frameImage.sprite = Sprite(PcArt + "pc-frame.png");
            frameImage.raycastTarget = false;
            view.profileView = PcContentBuilder.BuildProfile(desktop, font);
            view.photoView = PcContentBuilder.BuildPhotos(desktop, font);
            view.settingsView = PcSettingsBuilder.Build(desktop, font);
            view.roomAppearance = room.GetComponent<ConsultationRoomAppearance>();
            view.ResetDesktop();
            room.closePcButton.transform.SetAsLastSibling();
            // The supplied shop art says KRW. Overlay the game's established coin unit without editing the PNG.
            var unit = Child(view.shopWindow.transform, "CoinUnit");
            Place(unit, 1044, 91, 25, 28);
            var unitPaper = GetOrAdd<Image>(unit.gameObject);
            unitPaper.color = new Color32(231, 234, 218, 255);
            unitPaper.raycastTarget = false;
            var unitTextRect = Child(unit, "Label");
            Place(unitTextRect, 0, 0, 25, 28);
            var unitText = GetOrAdd<TextMeshProUGUI>(unitTextRect.gameObject);
            unitText.font = font;
            unitText.text = "C";
            unitText.fontSize = 21;
            unitText.alignment = TextAlignmentOptions.Center;
            unitText.color = new Color32(61, 87, 71, 255);
            unitText.raycastTarget = false;
            unitText.richText = false;
            var appearance = GetOrAdd<CosmeticAppearanceController>(room.gameObject);
            appearance.catalog = cosmetics;
            appearance.roomAppearance = view.roomAppearance;
            appearance.desktop = view;
            appearance.recordImage = room.recordView.GetComponent<Image>();
            appearance.defaultRecord = Sprite(Pack + "room/medical-record-front.png");
            CosmeticShopBuilder.Build(view, cosmetics, appearance, font);
            view.ResetDesktop();
            EditorUtility.SetDirty(appearance);
            EditorUtility.SetDirty(view);
            EditorUtility.SetDirty(room);
        }

        private static T GetOrAdd<T>(GameObject obj) where T : Component
        {
            var result = obj.GetComponent<T>();
            return result != null ? result : obj.AddComponent<T>();
        }

        private static RectTransform Child(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return (RectTransform)existing;
            var result = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            result.SetParent(parent, false);
            return result;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static Sprite Sprite(string path)
        {
            var result = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (result == null) throw new InvalidOperationException("Sprite missing: " + path);
            return result;
        }

        private static void ImportSprite(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Texture missing: " + path);
            if (importer.textureType == TextureImporterType.Sprite) return;
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
    }
}
