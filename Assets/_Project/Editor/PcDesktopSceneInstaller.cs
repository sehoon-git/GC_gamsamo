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
    /// <summary>Adds the PC UI to both existing assets without rebuilding the room or record.</summary>
    public static class PcDesktopSceneInstaller
    {
        const string PcArt = "Assets/_Project/Art/UI/PcDesktop/";
        const string ShopArt = "Assets/_Project/Art/UI/CosmeticShop/";
        const string PrefabPath = "Assets/_Project/Prefabs/ConsultationRoomUI.prefab";
        const string FontPath = "Assets/_Project/Fonts/NotoSansKR SDF.asset";

        [MenuItem("Gamsamo/Install PC Desktop UI")]
        public static void Install()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Stop Play mode before installing the PC UI.");

            AssetDatabase.Refresh();
            foreach (string directory in new[] { PcArt, ShopArt })
                foreach (string path in Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories))
                    ImportSprite(path.Replace('\\', '/'));

            var pc = JsonUtility.FromJson<PcLayout>(File.ReadAllText(PcArt + "layout.json"));
            var shop = JsonUtility.FromJson<ShopLayout>(File.ReadAllText(ShopArt + "layout.json"));
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null || pc?.shortcuts?.Length != 4 || shop?.tabs?.Length != 5)
                throw new InvalidOperationException("PC layout, shop layout, or Korean font is missing.");

            // Validate all images before creating anything in the scene or prefab.
            Sprite(PcArt + "pc-window.png");
            foreach (var shortcut in pc.shortcuts) Sprite(PcArt + shortcut.file);
            foreach (var tab in shop.tabs) Sprite(ShopArt + tab.active_window_png);

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == PrefabPath)
                throw new InvalidOperationException("Close the consultation prefab stage before installing the PC UI.");

            Scene scene = SceneManager.GetSceneByPath(ConsultationRoomSceneBuilder.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ConsultationRoomSceneBuilder.ScenePath, OpenSceneMode.Additive);
            if (scene.isDirty)
                throw new InvalidOperationException("Save the consultation scene before installing the PC UI.");
            var room = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ConsultationRoomController>(true)).Single();

            GameObject prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var prefabRoom = prefab.GetComponent<ConsultationRoomController>();
                ValidateRoom(room);
                ValidateRoom(prefabRoom);
                bool changed = AddDesktop(room, pc, shop, font);
                if (AddDesktop(prefabRoom, pc, shop, font))
                    PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
                if (changed)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefab);
            }

            Debug.Log("PC desktop UI installed in the scene and prefab. Existing record UI preserved.");
        }

        [MenuItem("Gamsamo/Fit PC Window to Room")]
        public static void FitPcWindowToRoom()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Stop Play mode before resizing the PC UI.");
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == PrefabPath)
                throw new InvalidOperationException("Close the consultation prefab stage before resizing the PC UI.");
            Scene scene = SceneManager.GetSceneByPath(ConsultationRoomSceneBuilder.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ConsultationRoomSceneBuilder.ScenePath, OpenSceneMode.Additive);
            if (scene.isDirty)
                throw new InvalidOperationException("Save the consultation scene before resizing the PC UI.");
            var room = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<ConsultationRoomController>(true)).Single();
            GameObject prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                FitToRoom(room);
                FitToRoom(prefab.GetComponent<ConsultationRoomController>());
                PrefabUtility.SaveAsPrefabAsset(prefab, PrefabPath);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefab);
            }
            Debug.Log("PC window now fits within 82% of the room artwork width, including letterboxed views.");
        }

        static void FitToRoom(ConsultationRoomController room)
        {
            if (room == null || room.pcModal == null || room.pcPanel == null)
                throw new InvalidOperationException("Install the PC UI before adjusting its size.");
            var safeArea = room.pcModal.transform.Find("PcSafeArea") as RectTransform;
            if (safeArea == null) throw new InvalidOperationException("PcSafeArea is missing.");
            Stretch(safeArea);
            var roomAspect = room.transform.Find("RoomFrame").GetComponent<AspectRatioFitter>();
            var boundsAspect = safeArea.GetComponent<AspectRatioFitter>();
            if (boundsAspect == null) boundsAspect = safeArea.gameObject.AddComponent<AspectRatioFitter>();
            boundsAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            boundsAspect.aspectRatio = roomAspect.aspectRatio;
            var inset = safeArea.Find("PcSafeInset") as RectTransform;
            if (inset == null) inset = Rect("PcSafeInset", safeArea);
            inset.anchorMin = new Vector2(0.09f, 0.08f);
            inset.anchorMax = new Vector2(0.91f, 0.92f);
            inset.offsetMin = inset.offsetMax = Vector2.zero;
            room.pcPanel.SetParent(inset, false);
            Stretch(room.pcPanel);
            room.pcPanel.localScale = Vector3.one;
        }

        static void ValidateRoom(ConsultationRoomController room)
        {
            if (room == null) throw new InvalidOperationException("Room controller is missing.");
            if (room.pcModal == null && room.transform.Find("PcDesktopModal") != null)
                throw new InvalidOperationException("An unlinked PC hierarchy exists; reconnect it before installing.");
            if (room.transform.Find("RoomFrame") == null)
                throw new InvalidOperationException("RoomFrame is missing.");
        }

        static bool AddDesktop(ConsultationRoomController room, PcLayout pc, ShopLayout shop, TMP_FontAsset font)
        {
            // Never replace an already installed hierarchy, including subsequent artist edits.
            if (room.pcModal != null) return false;
            Transform roomFrame = room.transform.Find("RoomFrame");

            var hotspot = Rect("MonitorHotspot", roomFrame);
            hotspot.anchorMin = new Vector2(1162f / 1672, 1 - 635f / 941);
            hotspot.anchorMax = new Vector2(1622f / 1672, 1 - 365f / 941);
            hotspot.offsetMin = hotspot.offsetMax = Vector2.zero;
            var open = TransparentButton(hotspot);
            var polygon = hotspot.gameObject.AddComponent<PolygonRaycastFilter>();
            polygon.normalizedPoints = new[]
            {
                new Vector2(0.003f, 0.05f), new Vector2(0.99f, 0),
                new Vector2(1, 0.98f), new Vector2(0.98f, 1),
                new Vector2(0.02f, 0.95f), new Vector2(0, 0.91f)
            };

            var modalRect = Rect("PcDesktopModal", room.transform);
            Stretch(modalRect);
            var modal = modalRect.gameObject.AddComponent<CanvasGroup>();
            var backdropRect = Rect("PcDimmedBackdrop", modalRect);
            Stretch(backdropRect);
            var backdropImage = backdropRect.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0.06f, 0.04f, 0.035f, 0.42f);
            var backdrop = backdropRect.gameObject.AddComponent<Button>();
            backdrop.targetGraphic = backdropImage;
            backdrop.transition = Selectable.Transition.None;
            backdrop.navigation = new Navigation { mode = Navigation.Mode.None };

            var safeArea = Rect("PcSafeArea", modalRect);
            Stretch(safeArea);
            var panel = Rect("PcPanel", safeArea);
            Stretch(panel);
            var aspect = panel.gameObject.AddComponent<AspectRatioFitter>();
            aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            aspect.aspectRatio = 1520f / 657;
            var desktop = Rect("PcDesktop", panel);
            desktop.anchorMin = desktop.anchorMax = desktop.pivot = new Vector2(0.5f, 0.5f);
            desktop.sizeDelta = new Vector2(1520, 657);
            desktop.gameObject.AddComponent<ReferenceRectFitter>().referenceSize = desktop.sizeDelta;
            var desktopImage = desktop.gameObject.AddComponent<Image>();
            desktopImage.sprite = Sprite(PcArt + "pc-window.png");
            desktopImage.raycastTarget = true;
            var view = desktop.gameObject.AddComponent<PcDesktopView>();

            var shortcuts = Rect("Shortcuts", desktop);
            Stretch(shortcuts);
            view.shortcuts = shortcuts.gameObject.AddComponent<CanvasGroup>();
            foreach (var shortcut in pc.shortcuts)
            {
                var buttonRect = Rect(shortcut.id + "Shortcut", shortcuts);
                Place(buttonRect, shortcut.draw_local);
                var image = buttonRect.gameObject.AddComponent<Image>();
                image.sprite = Sprite(PcArt + shortcut.file);
                var button = buttonRect.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                var colors = button.colors;
                colors.highlightedColor = colors.selectedColor = new Color(0.84f, 0.93f, 1f);
                colors.pressedColor = new Color(0.64f, 0.81f, 1f);
                button.colors = colors;
                switch (shortcut.id)
                {
                    case "profile": view.profileButton = button; break;
                    case "shop": view.shopButton = button; break;
                    case "photos": view.photosButton = button; break;
                    case "settings": view.settingsButton = button; break;
                }
            }

            var shopRect = Rect("CosmeticShopWindow", desktop);
            Place(shopRect, shop.window_rect_in_pc);
            view.shopWindow = shopRect.gameObject;
            view.shopImage = shopRect.gameObject.AddComponent<Image>();
            view.shopSprites = shop.tabs.Select(tab => Sprite(ShopArt + tab.active_window_png)).ToArray();
            view.shopImage.sprite = view.shopSprites[0];
            view.tabButtons = new Button[shop.tabs.Length];
            for (int i = 0; i < shop.tabs.Length; i++)
                view.tabButtons[i] = HitButton(shop.tabs[i].id + "Tab", shopRect, shop.tabs[i].hit_rect);
            view.closeShopButton = HitButton("CloseShopButton", shopRect, shop.close_hit_rect);
            view.balanceText = Label("Balance", shopRect, font, shop.balance.number_rect, 21,
                TextAlignmentOptions.MidlineRight, "—");
            view.balanceText.color = new Color(0.24f, 0.34f, 0.28f);

            // Only the three supplied shortcut icons exist so far; their windows show honest empty states.
            var app = Rect("DesktopAppWindow", desktop);
            Place(app, new PixelRect(280, 100, 960, 450));
            view.appWindow = app.gameObject;
            app.gameObject.AddComponent<Image>().color = new Color(0.94f, 0.92f, 0.85f);
            var header = Rect("Header", app);
            Place(header, new PixelRect(0, 0, 960, 52));
            header.gameObject.AddComponent<Image>().color = new Color(0.29f, 0.42f, 0.55f);
            view.appTitle = Label("AppTitle", app, font, new PixelRect(24, 5, 840, 42), 25,
                TextAlignmentOptions.MidlineLeft, "");
            view.appTitle.color = Color.white;
            view.appMessage = Label("AppMessage", app, font, new PixelRect(40, 100, 880, 280), 27,
                TextAlignmentOptions.Center, "");
            view.closeAppButton = HitButton("CloseAppButton", app, new PixelRect(910, 5, 42, 42));
            var appCloseLabel = Label("CloseLabel", view.closeAppButton.transform, font,
                new PixelRect(0, 0, 42, 42), 26, TextAlignmentOptions.Center, "×");
            appCloseLabel.color = Color.white;

            // Keep the PC close control above internal windows and outside the shortcut input group.
            room.openPcButton = open;
            room.closePcButton = HitButton("ClosePcButton", desktop, pc.close.hit_local);
            room.pcBackdropButton = backdrop;
            room.pcPanel = panel;
            room.pcModal = modal;
            room.pcDesktopView = view;
            FitToRoom(room);
            view.shopWindow.SetActive(false);
            view.appWindow.SetActive(false);
            modal.alpha = 0;
            modal.blocksRaycasts = modal.interactable = false;
            modalRect.gameObject.SetActive(false);
            EditorUtility.SetDirty(room);
            return true;
        }

        static void ImportSprite(string path)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            // Preserve subsequent import-setting edits made by artists, including slicing.
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

        static Sprite Sprite(string path)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("Sprite not imported: " + path);
            return sprite;
        }

        static Button HitButton(string name, Transform parent, PixelRect bounds)
        {
            var rect = Rect(name, parent);
            Place(rect, bounds);
            return TransparentButton(rect);
        }

        static Button TransparentButton(RectTransform rect)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.canvasRenderer.cullTransparentMesh = false;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = colors.disabledColor = new Color(1, 1, 1, 0);
            colors.highlightedColor = colors.selectedColor = new Color(1, 0.94f, 0.8f, 0.15f);
            colors.pressedColor = new Color(1, 0.94f, 0.8f, 0.27f);
            button.colors = colors;
            return button;
        }

        static TMP_Text Label(string name, Transform parent, TMP_FontAsset font, PixelRect bounds,
            float size, TextAlignmentOptions alignment, string value)
        {
            var rect = Rect(name, parent);
            Place(rect, bounds);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.fontSize = size;
            label.color = new Color(0.24f, 0.23f, 0.2f);
            label.alignment = alignment;
            label.text = value;
            label.richText = false;
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        static RectTransform Rect(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        static void Place(RectTransform rect, PixelRect bounds)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(bounds.x, -bounds.y);
            rect.sizeDelta = new Vector2(bounds.width, bounds.height);
        }

        // These DTO fields are populated by JsonUtility from the supplied layout files.
#pragma warning disable CS0649
        [Serializable] class PixelRect
        {
            public float x, y, width, height;
            public PixelRect(float x, float y, float width, float height)
            { this.x = x; this.y = y; this.width = width; this.height = height; }
        }
        [Serializable] class PcLayout { public Shortcut[] shortcuts; public CloseLayout close; }
        [Serializable] class Shortcut { public string id, file; public PixelRect draw_local; }
        [Serializable] class CloseLayout { public PixelRect hit_local; }
        [Serializable] class ShopLayout
        {
            public PixelRect window_rect_in_pc, close_hit_rect;
            public ShopTab[] tabs;
            public Balance balance;
        }
        [Serializable] class ShopTab { public string id, active_window_png; public PixelRect hit_rect; }
        [Serializable] class Balance { public PixelRect number_rect; }
#pragma warning restore CS0649
    }
}
