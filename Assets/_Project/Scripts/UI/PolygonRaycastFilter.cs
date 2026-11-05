using UnityEngine;

namespace Gamsamo.UI
{
    /// <summary>Restricts UI input to an artwork silhouette. Points use 0..1 bottom-left coordinates.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class PolygonRaycastFilter : MonoBehaviour, ICanvasRaycastFilter
    {
        public Vector2[] normalizedPoints;

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            if (!isActiveAndEnabled)
                return true;
            if (normalizedPoints == null || normalizedPoints.Length < 3)
                return false;

            RectTransform rectTransform = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rectTransform, screenPoint, eventCamera, out Vector2 localPoint))
                return false;

            Rect rect = rectTransform.rect;
            if (rect.width <= 0f || rect.height <= 0f || !rect.Contains(localPoint))
                return false;

            Vector2 point = new Vector2(
                (localPoint.x - rect.xMin) / rect.width,
                (localPoint.y - rect.yMin) / rect.height);
            bool inside = false;
            int previous = normalizedPoints.Length - 1;
            for (int current = 0; current < normalizedPoints.Length; current++)
            {
                Vector2 a = normalizedPoints[previous];
                Vector2 b = normalizedPoints[current];
                Vector2 segment = b - a;
                float lengthSquared = segment.sqrMagnitude;
                if (lengthSquared > 0f)
                {
                    float along = Mathf.Clamp01(Vector2.Dot(point - a, segment) / lengthSquared);
                    // Include the boundary so a click directly on the board edge is accepted.
                    if ((point - (a + along * segment)).sqrMagnitude <= 0.00000001f)
                        return true;
                }

                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
                previous = current;
            }
            return inside;
        }
    }
}
