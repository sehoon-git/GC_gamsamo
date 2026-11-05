using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Title navigation only. Game progress and autosave are not implemented here.</summary>
    [DisallowMultipleComponent]
    public sealed class StartMenuController : MonoBehaviour
    {
        public string consultationSceneName = "ConsultationRoom";
        public CanvasGroup menuGroup;
        public StartMenuButton[] menuItems;
        public Button newGameButton, loadButton, settingsButton, quitButton;
        public TMP_Text saveStatus, transitionStatus, settingsStatus;
        public GameObject settingsModal, quitModal;
        public Button settingsBackdrop, quitBackdrop, cancelQuitButton, confirmQuitButton;
        public PcSettingsView settingsView;
        public CanvasGroup fade;
        public bool IsBusy { get; private set; }
        public bool IsSettingsOpen => settingsModal != null && settingsModal.activeSelf;
        public bool IsQuitOpen => quitModal != null && quitModal.activeSelf;
        public bool QuitRequested { get; private set; }

        private StartMenuButton selected;

        private void Awake()
        {
            newGameButton.onClick.AddListener(StartNewGame);
            settingsButton.onClick.AddListener(OpenSettings);
            quitButton.onClick.AddListener(OpenQuit);
            settingsBackdrop.onClick.AddListener(CloseModal);
            settingsView.closeButton.onClick.AddListener(CloseModal);
            quitBackdrop.onClick.AddListener(CloseModal);
            cancelQuitButton.onClick.AddListener(CloseModal);
            confirmQuitButton.onClick.AddListener(ConfirmQuit);
            GameSettings.Applied += OnSettingsApplied;
            // Room entry preserves per-patient AI autosaves. Full game-progress loading is not implemented.
            loadButton.interactable = false;
            TMP_Text entryLabel = newGameButton.GetComponentInChildren<TMP_Text>(true);
            if (entryLabel != null) entryLabel.text = "상담실 입장";
            saveStatus.text = "기존 상담 기록은 상담실에서 자동 복원됩니다";
            settingsModal.SetActive(false);
            quitModal.SetActive(false);
            transitionStatus.text = "";
        }

        private IEnumerator Start()
        {
            IsBusy = true;
            SetMenuInput(false);
            fade.alpha = 1;
            yield return FadeTo(0, 0.3f);
            IsBusy = false;
            SetMenuInput(true);
            Focus(newGameButton);
        }

        private void Update()
        {
            if (IsBusy) return;
            if (Input.GetKeyDown(KeyCode.Escape)) CloseModal();
            if (!IsSettingsOpen && !IsQuitOpen && Input.GetKeyDown(KeyCode.Tab))
            {
                int current = Array.IndexOf(menuItems, selected);
                int direction = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1;
                for (int step = 1; step <= menuItems.Length; step++)
                {
                    int index = (current + direction * step + menuItems.Length * 2) % menuItems.Length;
                    if (menuItems[index].button.IsInteractable()) { Focus(menuItems[index].button); break; }
                }
            }
        }

        public void SelectMenu(StartMenuButton item)
        {
            if (IsBusy || IsSettingsOpen || IsQuitOpen || item == null || !item.button.IsInteractable()) return;
            selected = item;
            foreach (StartMenuButton candidate in menuItems) candidate.SetSelected(candidate == item);
            Focus(item.button);
        }

        public void OpenSettings()
        {
            if (IsBusy || IsSettingsOpen || IsQuitOpen) return;
            SetMenuInput(false);
            settingsStatus.text = "";
            settingsModal.SetActive(true);
            Focus(settingsView.modeDropdown);
        }

        public void OpenQuit()
        {
            if (IsBusy || IsSettingsOpen || IsQuitOpen) return;
            SetMenuInput(false);
            quitModal.SetActive(true);
            Focus(cancelQuitButton);
        }

        public void CloseModal()
        {
            if (IsBusy) return;
            if (IsSettingsOpen)
            {
                if (settingsView.TryCloseDropdown()) return;
                settingsModal.SetActive(false); // PcSettingsView discards its draft on disable.
                SetMenuInput(true);
                Focus(settingsButton);
            }
            else if (IsQuitOpen)
            {
                quitModal.SetActive(false);
                SetMenuInput(true);
                Focus(quitButton);
            }
        }

        public void ConfirmQuit()
        {
            if (IsBusy || !IsQuitOpen) return;
            QuitRequested = true;
#if UNITY_EDITOR
            Debug.Log("Start menu quit confirmed. The standalone player calls Application.Quit; the Editor remains open.");
            CloseModal();
#else
            IsBusy = true;
            Application.Quit();
#endif
        }

        public void StartNewGame()
        {
            if (IsBusy || IsSettingsOpen || IsQuitOpen) return;
            if (!Application.CanStreamedLevelBeLoaded(consultationSceneName))
            {
                transitionStatus.text = "상담실을 열 수 없습니다. 다시 시도해 주세요.";
                Debug.LogError("Start menu destination is absent from enabled build scenes: " + consultationSceneName, this);
                return;
            }
            IsBusy = true; // Lock synchronously before starting the fade or an async load.
            SetMenuInput(false);
            StartCoroutine(LoadConsultationRoom());
        }

        private IEnumerator LoadConsultationRoom()
        {
            transitionStatus.text = "";
            yield return FadeTo(1, 0.3f);
            AsyncOperation operation = null;
            try { operation = SceneManager.LoadSceneAsync(consultationSceneName, LoadSceneMode.Single); }
            catch (Exception exception) { Debug.LogException(exception, this); }
            if (operation != null) { yield return operation; yield break; }
            yield return FadeTo(0, 0.2f);
            IsBusy = false;
            SetMenuInput(true);
            transitionStatus.text = "상담실을 열 수 없습니다. 다시 시도해 주세요.";
            Focus(newGameButton);
        }

        private IEnumerator FadeTo(float target, float duration)
        {
            fade.blocksRaycasts = true;
            float initial = fade.alpha;
            for (float elapsed = 0; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                fade.alpha = Mathf.Lerp(initial, target, Mathf.SmoothStep(0, 1, elapsed / duration));
                yield return null;
            }
            fade.alpha = target;
            fade.blocksRaycasts = target > 0;
        }

        private void SetMenuInput(bool value)
        {
            menuGroup.interactable = value;
            menuGroup.blocksRaycasts = value;
        }

        private void OnSettingsApplied(GameSettings.Values values)
        {
            if (IsSettingsOpen) settingsStatus.text = "설정을 적용했습니다";
        }

        private static void Focus(Selectable control)
        {
            if (control != null && EventSystem.current != null
                && EventSystem.current.currentSelectedGameObject != control.gameObject)
                EventSystem.current.SetSelectedGameObject(control.gameObject);
        }

        private void OnDestroy()
        {
            if (newGameButton != null) newGameButton.onClick.RemoveListener(StartNewGame);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
            if (quitButton != null) quitButton.onClick.RemoveListener(OpenQuit);
            if (settingsBackdrop != null) settingsBackdrop.onClick.RemoveListener(CloseModal);
            if (settingsView != null && settingsView.closeButton != null) settingsView.closeButton.onClick.RemoveListener(CloseModal);
            if (quitBackdrop != null) quitBackdrop.onClick.RemoveListener(CloseModal);
            if (cancelQuitButton != null) cancelQuitButton.onClick.RemoveListener(CloseModal);
            if (confirmQuitButton != null) confirmQuitButton.onClick.RemoveListener(ConfirmQuit);
            GameSettings.Applied -= OnSettingsApplied;
        }
    }
}
