using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gamsamo.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Exercises the live PC UI. Editor updates keep the runner moving while menus have focus.</summary>
    public static class PcDesktopSmokeChecks
    {
        private const string MenuPath = "Gamsamo/Run PC UI Smoke Checks";
        private static CheckRun current;
        private static IEnumerator steps;
        private static double resumeAt;
        private static double deadline;
        private static bool finishing;

        [MenuItem(MenuPath)]
        public static void Run()
        {
            var room = UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>();
            if (!Application.isPlaying || room == null || current != null)
                return;
            if (EditorApplication.isPaused)
            {
                Debug.LogWarning("PC UI smoke checks require unpaused Play mode. Resume Play mode before running them.");
                return;
            }

            current = new CheckRun(room);
            Application.runInBackground = true;
            steps = CheckScene(current);
            resumeAt = EditorApplication.timeSinceStartup;
            deadline = resumeAt + 90d;
            finishing = false;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += FinishImmediately;
            EditorApplication.quitting += FinishImmediately;
        }

        [MenuItem(MenuPath, true)]
        private static bool CanRun()
        {
            return Application.isPlaying && !EditorApplication.isPaused && current == null
                && UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>() != null;
        }

        private static void Tick()
        {
            if (current == null)
            {
                EditorApplication.update -= Tick;
                return;
            }

            EditorApplication.QueuePlayerLoopUpdate();
            double now = EditorApplication.timeSinceStartup;
            if (!Application.isPlaying || current.Room == null)
            {
                current.Check(false, "Play mode remained active for the complete run");
                FinishImmediately();
                return;
            }
            if (EditorApplication.isPaused)
            {
                current.Check(false, "Play mode remained unpaused for the complete run");
                FinishImmediately();
                return;
            }
            if (now > deadline)
            {
                current.Check(false, "Run completed within 90 seconds");
                FinishImmediately();
                return;
            }
            if (now < resumeAt)
                return;

            if (finishing)
            {
                FinishImmediately();
                return;
            }

            try
            {
                if (steps.MoveNext())
                {
                    // Steps yield wall-clock seconds; no Game-view-dependent WaitForEndOfFrame.
                    resumeAt = now + (steps.Current is float seconds ? seconds : 0d);
                    return;
                }
            }
            catch (Exception exception)
            {
                current.Check(false, "Unexpected exception", exception.ToString());
            }

            CloseRoomWindows();
            finishing = true;
            resumeAt = now + 0.4d;
        }

        private static void CloseRoomWindows()
        {
            if (current?.Room == null)
                return;
            current.Room.ClosePc();
            current.Room.CloseRecord();
        }

        private static void FinishImmediately()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= FinishImmediately;
            EditorApplication.quitting -= FinishImmediately;
            try
            {
                CloseRoomWindows();
                current?.RestoreData();
                current?.WriteReport();
            }
            finally
            {
                if (current != null)
                    Application.runInBackground = current.OriginalRunInBackground;
                (steps as IDisposable)?.Dispose();
                steps = null;
                current = null;
                finishing = false;
            }
        }

        private static IEnumerator CheckScene(CheckRun run)
        {
            var room = run.Room;
            var desktop = room.pcDesktopView;
            bool references = room.pcModal != null && room.pcPanel != null
                && room.openPcButton != null && room.closePcButton != null && room.pcBackdropButton != null
                && room.modal != null && room.openRecordButton != null && room.closeRecordButton != null
                && room.background != null && room.backgroundTexture != null && room.blurShader != null
                && desktop != null && EventSystem.current != null;
            run.Check(references, "Room, PC, record, and EventSystem references are assigned");
            if (!references)
                yield break;

            bool desktopReferences = desktop.shopWindow != null && desktop.shopImage != null
                && desktop.shopButton != null && desktop.closeShopButton != null
                && desktop.profileButton != null && desktop.photosButton != null && desktop.settingsButton != null
                && desktop.shortcuts != null && desktop.profileView != null && desktop.photoView != null
                && desktop.settingsView != null && desktop.profileView.closeButton != null
                && desktop.photoView.closeButton != null && desktop.settingsView.closeButton != null
                && desktop.balanceText != null;
            run.Check(desktopReferences, "Desktop shortcuts, shop, profile, gallery, settings, and balance are assigned");
            if (!desktopReferences)
                yield break;

            room.ClosePc();
            room.CloseRecord();
            yield return 0.4f;
            Canvas.ForceUpdateCanvases();
            CheckRoomClosed(run, "Initial reset");

            var monitor = (RectTransform)room.openPcButton.transform;
            Vector2 inside = ScreenPoint(monitor, new Vector2(0.5f, 0.5f));
            Vector2 outside = ScreenPoint(monitor, new Vector2(-0.05f, 1.05f));
            var insideHits = Raycast(inside);
            var outsideHits = Raycast(outside);
            run.Check(FirstButton(insideHits) == room.openPcButton,
                "Monitor interior receives pointer input", DescribeHits(insideHits));
            run.Check(!outsideHits.Any(hit => hit.gameObject.GetComponentInParent<Button>() == room.openPcButton),
                "Outside the monitor does not trigger its hotspot", DescribeHits(outsideHits));

            EventSystem.current.SetSelectedGameObject(room.openPcButton.gameObject);
            int frameBeforeOpen = Time.frameCount;
            int renderedFrameBeforeOpen = Time.renderedFrameCount;
            room.openPcButton.onClick.Invoke();
            yield return 0.4f;
            Canvas.ForceUpdateCanvases();
            run.Check(room.IsPcOpen && room.pcModal.gameObject.activeInHierarchy
                && Mathf.Approximately(room.pcModal.alpha, 1f) && room.pcModal.blocksRaycasts
                && room.pcModal.interactable && !room.openPcButton.interactable
                && !room.openRecordButton.interactable && !room.IsRecordOpen,
                "Monitor click opens the PC and disables both room hotspots",
                "Game frames=" + frameBeforeOpen + " -> " + Time.frameCount
                    + ", advanced=" + (Time.frameCount > frameBeforeOpen)
                    + "; rendered frames=" + renderedFrameBeforeOpen + " -> " + Time.renderedFrameCount
                    + "; pcAlpha=" + room.pcModal.alpha + ", isPcOpen=" + room.IsPcOpen
                    + ", active=" + room.pcModal.gameObject.activeInHierarchy
                    + ", blocksRaycasts=" + room.pcModal.blocksRaycasts
                    + "; focused=" + Application.isFocused + ", paused=" + EditorApplication.isPaused
                    + ", runInBackground=" + Application.runInBackground
                    + "; Editor Screen=" + Screen.width + "x" + Screen.height);
            CheckBlur(run, "PC desktop");
            CheckPanelGeometry(run);
            CheckFreshDesktop(run, "Initial desktop");

            Vector2 clipboard = ScreenPoint((RectTransform)room.openRecordButton.transform, new Vector2(0.6f, 0.4f));
            var blockedHits = Raycast(clipboard);
            run.Check(FirstButton(blockedHits) != room.openPcButton
                && FirstButton(blockedHits) != room.openRecordButton && blockedHits.Count > 0,
                "PC overlay intercepts a click aimed at the room clipboard", DescribeHits(blockedHits));
            run.Capture("pc-desktop.png");
            yield return 0.2f;

            bool fiveTabs = desktop.tabButtons != null && desktop.tabButtons.Length == 5
                && desktop.tabButtons.All(button => button != null)
                && desktop.shopSprites != null && desktop.shopSprites.Length == 5
                && desktop.shopSprites.All(sprite => sprite != null);
            run.Check(fiveTabs, "Five category buttons and five shop sprites are assigned");
            if (fiveTabs)
            {
                run.Check(desktop.shopSprites.Distinct().Count() == 5, "Each shop category uses a distinct supplied sprite");
                desktop.shopButton.onClick.Invoke();
                yield return 0.15f;
                run.Check(desktop.IsShopOpen && desktop.shopWindow.activeInHierarchy
                    && !desktop.IsProfileOpen && !desktop.IsPhotosOpen && !desktop.IsSettingsOpen,
                    "Shop shortcut opens the shop window");
                run.Check(!desktop.shortcuts.interactable, "Open shop blocks desktop shortcut input");

                for (int index = 0; index < 5; index++)
                {
                    var button = desktop.tabButtons[index];
                    Vector2 tabPoint = ScreenPoint((RectTransform)button.transform, new Vector2(0.5f, 0.5f));
                    run.Check(FirstButton(Raycast(tabPoint)) == button,
                        "Category tab is reachable by pointer: " + index, DescribeHits(Raycast(tabPoint)));
                    button.onClick.Invoke();
                    yield return 0.1f;
                    run.Check(desktop.ActiveTabIndex == index && desktop.shopImage.sprite == desktop.shopSprites[index],
                        "Category selects the expected artwork: " + index);
                    CheckBlur(run, "Shop tab " + index);
                    run.Capture("pc-shop-tab-" + index + ".png");
                    yield return 0.15f;
                }

                Vector2 closeShop = ScreenPoint((RectTransform)desktop.closeShopButton.transform, new Vector2(0.5f, 0.5f));
                run.Check(FirstButton(Raycast(closeShop)) == desktop.closeShopButton,
                    "Shop close control receives pointer input", DescribeHits(Raycast(closeShop)));
                desktop.closeShopButton.onClick.Invoke();
                yield return 0.15f;
                run.Check(!desktop.IsShopOpen && !desktop.shopWindow.activeInHierarchy
                    && room.IsPcOpen && desktop.shortcuts.interactable,
                    "Closing the shop returns to desktop without closing the PC");
                CheckBlur(run, "After closing the internal shop");
            }

            var profile = desktop.profileView;
            desktop.profileButton.onClick.Invoke();
            yield return 0.2f;
            run.Check(desktop.IsProfileOpen && profile.gameObject.activeInHierarchy && !desktop.IsShopOpen
                && !desktop.IsPhotosOpen && !desktop.IsSettingsOpen && !desktop.shortcuts.interactable,
                "Profile shortcut opens the supplied profile window exclusively");
            bool profileFields = profile.nameText != null && profile.affiliationText != null && profile.introText != null
                && profile.backgroundImage != null && profile.femaleSprite != null && profile.maleSprite != null;
            run.Check(profileFields, "Profile fields and both visual avatar variants are assigned");
            if (profileFields)
            {
                profile.SetProfile(null, null, null, ProfileAvatar.Female);
                run.Check(profile.nameText.text == "—" && profile.affiliationText.text == "—" && profile.introText.text == "—"
                    && profile.Avatar == ProfileAvatar.Female && profile.backgroundImage.sprite == profile.femaleSprite,
                    "Unspecified profile values stay empty with the default female visual avatar");
                CheckText(run, profile.nameText, "profile name");
                CheckText(run, profile.affiliationText, "profile affiliation");
                CheckText(run, profile.introText, "profile introduction");
                run.Capture("pc-profile.png");
                yield return 0.2f;
                profile.SetProfile("UI 검사", "검사용", "프로필 표시 검사", ProfileAvatar.Male);
                run.Check(profile.backgroundImage.sprite == profile.maleSprite && profile.Avatar == ProfileAvatar.Male
                    && profile.nameText.text == "UI 검사" && profile.affiliationText.text == "검사용"
                    && profile.introText.text == "프로필 표시 검사", "Profile binding switches avatar and all supplied values");
                run.Check(!profile.nameText.richText && !profile.affiliationText.richText && !profile.introText.richText,
                    "Profile values are literal text");
                profile.SetProfile(null, null, null, ProfileAvatar.Female);
                run.Check(profile.nameText.text == "—" && profile.backgroundImage.sprite == profile.femaleSprite,
                    "Clearing profile data removes sample values and restores the requested visual avatar");
                run.RestoreProfile();
            }
            CheckButtonRaycast(run, profile.closeButton, "Profile close");
            profile.closeButton.onClick.Invoke();
            yield return 0.15f;
            CheckReturnedToDesktop(run, desktop.profileButton, "Profile close");

            var photos = desktop.photoView;
            desktop.photosButton.onClick.Invoke();
            yield return 0.2f;
            run.Check(desktop.IsPhotosOpen && photos.gameObject.activeInHierarchy && !desktop.IsShopOpen
                && !desktop.IsProfileOpen && !desktop.IsSettingsOpen && !desktop.shortcuts.interactable,
                "Photo shortcut opens the gallery exclusively");
            bool photoFields = photos.thumbnails != null && photos.thumbnails.Length == 6
                && photos.thumbnails.All(image => image != null) && photos.titles != null
                && photos.titles.Length == 6 && photos.titles.All(label => label != null) && photos.emptyMessage != null;
            run.Check(photoFields, "Gallery has six thumbnail/title pairs and an empty-state label");
            if (photoFields)
            {
                photos.SetPhotos(null);
                run.Check(photos.DisplayedPhotoCount == 0 && photos.thumbnails.All(image => !image.enabled && image.sprite == null)
                    && photos.titles.All(label => string.IsNullOrEmpty(label.text)) && photos.emptyMessage.gameObject.activeSelf,
                    "An empty gallery does not invent acquired photos or achievement captions");
                var cover = photos.transform.Find("ContentCover");
                var coverImage = cover != null ? cover.GetComponent<Image>() : null;
                run.Check(coverImage != null && coverImage.isActiveAndEnabled && Mathf.Approximately(coverImage.color.a, 1f),
                    "Opaque UI covers the baked example achievement before the empty slots");
                CheckText(run, photos.emptyMessage, "empty gallery");
                run.Capture("pc-photos.png");
                yield return 0.2f;
                Sprite sampleThumbnail = desktop.defaultWallpaper != null ? desktop.defaultWallpaper : profile.femaleSprite;
                run.Check(sampleThumbnail != null, "A supplied sprite is available for temporary thumbnail binding checks");
                if (sampleThumbnail != null)
                {
                    var samplePhotos = Enumerable.Range(0, 7).Select(index => new PhotoGalleryEntry
                    { thumbnail = sampleThumbnail, title = "검사 " + (index + 1) }).ToArray();
                    photos.SetPhotos(samplePhotos);
                    run.Check(photos.DisplayedPhotoCount == 6 && photos.thumbnails.All(image => image.enabled
                        && image.sprite == sampleThumbnail) && photos.titles[5].text == "검사 6"
                        && !photos.emptyMessage.gameObject.activeSelf, "Photo binding clips seven supplied entries to six slots");
                    photos.SetPhotos(new[] { null, new PhotoGalleryEntry { title = "표시 안 함" }, samplePhotos[2] });
                    run.Check(photos.DisplayedPhotoCount == 1 && !photos.thumbnails[0].enabled && !photos.thumbnails[1].enabled
                        && photos.thumbnails[2].enabled && photos.titles[0].text == "" && photos.titles[1].text == "",
                        "Null entries and missing thumbnails stay empty without shifting other slots");
                    photos.SetPhotos(null);
                    run.Check(photos.DisplayedPhotoCount == 0 && photos.emptyMessage.gameObject.activeSelf
                        && photos.thumbnails.All(image => !image.enabled && image.sprite == null),
                        "Clearing gallery data removes all temporary thumbnail content");
                }
                run.RestorePhotos();
            }
            CheckButtonRaycast(run, photos.closeButton, "Photo gallery close");
            photos.closeButton.onClick.Invoke();
            yield return 0.15f;
            CheckReturnedToDesktop(run, desktop.photosButton, "Photo gallery close");

            var settings = desktop.settingsView;
            desktop.settingsButton.onClick.Invoke();
            yield return 0.2f;
            run.Check(desktop.IsSettingsOpen && settings.gameObject.activeInHierarchy && settings.IsEditing
                && !desktop.IsShopOpen && !desktop.IsProfileOpen && !desktop.IsPhotosOpen && !desktop.shortcuts.interactable,
                "Settings shortcut opens an isolated settings draft");
            bool settingsFields = settings.masterSlider != null && settings.musicSlider != null && settings.effectsSlider != null
                && settings.resolutionDropdown != null && settings.modeDropdown != null && settings.masterPercent != null
                && settings.musicPercent != null && settings.effectsPercent != null && settings.applyButton != null
                && settings.secondaryCloseButton != null;
            run.Check(settingsFields, "Settings dropdowns, sliders, values, apply, and both close controls are assigned");
            if (settingsFields)
            {
                run.Check(settings.resolutionDropdown.options.Count > 0 && settings.modeDropdown.options.Count == 3,
                    "Settings offers supported resolutions and three window modes");
                CheckText(run, settings.masterPercent, "master volume");
                CheckText(run, settings.musicPercent, "music volume");
                CheckText(run, settings.effectsPercent, "effects volume");
                CheckText(run, settings.resolutionDropdown.captionText, "resolution caption");
                CheckText(run, settings.modeDropdown.captionText, "screen mode caption");
                run.Capture("pc-settings.png");
                yield return 0.2f;

                GameSettings.Values committed = GameSettings.Current;
                string committedJson = JsonUtility.ToJson(committed);
                float masterBeforeDraft = AudioListener.volume;
                settings.masterSlider.value = committed.masterVolume > 0.5f ? 23f : 77f;
                settings.musicSlider.value = committed.musicVolume > 0.5f ? 31f : 69f;
                settings.effectsSlider.value = committed.effectsVolume > 0.5f ? 42f : 58f;
                run.Check(settings.HasUnappliedChanges && JsonUtility.ToJson(GameSettings.Current) == committedJson
                    && Mathf.Approximately(AudioListener.volume, masterBeforeDraft) && run.SettingsPrefsUnchanged(),
                    "Editing sliders changes only the draft, not audio or saved settings");
                settings.CancelEdit();
                run.Check(!settings.IsEditing && !settings.HasUnappliedChanges
                    && JsonUtility.ToJson(settings.Draft) == committedJson && run.SettingsPrefsUnchanged(),
                    "Cancel restores committed settings without writing preferences");
                settings.BeginEdit();
                run.Check(settings.IsEditing && !settings.HasUnappliedChanges
                    && Mathf.Approximately(settings.masterSlider.value, Mathf.RoundToInt(committed.masterVolume * 100f)),
                    "BeginEdit rebuilds controls from committed values");

                settings.modeDropdown.Show();
                yield return 0.2f;
                run.Check(settings.modeDropdown.IsExpanded, "Screen-mode dropdown opens its real option list");
                run.Check(desktop.CloseActiveWindow() && desktop.IsSettingsOpen && room.IsPcOpen,
                    "Nested close consumes an expanded dropdown before closing settings");
                yield return 0.3f;
                run.Check(!settings.modeDropdown.IsExpanded && !settings.TryCloseDropdown(),
                    "Dropdown closes completely and no longer consumes subsequent close requests");

                CheckButtonRaycast(run, settings.applyButton, "Settings apply");
                settings.masterSlider.value = committed.masterVolume > 0.5f ? 37f : 83f;
                float appliedMaster = settings.Draft.masterVolume;
                settings.applyButton.onClick.Invoke();
                run.Check(!settings.HasUnappliedChanges && Mathf.Approximately(GameSettings.Current.masterVolume, appliedMaster)
                    && Mathf.Approximately(AudioListener.volume, appliedMaster)
                    && PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey), "Apply commits the draft and updates runtime master volume");
                run.RestoreSettings();
                settings.BeginEdit();
                run.Check(run.SettingsPrefsUnchanged(), "The exact original settings preference is restored after Apply verification");
                CheckButtonRaycast(run, settings.secondaryCloseButton, "Settings lower close");
            }
            CheckButtonRaycast(run, settings.closeButton, "Settings title close");
            settings.closeButton.onClick.Invoke();
            yield return 0.15f;
            CheckReturnedToDesktop(run, desktop.settingsButton, "Settings close");
            run.Check(!settings.IsEditing, "Closing settings cancels its editable draft");
            run.Check(desktop.balanceText.text.Trim() == "—", "Unconnected balance uses the explicit empty value");
            CheckText(run, desktop.balanceText, "balance");

            desktop.OpenShop();
            if (fiveTabs) desktop.SelectTab(4);
            run.Check(desktop.CloseActiveWindow() && !desktop.IsShopOpen && room.IsPcOpen,
                "Nested-window close consumes the shop without closing the PC");
            run.Check(!desktop.CloseActiveWindow(), "Nested-window close reports false on the plain desktop");
            desktop.OpenShop();
            if (fiveTabs) desktop.SelectTab(4);
            room.pcBackdropButton.onClick.Invoke();
            yield return 0.15f;
            run.Check(!desktop.IsShopOpen && room.IsPcOpen,
                "PC backdrop closes the nested shop before the whole desktop");
            CheckBlur(run, "After backdrop closes the nested shop");
            desktop.OpenShop();
            if (fiveTabs) desktop.SelectTab(4);
            room.ClosePc();
            yield return 0.4f;
            CheckRoomClosed(run, "Closing the PC with a shop tab open");
            room.OpenPc();
            yield return 0.4f;
            CheckFreshDesktop(run, "Reopened PC");

            room.OpenRecord();
            yield return 0.4f;
            run.Check(room.IsPcOpen != room.IsRecordOpen
                && !(room.pcModal.gameObject.activeInHierarchy && room.modal.gameObject.activeInHierarchy),
                "Requesting record while PC is open never leaves two modals active");
            room.CloseRecord();
            room.ClosePc();
            yield return 0.4f;
            room.OpenRecord();
            yield return 0.4f;
            room.OpenPc();
            yield return 0.4f;
            run.Check(room.IsPcOpen != room.IsRecordOpen
                && !(room.pcModal.gameObject.activeInHierarchy && room.modal.gameObject.activeInHierarchy),
                "Requesting PC while record is open never leaves two modals active");

            room.ClosePc();
            room.CloseRecord();
            room.OpenPc();
            room.ClosePc();
            room.OpenPc();
            yield return 0.45f;
            run.Check(room.IsPcOpen && !room.IsRecordOpen && room.pcModal.gameObject.activeInHierarchy
                && Mathf.Approximately(room.pcModal.alpha, 1f) && room.pcModal.blocksRaycasts,
                "Rapid close and reopen settles on the visible PC");
            CheckBlur(run, "After rapid reopening");

            room.closePcButton.onClick.Invoke();
            yield return 0.4f;
            CheckRoomClosed(run, "PC close button");
            run.Check(EventSystem.current.currentSelectedGameObject == room.openPcButton.gameObject,
                "PC close restores keyboard focus to its room hotspot");
            room.OpenPc();
            yield return 0.4f;
            room.pcBackdropButton.onClick.Invoke();
            yield return 0.4f;
            CheckRoomClosed(run, "PC backdrop close");
            run.Capture("pc-room-restored.png");
            yield return 0.25f;
        }

        private static void CheckFreshDesktop(CheckRun run, string action)
        {
            var desktop = run.Room.pcDesktopView;
            run.Check(!desktop.IsShopOpen && !desktop.shopWindow.activeInHierarchy
                && !desktop.IsProfileOpen && !desktop.IsPhotosOpen && !desktop.IsSettingsOpen
                && (desktop.appWindow == null || !desktop.appWindow.activeInHierarchy) && desktop.ActiveTabIndex == 0
                && desktop.shortcuts.interactable && desktop.shortcuts.blocksRaycasts,
                action + " resets windows, tab selection, and shortcut input");
        }

        private static void CheckButtonRaycast(CheckRun run, Button button, string action)
        {
            if (button == null)
            {
                run.Check(false, action + " control is assigned");
                return;
            }
            Canvas.ForceUpdateCanvases();
            var hits = Raycast(ScreenPoint((RectTransform)button.transform, new Vector2(0.5f, 0.5f)));
            run.Check(FirstButton(hits) == button, action + " receives pointer input", DescribeHits(hits));
        }

        private static void CheckReturnedToDesktop(CheckRun run, Button opener, string action)
        {
            var desktop = run.Room.pcDesktopView;
            run.Check(!desktop.IsProfileOpen && !desktop.IsPhotosOpen && !desktop.IsSettingsOpen
                && !desktop.IsShopOpen && run.Room.IsPcOpen && desktop.shortcuts.interactable
                && desktop.shortcuts.blocksRaycasts, action + " returns to the desktop");
            run.Check(EventSystem.current.currentSelectedGameObject == opener.gameObject,
                action + " restores focus to the opening shortcut");
            CheckBlur(run, "After " + action);
        }

        private static void CheckRoomClosed(CheckRun run, string action)
        {
            var room = run.Room;
            run.Check(!room.IsPcOpen && !room.IsRecordOpen
                && !room.pcModal.gameObject.activeInHierarchy && !room.modal.gameObject.activeInHierarchy
                && !room.pcModal.blocksRaycasts && !room.modal.blocksRaycasts
                && Mathf.Approximately(room.pcModal.alpha, 0f)
                && room.openPcButton.interactable && room.openRecordButton.interactable,
                action + " restores both room hotspots and closes both modals");
            run.Check(room.background.texture == room.backgroundTexture
                && room.background.material.shader != room.blurShader,
                action + " restores original background without blur");
        }

        private static void CheckBlur(CheckRun run, string action)
        {
            Material material = run.Room.background.material;
            run.Check(material != null && material.shader == run.Room.blurShader
                && material.HasProperty("_Amount") && Mathf.Approximately(material.GetFloat("_Amount"), 1f),
                action + " keeps the room fully blurred");
        }

        private static void CheckPanelGeometry(CheckRun run)
        {
            RectTransform panel = run.Room.pcPanel;
            Vector2 lower = ScreenPoint(panel, Vector2.zero);
            Vector2 upper = ScreenPoint(panel, Vector2.one);
            Vector2 center = (lower + upper) * 0.5f;
            var canvasRect = (RectTransform)panel.GetComponentInParent<Canvas>().rootCanvas.transform;
            Vector2 canvasLower = ScreenPoint(canvasRect, Vector2.zero);
            Vector2 canvasUpper = ScreenPoint(canvasRect, Vector2.one);
            Vector2 canvasCenter = (canvasLower + canvasUpper) * 0.5f;
            // Screen.width/height can describe a stale Editor view during EditorApplication.update.
            // Compare both rectangles in the actual root Canvas's rendered coordinate system.
            float tolerance = Mathf.Max(4f, Mathf.Abs(canvasUpper.y - canvasLower.y) * 0.005f);
            run.Check(Vector2.Distance(center, canvasCenter) <= tolerance,
                "PC panel is centered in the root canvas", "Panel center=" + center + ", canvas center=" + canvasCenter);
            run.Check(lower.x >= canvasLower.x - tolerance && lower.y >= canvasLower.y - tolerance
                && upper.x <= canvasUpper.x + tolerance && upper.y <= canvasUpper.y + tolerance,
                "PC panel fits inside the root canvas", "Panel lower=" + lower + ", upper=" + upper
                    + "; canvas lower=" + canvasLower + ", upper=" + canvasUpper);
            var roomFrame = canvasRect.Find("RoomFrame") as RectTransform;
            if (roomFrame == null)
            {
                run.Check(false, "RoomFrame is available for PC sizing checks");
            }
            else
            {
                Vector2 roomLower = ScreenPoint(roomFrame, Vector2.zero);
                Vector2 roomUpper = ScreenPoint(roomFrame, Vector2.one);
                run.Check(lower.x >= roomLower.x - tolerance && lower.y >= roomLower.y - tolerance
                    && upper.x <= roomUpper.x + tolerance && upper.y <= roomUpper.y + tolerance,
                    "PC panel fits completely inside the rendered room",
                    "Panel lower=" + lower + ", upper=" + upper
                        + "; room lower=" + roomLower + ", upper=" + roomUpper);
                float panelWidth = upper.x - lower.x;
                float roomWidth = roomUpper.x - roomLower.x;
                run.Check(panelWidth <= roomWidth * 0.82f + tolerance,
                    "PC width stays within 82 percent of the rendered room",
                    "Panel width=" + panelWidth + ", room width=" + roomWidth
                        + ", maximum=" + roomWidth * 0.82f + ", tolerance=" + tolerance);
            }
            var fitter = panel.GetComponent<AspectRatioFitter>();
            if (fitter != null && panel.rect.height > 0f)
                run.Check(Mathf.Abs(panel.rect.width / panel.rect.height - fitter.aspectRatio) < 0.01f,
                    "PC panel preserves its authored aspect ratio",
                    "Actual=" + panel.rect.width / panel.rect.height + ", expected=" + fitter.aspectRatio);
        }

        private static void CheckText(CheckRun run, TMP_Text text, string label)
        {
            if (text == null)
            {
                run.Check(false, "Text is assigned: " + label);
                return;
            }
            text.ForceMeshUpdate(true);
            run.Check(!text.isTextOverflowing, "Text fits: " + label, text.text);
            string missing = text.font == null ? "[font not assigned]"
                : new string(text.text.Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character)
                    && !text.font.HasCharacter(character, true, false)).Distinct().ToArray());
            run.Check(string.IsNullOrEmpty(missing), "Glyph coverage: " + label, missing);
        }

        private static Vector2 ScreenPoint(RectTransform rectTransform, Vector2 normalized)
        {
            Rect rect = rectTransform.rect;
            Vector3 world = rectTransform.TransformPoint(new Vector3(
                rect.xMin + rect.width * normalized.x, rect.yMin + rect.height * normalized.y, 0f));
            Canvas canvas = rectTransform.GetComponentInParent<Canvas>().rootCanvas;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.WorldToScreenPoint(camera, world);
        }

        private static List<RaycastResult> Raycast(Vector2 position)
        {
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, results);
            return results;
        }

        private static Button FirstButton(List<RaycastResult> hits)
        {
            return hits.Count == 0 ? null : hits[0].gameObject.GetComponentInParent<Button>();
        }

        private static string DescribeHits(List<RaycastResult> hits)
        {
            return hits.Count == 0 ? "No hits" : string.Join(", ", hits.Select(hit => hit.gameObject.name));
        }

        private sealed class CheckRun
        {
            public readonly ConsultationRoomController Room;
            public readonly bool OriginalRunInBackground;
            private readonly string outputDirectory;
            private readonly StringBuilder report = new StringBuilder();
            private readonly List<string> captures = new List<string>();
            private readonly DateTime startedUtc = DateTime.UtcNow;
            private readonly ProfileView originalProfile;
            private readonly string originalProfileName;
            private readonly string originalAffiliation;
            private readonly string originalIntro;
            private readonly ProfileAvatar originalAvatar;
            private readonly PhotoGalleryView originalGallery;
            private readonly PhotoGalleryEntry[] originalPhotos;
            private readonly GameSettings.Values originalSettings;
            private readonly bool hadSettingsPrefs;
            private readonly string originalSettingsPrefs;
            private readonly float originalListenerVolume;
            private int passed;
            private int failed;

            public CheckRun(ConsultationRoomController room)
            {
                Room = room;
                OriginalRunInBackground = Application.runInBackground;
                originalListenerVolume = AudioListener.volume;
                originalSettings = GameSettings.Current;
                hadSettingsPrefs = PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey);
                originalSettingsPrefs = PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "");
                var desktop = room.pcDesktopView;
                originalProfile = desktop != null ? desktop.profileView : null;
                if (originalProfile != null)
                {
                    var serialized = new SerializedObject(originalProfile);
                    originalProfileName = serialized.FindProperty("profileName").stringValue;
                    originalAffiliation = serialized.FindProperty("affiliation").stringValue;
                    originalIntro = serialized.FindProperty("introduction").stringValue;
                    originalAvatar = originalProfile.Avatar;
                }
                originalGallery = desktop != null ? desktop.photoView : null;
                if (originalGallery != null)
                {
                    var serialized = new SerializedObject(originalGallery);
                    SerializedProperty entries = serialized.FindProperty("entries");
                    originalPhotos = new PhotoGalleryEntry[entries.arraySize];
                    for (int index = 0; index < entries.arraySize; index++)
                    {
                        SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                        originalPhotos[index] = new PhotoGalleryEntry
                        {
                            thumbnail = entry.FindPropertyRelative("thumbnail").objectReferenceValue as Sprite,
                            title = entry.FindPropertyRelative("title").stringValue
                        };
                    }
                }
                outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../artifacts/ui"));
                Directory.CreateDirectory(outputDirectory);
                report.AppendLine("PC desktop UI smoke checks");
                report.AppendLine("Unity " + Application.unityVersion);
                report.AppendLine("Application.runInBackground=" + OriginalRunInBackground
                    + " before the run; temporarily enabled for checks and restored during cleanup.");
                var canvas = room.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    var canvasRect = (RectTransform)canvas.rootCanvas.transform;
                    Vector2 lower = ScreenPoint(canvasRect, Vector2.zero);
                    Vector2 upper = ScreenPoint(canvasRect, Vector2.one);
                    report.AppendLine("Root canvas rendered size=" + (upper - lower)
                        + " | lower=" + lower + ", upper=" + upper);
                }
                report.AppendLine("Editor Screen API=" + Screen.width + " x " + Screen.height
                    + " (diagnostic only; geometry checks use the root canvas)");
                report.AppendLine("Mouse and physical Escape-key validation are separate manual checks.");
                report.AppendLine("Temporary profile/photos and game settings are restored; unrelated PlayerPrefs keys are untouched.");
                report.AppendLine();
            }

            public void RestoreProfile()
            {
                if (originalProfile != null)
                    originalProfile.SetProfile(originalProfileName, originalAffiliation, originalIntro, originalAvatar);
            }

            public void RestorePhotos()
            {
                if (originalGallery != null)
                    originalGallery.SetPhotos(originalPhotos);
            }

            public bool SettingsPrefsUnchanged()
            {
                return PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey) == hadSettingsPrefs
                    && PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "") == originalSettingsPrefs;
            }

            public void RestoreSettings()
            {
                try
                {
                    // Apply also restores registered audio source gains; Editor never changes screen resolution here.
                    GameSettings.Apply(originalSettings);
                }
                finally
                {
                    AudioListener.volume = originalListenerVolume;
                    if (hadSettingsPrefs)
                        PlayerPrefs.SetString(GameSettings.PlayerPrefsKey, originalSettingsPrefs);
                    else
                        PlayerPrefs.DeleteKey(GameSettings.PlayerPrefsKey);
                    PlayerPrefs.Save();
                }
            }

            public void RestoreData()
            {
                RestoreStep("profile", RestoreProfile);
                RestoreStep("photos", RestorePhotos);
                RestoreStep("settings draft", () =>
                {
                    if (Room != null && Room.pcDesktopView != null && Room.pcDesktopView.settingsView != null)
                        Room.pcDesktopView.settingsView.CancelEdit();
                });
                RestoreStep("committed settings and preferences", RestoreSettings);
                Check(SettingsPrefsUnchanged(), "Cleanup preserves the original settings key and value");
            }

            private void RestoreStep(string label, Action restore)
            {
                try { restore(); }
                catch (Exception exception)
                {
                    Check(false, "Restore " + label, exception.ToString());
                }
            }

            public void Check(bool success, string name, string detail = null)
            {
                if (success) passed++; else failed++;
                report.AppendLine((success ? "PASS " : "FAIL ") + name);
                if (!string.IsNullOrEmpty(detail)) report.AppendLine("  " + detail);
            }

            public void Capture(string filename)
            {
                string path = Path.Combine(outputDirectory, filename);
                captures.Add(path);
                ScreenCapture.CaptureScreenshot(path);
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
                string reportPath = Path.Combine(outputDirectory, "pc-smoke-checks.txt");
                File.WriteAllText(reportPath, report.ToString());
                if (failed == 0) Debug.Log("PC UI smoke checks " + summary + " — " + reportPath);
                else Debug.LogError("PC UI smoke checks " + summary + " — " + reportPath);
            }
        }
    }
}
