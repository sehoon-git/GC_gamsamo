using System.Collections.Generic;
using Gamsamo.Progression;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamsamo.UI.Presentation
{
    public enum PresentationCue
    {
        PageTurn, Clear, UiConfirm, UiBack, PurchaseSuccess, ActionUnavailable, DoorOpen, DoorClose, RecordOpen, PhotoOpen
    }

    /// <summary>One persistent audio owner. The consultation scene binds it through PlayRoomMusic.</summary>
    public sealed class PresentationAudio : MonoBehaviour
    {
        private const float FadeSecondsPerSide = 0.22f;
        private const double RetriggerSeconds = 0.12;
        private static PresentationAudio instance;
        public static PresentationAudio Instance => instance;

        public AudioSource music;
        public AudioSource effects;
        public EndingContentCatalog content;

        [Header("Audio prefab defaults; populated ending catalog slots take precedence")]
        public AudioClip roomMusic, endingMusic, pageSound, clearSound;
        [Header("Optional interaction cues")]
        public AudioClip uiConfirm, uiBack, purchaseSuccess, actionUnavailable;
        public AudioClip doorOpen, doorClose, recordOpen, photoOpen;

        private AudioClip requestedMusic;
        private AudioClip roomTrack;
        private int roomResumeSample;
        private float musicEnvelope = 1f;
        private Scene spawnScene, roomScene;
        private AudioListener roomListener;
        private AudioSource secondEffect;
        private AudioClip lastEffect;
        private double nextRepeatTime;
        private readonly HashSet<string> announcedFirstClears = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOwner() => instance = null;

        private void Awake()
        {
            spawnScene = gameObject.scene;
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);
            if (music == null) music = gameObject.AddComponent<AudioSource>();
            if (effects == null || effects == music) effects = gameObject.AddComponent<AudioSource>();
            music.playOnAwake = false;
            music.spatialBlend = 0;
            music.loop = true;
            GameSettings.RegisterMusicSource(music);
            ConfigureEffect(effects);
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += RoomUnloaded;
        }

        private static void ConfigureEffect(AudioSource source)
        {
            source.playOnAwake = source.loop = false;
            source.Stop();
            source.spatialBlend = 0;
            GameSettings.RegisterEffectsSource(source);
        }

        public void PlayRoomMusic()
        {
            // Bind() calls on the freshly instantiated prefab, even if an earlier room owns the sources.
            var owner = instance;
            if (owner == null) return;
            owner.content = content;
            if (owner != this || !owner.roomScene.IsValid())
                owner.roomScene = spawnScene.IsValid() ? spawnScene : SceneManager.GetActiveScene();
            owner.EnsureRoomListener();
            owner.RestoreRoomMusic();
        }

        public void RestoreRoomMusic()
        {
            var clip = content != null && content.roomMusic != null ? content.roomMusic : roomMusic;
            if (roomTrack != clip) { roomTrack = clip; roomResumeSample = 0; }
            PlayMusic(clip);
        }
        public void PlayEndingMusic() => PlayMusic(content != null && content.endingMusic != null ? content.endingMusic : endingMusic);

        public void PlayMusic(AudioClip clip)
        {
            if (instance != this || music == null) return;
            requestedMusic = clip;
            if (clip == null)
            {
                RememberRoomPosition();
                music.Stop();
                music.clip = null;
                SetMusicEnvelope(1f);
            }
            else if (!music.isPlaying)
            {
                SetMusicEnvelope(1f);
                StartTrack(clip);
            }
        }

        // Fade through silence on one source. The current request can change mid-fade (skip/replay).
        private void Update()
        {
            if (instance != this || requestedMusic == null || music == null) return;
            bool switching = music.clip != requestedMusic;
            SetMusicEnvelope(Mathf.MoveTowards(musicEnvelope, switching ? 0f : 1f, Time.unscaledDeltaTime / FadeSecondsPerSide));
            if (switching && musicEnvelope == 0f)
            {
                RememberRoomPosition();
                StartTrack(requestedMusic);
            }
        }

        private void RememberRoomPosition()
        {
            if (roomTrack != null && music.clip == roomTrack && music.isPlaying) roomResumeSample = music.timeSamples;
        }

        private void StartTrack(AudioClip clip)
        {
            music.clip = clip;
            music.Play();
            if (clip == roomTrack && roomResumeSample > 0)
                music.timeSamples = Mathf.Min(roomResumeSample, clip.samples - 1);
        }

        private void SetMusicEnvelope(float envelope)
        {
            if (musicEnvelope == envelope) return;
            musicEnvelope = envelope;
            // Registered mix gain keeps a settings change during a fade from bypassing mute or the envelope.
            GameSettings.RegisterMusicSource(music, envelope);
        }

        public void PlayPageSound() => TryPlayCue(PresentationCue.PageTurn);
        public void PlayClearSound() => TryPlayCue(PresentationCue.Clear);

        /// <summary>May be called by B/D with B's saved result, including restoration of a pending first-clear notice.</summary>
        public bool TryPlayFirstClear(ConfirmedResult savedResult)
        {
            if (savedResult == null || !savedResult.Cleared || !savedResult.FirstClear || savedResult.Identity?.IsValid != true)
                return false;
            string key = savedResult.Identity.FirstClearKey;
            if (announcedFirstClears.Contains(key)) return false;
            var clip = content != null && content.clearSound != null ? content.clearSound : clearSound;
            if (!TryPlayEffect(clip, true)) return false;
            announcedFirstClears.Add(key);
            return true;
        }

        /// <summary>Returns false for missing, muted or throttled cues. Trigger Clear only after a saved first clear.</summary>
        public bool TryPlayCue(PresentationCue cue)
        {
            AudioClip clip;
            switch (cue)
            {
                case PresentationCue.PageTurn: clip = content != null && content.pageSound != null ? content.pageSound : pageSound; break;
                // The existing view calls the no-argument PlayClearSound. Read B's persisted receipt
                // only at that event; generic UI clicks and replay never synthesize a clear.
                case PresentationCue.Clear:
                    foreach (var runtime in FindObjectsByType<GameProgressRuntime>(FindObjectsSortMode.None))
                        if (runtime.gameObject.scene == roomScene) return TryPlayFirstClear(runtime.PendingResult);
                    return false;
                case PresentationCue.UiConfirm: clip = uiConfirm; break;
                case PresentationCue.UiBack: clip = uiBack; break;
                case PresentationCue.PurchaseSuccess: clip = purchaseSuccess; break;
                case PresentationCue.ActionUnavailable: clip = actionUnavailable; break;
                case PresentationCue.DoorOpen: clip = doorOpen; break;
                case PresentationCue.DoorClose: clip = doorClose; break;
                case PresentationCue.RecordOpen: clip = recordOpen; break;
                case PresentationCue.PhotoOpen: clip = photoOpen; break;
                default: return false;
            }
            return TryPlayEffect(clip, false);
        }

        public void PlayEffect(AudioClip clip) => TryPlayEffect(clip, false);

        private bool TryPlayEffect(AudioClip clip, bool priority)
        {
            if (instance != this || clip == null || effects == null || effects.volume <= 0 || AudioListener.volume <= 0) return false;
            double now = Time.realtimeSinceStartupAsDouble;
            if ((clip == lastEffect && now < nextRepeatTime) || Playing(effects, clip) || Playing(secondEffect, clip)) return false;
            AudioSource voice = effects;
            if (effects.isPlaying)
            {
                if (secondEffect == null)
                {
                    secondEffect = gameObject.AddComponent<AudioSource>();
                    ConfigureEffect(secondEffect);
                }
                if (secondEffect.isPlaying && !priority) return false;
                voice = secondEffect;
            }
            voice.clip = clip;
            voice.Play();
            lastEffect = clip;
            nextRepeatTime = now + RetriggerSeconds;
            return true;
        }

        private static bool Playing(AudioSource source, AudioClip clip) => source != null && source.isPlaying && source.clip == clip;

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => EnsureRoomListener();

        // The shipped consultation scene has a camera but no listener. Use an existing listener
        // when present. The 2D fallback belongs to the bound room, not the persistent owner.
        private void EnsureRoomListener()
        {
            if (!roomScene.IsValid() || !roomScene.isLoaded) return;
            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener == roomListener || !listener.isActiveAndEnabled) continue;
                ReleaseRoomListener();
                return;
            }
            if (roomListener == null)
            {
                var host = new GameObject("Presentation Audio Listener");
                SceneManager.MoveGameObjectToScene(host, roomScene);
                roomListener = host.AddComponent<AudioListener>();
            }
            else if (roomListener.gameObject.scene != roomScene)
                SceneManager.MoveGameObjectToScene(roomListener.gameObject, roomScene);
        }

        private void ReleaseRoomListener()
        {
            if (roomListener == null) return;
            roomListener.enabled = false;
            Destroy(roomListener.gameObject);
            roomListener = null;
        }

        private void RoomUnloaded(Scene scene)
        {
            if (scene != roomScene) { EnsureRoomListener(); return; }
            PlayMusic(null);
            effects.Stop();
            if (secondEffect != null) secondEffect.Stop();
            lastEffect = null;
            roomScene = default;
            roomResumeSample = 0;
            ReleaseRoomListener();
        }

        private void OnDestroy()
        {
            if (instance != this) return;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= RoomUnloaded;
            ReleaseRoomListener();
            GameSettings.UnregisterSource(music);
            GameSettings.UnregisterSource(effects);
            GameSettings.UnregisterSource(secondEffect);
            instance = null;
        }
    }
}
