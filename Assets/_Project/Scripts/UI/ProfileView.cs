using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>A visual avatar variant, not an inference about the player's or a patient's gender.</summary>
    public enum ProfileAvatar
    {
        Female,
        Male
    }

    /// <summary>Displays supplied counselor profile values without inventing or persisting identity data.</summary>
    [DisallowMultipleComponent]
    public sealed class ProfileView : MonoBehaviour
    {
        public Image backgroundImage;
        public Sprite femaleSprite;
        public Sprite maleSprite;
        public Sprite defaultProfileSprite;
        public TMP_Text nameText;
        public TMP_Text affiliationText;
        public TMP_Text introText;
        public Button closeButton;
        public TMP_Text progressText;
        private string progressLabel = "";

        [SerializeField] private string profileName;
        [SerializeField] private string affiliation;
        [SerializeField] private string introduction;
        [SerializeField] private ProfileAvatar avatar = ProfileAvatar.Female;

        public ProfileAvatar Avatar => avatar;
        public bool UsesDefaultArtwork => defaultProfileSprite != null && avatar == ProfileAvatar.Female
            && string.IsNullOrWhiteSpace(profileName) && string.IsNullOrWhiteSpace(affiliation)
            && string.IsNullOrWhiteSpace(introduction);

        /// <summary>The approved card is a display fallback; it never writes identity to a save.</summary>
        public void SetDefaultArtwork(Sprite sprite)
        {
            defaultProfileSprite = sprite;
            Refresh();
        }

        /// <summary>Displays B's authoritative labels/counts; no promotion or clear rules live here.</summary>
        public void SetProgress(string certification, string currentPatient, string state, int clearedCount, int totalCount)
        {
            if (clearedCount < 0 || totalCount < clearedCount) throw new System.ArgumentOutOfRangeException(nameof(clearedCount));
            progressLabel = "자격증: " + Value(certification) + "  ·  완료 " + clearedCount + " / " + totalCount
                + "\n현재 내담자: " + Value(currentPatient) + "\n" + Value(state);
            EnsureProgressLabel();
            Refresh();
        }

        private void EnsureProgressLabel()
        {
            if (progressText != null || introText == null) return;
            var obj = new GameObject("CommittedProgress", typeof(RectTransform));
            obj.transform.SetParent(transform, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(305, -355);
            rect.sizeDelta = new Vector2(480, 95);
            progressText = obj.AddComponent<TextMeshProUGUI>();
            progressText.font = introText.font; progressText.fontSize = 18;
            progressText.color = introText.color; progressText.raycastTarget = false;
        }

        private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

        public void SetProfile(string name, string affiliation, string intro,
            ProfileAvatar avatar = ProfileAvatar.Female)
        {
            profileName = name;
            this.affiliation = affiliation;
            introduction = intro;
            this.avatar = avatar;
            Refresh();
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void Refresh()
        {
            bool showArtwork = UsesDefaultArtwork;
            if (backgroundImage != null)
                backgroundImage.sprite = showArtwork ? defaultProfileSprite
                    : avatar == ProfileAvatar.Male ? maleSprite : femaleSprite;
            // The approved card already contains these three values. Custom values use blank art.
            foreach (TMP_Text label in new[] { nameText, affiliationText, introText })
                if (label != null) label.enabled = !showArtwork;
            SetText(nameText, profileName);
            SetText(affiliationText, affiliation);
            SetText(introText, introduction);
            SetText(progressText, progressLabel);
        }

        private static void SetText(TMP_Text label, string value)
        {
            if (label == null)
                return;
            label.richText = false;
            label.text = string.IsNullOrWhiteSpace(value) ? "—" : value;
        }
    }
}
