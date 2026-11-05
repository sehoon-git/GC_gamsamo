using System;
using UnityEngine;

namespace Gamsamo.UI.Presentation
{
    [Serializable]
    public sealed class EndingContent
    {
        public string patientId;
        public string photoId;
        public bool approved;
        public string title;
        [TextArea(3, 12)] public string description;
        public Sprite photo;
    }

    /// <summary>Authored media only. This asset grants no photos, endings, score or currency.</summary>
    [CreateAssetMenu(menuName = "Gamsamo/Presentation/Ending Content Catalog")]
    public sealed class EndingContentCatalog : ScriptableObject
    {
        public string contentVersion;
        public EndingContent[] patients = Array.Empty<EndingContent>();
        public bool creditsApproved;
        [TextArea(5, 20)] public string credits;
        public AudioClip roomMusic;
        public AudioClip endingMusic;
        public AudioClip pageSound;
        public AudioClip clearSound;

        public EndingContent FindApproved(string patientId)
        {
            if (patients == null) return null;
            foreach (var item in patients)
                if (item != null && item.approved && item.patientId == patientId) return item;
            return null;
        }
    }
}
