using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Gamsamo.UI.Presentation;

namespace Gamsamo.UI
{
    [Serializable]
    public sealed class PhotoGalleryEntry
    {
        public Sprite thumbnail;
        public string title;
        public string photoId;
        public string description;
        public Sprite fullSize;
    }

    /// <summary>A six-slot display of photos already granted by the game; it does not award photos.</summary>
    [DisallowMultipleComponent]
    public sealed class PhotoGalleryView : MonoBehaviour
    {
        public const int MaximumPhotos = 6;

        public Image[] thumbnails;
        public TMP_Text[] titles;
        public TMP_Text emptyMessage;
        public string EmptyStateText { get; set; }
        public Button closeButton;
        public PhotoViewer viewer;
        private readonly Dictionary<Button, UnityEngine.Events.UnityAction> slotListeners = new Dictionary<Button, UnityEngine.Events.UnityAction>();
        public string GameId { get; private set; } = "";

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
                    entries[index] = new PhotoGalleryEntry { thumbnail = entry.thumbnail, title = entry.title,
                        photoId = entry.photoId, description = entry.description, fullSize = entry.fullSize };
            }
            Refresh();
        }

        private void OnEnable()
        {
            Refresh();
        }

        /// <summary>B supplies the saved unlock list. This method replaces, never awards, the displayed set.</summary>
        public void SetUnlockedPhotos(string gameId, IReadOnlyList<string> unlockedIds, IReadOnlyList<PhotoGalleryEntry> catalog)
        {
            ShowGrantedPhotos(gameId, ResolveUnlockedPhotos(unlockedIds, catalog));
        }

        internal void ShowGrantedPhotos(string gameId, IReadOnlyList<PhotoGalleryEntry> photos)
        {
            if (GameId != (gameId ?? "") && viewer != null) viewer.Close();
            GameId = gameId ?? "";
            SetPhotos(string.IsNullOrWhiteSpace(GameId) ? null : photos);
        }

        internal static PhotoGalleryEntry[] ResolveUnlockedPhotos(IReadOnlyList<string> unlockedIds, IReadOnlyList<PhotoGalleryEntry> catalog)
        {
            var selected = new List<PhotoGalleryEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (unlockedIds != null && catalog != null)
                foreach (string id in unlockedIds)
                {
                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                    foreach (var entry in catalog)
                        if (entry != null && entry.photoId == id) { selected.Add(entry); break; }
                }
            return selected.ToArray();
        }

        public bool OpenPhoto(int index)
        {
            if (index < 0 || index >= entries.Length || viewer == null) return false;
            return viewer.ShowGrantedPhoto(entries[index]);
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
                    image.raycastTarget = hasPhoto;
                    Button button = image.GetComponent<Button>();
                    if (button == null && Application.isPlaying) button = image.gameObject.AddComponent<Button>();
                    if (button != null)
                    {
                        button.interactable = hasPhoto && viewer != null;
                        if (!slotListeners.ContainsKey(button))
                        {
                            int slot = index;
                            UnityEngine.Events.UnityAction action = () => OpenPhoto(slot);
                            button.onClick.AddListener(action); slotListeners.Add(button, action);
                        }
                    }
                }
                if (title != null)
                {
                    title.richText = false;
                    title.text = entry != null ? (string.IsNullOrWhiteSpace(entry.title) ? "사진" : entry.title) : "";
                }
            }
            if (emptyMessage != null)
            {
                emptyMessage.richText = false;
                emptyMessage.text = !string.IsNullOrEmpty(EmptyStateText) ? EmptyStateText : entries != null && Array.Exists(entries, entry => entry != null)
                    ? "해금된 사진의 이미지를 준비 중입니다." : "아직 등록된 사진이 없습니다.";
                emptyMessage.gameObject.SetActive(DisplayedPhotoCount == 0);
            }
        }

        private void OnDestroy()
        {
            foreach (var item in slotListeners) if (item.Key != null) item.Key.onClick.RemoveListener(item.Value);
            slotListeners.Clear();
        }
    }
}
