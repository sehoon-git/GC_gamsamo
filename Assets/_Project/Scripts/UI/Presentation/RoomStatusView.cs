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
        private GameProgressRuntime source;
        private string shownMessage;

        public void Bind(GameProgressRuntime runtime)
        {
            source = runtime;
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
            if (shownMessage == error) return;
            shownMessage = error;
            message.text = error ?? "";
            Canvas.ForceUpdateCanvases();
            messageScroll.verticalNormalizedPosition = 1;
        }
        private void Retry() => source?.RetryProgress();
        private void Home() { if (source != null) source.ReturnToStartMenu(); }
        private void OnDestroy()
        {
            retryButton.onClick.RemoveListener(Retry);
            homeButton.onClick.RemoveListener(Home);
        }
    }
}
