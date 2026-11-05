using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    [Serializable]
    public sealed class PhotoGalleryEntry
    {
        public Sprite thumbnail;
        public string title;
    }

    /// <summary>A six-slot display of photos already granted by the game; it does not award photos.</summary>
    [DisallowMultipleComponent]
    public sealed class PhotoGalleryView : MonoBehaviour
    {
        public const int MaximumPhotos = 6;

        public Image[] thumbnails;
        public TMP_Text[] titles;
        public TMP_Text emptyMessage;
        public Button closeButton;

        [SerializeField] private PhotoGalleryEntry[] entries = Array.Empty<PhotoGalleryEntry>();

        public int DisplayedPhotoCount { get; private set; }

        /// <summary>Null clears the gallery. Only the first six entries are used; missing entries stay empty.</summary>
        public void SetPhotos(IReadOnlyList<PhotoGalleryEntry> photos)
        {
            int count = photos == null ? 0 : Mathf.Min(photos.Count, MaximumPhotos);
            entries = new PhotoGalleryEntry[count];
            for (int index = 0; index < count; index++)
            {
                PhotoGalleryEntry entry = photos[index];
                if (entry != null)
                    entries[index] = new PhotoGalleryEntry { thumbnail = entry.thumbnail, title = entry.title };
            }
            Refresh();
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void Refresh()
        {
            DisplayedPhotoCount = 0;
            for (int index = 0; index < MaximumPhotos; index++)
            {
                PhotoGalleryEntry entry = entries != null && index < entries.Length ? entries[index] : null;
                bool hasPhoto = entry != null && entry.thumbnail != null;
                Image image = thumbnails != null && index < thumbnails.Length ? thumbnails[index] : null;
                TMP_Text title = titles != null && index < titles.Length ? titles[index] : null;
                if (image != null)
                {
                    image.sprite = hasPhoto ? entry.thumbnail : null;
                    image.preserveAspect = true;
                    image.enabled = hasPhoto;
                    if (hasPhoto)
                        DisplayedPhotoCount++;
                }
                if (title != null)
                {
                    title.richText = false;
                    title.text = hasPhoto ? (string.IsNullOrWhiteSpace(entry.title) ? "사진" : entry.title) : "";
                }
            }
            if (emptyMessage != null)
            {
                emptyMessage.richText = false;
                emptyMessage.text = "아직 등록된 사진이 없습니다.";
                emptyMessage.gameObject.SetActive(DisplayedPhotoCount == 0);
            }
        }
    }
}
