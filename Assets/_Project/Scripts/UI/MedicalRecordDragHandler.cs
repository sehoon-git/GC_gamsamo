using UnityEngine;
using UnityEngine.EventSystems;

namespace Gamsamo.UI
{
    /// <summary>Moves the expanded record without changing its fitting or transition scale.</summary>
    [DefaultExecutionOrder(100), DisallowMultipleComponent, RequireComponent(typeof(RectTransform))]
    public sealed class MedicalRecordDragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public ConsultationRoomController room;
        public RectTransform dragRoot;
        public RectTransform artwork;
        public RectTransform bounds;

        public bool IsDragging { get; private set; }
        public bool BlocksBackdropClick => IsDragging || lastDragEndFrame == Time.frameCount;

        private readonly Vector3[] corners = new Vector3[4];
        private int activePointerId = int.MinValue;
        private int lastDragEndFrame = -1;
        private Vector2 initialPointer;
        private Vector2 initialPosition;
        private Camera dragCamera;

        private bool CanDrag => isActiveAndEnabled && room != null && room.IsRecordOpen
            && room.modal != null && room.modal.interactable && dragRoot != null
            && dragRoot.parent is RectTransform && artwork != null && bounds != null;

        private void OnEnable()
        {
            Canvas.willRenderCanvases += ClampToBounds;
        }

        private void LateUpdate()
        {
            // The artwork's ReferenceRectFitter runs first; clamp the resulting visible rectangle.
            ClampToBounds();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!CanDrag || IsDragging || eventData.button != PointerEventData.InputButton.Left)
                return;
            dragCamera = eventData.pressEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)dragRoot.parent, eventData.position, dragCamera, out initialPointer))
                return;
            initialPosition = dragRoot.anchoredPosition;
            activePointerId = eventData.pointerId;
            IsDragging = true;
            eventData.eligibleForClick = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!CanDrag || !IsDragging || eventData.pointerId != activePointerId)
                return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)dragRoot.parent, eventData.position, dragCamera, out Vector2 pointer))
                return;
            dragRoot.anchoredPosition = initialPosition + pointer - initialPointer;
            ClampToBounds();
            eventData.eligibleForClick = false;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!IsDragging || eventData.pointerId != activePointerId)
                return;
            eventData.eligibleForClick = false;
            IsDragging = false;
            activePointerId = int.MinValue;
            lastDragEndFrame = Time.frameCount;
        }

        public void ResetPosition()
        {
            IsDragging = false;
            activePointerId = int.MinValue;
            lastDragEndFrame = -1;
            if (dragRoot != null) dragRoot.anchoredPosition = Vector2.zero;
        }

        public void ClampToBounds()
        {
            if (!isActiveAndEnabled || dragRoot == null || artwork == null || bounds == null)
                return;
            artwork.GetWorldCorners(corners);
            Vector2 min = bounds.InverseTransformPoint(corners[0]);
            Vector2 max = min;
            for (int index = 1; index < corners.Length; index++)
            {
                Vector2 point = bounds.InverseTransformPoint(corners[index]);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            Rect available = bounds.rect;
            if (available.width <= 0f || available.height <= 0f)
                return;
            Vector2 correction = new Vector2(
                AxisCorrection(min.x, max.x, available.xMin, available.xMax),
                AxisCorrection(min.y, max.y, available.yMin, available.yMax));
            if (correction.sqrMagnitude > 0.0001f)
                dragRoot.position += bounds.TransformVector(correction);
        }

        private static float AxisCorrection(float min, float max, float lower, float upper)
        {
            // If a viewport cannot contain the record, keep the excess balanced on both sides.
            if (max - min > upper - lower)
                return (lower + upper - min - max) * 0.5f;
            if (min < lower) return lower - min;
            if (max > upper) return upper - max;
            return 0f;
        }

        private void OnDisable()
        {
            Canvas.willRenderCanvases -= ClampToBounds;
            IsDragging = false;
            activePointerId = int.MinValue;
        }
    }
}
