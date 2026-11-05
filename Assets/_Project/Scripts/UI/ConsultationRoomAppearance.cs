using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gamsamo.UI
{
    /// <summary>
    /// Composes the room artwork when its weather or cosmetics change. The resulting texture
    /// is shared by the room display and existing blur, keeping every room layer in the blur.
    /// </summary>
    [DefaultExecutionOrder(-100), DisallowMultipleComponent]
    public sealed class ConsultationRoomAppearance : MonoBehaviour
    {
        public enum Weather { Clear, Cloudy, Rain }
        public enum CosmeticLayer { Window, Door, Clipboard, Pc, RoomWallpaper }

        public ConsultationRoomController room;
        public Shader compositeShader;
        public GameObject editorPreviewLayers;
        public Vector2Int canvasSize = new Vector2Int(1920, 1080);
        public Texture2D baseTexture;
        public Texture2D defaultRoomWallpaper;
        public Texture2D defaultDoor;
        public Texture2D clearWindow;
        public Texture2D cloudyWindow;
        public Texture2D rainWindow;
        public Texture2D defaultPc;
        public Texture2D defaultClipboard;
        // Rectangles use the pack's top-left pixel coordinates, not Unity's bottom-left coordinates.
        public Rect doorRect = new Rect(151, 103, 430, 644);
        public Rect windowRect = new Rect(731, 52, 1011, 534);
        public Rect pcRect = new Rect(1334, 417, 585, 505);
        public Rect clipboardRect = new Rect(37, 771, 407, 258);

        [SerializeField] private Weather weather;
        [SerializeField] private Texture2D doorCosmetic;
        [SerializeField] private Texture2D windowCosmetic;
        [SerializeField] private Texture2D pcCosmetic;
        [SerializeField] private Texture2D clipboardCosmetic;
        [SerializeField] private Texture2D roomWallpaperCosmetic;
        [SerializeField] private Texture2D[] windowVariantTextures = Array.Empty<Texture2D>();
        [SerializeField, Range(0, 2)] private int windowBlindState;

        public Weather CurrentWeather => weather;
        public RenderTexture CompositeTexture => compositeTexture;
        public int CompositionVersion { get; private set; }
        public bool HasWindowVariants => CompleteWindowVariants(windowVariantTextures);
        public bool HasBlindVariants => HasWindowVariants && windowVariantTextures.Length == 9;
        public int WindowBlindState => HasBlindVariants ? windowBlindState : 0;
        public Texture2D[] WindowVariantTextures => windowVariantTextures == null
            ? Array.Empty<Texture2D>() : (Texture2D[])windowVariantTextures.Clone();

        private static readonly int LayerTextureId = Shader.PropertyToID("_LayerTex");
        private static readonly int LayerRectId = Shader.PropertyToID("_LayerRect");
        private static readonly int ProjectionId = Shader.PropertyToID("_CanvasProjection");
        private RenderTexture compositeTexture;
        private Material compositeMaterial;
        private Mesh quad;

        private void OnEnable()
        {
            if (Application.isPlaying)
                Rebuild();
        }

        public void SetWeather(Weather value)
        {
            if (!Enum.IsDefined(typeof(Weather), value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (weather == value)
                return;
            weather = value;
            RebuildIfPlaying();
        }

        /// <summary>
        /// Supplies a complete layer texture; null restores that layer's default.
        /// A custom window replaces the complete window, including its weather artwork.
        /// </summary>
        public void SetCosmetic(CosmeticLayer layer, Texture2D texture)
        {
            switch (layer)
            {
                case CosmeticLayer.Window:
                    windowCosmetic = texture;
                    windowVariantTextures = Array.Empty<Texture2D>();
                    windowBlindState = 0;
                    break;
                case CosmeticLayer.Door: doorCosmetic = texture; break;
                case CosmeticLayer.Clipboard: clipboardCosmetic = texture; break;
                case CosmeticLayer.Pc: pcCosmetic = texture; break;
                case CosmeticLayer.RoomWallpaper: roomWallpaperCosmetic = texture; break;
                default: throw new ArgumentOutOfRangeException(nameof(layer));
            }
            RebuildIfPlaying();
        }

        /// <summary>
        /// Sets either three weather variants or nine blind/weather variants.
        /// Nine entries are up(clear/cloudy/rain), half(clear/cloudy/rain), down(clear/cloudy/rain).
        /// Subsequent weather changes retain the selected design and blind state.
        /// </summary>
        public void SetWindowVariant(Texture2D[] states, int blindState)
        {
            if (blindState < 0 || blindState > 2)
                throw new ArgumentOutOfRangeException(nameof(blindState));
            if (states == null || states.Length == 0)
            {
                RestoreDefault(CosmeticLayer.Window);
                return;
            }
            if (!CompleteWindowVariants(states))
                throw new ArgumentException("A window design requires three or nine non-null textures.", nameof(states));
            windowVariantTextures = (Texture2D[])states.Clone();
            windowCosmetic = null;
            windowBlindState = states.Length == 9 ? blindState : 0;
            RebuildIfPlaying();
        }

        /// <summary>Uses a supplied static blind image. The default window has no blind controls.</summary>
        public void SetBlindState(int state)
        {
            if (state < 0 || state > 2)
                throw new ArgumentOutOfRangeException(nameof(state));
            if (!HasBlindVariants || windowBlindState == state) return;
            windowBlindState = state;
            RebuildIfPlaying();
        }

        public void RestoreDefault(CosmeticLayer layer) => SetCosmetic(layer, null);

        public void RestoreDefaults()
        {
            weather = Weather.Clear;
            doorCosmetic = windowCosmetic = pcCosmetic = clipboardCosmetic = roomWallpaperCosmetic = null;
            windowVariantTextures = Array.Empty<Texture2D>();
            windowBlindState = 0;
            RebuildIfPlaying();
        }

        public Texture2D TextureForLayer(CosmeticLayer layer)
        {
            switch (layer)
            {
                case CosmeticLayer.RoomWallpaper: return roomWallpaperCosmetic != null ? roomWallpaperCosmetic : defaultRoomWallpaper;
                case CosmeticLayer.Door: return doorCosmetic != null ? doorCosmetic : defaultDoor;
                case CosmeticLayer.Pc: return pcCosmetic != null ? pcCosmetic : defaultPc;
                case CosmeticLayer.Clipboard: return clipboardCosmetic != null ? clipboardCosmetic : defaultClipboard;
                case CosmeticLayer.Window:
                    if (HasWindowVariants)
                    {
                        int blindOffset = HasBlindVariants ? windowBlindState * 3 : 0;
                        return windowVariantTextures[blindOffset + (int)weather];
                    }
                    if (windowCosmetic != null) return windowCosmetic;
                    return weather == Weather.Rain ? rainWindow : weather == Weather.Cloudy ? cloudyWindow : clearWindow;
                default: throw new ArgumentOutOfRangeException(nameof(layer));
            }
        }

        public bool ValidateConfiguration(out string reason)
        {
            if (room == null || room.background == null || room.backgroundBlur == null || room.blurShader == null)
                reason = "The room, background RawImage, blur component, or blur shader is missing.";
            else if (compositeShader == null || !compositeShader.isSupported)
                reason = "The room layer compositor shader is missing or unsupported.";
            else if (canvasSize.x <= 0 || canvasSize.y <= 0)
                reason = "Room canvas dimensions must be positive.";
            else if (baseTexture == null || defaultDoor == null || clearWindow == null || cloudyWindow == null
                || rainWindow == null || defaultPc == null || defaultClipboard == null)
                reason = "One or more default room layer textures are missing.";
            else if (windowVariantTextures != null && windowVariantTextures.Length > 0
                && (!CompleteWindowVariants(windowVariantTextures) || windowBlindState < 0 || windowBlindState > 2))
                reason = "The selected window design has incomplete weather or blind variants.";
            else if (!FitsCanvas(doorRect) || !FitsCanvas(windowRect) || !FitsCanvas(pcRect) || !FitsCanvas(clipboardRect))
                reason = "A room layer rectangle is empty or outside the room canvas.";
            else
            {
                reason = null;
                return true;
            }
            return false;
        }

        /// <summary>Rebuild only after appearance changes, never as a per-frame rendering step.</summary>
        public void Rebuild()
        {
            if (!Application.isPlaying)
                return;
            if (!ValidateConfiguration(out string reason))
            {
                Debug.LogError("Room appearance could not be composed: " + reason, this);
                return;
            }

            float blurAmount = CurrentBlurAmount();
            EnsureResources();
            if (!compositeTexture.IsCreated())
            {
                Debug.LogError("The room composite RenderTexture could not be created.", this);
                return;
            }

            var commands = new CommandBuffer { name = "Compose Consultation Room Layers" };
            RenderTexture previousTarget = RenderTexture.active;
            try
            {
                commands.SetRenderTarget(compositeTexture);
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.SetViewport(new Rect(0, 0, canvasSize.x, canvasSize.y));
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(
                    Matrix4x4.Ortho(0, canvasSize.x, canvasSize.y, 0, -1, 1), true);
                Draw(commands, baseTexture, new Rect(0, 0, canvasSize.x, canvasSize.y), projection);
                Draw(commands, TextureForLayer(CosmeticLayer.RoomWallpaper), new Rect(0, 0, canvasSize.x, canvasSize.y), projection);
                Draw(commands, TextureForLayer(CosmeticLayer.Door), doorRect, projection);
                Draw(commands, TextureForLayer(CosmeticLayer.Window), windowRect, projection);
                Draw(commands, TextureForLayer(CosmeticLayer.Pc), pcRect, projection);
                Draw(commands, TextureForLayer(CosmeticLayer.Clipboard), clipboardRect, projection);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                commands.Release();
            }

            // Keep the controller's original-texture contract intact for closing and smoke checks.
            room.backgroundTexture = compositeTexture;
            room.backgroundBlur.Configure(room.background, compositeTexture, room.blurShader);
            room.backgroundBlur.SetAmount(blurAmount);
            if (editorPreviewLayers != null)
                editorPreviewLayers.SetActive(false);
            CompositionVersion++;
        }

        private void RebuildIfPlaying()
        {
            if (Application.isPlaying && isActiveAndEnabled)
                Rebuild();
        }

        private bool FitsCanvas(Rect rect)
        {
            return rect.width > 0 && rect.height > 0 && rect.xMin >= 0 && rect.yMin >= 0
                && rect.xMax <= canvasSize.x && rect.yMax <= canvasSize.y;
        }

        private static bool CompleteWindowVariants(Texture2D[] textures)
        {
            if (textures == null || (textures.Length != 3 && textures.Length != 9)) return false;
            for (int index = 0; index < textures.Length; index++)
                if (textures[index] == null) return false;
            return true;
        }

        private float CurrentBlurAmount()
        {
            Material material = room.background.material;
            if (material != null && material.shader == room.blurShader && material.HasProperty("_Amount"))
                return Mathf.Clamp01(material.GetFloat("_Amount"));
            return 0f;
        }

        private void EnsureResources()
        {
            if (compositeMaterial == null)
                compositeMaterial = new Material(compositeShader)
                {
                    name = "Consultation Room Layer Compositor",
                    hideFlags = HideFlags.HideAndDontSave
                };
            if (quad == null)
            {
                quad = new Mesh { name = "Room Layer Quad", hideFlags = HideFlags.HideAndDontSave };
                quad.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
                quad.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
                quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                quad.UploadMeshData(true);
            }
            if (compositeTexture != null && (compositeTexture.width != canvasSize.x || compositeTexture.height != canvasSize.y))
            {
                compositeTexture.Release();
                DestroyOwned(compositeTexture);
                compositeTexture = null;
            }
            if (compositeTexture == null)
                compositeTexture = new RenderTexture(canvasSize.x, canvasSize.y, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Consultation Room Composite",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false
                };
            if (!compositeTexture.IsCreated()) compositeTexture.Create();
        }

        private void Draw(CommandBuffer commands, Texture texture, Rect rect, Matrix4x4 projection)
        {
            // The wallpaper layer is optional; null leaves the original wall visible.
            if (texture == null) return;
            var properties = new MaterialPropertyBlock();
            properties.SetTexture(LayerTextureId, texture);
            properties.SetVector(LayerRectId, new Vector4(rect.x, rect.y, rect.width, rect.height));
            properties.SetMatrix(ProjectionId, projection);
            commands.DrawMesh(quad, Matrix4x4.identity, compositeMaterial, 0, 0, properties);
        }

        private void OnDestroy()
        {
            // Disconnect owned GPU resources before releasing them, including component-only removal.
            if (room != null && room.backgroundTexture == compositeTexture)
            {
                room.backgroundTexture = baseTexture;
                if (room.background != null) room.background.texture = baseTexture;
                if (room.backgroundBlur != null)
                    room.backgroundBlur.Configure(room.background, baseTexture, room.blurShader);
                if (editorPreviewLayers != null) editorPreviewLayers.SetActive(true);
            }
            if (compositeTexture != null)
            {
                compositeTexture.Release();
                DestroyOwned(compositeTexture);
            }
            DestroyOwned(compositeMaterial);
            DestroyOwned(quad);
            compositeTexture = null;
            compositeMaterial = null;
            quad = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
