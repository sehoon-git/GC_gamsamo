using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Exercises actual shop input, all cosmetic artwork and GPU window combinations.</summary>
    public static class CosmeticSmokeChecks
    {
        private const string Menu = "Gamsamo/Run Cosmetic Checks";
        private static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper", "room-wallpaper" };
        private static RunState run;
        private static IEnumerator steps;
        private static double resumeAt, deadline;

        [MenuItem(Menu)]
        public static void Run()
        {
            var appearance = UnityEngine.Object.FindFirstObjectByType<CosmeticAppearanceController>();
            if (!Application.isPlaying || EditorApplication.isPaused || run != null || appearance == null) return;
            if (appearance.desktop == null || appearance.desktop.IsSettingsOpen)
            {
                Debug.LogWarning("Close settings before cosmetic checks to preserve unapplied edits.");
                return;
            }
            run = new RunState(appearance);
            Application.runInBackground = true;
            steps = Exercise(run);
            resumeAt = EditorApplication.timeSinceStartup;
            deadline = resumeAt + 180;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Finish;
            EditorApplication.quitting += Finish;
        }

        [MenuItem(Menu, true)]
        private static bool CanRun() => Application.isPlaying && !EditorApplication.isPaused && run == null;

        private static void Tick()
        {
            if (run == null) return;
            EditorApplication.QueuePlayerLoopUpdate();
            if (!Application.isPlaying || EditorApplication.isPaused || EditorApplication.timeSinceStartup > deadline)
            {
                run.Check(false, "Checks remained in unpaused Play mode within deadline");
                Finish();
                return;
            }
            if (EditorApplication.timeSinceStartup < resumeAt) return;
            try
            {
                if (steps.MoveNext())
                {
                    resumeAt = EditorApplication.timeSinceStartup + (steps.Current is float delay ? delay : 0);
                    return;
                }
            }
            catch (Exception exception) { run.Check(false, "Unexpected exception: " + exception); }
            Finish();
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Finish;
            EditorApplication.quitting -= Finish;
            if (run == null) return;
            try { run.Restore(); }
            catch (Exception exception) { run.Check(false, "Restore: " + exception); }
            finally
            {
                run.WriteReport();
                Application.runInBackground = run.OriginalBackground;
                (steps as IDisposable)?.Dispose();
                steps = null;
                run = null;
            }
        }

        private static IEnumerator Exercise(RunState r)
        {
            var appearance = r.Appearance;
            var room = appearance.roomAppearance.room;
            var desktop = appearance.desktop;
            var shop = desktop.shopView;
            var catalog = appearance.catalog;
            bool ready = catalog != null && catalog.items != null && catalog.items.Length == 48 && shop != null;
            r.Check(ready, "Catalog has 48 entries and shop is connected");
            if (!ready) yield break;
            r.Check(catalog.items.Select(item => item.id).Distinct().Count() == 48,
                "All product IDs are unique");
            r.Check(catalog.items.All(item => !item.hasPrice && item.thumbnail != null),
                "Every item has a thumbnail and an explicitly unset price");
            r.Check(Targets.All(target => catalog.GetItems(target).Length == 8), "Six categories contain eight items each");
            appearance.RestoreAll();
            room.CloseRecord();
            room.OpenPc();
            yield return 0.4f;
            desktop.OpenShop();
            yield return 0.2f;
            r.Check(shop.itemButtons.Length == 8 && room.windowBlindButton != null, "Eight design cards and a room window hotspot are installed");
            CheckFooterLayout(r, desktop, shop, "initial shop");
            r.Capture("cosmetics-footer-disabled.png");
            yield return 0.15f;

            for (int category = 0; category < Targets.Length; category++)
            {
                desktop.tabButtons[category].onClick.Invoke();
                yield return 0.1f;
                var entries = catalog.GetItems(Targets[category]);
                r.Check(shop.ActiveCategoryIndex == category && shop.ActiveTarget == Targets[category], "Tab routes to " + Targets[category]);
                r.Check(shop.blindControls == null || !shop.blindControls.activeSelf,
                    "Shop has no blind-state controls on tab " + category);
                r.Check(shop.windowBlindHint != null && shop.windowBlindHint.gameObject.activeSelf == (category == 0),
                    "Window click hint only appears on window tab " + category);
                for (int slot = 0; slot < entries.Length; slot++)
                {
                    CosmeticItem item = entries[slot];
                    r.Check(shop.ItemIdAt(slot) == item.id && shop.thumbnails[slot].sprite == item.thumbnail,
                        "Card order and thumbnail: " + item.id);
                    r.Check(Hit(shop.itemButtons[slot]) == shop.itemButtons[slot], "Card receives pointer input: " + item.id);
                    shop.itemButtons[slot].onClick.Invoke();
                    r.Check(appearance.SelectedId(item.target) == item.id, "Click previews " + item.id);
                    if (category == 0 && slot == 0)
                        r.Check(appearance.BlindState == 1, "First window design starts half lowered");
                    r.Check(shop.selectionBorders[slot].activeSelf
                        && shop.selectionBorders.Count(border => border.activeSelf) == 1,
                        "Exactly one selected card: " + item.id);
                    if (item.target == "wallpaper")
                        r.Check(desktop.wallpaper.sprite == item.artwork, "Desktop wallpaper changes: " + item.id);
                    else if (item.target == "clipboard")
                        r.Check(appearance.recordImage.sprite == item.detailArtwork
                            && appearance.roomAppearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Clipboard) == item.artwork.texture,
                            "Desk and enlarged record change together: " + item.id);
                    else if (item.target != "window")
                    {
                        var layer = item.target == "door" ? ConsultationRoomAppearance.CosmeticLayer.Door
                            : item.target == "room-wallpaper" ? ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper
                            : ConsultationRoomAppearance.CosmeticLayer.Pc;
                        r.Check(appearance.roomAppearance.TextureForLayer(layer) == item.artwork.texture, "Room layer changes: " + item.id);
                    }
                    r.Check(Mathf.Approximately(Blur(room), 1), "Preview preserves open-PC blur: " + item.id);
                    CheckText(r, shop.itemLabels[slot], item.id);
                    CheckText(r, shop.priceLabels[slot], item.id + " price");
                    CheckText(r, shop.selectedBadges[slot], item.id + " selection badge");
                    yield return 0.02f;
                }
                CheckText(r, shop.statusText, "shop status " + category);
                CheckFooterLayout(r, desktop, shop, Targets[category] + " selected controls");
                r.Capture("cosmetics-shop-" + Targets[category] + ".png");
                yield return 0.15f;
                r.Check(Hit(shop.restoreCategoryButton) == shop.restoreCategoryButton, "Restore control receives input: " + category);
                shop.restoreCategoryButton.onClick.Invoke();
                r.Check(string.IsNullOrEmpty(appearance.SelectedId(Targets[category])), "Category default restores: " + category);
            }

            CheckRoomWallpaper(r, appearance);
            var windowChecks = CheckWindowInteraction(r, room, appearance);
            while (windowChecks.MoveNext()) yield return windowChecks.Current;
            room.OpenPc();
            yield return 0.4f;
            desktop.OpenShop();
            desktop.SelectTab(0);
            yield return 0.1f;
            foreach (CosmeticItem item in catalog.GetItems("window"))
            {
                r.Check(appearance.Preview(item.id), "Window preview route accepts " + item.id);
                // Let the preview redraw before inspecting its rendered artwork combinations.
                yield return 0.05f;
                var hashes = new HashSet<string>();
                for (int blind = 0; blind < 3; blind++)
                {
                    // API setup exercises all artwork combinations while the PC modal stays open.
                    // Player input is covered separately through the room's actual window hotspot.
                    appearance.SetBlindState(blind);
                    r.Check(!room.windowBlindButton.IsInteractable(),
                        "Room window stays blocked behind PC: " + item.id + "/" + blind);
                    for (int weather = 0; weather < 3; weather++)
                    {
                        appearance.roomAppearance.SetWeather((ConsultationRoomAppearance.Weather)weather);
                        var expected = item.blindSprites[blind * 3 + weather];
                        r.Check(appearance.BlindState == blind && appearance.roomAppearance.TextureForLayer(
                            ConsultationRoomAppearance.CosmeticLayer.Window) == expected.texture,
                            "Window combination: " + item.id + "/" + blind + "/" + weather);
                        hashes.Add(PixelHash(appearance.roomAppearance.CompositeTexture));
                        r.Check(Mathf.Approximately(Blur(room), 1), "Weather/blinds keep modal blur: " + item.id + "/" + blind + "/" + weather);
                    }
                }
                r.Check(hashes.Count >= 7, "GPU shows distinct open/half weather and closed blind pixels: " + item.id);
                yield return 0.03f;
            }

            appearance.Preview("window.oak");
            appearance.roomAppearance.SetWeather(ConsultationRoomAppearance.Weather.Rain);
            room.ClosePc();
            yield return 0.4f;
            for (int blind = 0; blind < 3; blind++)
            {
                appearance.SetBlindState(blind);
                yield return 0.1f;
                r.Capture("cosmetics-blinds-" + blind + ".png");
                yield return 0.15f;
            }
            foreach (string target in Targets) appearance.Preview(target + ".sage");
            room.OpenRecord();
            yield return 0.4f;
            r.Check(appearance.recordImage.sprite == catalog.Find("clipboard.sage").detailArtwork,
                "Selected record artwork survives opening the record");
            r.Check(room.recordView.GetComponentsInChildren<TMP_Text>(true).Length >= 12
                && JsonUtility.ToJson(room.record) == r.OriginalRecord, "Cosmetics preserve medical record data and content fields");
            r.Capture("cosmetics-record-sage.png");
            yield return 0.2f;
            room.CloseRecord();
            yield return 0.4f;
            r.Capture("cosmetics-room-sage.png");
            yield return 0.2f;
            room.OpenPc();
            yield return 0.4f;
            desktop.OpenShop();
            r.Check(Targets.All(target => appearance.SelectedId(target) == target + ".sage"), "All six previews survive closing and reopening PC");
            string selected = appearance.SelectedId("window");
            r.Check(!appearance.Preview("missing.product") && appearance.SelectedId("window") == selected,
                "Unknown IDs cannot change appearance");
            r.Check(PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey) == r.HadSettings
                && PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "") == r.Settings,
                "Cosmetic previews do not change saved game settings");
            r.Check(desktop.balanceText.text == "—", "No invented currency or free purchase is granted");
        }

        private static void CheckRoomWallpaper(RunState r, CosmeticAppearanceController appearance)
        {
            var room = appearance.roomAppearance;
            appearance.Preview("wallpaper.midnight");
            Sprite desktopWallpaper = appearance.desktop.wallpaper.sprite;
            Color32[] baseline = RoomSamples(room.CompositeTexture);
            var wallColors = new HashSet<Color32>();
            foreach (CosmeticItem item in appearance.catalog.GetItems("room-wallpaper"))
            {
                appearance.Preview(item.id);
                Color32[] changed = RoomSamples(room.CompositeTexture);
                r.Check(!changed[0].Equals(baseline[0]) && wallColors.Add(changed[0]), "Distinct rendered wall color: " + item.id);
                r.Check(changed.Skip(1).SequenceEqual(baseline.Skip(1)), "Wallpaper preserves door, window, PC, record and desk pixels: " + item.id);
                r.Check(appearance.desktop.wallpaper.sprite == desktopWallpaper, "Room wallpaper preserves PC wallpaper: " + item.id);
                appearance.RestoreCategory("room-wallpaper");
                r.Check(RoomSamples(room.CompositeTexture).SequenceEqual(baseline)
                    && appearance.SelectedId("wallpaper") == "wallpaper.midnight", "Wallpaper restore recovers room pixels independently: " + item.id);
            }
            appearance.Preview("room-wallpaper.sage");
            appearance.RestoreCategory("wallpaper");
            r.Check(appearance.SelectedId("room-wallpaper") == "room-wallpaper.sage"
                && room.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper)
                    == appearance.catalog.Find("room-wallpaper.sage").artwork.texture,
                "PC wallpaper restore preserves selected room wallpaper");
            appearance.RestoreAll();
            r.Check(Targets.All(target => string.IsNullOrEmpty(appearance.SelectedId(target)))
                && RoomSamples(room.CompositeTexture).SequenceEqual(baseline), "RestoreAll includes room wallpaper");
        }

        private static Color32[] RoomSamples(RenderTexture source)
        {
            // Top-left source coordinates: exposed wall, door, window, PC, clipboard, desk.
            var points = new[] { new Vector2Int(650, 300), new Vector2Int(300, 350),
                new Vector2Int(1000, 250), new Vector2Int(1600, 500), new Vector2Int(150, 850), new Vector2Int(1000, 980) };
            var readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                return points.Select(point => (Color32)readback.GetPixel(point.x, source.height - 1 - point.y)).ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        private static IEnumerator CheckWindowInteraction(RunState r, ConsultationRoomController room,
            CosmeticAppearanceController appearance)
        {
            Button window = room.windowBlindButton;
            r.Check(window != null, "Window interaction is connected");
            if (window == null) yield break;
            room.ClosePc();
            room.CloseRecord();
            yield return 0.4f;
            appearance.RestoreCategory("window");
            int originalState = appearance.BlindState;
            room.CycleWindowBlinds();
            r.Check(!appearance.CanControlBlinds && !window.IsInteractable()
                && appearance.BlindState == originalState, "Original room window cannot cycle blinds");

            appearance.Preview("window.oak");
            yield return 0.1f;
            r.Check(room.CanInteractWithRoom && window.IsInteractable() && appearance.BlindState == 1,
                "Choosing a window design enables room interaction at half lowered");
            r.Check(Hit(window) == window, "Exposed window receives pointer input");
            r.Check(Hit(window, new Vector2(-0.1f, 0.5f)) != window,
                "Adjacent wall does not receive window input");
            r.Check(Hit(window, new Vector2(0.9f, 0.1f)) == room.openPcButton,
                "Monitor overlapping the window keeps PC input");

            foreach (CosmeticItem item in appearance.catalog.GetItems("window"))
            {
                int retained = appearance.BlindState;
                appearance.Preview(item.id);
                yield return 0.05f;
                r.Check(appearance.BlindState == retained, "Changing design preserves blind degree: " + item.id);
                for (int click = 0; click < 3; click++)
                {
                    int expected = (appearance.BlindState + 1) % 3;
                    r.Check(Hit(window) == window, "Window hotspot receives input: " + item.id + "/" + click);
                    PointerClick(window);
                    r.Check(appearance.BlindState == expected
                        && appearance.SelectedId("window") == item.id,
                        "Window click advances one degree and preserves design: " + item.id + "/" + click);
                    yield return 0.03f;
                }
                r.Check(appearance.BlindState == retained, "Three clicks complete one cycle: " + item.id);
            }

            room.OpenPc();
            CheckWindowBlocked(r, room, appearance, "PC opening", false);
            // Input guards update synchronously; newly activated modal graphics register on a player frame.
            int pcOpeningFrame = Time.frameCount;
            while (Time.frameCount == pcOpeningFrame) yield return 0.01f;
            r.Check(Hit(window) != window, "PC backdrop intercepts window input after its first player frame");
            yield return 0.4f;
            appearance.Preview("window.sage");
            CheckWindowBlocked(r, room, appearance, "PC open after changing design");
            room.ClosePc();
            CheckWindowBlocked(r, room, appearance, "PC closing transition");
            yield return 0.4f;
            int previous = appearance.BlindState;
            PointerClick(window);
            r.Check(window.IsInteractable() && appearance.BlindState == (previous + 1) % 3,
                "Window input resumes after PC closes");

            room.OpenRecord();
            CheckWindowBlocked(r, room, appearance, "Record opening", false);
            int recordOpeningFrame = Time.frameCount;
            while (Time.frameCount == recordOpeningFrame) yield return 0.01f;
            r.Check(Hit(window) != window, "Record backdrop intercepts window input after its first player frame");
            yield return 0.4f;
            CheckWindowBlocked(r, room, appearance, "Record open");
            room.CloseRecord();
            CheckWindowBlocked(r, room, appearance, "Record closing transition");
            yield return 0.4f;
            previous = appearance.BlindState;
            PointerClick(window);
            r.Check(window.IsInteractable() && appearance.BlindState == (previous + 1) % 3,
                "Window input resumes after record closes");

            previous = appearance.BlindState;
            appearance.roomAppearance.SetWeather(ConsultationRoomAppearance.Weather.Rain);
            r.Check(appearance.BlindState == previous && window.IsInteractable(),
                "Weather changes preserve room window degree and interaction");
            appearance.RestoreCategory("window");
            originalState = appearance.BlindState;
            PointerClick(window);
            room.CycleWindowBlinds();
            r.Check(!window.IsInteractable() && appearance.BlindState == originalState,
                "Restoring the default window disables pointer and direct interaction");
            appearance.Preview("window.oak");
            r.Check(window.IsInteractable() && appearance.BlindState == 1,
                "Selecting a design again starts at half lowered");
        }

        private static void CheckWindowBlocked(RunState r, ConsultationRoomController room,
            CosmeticAppearanceController appearance, string context, bool requireRegisteredBackdrop = true)
        {
            int state = appearance.BlindState;
            Button window = room.windowBlindButton;
            r.Check(!room.CanInteractWithRoom && !window.IsInteractable()
                && (!requireRegisteredBackdrop || Hit(window) != window),
                "Window is blocked by modal: " + context);
            PointerClick(window);
            room.CycleWindowBlinds();
            r.Check(appearance.BlindState == state, "Blocked input cannot change blind degree: " + context);
        }

        private static void PointerClick(Button button)
        {
            ExecuteEvents.Execute(button.gameObject,
                new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left },
                ExecuteEvents.pointerClickHandler);
        }

        private static Button Hit(Button button) => Hit(button, new Vector2(0.5f, 0.5f));

        private static Button Hit(Button button, Vector2 normalized)
        {
            Canvas.ForceUpdateCanvases();
            RectTransform rect = (RectTransform)button.transform;
            var canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var local = new Vector3(Mathf.LerpUnclamped(rect.rect.xMin, rect.rect.xMax, normalized.x),
                Mathf.LerpUnclamped(rect.rect.yMin, rect.rect.yMax, normalized.y));
            var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(local));
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Count > 0 ? hits[0].gameObject.GetComponentInParent<Button>() : null;
        }

        private static float Blur(ConsultationRoomController room) => room.background.material.GetFloat("_Amount");

        private static void CheckText(RunState r, TMP_Text text, string context)
        {
            text.ForceMeshUpdate(true);
            r.Check(!text.isTextOverflowing && text.text.All(c => char.IsWhiteSpace(c) || text.font.HasCharacter(c, true, false)),
                "Readable Korean text: " + context);
        }

        private static void CheckFooterLayout(RunState r, PcDesktopView desktop,
            CosmeticShopView shop, string context)
        {
            Canvas.ForceUpdateCanvases();
            var window = desktop.shopWindow.transform as RectTransform;
            var pc = desktop.transform as RectTransform;
            var status = shop.statusText != null ? shop.statusText.rectTransform : null;
            var restore = shop.restoreCategoryButton != null ? shop.restoreCategoryButton.transform as RectTransform : null;
            var hint = shop.windowBlindHint;
            bool ready = window != null && pc != null && status != null && restore != null && hint != null
                && desktop.wallpaper != null && shop.itemButtons != null && shop.itemButtons.Length == 8
                && shop.itemButtons.All(button => button != null);
            r.Check(ready, "Footer geometry references are complete: " + context);
            if (!ready) return;

            var visibleControls = new List<RectTransform> { status, restore };
            if (hint.gameObject.activeInHierarchy) visibleControls.Add(hint.rectTransform);
            Rect[] footerBounds = visibleControls.Select(rect => BoundsIn(rect, window)).ToArray();
            const float margin = 8f;
            Rect paper = Rect.MinMaxRect(window.rect.xMin + 8f + margin, window.rect.yMin + 12f + margin,
                window.rect.xMax - 8f - margin, window.rect.yMax - margin);
            r.Check(footerBounds.All(bounds => Contains(paper, bounds)),
                "Footer stays inside the paper with frame clearance: " + context);
            float lowestCardEdge = shop.itemButtons.Min(button => BoundsIn((RectTransform)button.transform, window).yMin);
            r.Check(footerBounds.All(bounds => bounds.yMax <= lowestCardEdge - margin + 0.1f),
                "Footer has at least eight pixels below product cards: " + context);
            r.Check(DoNotOverlap(footerBounds), "Status, window hint and restore button do not overlap: " + context);
            r.Check(Contains(BoundsIn(desktop.wallpaper.rectTransform, pc), BoundsIn(window, pc)),
                "Shop window fits inside the PC desktop content: " + context);
            if (hint.gameObject.activeInHierarchy) CheckText(r, hint, "window click hint " + context);
            CheckText(r, shop.restoreCategoryButton.GetComponentInChildren<TMP_Text>(true), "restore label " + context);
        }

        private static Rect BoundsIn(RectTransform rect, RectTransform relativeTo)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (Vector3 corner in corners)
            {
                Vector2 local = relativeTo.InverseTransformPoint(corner);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            const float tolerance = 0.1f;
            return inner.width > 0 && inner.height > 0
                && inner.xMin >= outer.xMin - tolerance && inner.xMax <= outer.xMax + tolerance
                && inner.yMin >= outer.yMin - tolerance && inner.yMax <= outer.yMax + tolerance;
        }

        private static bool DoNotOverlap(Rect[] bounds)
        {
            for (int first = 0; first < bounds.Length; first++)
                for (int second = first + 1; second < bounds.Length; second++)
                    if (bounds[first].Overlaps(bounds[second])) return false;
            return true;
        }

        private static string PixelHash(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var pixels = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                pixels.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                pixels.Apply();
                using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(pixels.GetRawTextureData()));
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); }
        }

        private sealed class RunState
        {
            public readonly CosmeticAppearanceController Appearance;
            public readonly bool OriginalBackground, HadSettings;
            public readonly string Settings, OriginalRecord;
            private readonly ConsultationRoomAppearance roomAppearance;
            private readonly ConsultationRoomController room;
            private readonly ConsultationRoomAppearance.Weather weather;
            private readonly Texture2D[] cosmetics, windowVariants;
            private readonly string[] selection;
            private readonly Sprite wallpaper, record;
            private readonly int blind, tab;
            private readonly bool pcOpen, recordOpen, shopOpen, profileOpen, photosOpen;
            private readonly GameObject focus;
            private readonly StringBuilder report = new StringBuilder();
            private readonly string directory = Path.GetFullPath("artifacts/ui");
            private int passed, failed;
            private static readonly ConsultationRoomAppearance.CosmeticLayer[] Layers =
            {
                ConsultationRoomAppearance.CosmeticLayer.Window, ConsultationRoomAppearance.CosmeticLayer.Door,
                ConsultationRoomAppearance.CosmeticLayer.Clipboard, ConsultationRoomAppearance.CosmeticLayer.Pc,
                ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper
            };
            private static readonly string[] CosmeticProperties =
                { "windowCosmetic", "doorCosmetic", "clipboardCosmetic", "pcCosmetic", "roomWallpaperCosmetic" };

            public RunState(CosmeticAppearanceController appearance)
            {
                Appearance = appearance;
                OriginalBackground = Application.runInBackground;
                HadSettings = PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey);
                Settings = PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "");
                roomAppearance = appearance.roomAppearance;
                room = roomAppearance.room;
                OriginalRecord = JsonUtility.ToJson(room.record);
                weather = roomAppearance.CurrentWeather;
                windowVariants = roomAppearance.WindowVariantTextures;
                var serialized = new SerializedObject(roomAppearance);
                cosmetics = CosmeticProperties.Select(name =>
                    serialized.FindProperty(name).objectReferenceValue as Texture2D).ToArray();
                selection = Targets.Select(appearance.SelectedId).ToArray();
                blind = roomAppearance.WindowBlindState;
                wallpaper = appearance.desktop.wallpaper.sprite;
                record = appearance.recordImage.sprite;
                pcOpen = room.IsPcOpen; recordOpen = room.IsRecordOpen;
                shopOpen = appearance.desktop.IsShopOpen;
                profileOpen = appearance.desktop.IsProfileOpen; photosOpen = appearance.desktop.IsPhotosOpen;
                tab = appearance.desktop.ActiveTabIndex;
                focus = EventSystem.current.currentSelectedGameObject;
                Directory.CreateDirectory(directory);
                report.AppendLine("Cosmetic runtime checks | Unity " + Application.unityVersion + " | " + DateTime.UtcNow.ToString("O"));
            }

            public void Check(bool success, string label)
            {
                if (success) passed++; else failed++;
                report.AppendLine((success ? "PASS " : "FAIL ") + label);
            }
            public void Capture(string filename) => ScreenCapture.CaptureScreenshot(Path.Combine(directory, filename));
            public void Restore()
            {
                if (Appearance == null || !Application.isPlaying) return;
                room.ClosePc(); room.CloseRecord();
                Appearance.RestoreAll();
                foreach (string id in selection) if (!string.IsNullOrEmpty(id)) Appearance.Preview(id);
                // Restore only appearance choices. Serializing a MonoBehaviour wholesale can replace
                // live scene-object references with null when EditorJsonUtility resolves them again.
                roomAppearance.SetWeather(weather);
                for (int index = 0; index < Layers.Length; index++)
                    roomAppearance.SetCosmetic(Layers[index], cosmetics[index]);
                if (windowVariants.Length > 0)
                    roomAppearance.SetWindowVariant(windowVariants, blind);
                Appearance.desktop.wallpaper.sprite = wallpaper;
                Appearance.recordImage.sprite = record;
                if (recordOpen) room.OpenRecord();
                else if (pcOpen)
                {
                    room.OpenPc();
                    if (shopOpen) Appearance.desktop.OpenShop();
                    else if (profileOpen) Appearance.desktop.OpenProfile();
                    else if (photosOpen) Appearance.desktop.OpenPhotos();
                    Appearance.desktop.SelectTab(tab);
                }
                if (focus != null && focus.activeInHierarchy) EventSystem.current.SetSelectedGameObject(focus);
                Check(Targets.Select(Appearance.SelectedId).SequenceEqual(selection)
                    && Appearance.recordImage.sprite == record && Appearance.desktop.wallpaper.sprite == wallpaper,
                    "Cleanup restores selections, record and wallpaper");
                var restoredState = new SerializedObject(roomAppearance);
                bool exactCosmetics = Enumerable.Range(0, Layers.Length).All(index =>
                    restoredState.FindProperty(CosmeticProperties[index]).objectReferenceValue == cosmetics[index]);
                bool validConfiguration = roomAppearance.ValidateConfiguration(out string reason);
                Check(Appearance.roomAppearance == roomAppearance && roomAppearance.room == room
                    && validConfiguration && roomAppearance.CurrentWeather == weather && exactCosmetics
                    && roomAppearance.WindowVariantTextures.SequenceEqual(windowVariants)
                    && roomAppearance.WindowBlindState == blind && roomAppearance.CompositeTexture != null
                    && roomAppearance.CompositeTexture.IsCreated()
                    && room.backgroundTexture == roomAppearance.CompositeTexture,
                    "Cleanup preserves live room references and restores weather, cosmetics, window variants, blinds and valid composite"
                        + (validConfiguration ? "" : ": " + reason));
            }
            public void WriteReport()
            {
                report.AppendLine("RESULT passed=" + passed + " failed=" + failed);
                File.WriteAllText(Path.Combine(directory, "cosmetic-checks.txt"), report.ToString());
                Debug.Log("Cosmetic checks: passed=" + passed + ", failed=" + failed);
            }
        }
    }
}
