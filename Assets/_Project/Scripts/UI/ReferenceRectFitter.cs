using UnityEngine;

namespace Gamsamo.UI
{
    /// <summary>Scales the entire form, including text, from its original artwork coordinates.</summary>
    [ExecuteAlways, RequireComponent(typeof(RectTransform))]
    public sealed class ReferenceRectFitter : MonoBehaviour
    {
        public Vector2 referenceSize = new Vector2(1024, 1536);

        private void OnEnable() => Fit();
        private void OnRectTransformDimensionsChange() => Fit();
        private void LateUpdate() => Fit();

        private void Fit()
        {
            var parent = transform.parent as RectTransform;
            if (parent == null || referenceSize.x <= 0 || referenceSize.y <= 0) return;
            float scale = Mathf.Min(parent.rect.width / referenceSize.x, parent.rect.height / referenceSize.y);
            if (scale > 0 && !Mathf.Approximately(transform.localScale.x, scale))
                transform.localScale = new Vector3(scale, scale, 1);
        }
    }
}
