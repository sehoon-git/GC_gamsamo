using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>A paper index with clipped top corners; the active tab opens onto the writing area.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class RecordIndexTabGraphic : MaskableGraphic
    {
        private bool selected;

        public void SetSelected(bool value)
        {
            if (selected == value) return;
            selected = value;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var r = rectTransform.rect;
            var edge = selected ? new Color32(109, 88, 69, 255) : new Color32(171, 154, 136, 255);
            var face = selected ? new Color32(250, 243, 237, 255) : new Color32(229, 220, 207, 255);
            Paper(mesh, r.xMin, r.yMin, r.xMax, r.yMax, 9, edge);
            // Keeping the active tab open at the bottom visually attaches it to the page below.
            Paper(mesh, r.xMin + 2, r.yMin + (selected ? 0 : 2), r.xMax - 2,
                r.yMax - (selected ? 4 : 2), 8, face);
        }

        private static void Paper(VertexHelper mesh, float left, float bottom, float right, float top,
            float corner, Color32 tint)
        {
            int first = mesh.currentVertCount;
            mesh.AddVert(new Vector3(left, bottom), tint, Vector2.zero);
            mesh.AddVert(new Vector3(left, top - corner), tint, Vector2.zero);
            mesh.AddVert(new Vector3(left + corner, top), tint, Vector2.zero);
            mesh.AddVert(new Vector3(right - corner, top), tint, Vector2.zero);
            mesh.AddVert(new Vector3(right, top - corner), tint, Vector2.zero);
            mesh.AddVert(new Vector3(right, bottom), tint, Vector2.zero);
            for (int i = 1; i < 5; i++)
                mesh.AddTriangle(first, first + i, first + i + 1);
        }
    }
}
