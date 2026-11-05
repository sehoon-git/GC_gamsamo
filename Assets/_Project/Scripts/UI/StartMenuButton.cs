using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Shares the title's pointer and keyboard selection marker.</summary>
    [RequireComponent(typeof(Button))]
    public sealed class StartMenuButton : MonoBehaviour, IPointerEnterHandler, ISelectHandler
    {
        public StartMenuController menu;
        public Button button;
        public TMP_Text label;
        public GameObject dot, underline;

        public void OnPointerEnter(PointerEventData eventData) => menu.SelectMenu(this);
        public void OnSelect(BaseEventData eventData) => menu.SelectMenu(this);

        public void SetSelected(bool value)
        {
            dot.SetActive(value);
            underline.SetActive(value);
            label.color = !button.interactable ? new Color32(131, 120, 104, 255)
                : value ? new Color32(255, 246, 221, 255) : new Color32(238, 223, 197, 255);
        }
    }
}
