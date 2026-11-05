using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Session-only appearance previews. This view never buys, grants or saves items.</summary>
    [DisallowMultipleComponent]
    public sealed class CosmeticShopView : MonoBehaviour
    {
        public const int SlotCount = 8;
        private static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper" };

        public CosmeticCatalog catalog;
        public CosmeticAppearanceController appearance;
        public Button[] itemButtons;
        public Image[] thumbnails;
        public TMP_Text[] itemLabels;
        public GameObject[] selectionBorders;
        public TMP_Text[] selectedBadges;
        public TMP_Text[] priceLabels;
        public TMP_Text statusText;
        public Button restoreCategoryButton;
        public TMP_Text windowBlindHint;
        // Retained for installed scenes/prefabs; blind operation now belongs to the room window hotspot.
        public GameObject blindControls;
        public Button[] blindButtons;
        public GameObject[] blindSelections;

        public int ActiveCategoryIndex { get; private set; }
        public string ActiveTarget => Targets[ActiveCategoryIndex];

        private CosmeticItem[] visibleItems = Array.Empty<CosmeticItem>();
        private Button[] boundItems;
        private UnityAction[] itemActions;
        private CosmeticAppearanceController subscribedAppearance;

        private void Awake()
        {
            BindButtons(itemButtons, index => PreviewSlot(index), out boundItems, out itemActions);
            HideLegacyBlindControls();
            if (restoreCategoryButton != null)
                restoreCategoryButton.onClick.AddListener(RestoreCategory);
        }

        private void OnEnable()
        {
            if (appearance != null)
            {
                subscribedAppearance = appearance;
                subscribedAppearance.Changed += RefreshSelection;
            }
            ShowCategory(ActiveCategoryIndex);
        }

        public void ShowCategory(int index)
        {
            if (index < 0 || index >= Targets.Length) return;
            ActiveCategoryIndex = index;
            visibleItems = catalog != null ? catalog.GetItems(ActiveTarget) : null;
            if (visibleItems == null) visibleItems = Array.Empty<CosmeticItem>();
            for (int slot = 0; slot < SlotCount; slot++)
            {
                CosmeticItem item = ItemAt(slot);
                Button button = At(itemButtons, slot);
                if (button != null) button.interactable = item != null && appearance != null;
                Image thumbnail = At(thumbnails, slot);
                if (thumbnail != null)
                {
                    thumbnail.sprite = item != null ? item.thumbnail : null;
                    thumbnail.enabled = thumbnail.sprite != null;
                }
                SetText(At(itemLabels, slot), item == null ? "—"
                    : string.IsNullOrWhiteSpace(item.cardLabel) ? item.displayName : item.cardLabel);
                SetText(At(priceLabels, slot), item != null ? "미정" : "");
                SetText(At(selectedBadges, slot), "미리보기 중");
            }
            SetText(statusText, "미리보기 · 가격 미정");
            if (windowBlindHint != null)
            {
                SetText(windowBlindHint, "창문을 눌러 블라인드 조절");
                windowBlindHint.gameObject.SetActive(index == 0);
            }
            HideLegacyBlindControls();
            RefreshSelection();
        }

        public string ItemIdAt(int index)
        {
            CosmeticItem item = ItemAt(index);
            return item != null ? item.id : null;
        }

        public bool PreviewSlot(int index)
        {
            CosmeticItem item = ItemAt(index);
            return item != null && appearance != null && appearance.Preview(item.id);
        }

        public void RestoreCategory()
        {
            if (appearance != null) appearance.RestoreCategory(ActiveTarget);
        }

        private void RefreshSelection()
        {
            string selected = appearance != null ? appearance.SelectedId(ActiveTarget) : null;
            for (int index = 0; index < SlotCount; index++)
            {
                GameObject border = At(selectionBorders, index);
                if (border != null)
                    border.SetActive(!string.IsNullOrEmpty(selected) && selected == ItemIdAt(index));
            }
            if (restoreCategoryButton != null)
                restoreCategoryButton.interactable = appearance != null && !string.IsNullOrEmpty(selected);

            HideLegacyBlindControls();
        }

        private void HideLegacyBlindControls()
        {
            if (blindControls != null) blindControls.SetActive(false);
            for (int index = 0; index < 3; index++)
            {
                Button button = At(blindButtons, index);
                if (button != null) button.interactable = false;
                GameObject selection = At(blindSelections, index);
                if (selection != null) selection.SetActive(false);
            }
        }

        private CosmeticItem ItemAt(int index)
        {
            return index >= 0 && index < SlotCount && index < visibleItems.Length ? visibleItems[index] : null;
        }

        private static T At<T>(T[] array, int index) where T : class
        {
            return array != null && index >= 0 && index < array.Length ? array[index] : null;
        }

        private static void SetText(TMP_Text label, string text)
        {
            if (label == null) return;
            label.richText = false;
            label.text = text ?? "";
        }

        private static void BindButtons(Button[] buttons, Action<int> action,
            out Button[] boundButtons, out UnityAction[] callbacks)
        {
            boundButtons = buttons != null ? (Button[])buttons.Clone() : Array.Empty<Button>();
            callbacks = new UnityAction[boundButtons.Length];
            for (int index = 0; index < boundButtons.Length; index++)
            {
                int capturedIndex = index;
                callbacks[index] = () => action(capturedIndex);
                if (boundButtons[index] != null) boundButtons[index].onClick.AddListener(callbacks[index]);
            }
        }

        private void OnDisable()
        {
            if (subscribedAppearance != null)
                subscribedAppearance.Changed -= RefreshSelection;
            subscribedAppearance = null;
        }

        private void OnDestroy()
        {
            if (restoreCategoryButton != null)
                restoreCategoryButton.onClick.RemoveListener(RestoreCategory);
            Unbind(boundItems, itemActions);
        }

        private static void Unbind(Button[] buttons, UnityAction[] callbacks)
        {
            if (buttons == null || callbacks == null) return;
            for (int index = 0; index < buttons.Length; index++)
                if (buttons[index] != null) buttons[index].onClick.RemoveListener(callbacks[index]);
        }
    }
}
