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
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor
{
    /// <summary>Focused title checks. Finishes in the consultation scene; does not exercise its features.</summary>
    public static class StartMenuSmokeChecks
    {
        private const string Menu = "Gamsamo/Run Start Menu Checks";
        private static IEnumerator steps;
        private static double nextStep, deadline;
        private static int passed, failed;
        private static StringBuilder report;
        private static GameSettings.Values originalSettings;
        private static bool hadSettings, originalBackground;
        private static string originalJson;

        [MenuItem(Menu, true)]
        private static bool CanRun() => Application.isPlaying && !EditorApplication.isPaused && steps == null
            && UnityEngine.Object.FindFirstObjectByType<StartMenuController>() != null;

        [MenuItem(Menu)]
        public static void Run()
        {
            if (!CanRun()) return;
            var menu = UnityEngine.Object.FindFirstObjectByType<StartMenuController>();
            if (menu.IsSettingsOpen || menu.IsQuitOpen || menu.IsBusy)
            {
                Debug.LogWarning("Return to the idle title before running start menu checks.");
                return;
            }
            passed = failed = 0;
            report = new StringBuilder("Start menu focused checks | Unity " + Application.unityVersion + "\n");
            originalSettings = GameSettings.Current;
            hadSettings = PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey);
            originalJson = PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "");
            originalBackground = Application.runInBackground;
            Application.runInBackground = true;
            Directory.CreateDirectory("artifacts/start-menu");
            steps = Exercise(menu);
            deadline = EditorApplication.timeSinceStartup + 40;
            nextStep = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Finish;
            EditorApplication.quitting += Finish;
        }

        private static void Tick()
        {
            if (steps == null) return;
            EditorApplication.QueuePlayerLoopUpdate();
            if (!Application.isPlaying || EditorApplication.isPaused || EditorApplication.timeSinceStartup > deadline)
            { Check(false, "Run stays in unpaused Play mode within deadline"); Finish(); return; }
            if (EditorApplication.timeSinceStartup < nextStep) return;
            try
            {
                if (steps.MoveNext())
                {
                    nextStep = EditorApplication.timeSinceStartup + (steps.Current is float seconds ? seconds : 0);
                    return;
                }
            }
            catch (Exception exception) { Check(false, "Unexpected exception: " + exception); }
            Finish();
        }

        private static IEnumerator Exercise(StartMenuController menu)
        {
            Check(EditorBuildSettings.scenes.First(entry => entry.enabled).path == StartMenuSceneBuilder.ScenePath,
                "StartMenu is the first enabled build scene");
            Check(SceneManager.GetActiveScene().name == "StartMenu" && EventSystem.current != null,
                "Title scene has its event system");
            Check(!menu.loadButton.IsInteractable() && menu.saveStatus.text == "저장된 진행이 없습니다",
                "Continue stays disabled without game-progress storage");
            Check(menu.menuItems[0].dot.activeSelf && menu.menuItems[0].underline.activeSelf
                && EventSystem.current.currentSelectedGameObject == menu.newGameButton.gameObject,
                "New game is initially selected");
            Check(menu.menuItems.Where(item => item.button.interactable).All(item => Hit(item.button) == item.button),
                "Visible title menu buttons receive pointer input");
            var overflow = menu.GetComponentsInChildren<TMP_Text>().Where(text => text.isTextOverflowing).ToArray();
            Check(overflow.Length == 0, "Title and menu text fit their rectangles: "
                + string.Join(", ", overflow.Select(text => text.name)));
            var shade = menu.GetComponentInChildren<StartMenuShade>();
            Check(shade != null && shade.GetComponent<CanvasRenderer>() != null && shade.rectTransform.rect.width > 0,
                "Title gradient has its renderer and a visible-sized rectangle");
            ScreenCapture.CaptureScreenshot("artifacts/start-menu/unity-title.png");
            yield return 0.2f;
            ExecuteEvents.Execute(menu.settingsButton.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerEnterHandler);
            Check(menu.menuItems[2].dot.activeSelf && !menu.menuItems[0].dot.activeSelf,
                "Hover moves the shared selection marker");
            var down = new AxisEventData(EventSystem.current) { moveDir = MoveDirection.Down };
            ExecuteEvents.Execute(menu.newGameButton.gameObject, down, ExecuteEvents.moveHandler);
            Check(EventSystem.current.currentSelectedGameObject == menu.settingsButton.gameObject,
                "Keyboard navigation skips the disabled continue button");
            ExecuteEvents.Execute(menu.settingsButton.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            yield return 0.15f;
            Check(menu.IsSettingsOpen && !menu.menuGroup.interactable && Hit(menu.newGameButton) != menu.newGameButton,
                "Settings blocks the title menu and its raycasts");
            menu.StartNewGame();
            Check(!menu.IsBusy && SceneManager.GetActiveScene().name == "StartMenu", "Open modal prevents scene entry");
            var view = menu.settingsView;
            float percent = originalSettings.masterVolume > 0.5f ? 25 : 75;
            view.masterSlider.value = percent;
            Check(Mathf.Approximately(GameSettings.Current.masterVolume, originalSettings.masterVolume)
                && PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "") == originalJson,
                "Editing a volume does not commit preferences");
            view.closeButton.onClick.Invoke();
            Check(!menu.IsSettingsOpen && EventSystem.current.currentSelectedGameObject == menu.settingsButton.gameObject,
                "Cancel closes settings and restores menu focus");
            menu.OpenSettings();
            Check(Mathf.Approximately(view.masterSlider.value, Mathf.Round(originalSettings.masterVolume * 100)),
                "Reopening settings discards the canceled draft");
            view.masterSlider.value = percent;
            view.applyButton.onClick.Invoke();
            Check(Mathf.Approximately(GameSettings.Current.masterVolume, percent / 100)
                && Mathf.Approximately(AudioListener.volume, percent / 100)
                && PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey) && menu.settingsStatus.text.Length > 0,
                "Apply commits through shared GameSettings and updates runtime volume");
            ScreenCapture.CaptureScreenshot("artifacts/start-menu/unity-settings.png");
            yield return 0.15f;
            view.modeDropdown.Show();
            yield return 0.15f;
            menu.CloseModal();
            yield return 0.2f; // TMP keeps its list alive while the close animation runs.
            Check(menu.IsSettingsOpen && !view.modeDropdown.IsExpanded, "Close first dismisses an expanded dropdown");
            menu.CloseModal();
            Check(!menu.IsSettingsOpen, "Next close returns from settings to the title");
            menu.OpenQuit();
            Check(menu.IsQuitOpen && !menu.menuGroup.interactable
                && EventSystem.current.currentSelectedGameObject == menu.cancelQuitButton.gameObject,
                "Quit confirmation blocks the menu and defaults to cancel");
            ExecuteEvents.Execute(menu.cancelQuitButton.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
            Check(!menu.IsQuitOpen && !menu.QuitRequested, "Submitting cancel does not request exit");
            menu.OpenQuit();
            menu.confirmQuitButton.onClick.Invoke();
            Check(menu.QuitRequested && Application.isPlaying && !menu.IsQuitOpen,
                "Editor quit confirmation is handled without terminating the Editor");
            RestoreSettings();
            string beforeTransition = JsonUtility.ToJson(GameSettings.Current);
            menu.newGameButton.onClick.Invoke();
            menu.StartNewGame();
            Check(menu.IsBusy && !menu.menuGroup.interactable, "New game locks input immediately, including a second request");
            for (int i = 0; i < 30 && SceneManager.GetActiveScene().name != "ConsultationRoom"; i++) yield return 0.1f;
            yield return 0.2f;
            Check(SceneManager.GetActiveScene().name == "ConsultationRoom" && SceneManager.sceneCount == 1
                && UnityEngine.Object.FindFirstObjectByType<ConsultationRoomController>() != null,
                "New game loads exactly one consultation scene");
            Check(UnityEngine.Object.FindFirstObjectByType<StartMenuController>() == null
                && JsonUtility.ToJson(GameSettings.Current) == beforeTransition,
                "Scene transition removes title UI and preserves shared preferences");
        }

        private static Button Hit(Button button)
        {
            var point = RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)button.transform).TransformPoint(
                ((RectTransform)button.transform).rect.center));
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.Count > 0 ? hits[0].gameObject.GetComponentInParent<Button>() : null;
        }

        private static void Check(bool value, string label)
        {
            if (value) passed++; else failed++;
            report.AppendLine((value ? "PASS " : "FAIL ") + label);
        }

        private static void RestoreSettings()
        {
            if (JsonUtility.ToJson(GameSettings.Current) != JsonUtility.ToJson(originalSettings)) GameSettings.Apply(originalSettings);
            if (hadSettings) PlayerPrefs.SetString(GameSettings.PlayerPrefsKey, originalJson);
            else PlayerPrefs.DeleteKey(GameSettings.PlayerPrefsKey);
            PlayerPrefs.Save();
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Finish;
            EditorApplication.quitting -= Finish;
            if (steps == null) return;
            try { RestoreSettings(); }
            catch (Exception exception) { Check(false, "Restore settings: " + exception); }
            Application.runInBackground = originalBackground;
            (steps as IDisposable)?.Dispose();
            steps = null;
            report.AppendLine("RESULT passed=" + passed + " failed=" + failed);
            File.WriteAllText("artifacts/start-menu/unity-checks.txt", report.ToString());
            Debug.Log("Start menu checks: passed=" + passed + ", failed=" + failed);
        }
    }
}
