using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gamsamo.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Gamsamo.Editor
{
    /// <summary>Checks actual GPU composites and equip routes, restoring the original display state.</summary>
    public static class RoomAppearanceSmokeChecks
    {
        private const string MenuPath = "Gamsamo/Run Room Appearance Checks";
        private static CheckRun current;
        private static IEnumerator steps;
        private static double resumeAt;
        private static double deadline;
        private static bool finishing;

        [MenuItem(MenuPath)]
        public static void Run()
        {
            var appearance = UnityEngine.Object.FindFirstObjectByType<ConsultationRoomAppearance>();
            if (!Application.isPlaying || current != null || appearance == null) return;
            if (EditorApplication.isPaused || appearance.room?.pcDesktopView == null)
            {
                Debug.LogWarning("Room appearance checks require an installed room in unpaused Play mode.");
                return;
            }
            if (appearance.room.pcDesktopView.IsSettingsOpen)
            {
                Debug.LogWarning("Close the settings window before running room checks so pending settings remain untouched.");
                return;
            }
            current = new CheckRun(appearance);
            Application.runInBackground = true;
            steps = CheckAppearance(current);
            finishing = false;
            resumeAt = EditorApplication.timeSinceStartup;
            deadline = resumeAt + 90d;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += AbortForEditorChange;
            EditorApplication.quitting += AbortForEditorChange;
        }

        [MenuItem(MenuPath, true)]
        private static bool CanRun()
        {
            return Application.isPlaying && !EditorApplication.isPaused && current == null
                && UnityEngine.Object.FindFirstObjectByType<ConsultationRoomAppearance>() != null;
        }

        private static void Tick()
        {
            if (current == null) { Unsubscribe(); return; }
            EditorApplication.QueuePlayerLoopUpdate();
            double now = EditorApplication.timeSinceStartup;
            if (!Application.isPlaying || EditorApplication.isPaused || current.Appearance == null || now > deadline)
            {
                current.Check(false, "Run remained active and unpaused within its 90-second deadline",
                    "playing=" + Application.isPlaying + ", paused=" + EditorApplication.isPaused);
                Finish();
                return;
            }
            if (now < resumeAt) return;
            if (finishing)
            {
                current.CheckRestoredState();
                Finish();
                return;
            }
            try
            {
                if (steps.MoveNext())
                {
                    resumeAt = now + (steps.Current is float seconds ? seconds : 0d);
                    return;
                }
            }
            catch (Exception exception)
            {
                current.Check(false, "Unexpected exception", exception.ToString());
            }
            current.Restore();
            finishing = true;
            resumeAt = now + 0.5d;
        }

        private static void AbortForEditorChange()
        {
            current?.Check(false, "Run was interrupted by editor reload or shutdown");
            Finish();
        }

        private static void Unsubscribe()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= AbortForEditorChange;
            EditorApplication.quitting -= AbortForEditorChange;
        }

        private static void Finish()
        {
            Unsubscribe();
            try
            {
                current?.Restore();
                current?.WriteReport();
            }
            finally
            {
                if (current != null)
                {
                    current.ReleaseFixtures();
                    Application.runInBackground = current.OriginalRunInBackground;
                }
                (steps as IDisposable)?.Dispose();
                steps = null;
                current = null;
                finishing = false;
            }
        }

        private static IEnumerator CheckAppearance(CheckRun run)
        {
            var appearance = run.Appearance;
            var room = run.Room;
            var desktop = run.Desktop;
            bool configuration = appearance.ValidateConfiguration(out string reason);
            run.Check(configuration, "Room appearance configuration is complete", reason);
            run.Check(desktop.roomAppearance == appearance && desktop.wallpaper != null && desktop.defaultWallpaper != null,
                "PC cosmetic routes reference the installed room and wallpaper");
            if (!configuration || desktop.wallpaper == null || desktop.defaultWallpaper == null) yield break;

            room.ClosePc();
            room.CloseRecord();
            appearance.RestoreDefaults();
            yield return 0.45f;
            run.Check(!room.IsPcOpen && !room.IsRecordOpen, "Fixture begins with the basic room and closed modals");
            RenderTexture composite = appearance.CompositeTexture;
            run.Check(composite != null && composite.IsCreated() && composite.width == 1920 && composite.height == 1080,
                "Composite is a created 1920 x 1080 RenderTexture");
            if (composite == null || !composite.IsCreated()) yield break;
            run.Check(appearance.editorPreviewLayers != null && !appearance.editorPreviewLayers.activeSelf,
                "Editor-only layer previews are disabled during Play mode");
            run.Check(room.backgroundTexture == composite && room.background.texture == composite,
                "Displayed room and controller share the composed texture");

            var weather = new[] { ConsultationRoomAppearance.Weather.Clear,
                ConsultationRoomAppearance.Weather.Cloudy, ConsultationRoomAppearance.Weather.Rain };
            var windows = new[] { appearance.clearWindow, appearance.cloudyWindow, appearance.rainWindow };
            var hashes = new string[3];
            // Begin on Rain so the first Clear request is a real change, not a deliberate no-op.
            appearance.SetWeather(ConsultationRoomAppearance.Weather.Rain);
            for (int index = 0; index < weather.Length; index++)
            {
                int versionBefore = appearance.CompositionVersion;
                int frameBefore = Time.frameCount;
                appearance.SetWeather(weather[index]);
                yield return 0.25f;
                string label = weather[index].ToString().ToLowerInvariant();
                run.Check(appearance.CurrentWeather == weather[index]
                    && appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Window) == windows[index],
                    label + " selects its expected window texture");
                run.Check(appearance.CompositionVersion > versionBefore,
                    label + " rebuilds the GPU composite", "version=" + versionBefore + " -> " + appearance.CompositionVersion);
                run.Check(appearance.CompositeTexture == composite && room.backgroundTexture == composite,
                    label + " reuses the existing room RenderTexture");
                hashes[index] = PixelHash(composite);
                run.Check(Time.frameCount > frameBefore, label + " advances game frames before capture",
                    "frames=" + frameBefore + " -> " + Time.frameCount + ", hash=" + hashes[index]);
                run.Capture("room-" + label + ".png");
                yield return 0.25f;
            }
            run.Check(hashes.Distinct().Count() == 3, "Clear, cloudy, and rain produce three different pixel hashes");

            room.OpenPc();
            yield return 0.4f;
            float pcBefore = BlurAmount(room);
            appearance.SetWeather(ConsultationRoomAppearance.Weather.Cloudy);
            run.Check(room.IsPcOpen && Mathf.Approximately(pcBefore, 1f) && Mathf.Approximately(BlurAmount(room), 1f),
                "Changing weather while PC is open preserves full blur", "before=" + pcBefore + ", after=" + BlurAmount(room));
            run.Check(room.backgroundTexture == composite && room.background.texture == composite,
                "PC-open weather change keeps the same display texture reference");
            yield return 0.2f;
            run.Capture("room-pc-weather-blur.png");
            yield return 0.25f;

            room.OpenRecord();
            yield return 0.4f;
            float recordBefore = BlurAmount(room);
            appearance.SetWeather(ConsultationRoomAppearance.Weather.Rain);
            run.Check(room.IsRecordOpen && !room.IsPcOpen && Mathf.Approximately(recordBefore, 1f)
                && Mathf.Approximately(BlurAmount(room), 1f),
                "Changing weather while record is open preserves full blur",
                "before=" + recordBefore + ", after=" + BlurAmount(room));
            run.Check(room.backgroundTexture == composite && appearance.CompositeTexture == composite,
                "Record-open weather change keeps the same RenderTexture");
            yield return 0.2f;
            run.Capture("room-record-weather-blur.png");
            yield return 0.25f;
            room.CloseRecord();
            yield return 0.4f;

            Sprite fixture = run.CreateFixture(appearance.cloudyWindow);
            int windowVersion = appearance.CompositionVersion;
            run.Check(desktop.TryApplyCosmetic("window", fixture)
                && appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Window) == fixture.texture
                && appearance.CompositionVersion > windowVersion,
                "Window equip route applies a full-texture cosmetic and rebuilds the room");
            string customWindowHash = PixelHash(composite);
            appearance.SetWeather(ConsultationRoomAppearance.Weather.Clear);
            run.Check(appearance.CurrentWeather == ConsultationRoomAppearance.Weather.Clear
                && appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Window) == fixture.texture
                && PixelHash(composite) == customWindowHash,
                "Custom window takes priority while the weather selection changes");
            run.Check(desktop.TryApplyCosmetic("window", null)
                && appearance.TextureForLayer(ConsultationRoomAppearance.CosmeticLayer.Window) == appearance.clearWindow
                && PixelHash(composite) == hashes[0],
                "Null window equip restores the currently selected weather and original clear pixels");

            string[] targets = { "door", "clipboard", "pc" };
            var layers = new[] { ConsultationRoomAppearance.CosmeticLayer.Door,
                ConsultationRoomAppearance.CosmeticLayer.Clipboard, ConsultationRoomAppearance.CosmeticLayer.Pc };
            var defaults = new[] { appearance.defaultDoor, appearance.defaultClipboard, appearance.defaultPc };
            for (int index = 0; index < targets.Length; index++)
            {
                string beforeHash = PixelHash(composite);
                int beforeVersion = appearance.CompositionVersion;
                run.Check(desktop.TryApplyCosmetic(targets[index], fixture)
                    && appearance.TextureForLayer(layers[index]) == fixture.texture
                    && appearance.CompositionVersion > beforeVersion,
                    targets[index] + " equip route updates the intended layer");
                run.Check(PixelHash(composite) != beforeHash, targets[index] + " equip changes rendered room pixels");
                run.Check(desktop.TryApplyCosmetic(targets[index], null)
                    && appearance.TextureForLayer(layers[index]) == defaults[index]
                    && PixelHash(composite) == beforeHash,
                    targets[index] + " reset restores its original layer and rendered pixels");
            }

            int wallpaperVersion = appearance.CompositionVersion;
            run.Check(desktop.TryApplyCosmetic("wallpaper", fixture) && desktop.wallpaper.sprite == fixture,
                "Wallpaper equip route installs the supplied Sprite");
            desktop.ResetDesktop();
            run.Check(desktop.wallpaper.sprite == fixture, "Desktop navigation reset preserves equipped wallpaper");
            run.Check(appearance.CompositionVersion == wallpaperVersion,
                "Wallpaper-only changes do not rebuild the room artwork");
            run.Check(desktop.TryApplyCosmetic("wallpaper", null)
                && desktop.wallpaper.sprite == desktop.defaultWallpaper,
                "Null wallpaper equip restores the default wallpaper");

            int unknownVersion = appearance.CompositionVersion;
            string unknownHash = PixelHash(composite);
            Sprite beforeUnknown = desktop.wallpaper.sprite;
            run.Check(!desktop.TryApplyCosmetic("unknown-cosmetic-target", fixture)
                && appearance.CompositionVersion == unknownVersion && desktop.wallpaper.sprite == beforeUnknown
                && PixelHash(composite) == unknownHash,
                "Unknown cosmetic target returns false without changing room or wallpaper");
            run.Check(room.backgroundTexture == composite && room.background.texture == composite
                && room.background.material.shader != room.blurShader,
                "Closed room displays the unblurred current composite after equip tests");
        }

        private static float BlurAmount(ConsultationRoomController room)
        {
            Material material = room.background.material;
            return material != null && material.shader == room.blurShader && material.HasProperty("_Amount")
                ? material.GetFloat("_Amount") : 0f;
        }

        private static string PixelHash(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            var readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(readback.GetRawTextureData())).Replace("-", "");
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        private sealed class CheckRun
        {
            public readonly ConsultationRoomAppearance Appearance;
            public readonly ConsultationRoomController Room;
            public readonly PcDesktopView Desktop;
            public readonly bool OriginalRunInBackground;
            private readonly ConsultationRoomAppearance.Weather originalWeather;
            private readonly Texture2D[] originalCosmetics;
            private readonly Sprite originalWallpaper;
            private readonly bool originalPcOpen;
            private readonly bool originalRecordOpen;
            private readonly bool originalShopOpen;
            private readonly bool originalProfileOpen;
            private readonly bool originalPhotosOpen;
            private readonly int originalTab;
            private readonly GameObject originalSelection;
            private readonly DateTime startedUtc = DateTime.UtcNow;
            private readonly string directory;
            private readonly StringBuilder report = new StringBuilder();
            private readonly List<string> captures = new List<string>();
            private readonly List<Sprite> fixtures = new List<Sprite>();
            private bool restored;
            private int passed;
            private int failed;
            private static readonly ConsultationRoomAppearance.CosmeticLayer[] Layers =
            {
                ConsultationRoomAppearance.CosmeticLayer.Window, ConsultationRoomAppearance.CosmeticLayer.Door,
                ConsultationRoomAppearance.CosmeticLayer.Clipboard, ConsultationRoomAppearance.CosmeticLayer.Pc
            };
            private static readonly string[] Properties = { "windowCosmetic", "doorCosmetic", "clipboardCosmetic", "pcCosmetic" };

            public CheckRun(ConsultationRoomAppearance appearance)
            {
                Appearance = appearance;
                Room = appearance.room;
                Desktop = Room.pcDesktopView;
                OriginalRunInBackground = Application.runInBackground;
                originalWeather = appearance.CurrentWeather;
                var serialized = new SerializedObject(appearance);
                originalCosmetics = Properties.Select(name => serialized.FindProperty(name).objectReferenceValue as Texture2D).ToArray();
                originalWallpaper = Desktop.wallpaper != null ? Desktop.wallpaper.sprite : null;
                originalPcOpen = Room.IsPcOpen;
                originalRecordOpen = Room.IsRecordOpen;
                originalShopOpen = Desktop.IsShopOpen;
                originalProfileOpen = Desktop.IsProfileOpen;
                originalPhotosOpen = Desktop.IsPhotosOpen;
                originalTab = Desktop.ActiveTabIndex;
                originalSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../artifacts/ui"));
                Directory.CreateDirectory(directory);
                report.AppendLine("Room appearance runtime checks");
                report.AppendLine("Unity " + Application.unityVersion + " | GPU " + SystemInfo.graphicsDeviceType);
                report.AppendLine("Pixel hashes read the runtime RenderTexture in memory; supplied PNGs are not modified.");
                report.AppendLine("Original runInBackground=" + OriginalRunInBackground + "; temporarily enabled for this run.");
                report.AppendLine();
            }

            public void Check(bool success, string label, string detail = null)
            {
                if (success) passed++; else failed++;
                report.AppendLine((success ? "PASS " : "FAIL ") + label);
                if (!string.IsNullOrEmpty(detail)) report.AppendLine("  " + detail);
            }

            public Sprite CreateFixture(Texture2D texture)
            {
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect);
                sprite.name = "Temporary Room Cosmetic Check";
                sprite.hideFlags = HideFlags.HideAndDontSave;
                fixtures.Add(sprite);
                return sprite;
            }

            public void Capture(string filename)
            {
                string path = Path.Combine(directory, filename);
                captures.Add(path);
                ScreenCapture.CaptureScreenshot(path);
            }

            public void Restore()
            {
                if (restored) return;
                restored = true;
                try
                {
                    if (Room != null) { Room.ClosePc(); Room.CloseRecord(); }
                    if (Appearance != null)
                    {
                        Appearance.SetWeather(originalWeather);
                        for (int index = 0; index < Layers.Length; index++)
                            Appearance.SetCosmetic(Layers[index], originalCosmetics[index]);
                    }
                    if (Desktop != null && Desktop.wallpaper != null)
                        Desktop.wallpaper.sprite = originalWallpaper;
                    if (Room != null && Application.isPlaying)
                    {
                        if (originalRecordOpen) Room.OpenRecord();
                        else if (originalPcOpen)
                        {
                            Room.OpenPc();
                            Desktop.SelectTab(originalTab);
                            if (originalShopOpen) { Desktop.OpenShop(); Desktop.SelectTab(originalTab); }
                            else if (originalProfileOpen) Desktop.OpenProfile();
                            else if (originalPhotosOpen) Desktop.OpenPhotos();
                        }
                    }
                }
                catch (Exception exception) { Check(false, "Cleanup restores original state", exception.ToString()); }
            }

            public void CheckRestoredState()
            {
                if (Appearance == null || Room == null || Desktop == null) return;
                var serialized = new SerializedObject(Appearance);
                bool cosmeticsRestored = Enumerable.Range(0, Layers.Length).All(index =>
                    serialized.FindProperty(Properties[index]).objectReferenceValue == originalCosmetics[index]);
                Check(Appearance.CurrentWeather == originalWeather && cosmeticsRestored,
                    "Cleanup restores original weather and exact cosmetic overrides");
                Check(Desktop.wallpaper != null && Desktop.wallpaper.sprite == originalWallpaper,
                    "Cleanup restores the original wallpaper Sprite");
                Check(Room.IsPcOpen == originalPcOpen && Room.IsRecordOpen == originalRecordOpen
                    && (!originalPcOpen || (Desktop.IsShopOpen == originalShopOpen
                        && Desktop.IsProfileOpen == originalProfileOpen && Desktop.IsPhotosOpen == originalPhotosOpen)),
                    "Cleanup restores the original room and nested-window open state");
                if (EventSystem.current != null && originalSelection != null && originalSelection.activeInHierarchy)
                    EventSystem.current.SetSelectedGameObject(originalSelection);
            }

            public void ReleaseFixtures()
            {
                foreach (var sprite in fixtures)
                    if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                fixtures.Clear();
            }

            public void WriteReport()
            {
                foreach (string path in captures)
                    Check(File.Exists(path) && new FileInfo(path).Length > 0
                        && File.GetLastWriteTimeUtc(path) >= startedUtc.AddSeconds(-1),
                        "Screenshot saved in this run: " + Path.GetFileName(path));
                string summary = (failed == 0 ? "PASS" : "FAIL") + ": " + passed + " passed, " + failed + " failed";
                report.AppendLine();
                report.AppendLine(summary);
                string pathToReport = Path.Combine(directory, "room-appearance-checks.txt");
                File.WriteAllText(pathToReport, report.ToString());
                if (failed == 0) Debug.Log("Room appearance checks " + summary + " — " + pathToReport);
                else Debug.LogError("Room appearance checks " + summary + " — " + pathToReport);
            }
        }
    }
}
