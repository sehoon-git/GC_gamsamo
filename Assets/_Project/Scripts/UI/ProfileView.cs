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
        public TMP_Text nameText;
        public TMP_Text affiliationText;
        public TMP_Text introText;
        public Button closeButton;

        [SerializeField] private string profileName;
        [SerializeField] private string affiliation;
        [SerializeField] private string introduction;
        [SerializeField] private ProfileAvatar avatar = ProfileAvatar.Female;

        public ProfileAvatar Avatar => avatar;

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
            if (backgroundImage != null)
                backgroundImage.sprite = avatar == ProfileAvatar.Male ? maleSprite : femaleSprite;
            SetText(nameText, profileName);
            SetText(affiliationText, affiliation);
            SetText(introText, introduction);
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
