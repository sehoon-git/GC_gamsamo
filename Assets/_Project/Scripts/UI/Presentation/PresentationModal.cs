using System;
using Gamsamo.Dialogue;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamsamo.UI.Presentation
{
    /// <summary>D-owned view lease. B must implement the room-wide lock before any modal can open.</summary>
    public class PresentationModal : MonoBehaviour
    {
        public GameObject panel;
        public Selectable initialFocus;
        public PatientDialogueView dialogue;
        public bool IsOpen { get; private set; }
        public static int LastClosedFrame { get; private set; } = -1;
        public bool IsConnected => acquire != null && release != null;
        private Func<bool> acquire;
        private Action release;
        private GameObject previousFocus;

        public void BindModalGate(Func<bool> tryAcquireRoomInput, Action releaseRoomInput)
        {
            if (IsOpen) throw new InvalidOperationException("Close the presentation before replacing its modal gate.");
            acquire = tryAcquireRoomInput;
            release = releaseRoomInput;
        }

        protected bool OpenModal()
        {
            if (IsOpen) return true;
            // A raycast blocker alone cannot stop the door/controller/keyboard entry points.
            if (!isActiveAndEnabled || panel == null || !IsConnected || !acquire()) return false;
            IsOpen = true;
            previousFocus = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (dialogue != null) dialogue.SetPresentationBlock(this, true);
            panel.SetActive(true);
            if (initialFocus != null) initialFocus.Select();
            return true;
        }

        protected void CloseModal()
        {
            if (!IsOpen) return;
            IsOpen = false;
            LastClosedFrame = Time.frameCount;
            if (panel != null) panel.SetActive(false);
            if (dialogue != null) dialogue.SetPresentationBlock(this, false);
            release?.Invoke();
            if (EventSystem.current != null && previousFocus != null && previousFocus.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(previousFocus);
            previousFocus = null;
        }

        protected virtual void LateUpdate()
        {
            if (!IsOpen || EventSystem.current == null || panel == null) return;
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            if (selected == null || !selected.transform.IsChildOf(panel.transform))
                if (initialFocus != null && initialFocus.IsInteractable()) initialFocus.Select();
            if (!Input.GetKeyDown(KeyCode.Tab)) return;
            Selectable[] choices = panel.GetComponentsInChildren<Selectable>();
            int current = Array.FindIndex(choices, item => selected == item.gameObject);
            int step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? -1 : 1;
            for (int count = 1; count <= choices.Length; count++)
            {
                int index = (current + step * count + choices.Length * 2) % choices.Length;
                if (choices[index].IsInteractable()) { choices[index].Select(); break; }
            }
        }

        protected virtual void OnDisable() => CloseModal();
    }
}
