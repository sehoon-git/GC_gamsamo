using Gamsamo.Progression;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    /// <summary>Room-scaled presentation only; progression owns retries and navigation.</summary>
    public sealed class RoomStatusView : MonoBehaviour
    {
        public CanvasGroup controls;
        public GameObject errorCard;
        public TMP_Text message;
        public ScrollRect messageScroll;
        public Button retryButton, homeButton;
        public Button DevelopmentSkipButton { get; private set; }
        private GameProgressRuntime source;
        private string shownMessage;

        public void Bind(GameProgressRuntime runtime)
        {
            source = runtime;
            if (DevelopmentProgressMode.Enabled)
            {
                // Both actions use one raycaster and scale together at every Game View resolution.
                DevelopmentSkipButton = Instantiate(homeButton, homeButton.transform.parent, false);
                DevelopmentSkipButton.name = "DevelopmentSkip";
                var rect = (RectTransform)DevelopmentSkipButton.transform;
                rect.anchoredPosition += new Vector2(rect.rect.width + 16f, 0f);
                var label = DevelopmentSkipButton.GetComponentInChildren<TMP_Text>(true);
                rect.sizeDelta += new Vector2(90f, 0f);
                label.rectTransform.sizeDelta += new Vector2(90f, 0f);
                label.text = "Skip patient (70 / 70)";
                DevelopmentSkipButton.onClick = new Button.ButtonClickedEvent();
                DevelopmentSkipButton.onClick.AddListener(SkipDevelopmentPatient);
            }
            retryButton.onClick.AddListener(Retry);
            homeButton.onClick.AddListener(Home);
            Render(source.LastError, source.Progress != null, source.RoomControlsAvailable);
        }
        private void Update()
        {
            if (source != null) Render(source.LastError, source.Progress != null, source.RoomControlsAvailable);
        }
        public void Render(string error, bool canRetry, bool canInteract)
        {
            bool hasError = !string.IsNullOrEmpty(error);
            errorCard.SetActive(hasError);
            retryButton.gameObject.SetActive(hasError && canRetry);
            controls.interactable = canInteract;
            controls.blocksRaycasts = canInteract;
            if (DevelopmentSkipButton != null) DevelopmentSkipButton.interactable = canInteract && source.CanSkipDevelopmentPatient;
            if (shownMessage == error) return;
            shownMessage = error;
            message.text = error ?? "";
            Canvas.ForceUpdateCanvases();
            messageScroll.verticalNormalizedPosition = 1;
        }
        private void Retry() => source?.RetryProgress();
        private void SkipDevelopmentPatient() => source?.SkipDevelopmentPatient();
        private void Home() { if (source != null) source.ReturnToStartMenu(); }
        private void OnDestroy()
        {
            retryButton.onClick.RemoveListener(Retry);
            homeButton.onClick.RemoveListener(Home);
            if (DevelopmentSkipButton != null) DevelopmentSkipButton.onClick.RemoveListener(SkipDevelopmentPatient);
        }
    }
}
