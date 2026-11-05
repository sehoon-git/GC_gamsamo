using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>PC window navigation only. Inventory, currency and purchase data are not connected.</summary>
    [DisallowMultipleComponent]
    public sealed class PcDesktopView : MonoBehaviour
    {
        public GameObject shopWindow;
        public Image shopImage;
        public Sprite[] shopSprites;
        public Button[] tabButtons;
        public Button profileButton;
        public Button shopButton;
        public Button photosButton;
        public Button settingsButton;
        public Button closeShopButton;
        public CanvasGroup shortcuts;
        public GameObject appWindow;
        public TMP_Text appTitle;
        public TMP_Text appMessage;
        public TMP_Text balanceText;
        public Button closeAppButton;
        public ProfileView profileView;
        public PhotoGalleryView photoView;
        public PcSettingsView settingsView;
        public CosmeticShopView shopView;
        public Image wallpaper;
        public Sprite defaultWallpaper;
        public ConsultationRoomAppearance roomAppearance;

        public bool IsShopOpen => shopWindow != null && shopWindow.activeSelf;
        public bool IsProfileOpen => profileView != null && profileView.gameObject.activeSelf;
        public bool IsPhotosOpen => photoView != null && photoView.gameObject.activeSelf;
        public bool IsSettingsOpen => settingsView != null && settingsView.gameObject.activeSelf;
        public int ActiveTabIndex { get; private set; }

        private Button appOpener;
        private Button[] boundTabButtons;
        private UnityAction[] tabActions;

        private void Awake()
        {
            Bind(profileButton, OpenProfile);
            Bind(shopButton, OpenShop);
            Bind(photosButton, OpenPhotos);
            Bind(settingsButton, OpenSettings);
            Bind(closeShopButton, CloseShop);
            Bind(closeAppButton, CloseApp);
            if (profileView != null) Bind(profileView.closeButton, CloseApp);
            if (photoView != null) Bind(photoView.closeButton, CloseApp);
            if (settingsView != null)
            {
                Bind(settingsView.closeButton, CloseApp);
                Bind(settingsView.secondaryCloseButton, CloseApp);
            }

            if (tabButtons != null)
            {
                boundTabButtons = (Button[])tabButtons.Clone();
                tabActions = new UnityAction[boundTabButtons.Length];
                for (int index = 0; index < boundTabButtons.Length; index++)
                {
                    int capturedIndex = index;
                    tabActions[index] = () => SelectTab(capturedIndex);
                    Bind(boundTabButtons[index], tabActions[index]);
                }
            }
            ResetDesktop();
        }

        public void OpenShop()
        {
            if (!isActiveAndEnabled || shopWindow == null)
                return;
            HideInnerWindows();
            appOpener = null;
            shopWindow.SetActive(true);
            SelectTab(0);
            SetShortcutInput(false);
            SetPlaceholderBalance();
            Focus(closeShopButton);
        }

        public void CloseShop()
        {
            if (!IsShopOpen)
                return;
            shopWindow.SetActive(false);
            SetShortcutInput(true);
            Focus(shopButton);
        }

        /// <summary>0 window, 1 door, 2 clipboard, 3 PC, 4 PC wallpaper, 5 room wallpaper.</summary>
        public void SelectTab(int index)
        {
            if (index < 0 || shopSprites == null
                || index >= shopSprites.Length || shopSprites[index] == null)
                return;
            ActiveTabIndex = index;
            if (shopImage != null)
                shopImage.sprite = shopSprites[index];
            if (shopView != null)
                shopView.ShowCategory(index);
        }

        /// <summary>Returns to the desktop without taking focus away from the room controller.</summary>
        public void ResetDesktop()
        {
            HideInnerWindows();
            appOpener = null;
            ActiveTabIndex = 0;
            SelectTab(0);
            SetShortcutInput(true);
            SetPlaceholderBalance();
        }

        /// <summary>Used by Escape and the outer backdrop; false means the whole PC can close.</summary>
        public bool CloseActiveWindow()
        {
            if (IsSettingsOpen && settingsView.TryCloseDropdown()) return true;
            if (IsShopOpen)
            {
                CloseShop();
                return true;
            }
            if (IsProfileOpen || IsPhotosOpen || IsSettingsOpen
                || (appWindow != null && appWindow.activeSelf))
            {
                CloseApp();
                return true;
            }
            return false;
        }

        public void OpenProfile()
        {
            if (profileView != null) OpenContent(profileButton, profileView.gameObject, profileView.closeButton);
        }

        public void OpenPhotos()
        {
            if (photoView != null) OpenContent(photosButton, photoView.gameObject, photoView.closeButton);
        }

        public void OpenSettings()
        {
            if (settingsView == null || !isActiveAndEnabled) return;
            OpenContent(settingsButton, settingsView.gameObject, settingsView.closeButton);
            settingsView.BeginEdit();
        }

        private void OpenContent(Button opener, GameObject content, Button closeButton)
        {
            if (!isActiveAndEnabled || content == null) return;
            HideInnerWindows();
            appOpener = opener;
            content.SetActive(true);
            SetShortcutInput(false);
            Focus(closeButton);
        }

        private void CloseApp()
        {
            if (!IsProfileOpen && !IsPhotosOpen && !IsSettingsOpen
                && (appWindow == null || !appWindow.activeSelf)) return;
            HideInnerWindows();
            SetShortcutInput(true);
            Focus(appOpener);
            appOpener = null;
        }

        private void HideInnerWindows()
        {
            if (IsSettingsOpen) settingsView.CancelEdit();
            if (shopWindow != null) shopWindow.SetActive(false);
            if (appWindow != null) appWindow.SetActive(false);
            if (profileView != null) profileView.gameObject.SetActive(false);
            if (photoView != null) photoView.gameObject.SetActive(false);
            if (settingsView != null) settingsView.gameObject.SetActive(false);
        }

        /// <summary>Called after inventory has authorized an equip action. Does not purchase or grant items.</summary>
        public bool TryApplyCosmetic(string target, Sprite artwork)
        {
            if (target == "wallpaper")
            {
                if (wallpaper == null) return false;
                wallpaper.sprite = artwork != null ? artwork : defaultWallpaper;
                return true;
            }
            if (roomAppearance == null) return false;
            ConsultationRoomAppearance.CosmeticLayer layer;
            switch (target)
            {
                case "window": layer = ConsultationRoomAppearance.CosmeticLayer.Window; break;
                case "door": layer = ConsultationRoomAppearance.CosmeticLayer.Door; break;
                case "clipboard": layer = ConsultationRoomAppearance.CosmeticLayer.Clipboard; break;
                case "pc": layer = ConsultationRoomAppearance.CosmeticLayer.Pc; break;
                case "room-wallpaper": layer = ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper; break;
                default: return false;
            }
            if (artwork == null) roomAppearance.RestoreDefault(layer);
            else
            {
                // Room layers use the full source canvas, not a packed sprite atlas rectangle.
                if (artwork.packed || artwork.rect.size != new Vector2(artwork.texture.width, artwork.texture.height))
                    return false;
                roomAppearance.SetCosmetic(layer, artwork.texture);
            }
            return true;
        }

        private void SetPlaceholderBalance()
        {
            if (balanceText == null)
                return;
            balanceText.richText = false;
            balanceText.text = "—";
        }

        private void SetShortcutInput(bool enabled)
        {
            if (shortcuts != null)
            {
                shortcuts.interactable = enabled;
                shortcuts.blocksRaycasts = enabled;
            }
        }

        private static void Focus(Button button)
        {
            if (button != null && button.gameObject.activeInHierarchy && button.IsInteractable()
                && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(button.gameObject);
        }

        private static void Bind(Button button, UnityAction callback)
        {
            if (button == null)
                return;
            if (button.targetGraphic != null)
                button.targetGraphic.canvasRenderer.cullTransparentMesh = false;
            button.onClick.AddListener(callback);
        }

        private void OnDisable()
        {
            ResetDesktop();
        }

        private void OnDestroy()
        {
            Unbind(profileButton, OpenProfile);
            Unbind(shopButton, OpenShop);
            Unbind(photosButton, OpenPhotos);
            Unbind(settingsButton, OpenSettings);
            Unbind(closeShopButton, CloseShop);
            Unbind(closeAppButton, CloseApp);
            if (profileView != null) Unbind(profileView.closeButton, CloseApp);
            if (photoView != null) Unbind(photoView.closeButton, CloseApp);
            if (settingsView != null)
            {
                Unbind(settingsView.closeButton, CloseApp);
                Unbind(settingsView.secondaryCloseButton, CloseApp);
            }
            if (boundTabButtons != null && tabActions != null)
                for (int index = 0; index < boundTabButtons.Length; index++)
                    Unbind(boundTabButtons[index], tabActions[index]);
        }

        private static void Unbind(Button button, UnityAction callback)
        {
            if (button != null)
                button.onClick.RemoveListener(callback);
        }
    }
}
