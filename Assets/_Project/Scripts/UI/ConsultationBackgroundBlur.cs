using UnityEngine;
using UnityEngine.UI;

namespace Gamsamo.UI
{
    /// <summary>
    /// Blurs the supplied room artwork once, then crossfades it with the original.
    /// The record window remains sharp because no screen capture is involved.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ConsultationBackgroundBlur : MonoBehaviour
    {
        [SerializeField, Range(256, 1536)] private int maximumBlurDimension = 768;
        [SerializeField, Range(1, 5)] private int blurIterations = 3;
        [SerializeField, Range(0.5f, 4f)] private float blurRadius = 2f;

        private static readonly int BlurredTextureId = Shader.PropertyToID("_BlurredTex");
        private static readonly int AmountId = Shader.PropertyToID("_Amount");
        private static readonly int RadiusId = Shader.PropertyToID("_BlurRadius");

        private RawImage target;
        private Texture source;
        private Shader blurShader;
        private Material originalMaterial;
        private Material displayMaterial;
        private Material filterMaterial;
        private RenderTexture blurredTexture;
        private float amount;
        private bool configured;

        /// <summary>Call once with the background RawImage, its original artwork, and the included shader.</summary>
        public void Configure(RawImage image, Texture original, Shader shader)
        {
            ReleaseResources();
            target = image;
            source = original;
            blurShader = shader;
            originalMaterial = target != null ? target.material : null;
            amount = 0f;
            configured = target != null && source != null;

            if (configured)
            {
                target.texture = source;
                if (isActiveAndEnabled)
                    EnsureResources();
            }
        }

        /// <summary>Zero shows the original artwork; one shows the fully blurred background.</summary>
        public void SetAmount(float value)
        {
            amount = Mathf.Clamp01(value);
            if (!configured || !isActiveAndEnabled || target == null)
                return;

            if (amount <= 0.0001f)
            {
                RestoreOriginal();
                return;
            }

            if (!EnsureResources())
                return;

            displayMaterial.SetFloat(AmountId, amount);
            target.texture = source;
            if (target.material != displayMaterial)
                target.material = displayMaterial;
        }

        private void OnEnable()
        {
            if (configured)
                SetAmount(amount);
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private bool EnsureResources()
        {
            if (displayMaterial != null && blurredTexture != null && blurredTexture.IsCreated())
                return true;

            if (!configured || blurShader == null || !blurShader.isSupported)
                return false;

            // Recreate cleanly after a graphics-device reset without retaining old GPU objects.
            ReleaseResources();
            float scale = Mathf.Min(1f, maximumBlurDimension / (float)Mathf.Max(source.width, source.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            blurredTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "Consultation Room Blurred Background",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!blurredTexture.Create())
            {
                ReleaseResources();
                return false;
            }

            filterMaterial = new Material(blurShader)
            {
                name = "Consultation Background Blur Filter",
                hideFlags = HideFlags.HideAndDontSave
            };
            filterMaterial.SetFloat(RadiusId, blurRadius);

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            temporary.filterMode = FilterMode.Bilinear;
            temporary.wrapMode = TextureWrapMode.Clamp;
            try
            {
                Graphics.Blit(source, blurredTexture);
                for (int iteration = 0; iteration < blurIterations; iteration++)
                {
                    Graphics.Blit(blurredTexture, temporary, filterMaterial, 1);
                    Graphics.Blit(temporary, blurredTexture, filterMaterial, 2);
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }

            displayMaterial = new Material(blurShader)
            {
                name = "Consultation Background Blur Display",
                hideFlags = HideFlags.HideAndDontSave
            };
            displayMaterial.SetTexture(BlurredTextureId, blurredTexture);
            displayMaterial.SetFloat(AmountId, amount);
            return true;
        }

        private void RestoreOriginal()
        {
            if (target == null)
                return;

            target.texture = source;
            if (displayMaterial != null && target.material == displayMaterial)
                target.material = originalMaterial;
        }

        private void ReleaseResources()
        {
            RestoreOriginal();
            if (blurredTexture != null)
            {
                blurredTexture.Release();
                DestroyOwnedObject(blurredTexture);
                blurredTexture = null;
            }
            DestroyOwnedObject(displayMaterial);
            DestroyOwnedObject(filterMaterial);
            displayMaterial = null;
            filterMaterial = null;
        }

        private static void DestroyOwnedObject(Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }
    }
}
