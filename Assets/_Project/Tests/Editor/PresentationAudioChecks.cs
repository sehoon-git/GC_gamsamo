using System;
using System.Collections;
using Gamsamo.Progression;
using Gamsamo.UI;
using Gamsamo.UI.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamsamo.Tests.Editor
{
    /// <summary>Play Mode checks with silent, in-memory clips. No catalog or game progress is written.</summary>
    public static class PresentationAudioChecks
    {
        private const string Pending = "Gamsamo.AudioChecks.Pending";
        private static IEnumerator steps;
        private static double deadline;
        private static int count;
        private static GameSettings.Values previous;
        private static string previousPrefs;
        private static bool hadPrefs;
        private static float previousTimeScale;
        private static PresentationAudio owner;
        private static AudioClip[] clips;
        private static EndingContentCatalog catalog;
        private static AudioListener[] originalListeners;

        [MenuItem("Gamsamo/Audio/Check Delivered Clips")]
        public static void CheckDeliveredClips()
        {
            const string prefabPath = "Assets/_Project/Prefabs/Presentation/PresentationAudio.prefab";
            var audio = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<PresentationAudio>();
            var entries = new (string id, AudioClip clip)[] {
                ("bgm_room", audio.roomMusic), ("bgm_ending", audio.endingMusic), ("sfx_page_turn", audio.pageSound),
                ("sfx_clear", audio.clearSound), ("sfx_ui_confirm", audio.uiConfirm), ("sfx_ui_back", audio.uiBack),
                ("sfx_purchase_success", audio.purchaseSuccess), ("sfx_action_unavailable", audio.actionUnavailable),
                ("sfx_door_open", audio.doorOpen), ("sfx_door_close", audio.doorClose),
                ("sfx_record_open", audio.recordOpen), ("sfx_photo_open", audio.photoOpen)
            };
            foreach (var entry in entries)
            {
                string path = "Assets/_Project/Audio/Produced/" + entry.id + ".wav";
                if (entry.clip == null || AssetDatabase.GetAssetPath(entry.clip) != path)
                    throw new InvalidOperationException("Missing or miswired prefab clip: " + path);
                int channels = entry.id.StartsWith("bgm_", StringComparison.Ordinal) ? 2 : 1;
                if (entry.clip.samples == 0 || entry.clip.channels != channels ||
                    (entry.clip.frequency != 44100 && entry.clip.frequency != 48000))
                    throw new InvalidOperationException("Unexpected imported PCM format: " + path);
            }
            Debug.Log("AUDIO_DELIVERY_PASS " + entries.Length + " imported and wired clips");
        }

        [InitializeOnLoadMethod]
        private static void Listen()
        {
            EditorApplication.playModeStateChanged -= ModeChanged;
            EditorApplication.playModeStateChanged += ModeChanged;
        }

        [MenuItem("Gamsamo/Audio/Run Playback Checks")]
        public static void Run()
        {
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode ||
                Object.FindFirstObjectByType<GameProgressRuntime>() != null || PresentationAudio.Instance != null)
                throw new InvalidOperationException("Open an empty scene outside Play Mode to run isolated audio checks.");
            SessionState.SetBool(Pending, true);
            EditorApplication.isPlaying = true;
        }

        private static void ModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode && steps != null) Finish(false);
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.EraseBool(Pending);
            count = 0;
            deadline = EditorApplication.timeSinceStartup + 40;
            steps = Exercise();
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Audio playback checks timed out");
                if (steps.MoveNext()) return;
                Debug.Log("AUDIO_PLAYBACK_PASS " + count);
                Finish(true);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Finish(false);
            }
        }

        private static void Finish(bool success)
        {
            EditorApplication.update -= Tick;
            steps = null;
            if (previous != null)
            {
                GameSettings.Apply(previous);
                if (hadPrefs) PlayerPrefs.SetString(GameSettings.PlayerPrefsKey, previousPrefs);
                else PlayerPrefs.DeleteKey(GameSettings.PlayerPrefsKey);
                PlayerPrefs.Save();
                Time.timeScale = previousTimeScale;
                previous = null;
            }
            if (owner != null) Object.DestroyImmediate(owner.gameObject);
            if (originalListeners != null)
                foreach (var listener in originalListeners) if (listener != null) listener.enabled = true;
            if (catalog != null) Object.DestroyImmediate(catalog);
            if (clips != null) foreach (var clip in clips) if (clip != null) Object.DestroyImmediate(clip);
            if (Application.isBatchMode) EditorApplication.Exit(success ? 0 : 1);
            else if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Audio check failed: " + name);
            count++;
        }

        private static PresentationAudio Spawn(Scene scene, EndingContentCatalog source)
        {
            var go = new GameObject("Audio QA Owner");
            go.SetActive(false);
            SceneManager.MoveGameObjectToScene(go, scene);
            var audio = go.AddComponent<PresentationAudio>();
            audio.content = source;
            go.SetActive(true);
            return audio;
        }

        private static IEnumerator Exercise()
        {
            Check(PresentationAudio.Instance == null, "empty fixture");
            previousTimeScale = Time.timeScale;
            previous = GameSettings.Current;
            hadPrefs = PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey);
            previousPrefs = PlayerPrefs.GetString(GameSettings.PlayerPrefsKey);
            var settings = previous.Copy();
            settings.masterVolume = settings.musicVolume = settings.effectsVolume = 0f;
            GameSettings.Apply(settings);
            var firstRoom = SceneManager.CreateScene("Audio QA room 1");
            var secondRoom = SceneManager.CreateScene("Audio QA room 2");
            originalListeners = Array.FindAll(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None), item => item.isActiveAndEnabled);
            foreach (var listener in originalListeners) listener.enabled = false;
            catalog = ScriptableObject.CreateInstance<EndingContentCatalog>();
            clips = new AudioClip[5];
            for (int i = 0; i < clips.Length; i++) clips[i] = AudioClip.Create("silent-audio-qa-" + i, 4 * 44100, 1, 44100, false);
            owner = Spawn(firstRoom, catalog);
            owner.PlayRoomMusic();
            Check(ActiveListeners() == 1, "listener created for a room without one");
            var listenerHost = new GameObject("Existing QA listener");
            SceneManager.MoveGameObjectToScene(listenerHost, firstRoom);
            var existingListener = listenerHost.AddComponent<AudioListener>();
            owner.PlayRoomMusic();
            Check(ActiveListeners() == 1 && existingListener.enabled, "reuse an existing listener without duplicate playback");
            owner.PlayEndingMusic();
            owner.PlayPageSound();
            owner.PlayClearSound();
            Check(owner.music.clip == null && !owner.music.isPlaying, "missing catalog clips");
            Check(!owner.TryPlayCue(PresentationCue.PhotoOpen), "optional empty cue");
            owner.roomMusic = clips[0]; owner.endingMusic = clips[1];
            owner.pageSound = clips[2]; owner.clearSound = clips[3]; owner.uiConfirm = clips[4];
            owner.PlayRoomMusic();
            Check(owner.music.volume == 0f && owner.effects.volume == 0f && AudioListener.volume == 0f, "mute before new sources");
            Check(!owner.TryPlayCue(PresentationCue.PageTurn), "muted effects do not queue");
            settings.masterVolume = 0.8f; settings.effectsVolume = 0.3f; settings.musicVolume = 0.4f;
            GameSettings.Apply(settings);
            Check(Mathf.Approximately(owner.effects.volume, 0.3f) && Mathf.Approximately(AudioListener.volume, 0.8f), "restore category and master levels separately");
            Check(owner.music.clip == clips[0] && owner.music.isPlaying && owner.music.loop, "room fallback loop");

            Time.timeScale = 0f;
            owner.music.timeSamples = 44100;
            owner.PlayEndingMusic();
            Check(owner.music.clip == clips[0], "request starts a fade on existing source");
            while (owner.music.clip != clips[1] || owner.music.volume < 0.399f) yield return null;
            Check(owner.music.isPlaying, "ending loop at saved volume even while paused");
            owner.RestoreRoomMusic();
            owner.PlayEndingMusic();
            yield return null;
            Check(owner.music.clip == clips[1], "last replay request wins during fade");
            owner.RestoreRoomMusic();
            while (owner.music.clip != clips[0] || owner.music.volume < 0.399f) yield return null;
            Check(owner.music.isPlaying && owner.music.timeSamples >= 44100, "skip resumes the room playhead");
            catalog.roomMusic = clips[1];
            owner.PlayRoomMusic();
            while (owner.music.clip != clips[1]) yield return null;
            Check(owner.music.isPlaying, "catalog precedes prefab fallback");
            owner.PlayMusic(null);
            Check(owner.music.clip == null && !owner.music.isPlaying, "null track stops safely");

            var duplicate = Spawn(secondRoom, catalog);
            duplicate.PlayRoomMusic();
            while (duplicate != null) yield return null;
            Check(PresentationAudio.Instance == owner && Object.FindObjectsByType<PresentationAudio>(FindObjectsSortMode.None).Length == 1,
                "room reentry forwards to the persistent owner");
            Check(owner.music.isPlaying && owner.music.clip == clips[1], "reentry restarts room music");
            owner.music.timeSamples = 1000;
            owner.PlayRoomMusic();
            Check(owner.music.timeSamples >= 1000, "same BGM does not restart");
            var unloading = SceneManager.UnloadSceneAsync(firstRoom);
            while (!unloading.isDone) yield return null;
            Check(owner.music.isPlaying, "unloading old room preserves rebound audio");
            Check(ActiveListeners() == 1, "room regains a listener after the previous scene unloads");

            Check(owner.TryPlayCue(PresentationCue.PageTurn), "first page turn");
            Check(!owner.TryPlayCue(PresentationCue.PageTurn), "repeated page turn limited");
            Check(owner.TryPlayCue(PresentationCue.UiConfirm), "second concurrent cue");
            owner.recordOpen = clips[0];
            Check(!owner.TryPlayCue(PresentationCue.RecordOpen), "normal voices bounded at two");
            Check(!owner.TryPlayCue(PresentationCue.Clear), "no clear without saved result");
            var receipt = new ConfirmedResult { Cleared = true, Identity = new RoundIdentity {
                ProgressId = "test-run", PatientId = "test-patient", CaseId = "test-case", RoundId = "test-round" } };
            Check(!owner.TryPlayFirstClear(receipt), "later clears are silent");
            receipt.FirstClear = true;
            Check(owner.TryPlayFirstClear(receipt) && !owner.TryPlayFirstClear(receipt), "saved first clear has priority and is announced once");
            Check(owner.GetComponents<AudioSource>().Length == 3, "one music and two effect sources");
            settings.effectsVolume = 0f;
            GameSettings.Apply(settings);
            foreach (var voice in owner.GetComponents<AudioSource>())
                if (voice != owner.music) Check(voice.volume == 0f, "mute includes second effects channel");

            unloading = SceneManager.UnloadSceneAsync(secondRoom);
            while (!unloading.isDone) yield return null;
            Check(owner.music.clip == null && !owner.music.isPlaying, "leaving bound room silences persistent sources");
            Check(ActiveListeners() == 0, "temporary listener leaves with the bound scene");
        }

        private static int ActiveListeners() =>
            Array.FindAll(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None), item => item.isActiveAndEnabled).Length;
    }
}
