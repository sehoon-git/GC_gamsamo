using System;
using System.IO;
using Gamsamo.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Updates an existing room hierarchy in place; the caller owns scene/prefab saving.</summary>
    public static class RoomAssetPackInstaller
    {
        public const string ArtRoot = "Assets/_Project/Art/UI/CounselingPack/room/";

        public static void Apply(GameObject root)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Stop Play mode before applying the room asset pack.");
            if (root == null) throw new ArgumentNullException(nameof(root));
            var room = root.GetComponent<ConsultationRoomController>();
            if (room == null || room.background == null || room.recordView == null
                || room.openRecordButton == null || room.openPcButton == null)
                throw new InvalidOperationException("The existing room, record UI, and PC hotspot must be installed first.");
            var roomFrame = root.transform.Find("RoomFrame") as RectTransform;
            var recordImage = room.recordView.GetComponent<Image>();
            var scaler = root.GetComponent<CanvasScaler>();
            if (roomFrame == null || recordImage == null || scaler == null)
                throw new InvalidOperationException("RoomFrame, record artwork Image, or CanvasScaler is missing.");

            // JsonUtility fields cannot contain the source's hyphenated property name.
            string json = File.ReadAllText(ArtRoot + "layout.json").Replace("\"room-wallpaper\":", "\"roomWallpaper\":");
            var layout = JsonUtility.FromJson<RoomLayout>(json);
            ValidateLayout(layout);
            Texture2D baseTexture = Texture(ArtRoot + layout.@base);
            Texture2D wallpaper = Texture(ArtRoot + layout.assets.roomWallpaper.file);
            Texture2D door = Texture(ArtRoot + layout.assets.door.file);
            Texture2D clear = Texture(ArtRoot + layout.assets.window.clear.file);
            Texture2D cloudy = Texture(ArtRoot + layout.assets.window.cloudy.file);
            Texture2D rain = Texture(ArtRoot + layout.assets.window.rain.file);
            Texture2D pc = Texture(ArtRoot + layout.assets.pc.file);
            Texture2D clipboard = Texture(ArtRoot + layout.assets.clipboard.file);
            var record = AssetDatabase.LoadAssetAtPath<Sprite>(ArtRoot + "medical-record-front.png");
            Shader shader = Shader.Find("Hidden/Gamsamo/Room Layer Composite");
            if (record == null || shader == null)
                throw new InvalidOperationException("Import medical-record-front.png as a Single sprite and the compositor shader before applying.");

            // All required references are present. Existing buttons, modal nodes, and text fields survive.
            scaler.referenceResolution = new Vector2(layout.canvas.width, layout.canvas.height);
            var roomAspect = GetOrAdd<AspectRatioFitter>(roomFrame.gameObject);
            roomAspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            roomAspect.aspectRatio = layout.canvas.width / (float)layout.canvas.height;
            if (room.pcModal != null)
            {
                var pcSafe = room.pcModal.transform.Find("PcSafeArea");
                var safeAspect = pcSafe != null ? pcSafe.GetComponent<AspectRatioFitter>() : null;
                if (safeAspect != null) safeAspect.aspectRatio = roomAspect.aspectRatio;
            }
            room.backgroundTexture = baseTexture;
            room.background.texture = baseTexture;
            room.background.raycastTarget = false;
            recordImage.sprite = record;
            recordImage.preserveAspect = true;
            ConfigureRecordInteraction(room);

            var appearance = GetOrAdd<ConsultationRoomAppearance>(root);
            appearance.room = room;
            appearance.compositeShader = shader;
            appearance.canvasSize = new Vector2Int(layout.canvas.width, layout.canvas.height);
            appearance.baseTexture = baseTexture;
            appearance.defaultRoomWallpaper = wallpaper;
            appearance.defaultDoor = door;
            appearance.clearWindow = clear;
            appearance.cloudyWindow = cloudy;
            appearance.rainWindow = rain;
            appearance.defaultPc = pc;
            appearance.defaultClipboard = clipboard;
            appearance.doorRect = layout.assets.door.ToRect();
            appearance.windowRect = layout.assets.window.clear.ToRect();
            appearance.pcRect = layout.assets.pc.ToRect();
            appearance.clipboardRect = layout.assets.clipboard.ToRect();

            var previews = ChildRect(roomFrame, "RoomPackLayers");
            Stretch(previews);
            previews.SetSiblingIndex(room.background.transform.GetSiblingIndex() + 1);
            appearance.editorPreviewLayers = previews.gameObject;
            previews.gameObject.SetActive(true);
            LayerPreview(previews, "RoomWallpaper", appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper), layout.assets.roomWallpaper.ToRect(), layout);
            LayerPreview(previews, "Door", appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Door), appearance.doorRect, layout);
            LayerPreview(previews, "Window", appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Window), appearance.windowRect, layout);
            LayerPreview(previews, "Pc", appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Pc), appearance.pcRect, layout);
            LayerPreview(previews, "Clipboard", appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Clipboard), appearance.clipboardRect, layout);

            // The room PC layer includes keyboard/mouse; only the monitor face opens the desktop.
            Rect monitorBounds = new Rect(layout.assets.pc.x, layout.assets.pc.y, 529, 315);
            UpdateHotspot(room.openPcButton, monitorBounds, layout, new[]
            {
                new Vector2(0, 297), new Vector2(527, 313), new Vector2(527, 5),
                new Vector2(520, 0), new Vector2(15, 15), new Vector2(0, 20)
            });

            var windowHotspot = ChildRect(roomFrame, "WindowBlindHotspot");
            var windowGraphic = GetOrAdd<Image>(windowHotspot.gameObject);
            windowGraphic.color = new Color(1, 1, 1, 0);
            windowGraphic.raycastTarget = true;
            var windowButton = GetOrAdd<Button>(windowHotspot.gameObject);
            windowButton.targetGraphic = windowGraphic;
            windowButton.transition = Selectable.Transition.None;
            windowButton.navigation = new Navigation { mode = Navigation.Mode.None };
            windowButton.interactable = false; // The default window has no blind variants.
            room.windowBlindButton = windowButton;
            Rect windowBounds = appearance.windowRect;
            float monitorLeft = monitorBounds.xMin - windowBounds.xMin;
            float monitorTop = monitorBounds.yMin - windowBounds.yMin;
            // Follow the frame, excluding its cast shadow and the monitor's overlapping bottom-right area.
            UpdateHotspot(windowButton, windowBounds, layout, new[]
            {
                new Vector2(29, 4), new Vector2(windowBounds.width - 6, 4),
                new Vector2(windowBounds.width - 6, monitorTop), new Vector2(monitorLeft, monitorTop),
                new Vector2(monitorLeft, windowBounds.height - 28), new Vector2(29, windowBounds.height - 28)
            });
            // The monitor stays in front even if an existing scene placed the room buttons in a different order.
            int monitorIndex = room.openPcButton.transform.GetSiblingIndex();
            if (windowHotspot.GetSiblingIndex() < monitorIndex) monitorIndex--;
            windowHotspot.SetSiblingIndex(monitorIndex);

            // The pen is a separate visible part of the layer and must not open the record.
            Rect clipboardBounds = new Rect(layout.assets.clipboard.x, layout.assets.clipboard.y, 350, 250);
            UpdateHotspot(room.openRecordButton, clipboardBounds, layout, new[]
            {
                new Vector2(5, 245), new Vector2(12, 249), new Vector2(317, 249),
                new Vector2(350, 40), new Vector2(341, 34), new Vector2(253, 34),
                new Vector2(248, 24), new Vector2(235, 20), new Vector2(225, 24),
                new Vector2(219, 34), new Vector2(120, 34)
            });
            EditorUtility.SetDirty(appearance);
            EditorUtility.SetDirty(room);
            EditorUtility.SetDirty(recordImage);
        }

        internal static void ConfigureRecordInteraction(ConsultationRoomController room)
        {
            if (room.recordPanel == null || room.recordView == null || room.modal == null)
                throw new InvalidOperationException("The existing record panel, artwork and modal are required.");
            RectTransform panel = room.recordPanel;
            var parent = panel.parent as RectTransform;
            if (parent == null)
                throw new InvalidOperationException("The record panel requires a RectTransform parent.");
            // Move a separate wrapper so the panel's aspect fitter and transition scale remain independent.
            RectTransform dragRoot = parent.name == "RecordDragRoot" ? parent : ChildRect(parent, "RecordDragRoot");
            Stretch(dragRoot);
            if (panel.parent != dragRoot) panel.SetParent(dragRoot, false);
            var drag = GetOrAdd<MedicalRecordDragHandler>(room.recordView.gameObject);
            drag.room = room;
            drag.dragRoot = dragRoot;
            drag.artwork = (RectTransform)room.recordView.transform;
            drag.bounds = (RectTransform)room.modal.transform;
            drag.ResetPosition();
            room.recordDrag = drag;
            if (room.closeRecordButton != null)
            {
                room.closeRecordButton.interactable = false;
                room.closeRecordButton.gameObject.SetActive(false);
            }
            EditorUtility.SetDirty(drag);
        }

        private static void ValidateLayout(RoomLayout layout)
        {
            if (layout?.canvas == null || layout.canvas.width != 1920 || layout.canvas.height != 1080
                || layout.origin != "top-left" || layout.units != "px" || layout.assets?.door == null
                || layout.assets.pc == null || layout.assets.clipboard == null || layout.assets.window?.clear == null
                || layout.assets.window.cloudy == null || layout.assets.window.rain == null || layout.assets.roomWallpaper == null)
                throw new InvalidOperationException("The supported room pack uses a complete 1920 x 1080 top-left pixel layout.");
            string order = layout.draw_order != null ? string.Join(",", layout.draw_order) : "";
            if (order != "base,room-wallpaper,door,window,pc,clipboard")
                throw new InvalidOperationException("The room pack draw order has changed; update the compositor before applying it.");
            if (layout.assets.roomWallpaper.ToRect() != new Rect(0, 0, layout.canvas.width, layout.canvas.height))
                throw new InvalidOperationException("Room wallpaper must cover the full canvas behind the furniture.");
            if (layout.assets.window.clear.ToRect() != layout.assets.window.cloudy.ToRect()
                || layout.assets.window.clear.ToRect() != layout.assets.window.rain.ToRect())
                throw new InvalidOperationException("Weather window variants must share the same room rectangle.");
        }

        private static Texture2D Texture(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new FileNotFoundException("Room pack texture is missing: " + path);
            return texture;
        }

        private static void LayerPreview(RectTransform parent, string name, Texture2D texture, Rect bounds, RoomLayout layout)
        {
            var rect = ChildRect(parent, name);
            Place(rect, bounds, layout);
            rect.SetAsLastSibling();
            var image = GetOrAdd<RawImage>(rect.gameObject);
            image.texture = texture;
            image.raycastTarget = false;
            image.color = Color.white;
        }

        private static void UpdateHotspot(Button button, Rect bounds, RoomLayout layout, Vector2[] topLeftPixels)
        {
            var rect = (RectTransform)button.transform;
            Place(rect, bounds, layout);
            var polygon = GetOrAdd<PolygonRaycastFilter>(button.gameObject);
            polygon.normalizedPoints = new Vector2[topLeftPixels.Length];
            for (int i = 0; i < topLeftPixels.Length; i++)
                polygon.normalizedPoints[i] = new Vector2(topLeftPixels[i].x / bounds.width, 1f - topLeftPixels[i].y / bounds.height);
            if (button.targetGraphic != null)
                button.targetGraphic.canvasRenderer.cullTransparentMesh = false;
            var colors = button.colors;
            colors.normalColor = new Color(1, 1, 1, 0);
            colors.disabledColor = colors.normalColor;
            button.colors = colors;
        }

        private static void Place(RectTransform rect, Rect bounds, RoomLayout layout)
        {
            rect.anchorMin = new Vector2(bounds.xMin / layout.canvas.width, 1f - bounds.yMax / layout.canvas.height);
            rect.anchorMax = new Vector2(bounds.xMax / layout.canvas.width, 1f - bounds.yMin / layout.canvas.height);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
        }

        private static RectTransform ChildRect(Transform parent, string name)
        {
            var existing = parent.Find(name) as RectTransform;
            if (existing != null) return existing;
            var created = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            created.SetParent(parent, false);
            return created;
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            var component = target.GetComponent<T>();
            return component != null ? component : target.AddComponent<T>();
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        [Serializable] private sealed class CanvasSize { public int width; public int height; }
        [Serializable] private sealed class Layer
        {
            public string file;
            public float x;
            public float y;
            public float width;
            public float height;
            public Rect ToRect() => new Rect(x, y, width, height);
        }
        [Serializable] private sealed class Windows { public Layer clear; public Layer cloudy; public Layer rain; }
        [Serializable] private sealed class RoomAssets { public Layer door; public Windows window; public Layer pc; public Layer clipboard; public Layer roomWallpaper; }
        [Serializable] private sealed class RoomLayout
        {
            public CanvasSize canvas;
            public string origin;
            public string units;
            public string @base;
            public string[] draw_order;
            public RoomAssets assets;
        }
    }
}
