using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>Resolution-independent brown gradient and subtle vignette over the unmodified artwork.</summary>
    [AddComponentMenu("UI/Gamsamo Start Menu Shade")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class StartMenuShade : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            vertices.Clear();
            Rect rect = rectTransform.rect;
            const int columns = 32, rows = 16;
            for (int y = 0; y <= rows; y++)
                for (int x = 0; x <= columns; x++)
                {
                    float u = x / (float)columns, v = y / (float)rows;
                    float horizontal = u < 0.26f ? Mathf.Lerp(0.96f, 0.86f, u / 0.26f)
                        : u < 0.46f ? Mathf.Lerp(0.86f, 0.51f, (u - 0.26f) / 0.2f)
                        : Mathf.Lerp(0.51f, 0.09f, Mathf.Clamp01((u - 0.46f) / 0.27f));
                    float distance = new Vector2((u - 0.72f) * 0.85f, v - 0.62f).magnitude;
                    float edge = Mathf.SmoothStep(0, 0.27f, Mathf.InverseLerp(0.26f, 0.85f, distance));
                    var shade = new Color(0.18f, 0.14f, 0.14f, 1 - (1 - horizontal) * (1 - edge));
                    vertices.AddVert(new Vector3(rect.xMin + u * rect.width, rect.yMin + v * rect.height), shade, Vector2.zero);
                    if (x < columns && y < rows)
                    {
                        int i = y * (columns + 1) + x;
                        vertices.AddTriangle(i, i + columns + 1, i + 1);
                        vertices.AddTriangle(i + 1, i + columns + 1, i + columns + 2);
                    }
                }
        }
    }
}
