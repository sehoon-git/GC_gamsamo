using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    public sealed class PhotoViewer : PresentationModal
    {
        public Image image;
        public TMP_Text titleText, descriptionText, pageText;
        public Button closeButton, previousButton, nextButton;
        public int Page => descriptionText != null ? descriptionText.pageToDisplay : 1;
        public int PageCount => descriptionText != null ? Mathf.Max(1, descriptionText.textInfo.pageCount) : 1;
        private void Awake()
        {
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (previousButton != null) previousButton.onClick.AddListener(Previous);
            if (nextButton != null) nextButton.onClick.AddListener(Next);
            if (panel != null) panel.SetActive(false);
        }
        public bool ShowGrantedPhoto(PhotoGalleryEntry entry)
        {
            if (entry == null || entry.thumbnail == null || !OpenModal()) return false;
            if (image != null) { image.sprite = entry.fullSize != null ? entry.fullSize : entry.thumbnail; image.preserveAspect = true; }
            GuidebookView.Set(titleText, entry.title);
            GuidebookView.Set(descriptionText, entry.description);
            if (descriptionText != null) { descriptionText.pageToDisplay = 1; descriptionText.ForceMeshUpdate(); }
            RefreshPages();
            return true;
        }
        public void Previous() => GoTo(Page - 1);
        public void Next() => GoTo(Page + 1);
        private void GoTo(int page)
        {
            if (!IsOpen || descriptionText == null) return;
            descriptionText.pageToDisplay = Mathf.Clamp(page, 1, PageCount); RefreshPages();
        }
        private void RefreshPages()
        {
            GuidebookView.Set(pageText, Page + " / " + PageCount);
            if (previousButton != null) previousButton.interactable = Page > 1;
            if (nextButton != null) nextButton.interactable = Page < PageCount;
        }
        public void Close() => CloseModal();
        private void Update()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) Previous();
            else if (Input.GetKeyDown(KeyCode.RightArrow)) Next();
        }
    }
}
