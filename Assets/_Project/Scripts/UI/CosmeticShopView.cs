using System;
using Gamsamo.Economy;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Shop UI. Only a committed wallet change establishes ownership or equipment.</summary>
    [DisallowMultipleComponent]
    public sealed class CosmeticShopView : MonoBehaviour
    {
        public const int SlotCount = 8;
        private static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper", "room-wallpaper" };

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
        public Button ItemActionButton { get; private set; }

        private CosmeticItem[] visibleItems = Array.Empty<CosmeticItem>();
        private Button[] boundItems;
        private UnityAction[] itemActions;
        private CosmeticAppearanceController subscribedAppearance;
        private EconomyWallet wallet;
        private CosmeticItem selectedItem;
        private bool confirmingPurchase;
        private int quotedPrice;
        private bool invokingAction;
        private string status;
        private TMP_Text actionLabel, cancelLabel;

        private void Awake()
        {
            BindButtons(itemButtons, index => PreviewSlot(index), out boundItems, out itemActions);
            HideLegacyBlindControls();
            if (restoreCategoryButton != null)
            {
                // Use the installed footer control as a skin. Existing scenes need no prefab migration.
                ItemActionButton = Instantiate(restoreCategoryButton, restoreCategoryButton.transform.parent);
                ItemActionButton.name = "CosmeticItemAction";
                var actionRect = (RectTransform)ItemActionButton.transform;
                actionRect.anchoredPosition = new Vector2(347, actionRect.anchoredPosition.y);
                ItemActionButton.onClick = new Button.ButtonClickedEvent();
                ItemActionButton.onClick.AddListener(HandleItemAction);
                restoreCategoryButton.onClick.AddListener(HandleFooterAction);
                actionLabel = ItemActionButton.GetComponentInChildren<TMP_Text>(true);
                cancelLabel = restoreCategoryButton.GetComponentInChildren<TMP_Text>(true);
            }
        }

        public void BindEconomy(EconomyWallet confirmedWallet)
        {
            if (wallet != null) wallet.Changed -= OnWalletChanged;
            wallet = confirmedWallet;
            if (wallet != null) wallet.Changed += OnWalletChanged;
            OnWalletChanged();
        }

        private void OnWalletChanged()
        {
            selectedItem = null;
            confirmingPurchase = false;
            status = null;
            if (appearance != null) appearance.RestoreEquipped(wallet);
            RefreshSelection();
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
            if (ActiveCategoryIndex != index) RestoreCategory();
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
            }
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
            if (item == null || appearance == null || !appearance.Preview(item.id)) return false;
            selectedItem = item;
            confirmingPurchase = false;
            status = null;
            RefreshSelection();
            return true;
        }

        public void RestoreCategory()
        {
            selectedItem = null;
            confirmingPurchase = false;
            status = null;
            if (appearance != null) appearance.RestoreEquipped(ActiveTarget, wallet);
            RefreshSelection();
        }

        private void HandleItemAction()
        {
            if (invokingAction || selectedItem == null) return;
            if (confirmingPurchase) { RestoreCategory(); return; }
            if (wallet == null || !wallet.IsReady) return;
            if (!wallet.Owns(selectedItem.id))
            {
                if (!wallet.TryGetApprovedPrice(selectedItem.id, out int price)) return;
                if (wallet.Balance < price) { status = "잔액이 부족합니다"; }
                else
                {
                    confirmingPurchase = true;
                    quotedPrice = price;
                    status = price + " C에 구매할까요?";
                }
                RefreshSelection();
                return;
            }
            string id = selectedItem.id;
            bool unequip = wallet.GetEquipped(ActiveTarget) == id;
            Perform(() => unequip ? wallet.Unequip(ActiveTarget) : wallet.Equip(id), unequip ? "장착 해제됨" : "장착됨");
        }

        private void HandleFooterAction()
        {
            if (invokingAction) return;
            if (!confirmingPurchase) { RestoreCategory(); return; }
            if (selectedItem == null || wallet == null || !wallet.IsReady) { RestoreCategory(); return; }
            string id = selectedItem.id;
            int price = quotedPrice;
            confirmingPurchase = false;
            Perform(() => wallet.Purchase(id, price), "구매 완료");
        }

        private void Perform(Func<EconomyStatus> operation, string successMessage)
        {
            invokingAction = true;
            try
            {
                EconomyStatus result = operation();
                // Even after a save failure, the artwork must reflect the committed equipment.
                RestoreCategory();
                switch (result)
                {
                    case EconomyStatus.Success: status = successMessage; break;
                    case EconomyStatus.AlreadyOwned: status = "이미 보유한 상품입니다"; break;
                    case EconomyStatus.InsufficientFunds: status = "잔액이 부족합니다"; break;
                    case EconomyStatus.PricePending: status = "가격 미정"; break;
                    case EconomyStatus.PriceChanged: status = "가격 변경 · 다시 확인해 주세요"; break;
                    case EconomyStatus.NotOwned: status = "보유하지 않은 상품입니다"; break;
                    case EconomyStatus.Busy: status = "저장 중입니다"; break;
                    case EconomyStatus.Conflict: status = "상태 갱신 후 다시 선택해 주세요"; break;
                    case EconomyStatus.SaveFailed: status = "저장 실패 · 다시 시도해 주세요"; break;
                    case EconomyStatus.NoChange: status = "변경 사항 없음"; break;
                    default: status = "현재 처리할 수 없습니다"; break;
                }
            }
            finally { invokingAction = false; RefreshSelection(); }
        }

        private void RefreshSelection()
        {
            bool ready = wallet != null && wallet.IsReady;
            string equipped = ready ? wallet.GetEquipped(ActiveTarget) : null;
            for (int index = 0; index < SlotCount; index++)
            {
                CosmeticItem item = ItemAt(index);
                bool isEquipped = item != null && equipped == item.id;
                bool owned = item != null && ready && wallet.Owns(item.id);
                bool previewed = item != null && selectedItem == item && !isEquipped;
                string price = "";
                if (item != null)
                {
                    int coins = 0;
                    bool priced = wallet != null ? wallet.TryGetApprovedPrice(item.id, out coins)
                        : catalog != null && catalog.TryGetApprovedPrice(item.id, out coins);
                    price = owned ? "보유" : priced ? coins + " C" : "미정";
                }
                TMP_Text priceLabel = At(priceLabels, index);
                if (priceLabel != null)
                {
                    priceLabel.enableAutoSizing = true;
                    priceLabel.fontSizeMin = 10;
                    priceLabel.fontSizeMax = 16;
                    SetText(priceLabel, price);
                }
                GameObject border = At(selectionBorders, index);
                if (border != null) border.SetActive(isEquipped || previewed);
                SetText(At(selectedBadges, index), isEquipped ? "장착 중" : "미리보기 중");
            }
            bool hasItem = selectedItem != null;
            bool selectedOwned = hasItem && ready && wallet.Owns(selectedItem.id);
            bool canBuy = hasItem && ready && wallet.TryGetApprovedPrice(selectedItem.id, out _);
            if (ItemActionButton != null)
            {
                ItemActionButton.gameObject.SetActive(hasItem);
                ItemActionButton.interactable = !invokingAction && (selectedOwned || canBuy);
                SetText(actionLabel, confirmingPurchase ? "취소" : selectedOwned
                    ? (equipped == selectedItem.id ? "장착 해제" : "장착") : "구매");
            }
            if (restoreCategoryButton != null)
                restoreCategoryButton.interactable = hasItem && !invokingAction;
            SetText(cancelLabel, confirmingPurchase ? "구매 확정" : "미리보기 취소");
            string message = ready ? "상품을 선택하세요" : "지갑 연결 대기";
            if (hasItem && !confirmingPurchase)
                message = selectedOwned ? (equipped == selectedItem.id ? "장착 중" : "보유 상품")
                    : canBuy ? "미리보기 중" : ready ? "미리보기 · 가격 미정" : "미리보기 · 지갑 연결 대기";
            SetText(statusText, status ?? message);

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
            OnWalletChanged();
        }

        private void OnDestroy()
        {
            if (restoreCategoryButton != null)
                restoreCategoryButton.onClick.RemoveListener(HandleFooterAction);
            if (ItemActionButton != null) ItemActionButton.onClick.RemoveListener(HandleItemAction);
            if (wallet != null) wallet.Changed -= OnWalletChanged;
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
