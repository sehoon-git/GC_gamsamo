using System;
using System.Collections.Generic;
using Gamsamo.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>
    /// Applies room and desktop artwork. The wallet owns the committed equipment.
    /// </summary>
    [DefaultExecutionOrder(-50), DisallowMultipleComponent]
    public sealed class CosmeticAppearanceController : MonoBehaviour
    {
        public CosmeticCatalog catalog;
        public ConsultationRoomAppearance roomAppearance;
        public PcDesktopView desktop;
        public Image recordImage;
        public Sprite defaultRecord;

        public event Action Changed;

        public int BlindState => roomAppearance != null ? roomAppearance.WindowBlindState : 0;
        public bool CanControlBlinds => roomAppearance != null && roomAppearance.HasBlindVariants
            && !string.IsNullOrEmpty(SelectedId("window"));

        private readonly Dictionary<string, string> selected = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly string[] Targets = { "window", "door", "clipboard", "pc", "wallpaper", "room-wallpaper" };

        private void Awake()
        {
            EnsureReferences();
        }

        public string SelectedId(string target)
        {
            return target != null && selected.TryGetValue(target, out string id) ? id : string.Empty;
        }

        /// <summary>Returns false without changing a selection if an ID or required artwork is invalid.</summary>
        public bool Preview(string id)
        {
            EnsureReferences();
            if (catalog == null || string.IsNullOrWhiteSpace(id)) return false;
            CosmeticItem item = catalog.Find(id);
            if (item == null || string.IsNullOrEmpty(item.id)) return false;

            bool applied;
            switch (item.target)
            {
                case "room-wallpaper":
                    applied = PreviewRoomLayer(ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper, item.artwork);
                    break;
                case "window":
                    applied = PreviewWindow(item);
                    break;
                case "door":
                    applied = PreviewRoomLayer(ConsultationRoomAppearance.CosmeticLayer.Door, item.artwork);
                    break;
                case "pc":
                    applied = PreviewRoomLayer(ConsultationRoomAppearance.CosmeticLayer.Pc, item.artwork);
                    break;
                case "clipboard":
                    // Validate both parts before updating either one. Existing TMP fields remain intact.
                    applied = recordImage != null && defaultRecord != null && item.detailArtwork != null
                        && PreviewRoomLayer(ConsultationRoomAppearance.CosmeticLayer.Clipboard, item.artwork);
                    if (applied)
                    {
                        recordImage.sprite = item.detailArtwork;
                        recordImage.preserveAspect = true;
                    }
                    break;
                case "wallpaper":
                    applied = desktop != null && item.artwork != null
                        && desktop.TryApplyCosmetic("wallpaper", item.artwork);
                    break;
                default:
                    return false;
            }
            if (!applied) return false;
            selected[item.target] = item.id;
            Changed?.Invoke();
            return true;
        }

        public void SetBlindState(int state)
        {
            if (state < 0 || state > 2) throw new ArgumentOutOfRangeException(nameof(state));
            if (!CanControlBlinds || BlindState == state) return;
            roomAppearance.SetBlindState(state);
            Changed?.Invoke();
        }

        public void RestoreCategory(string target)
        {
            EnsureReferences();
            if (!RestoreCategoryInternal(target)) return;
            selected.Remove(target);
            Changed?.Invoke();
        }

        /// <summary>Reapply the committed design, or the default if the save has no design for this category.</summary>
        public void RestoreEquipped(string target, EconomyWallet wallet)
        {
            string equipped = wallet != null && wallet.IsReady ? wallet.GetEquipped(target) : null;
            if (string.IsNullOrEmpty(equipped))
            {
                if (!string.IsNullOrEmpty(SelectedId(target))) RestoreCategory(target);
                return;
            }
            if (SelectedId(target) == equipped) return;
            if (catalog != null && catalog.Find(equipped)?.target == target && Preview(equipped)) return;
            RestoreCategory(target);
        }

        public void RestoreEquipped(EconomyWallet wallet)
        {
            foreach (string target in Targets) RestoreEquipped(target, wallet);
        }

        /// <summary>Restores all cosmetic categories without changing the game's current weather.</summary>
        public void RestoreAll()
        {
            EnsureReferences();
            bool restoredAny = false;
            foreach (string target in Targets)
            {
                if (!RestoreCategoryInternal(target)) continue;
                selected.Remove(target);
                restoredAny = true;
            }
            if (restoredAny) Changed?.Invoke();
        }

        private bool PreviewWindow(CosmeticItem item)
        {
            if (roomAppearance == null) return false;
            // All shipped window designs supply nine complete composites. No blind overlay is
            // drawn a second time; the catalog's overlay assets remain available for future uses.
            if (item.blindSprites != null && item.blindSprites.Length > 0)
            {
                if (!TryTextures(item.blindSprites, 9, out Texture2D[] blindTextures)) return false;
                // A first selection matches the catalog's half-open preview; later designs retain the user's state.
                int state = CanControlBlinds ? BlindState : 1;
                roomAppearance.SetWindowVariant(blindTextures, state);
                return true;
            }
            if (TryTextures(item.weatherSprites, 3, out Texture2D[] weatherTextures))
            {
                roomAppearance.SetWindowVariant(weatherTextures, 0);
                return true;
            }
            return false;
        }

        private bool PreviewRoomLayer(ConsultationRoomAppearance.CosmeticLayer layer, Sprite artwork)
        {
            if (roomAppearance == null || !TryTexture(artwork, out Texture2D texture)) return false;
            roomAppearance.SetCosmetic(layer, texture);
            return true;
        }

        private bool RestoreCategoryInternal(string target)
        {
            switch (target)
            {
                case "room-wallpaper":
                    if (roomAppearance == null) return false;
                    roomAppearance.RestoreDefault(ConsultationRoomAppearance.CosmeticLayer.RoomWallpaper);
                    return true;
                case "window":
                    if (roomAppearance == null) return false;
                    roomAppearance.RestoreDefault(ConsultationRoomAppearance.CosmeticLayer.Window);
                    return true;
                case "door":
                    if (roomAppearance == null) return false;
                    roomAppearance.RestoreDefault(ConsultationRoomAppearance.CosmeticLayer.Door);
                    return true;
                case "pc":
                    if (roomAppearance == null) return false;
                    roomAppearance.RestoreDefault(ConsultationRoomAppearance.CosmeticLayer.Pc);
                    return true;
                case "clipboard":
                    if (roomAppearance == null || recordImage == null || defaultRecord == null) return false;
                    roomAppearance.RestoreDefault(ConsultationRoomAppearance.CosmeticLayer.Clipboard);
                    recordImage.sprite = defaultRecord;
                    recordImage.preserveAspect = true;
                    return true;
                case "wallpaper":
                    return desktop != null && desktop.TryApplyCosmetic("wallpaper", null);
                default:
                    return false;
            }
        }

        private void EnsureReferences()
        {
            if (roomAppearance == null) roomAppearance = GetComponent<ConsultationRoomAppearance>();
            var room = GetComponent<ConsultationRoomController>();
            if (desktop == null && room != null) desktop = room.pcDesktopView;
            if (recordImage == null && room != null && room.recordView != null)
                recordImage = room.recordView.GetComponent<Image>();
            // Capture once before any preview, while honoring the installer-provided default reference.
            if (defaultRecord == null && recordImage != null) defaultRecord = recordImage.sprite;
        }

        private static bool TryTextures(Sprite[] sprites, int count, out Texture2D[] textures)
        {
            textures = null;
            if (sprites == null || sprites.Length != count) return false;
            var result = new Texture2D[count];
            for (int index = 0; index < count; index++)
                if (!TryTexture(sprites[index], out result[index])) return false;
            textures = result;
            return true;
        }

        private static bool TryTexture(Sprite sprite, out Texture2D texture)
        {
            texture = sprite != null ? sprite.texture : null;
            // Room coordinates describe a complete layer image, not a sprite-atlas subrectangle.
            return sprite != null && texture != null && !sprite.packed
                && sprite.rect.size == new Vector2(texture.width, texture.height);
        }
    }
}
