using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gamsamo.UI
{
    /// <summary>Committed game settings. Editing a settings window never mutates this state.</summary>
    public static class GameSettings
    {
        public const string PlayerPrefsKey = "Gamsamo.GameSettings.v1";
        public const int SchemaVersion = 1;

        public enum ScreenMode { Windowed, Fullscreen, Borderless }

        [Serializable]
        public sealed class Values
        {
            public int version = SchemaVersion;
            public int width;
            public int height;
            public ScreenMode screenMode;
            public float masterVolume = 1f;
            public float musicVolume = 1f;
            public float effectsVolume = 1f;

            public Values Copy() => (Values)MemberwiseClone();
        }

        private static Values current;
        private static readonly Dictionary<AudioSource, float> MusicSources = new Dictionary<AudioSource, float>();
        private static readonly Dictionary<AudioSource, float> EffectsSources = new Dictionary<AudioSource, float>();
        public static event Action<Values> Applied;

        public static Values Current
        {
            get { EnsureLoaded(); return current.Copy(); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            current = null;
            MusicSources.Clear();
            EffectsSources.Clear();
            Applied = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize() => EnsureLoaded();

        private static void EnsureLoaded()
        {
            if (current != null) return;
            Values defaults = Defaults();
            Values loaded = defaults.Copy();
            if (PlayerPrefs.HasKey(PlayerPrefsKey))
            {
                try
                {
                    string json = PlayerPrefs.GetString(PlayerPrefsKey, "");
                    // Overwrite defaults so omitted fields do not silently become zero volume.
                    loaded.version = -1;
                    JsonUtility.FromJsonOverwrite(json, loaded);
                    if (loaded.version != SchemaVersion) loaded = defaults;
                }
                catch (ArgumentException) { loaded = defaults; }
            }
            current = Sanitize(loaded, defaults);
            ApplyRuntime(current);
        }

        public static void Apply(Values values)
        {
            EnsureLoaded();
            Values validated = Sanitize(values ?? current, Defaults());
            // One versioned record contains only committed settings, never a window's draft.
            PlayerPrefs.SetString(PlayerPrefsKey, JsonUtility.ToJson(validated));
            PlayerPrefs.Save();
            current = validated;
            ApplyRuntime(current);
            Applied?.Invoke(current.Copy());
        }

        public static List<Vector2Int> GetSupportedResolutions()
        {
            // Product choices are shared by the title and PC settings, independent of the display.
            return new List<Vector2Int>
            {
                new Vector2Int(1280, 720),
                new Vector2Int(1920, 1080)
            };
        }

        private static Values Defaults()
        {
            List<Vector2Int> options = GetSupportedResolutions();
            Vector2Int size = new Vector2Int(Screen.width, Screen.height);
            if (!options.Contains(size)) size = options[options.Count - 1];
            return new Values
            {
                width = size.x,
                height = size.y,
                screenMode = Screen.fullScreenMode == FullScreenMode.Windowed ? ScreenMode.Windowed
                    : Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen ? ScreenMode.Fullscreen
                    : ScreenMode.Borderless,
                masterVolume = ValidVolume(AudioListener.volume, 1f),
                musicVolume = 1f,
                effectsVolume = 1f
            };
        }

        private static Values Sanitize(Values source, Values defaults)
        {
            Values result = source.Copy();
            result.version = SchemaVersion;
            if (!GetSupportedResolutions().Contains(new Vector2Int(result.width, result.height)))
            {
                result.width = defaults.width;
                result.height = defaults.height;
            }
            if (!Enum.IsDefined(typeof(ScreenMode), result.screenMode)) result.screenMode = defaults.screenMode;
            result.masterVolume = ValidVolume(result.masterVolume, defaults.masterVolume);
            result.musicVolume = ValidVolume(result.musicVolume, 1f);
            result.effectsVolume = ValidVolume(result.effectsVolume, 1f);
            return result;
        }

        private static float ValidVolume(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
        }

        private static void ApplyRuntime(Values values)
        {
            AudioListener.volume = values.masterVolume;
            UpdateSources(MusicSources, values.musicVolume);
            UpdateSources(EffectsSources, values.effectsVolume);
            // Screen.SetResolution is intentionally never called for the Unity Editor/Game view.
#if !UNITY_EDITOR
            FullScreenMode mode = values.screenMode == ScreenMode.Windowed ? FullScreenMode.Windowed
                : values.screenMode == ScreenMode.Fullscreen ? FullScreenMode.ExclusiveFullScreen
                : FullScreenMode.FullScreenWindow;
#if !UNITY_STANDALONE_WIN
            if (mode == FullScreenMode.ExclusiveFullScreen) mode = FullScreenMode.FullScreenWindow;
#endif
            if (Screen.width != values.width || Screen.height != values.height || Screen.fullScreenMode != mode)
                Screen.SetResolution(values.width, values.height, mode);
#endif
        }

        /// <summary>Base volume is the source's authored mix level; master uses AudioListener separately.</summary>
        public static void RegisterMusicSource(AudioSource source, float baseVolume = 1f)
        {
            EnsureLoaded();
            RegisterSource(MusicSources, source, baseVolume, current.musicVolume);
        }

        public static void RegisterEffectsSource(AudioSource source, float baseVolume = 1f)
        {
            EnsureLoaded();
            RegisterSource(EffectsSources, source, baseVolume, current.effectsVolume);
        }

        public static void UnregisterSource(AudioSource source)
        {
            if (ReferenceEquals(source, null)) return;
            MusicSources.Remove(source);
            EffectsSources.Remove(source);
        }

        private static void RegisterSource(Dictionary<AudioSource, float> sources, AudioSource source,
            float baseVolume, float categoryVolume)
        {
            if (source == null) return;
            UnregisterSource(source);
            float authoredVolume = ValidVolume(baseVolume, 1f);
            sources[source] = authoredVolume;
            source.volume = authoredVolume * categoryVolume;
        }

        private static void UpdateSources(Dictionary<AudioSource, float> sources, float categoryVolume)
        {
            var removed = new List<AudioSource>();
            foreach (var entry in sources)
                if (entry.Key == null) removed.Add(entry.Key);
                else entry.Key.volume = entry.Value * categoryVolume;
            foreach (AudioSource source in removed) sources.Remove(source);
        }
    }
}
