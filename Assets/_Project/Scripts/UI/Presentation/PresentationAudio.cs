using UnityEngine;

namespace Gamsamo.UI.Presentation
{
    /// <summary>One persistent audio owner; repeated scene prefab instances cannot start a second BGM.</summary>
    public sealed class PresentationAudio : MonoBehaviour
    {
        private static PresentationAudio instance;
        public AudioSource music;
        public AudioSource effects;
        public EndingContentCatalog content;
        private AudioClip roomClip;
        public static PresentationAudio Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOwner() => instance = null;

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            // Use this as a dedicated prefab root so no scene UI is carried into the next scene.
            if (Application.isPlaying) DontDestroyOnLoad(gameObject);
            if (music == null) music = gameObject.AddComponent<AudioSource>();
            if (effects == null || effects == music) effects = gameObject.AddComponent<AudioSource>();
            music.playOnAwake = effects.playOnAwake = false;
            music.spatialBlend = effects.spatialBlend = 0;
            music.loop = true;
            GameSettings.RegisterMusicSource(music);
            GameSettings.RegisterEffectsSource(effects);
        }
        public void PlayRoomMusic()
        {
            roomClip = content != null ? content.roomMusic : null;
            PlayMusic(roomClip);
        }
        public void PlayEndingMusic() => PlayMusic(content != null ? content.endingMusic : null);
        public void RestoreRoomMusic() => PlayMusic(roomClip);
        public void PlayMusic(AudioClip clip)
        {
            if (instance != this || music == null) return;
            if (clip == null) { music.Stop(); music.clip = null; return; }
            if (music.clip == clip && music.isPlaying) return;
            music.clip = clip; music.Play();
        }
        public void PlayPageSound() => PlayEffect(content != null ? content.pageSound : null);
        public void PlayClearSound() => PlayEffect(content != null ? content.clearSound : null);
        public void PlayEffect(AudioClip clip)
        { if (instance == this && effects != null && clip != null) effects.PlayOneShot(clip); }
        private void OnDestroy()
        {
            if (instance != this) return;
            GameSettings.UnregisterSource(music); GameSettings.UnregisterSource(effects);
            instance = null;
        }
    }
}
