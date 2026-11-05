using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gamsamo.Dialogue;
using Gamsamo.UI;
using Gamsamo.UI.Presentation;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Gamsamo.Editor.Presentation
{
    /// <summary>Explicit, unsaved development scene. Synthetic data never enters a normal scene/build.</summary>
    [InitializeOnLoad]
    public static class PresentationPreviewTools
    {
        private const string RunKey = "Gamsamo.DPreview";
        private static IEnumerator checks;
        private static readonly StringBuilder report = new StringBuilder();
        private static bool locked;
        private static int passed;
        private static GameSettings.Values oldSettings;
        private static string oldPrefs;
        private static bool hadPrefs;
        private static AudioClip testClip;
        private static double deadline;

        static PresentationPreviewTools() => EditorApplication.playModeStateChanged += PlayChanged;

        [MenuItem("Gamsamo/Presentation/Open D Preview (Synthetic Only)")]
        public static void OpenPreview()
        {
            PrepareScene(); SessionState.SetInt(RunKey, 1); EditorApplication.isPlaying = true;
        }
        public static void RunBatchPlayChecks()
        {
            PrepareScene(); SessionState.SetInt(RunKey, 2); EditorApplication.isPlaying = true;
        }
        private static void PrepareScene()
        {
            if (Application.isPlaying || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Leave Play and save your own scene before opening the isolated D preview.");
            for (int index = 0; index < SceneManager.sceneCount; index++)
                if (SceneManager.GetSceneAt(index).isDirty)
                    throw new InvalidOperationException("Save all open scenes before opening the isolated D preview.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("DEV EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            var camera = new GameObject("DEV Camera", typeof(Camera)).GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color32(42, 33, 28, 255);
            new GameObject("DEV AudioListener", typeof(AudioListener));
            var background = PresentationPrefabBuilder.CanvasRoot("DEV Controls");
            background.GetComponent<Canvas>().sortingOrder = 0;
            PresentationPrefabBuilder.Label("Banner", background.transform, "개발 전용 · 합성 데이터 · 실제 상담/지급/저장 없음", 70, 30, 1400, 80, 28);
            PresentationPrefabBuilder.Button("DEV Door", background.transform, "문 (입력 차단 확인)", 50, 200, 300, 80);
            PresentationPrefabBuilder.Button("DEV Clear", background.transform, "개발용 클리어 보기", 50, 310, 300, 80);
            PresentationPrefabBuilder.Button("DEV Credits", background.transform, "개발용 크레딧 보기", 50, 420, 300, 80);
            var view = background.AddComponent<PatientDialogueView>();
            view.root = PresentationPrefabBuilder.Rect("Dialogue", background.transform, 400, 800, 1100, 230).gameObject.AddComponent<CanvasGroup>();
            view.speakerText = PresentationPrefabBuilder.Label("Speaker", view.root.transform, "개발용", 0, 0, 400, 55, 26);
            view.bodyText = PresentationPrefabBuilder.Label("Body", view.root.transform, "", 0, 60, 1000, 80, 27);
            view.statusText = PresentationPrefabBuilder.Label("Status", view.root.transform, "", 0, 145, 1100, 75, 23);
            background.SetActive(true);
            PresentationPrefabBuilder.CreateGuidebook().SetActive(true);
            PresentationPrefabBuilder.CreateEnding().SetActive(true);
            PresentationPrefabBuilder.CreatePhotoViewer().SetActive(true);
            var audio = new GameObject("DEV Audio", typeof(PresentationAudio));
            audio.GetComponent<PresentationAudio>().content = AssetDatabase.LoadAssetAtPath<EndingContentCatalog>(PresentationPrefabBuilder.CatalogPath);
        }
        private static void PlayChanged(PlayModeStateChange state)
        {
            int mode = SessionState.GetInt(RunKey, 0);
            if (state == PlayModeStateChange.EnteredPlayMode && mode != 0)
            {
                locked = false;
                var dialogue = UnityEngine.Object.FindFirstObjectByType<PatientDialogueView>();
                dialogue.ShowLine("개발용 표시", "대기 중에도 보존되는 대화 예시입니다."); dialogue.RevealAll();
                dialogue.SetProcessingState(DialogueProcessingState.WaitingForReply);
                foreach (var modal in UnityEngine.Object.FindObjectsByType<PresentationModal>(FindObjectsSortMode.None))
                {
                    modal.dialogue = dialogue;
                    modal.BindModalGate(() => { if (locked) return false; locked = true; return true; }, () => locked = false);
                }
                var ending = UnityEngine.Object.FindFirstObjectByType<EndingPresentationView>();
                ending.PlaybackSaveRequested += (g, p, c, s, stage) => ending.AcknowledgePlaybackSaved(g, p, c, s, stage, true);
                GameObject.Find("DEV Clear").GetComponent<Button>().onClick.AddListener(() => ending.RestoreCommittedClear("DEV", "DEV", "DEV", Guid.NewGuid().ToString(), true, true, "개발용", 7, true));
                GameObject.Find("DEV Credits").GetComponent<Button>().onClick.AddListener(() => ending.RestoreFinalEnding("DEV", true, true));
                if (mode == 2)
                {
                    passed = 0; report.Clear(); deadline = EditorApplication.timeSinceStartup + 35;
                    checks = PlayChecks(); EditorApplication.update += Tick;
                }
            }
            if (state == PlayModeStateChange.ExitingPlayMode) Cleanup();
            if (state == PlayModeStateChange.EnteredEditMode)
            { SessionState.EraseInt(RunKey); if (mode == 2 && Application.isBatchMode) EditorApplication.Exit(0); }
        }
        private static void Check(bool value, string message)
        {
            report.AppendLine((value ? "PASS " : "FAIL ") + message);
            if (!value) throw new InvalidOperationException(message);
            passed++;
        }
        private static IEnumerator PlayChecks()
        {
            yield return null;
            var guide = UnityEngine.Object.FindFirstObjectByType<GuidebookView>();
            var dialogue = UnityEngine.Object.FindFirstObjectByType<PatientDialogueView>();
            string before = dialogue.DisplayedText;
            Check(guide.TryOpen(), "Real Play guide opens using the room lease");
            yield return null;
            Canvas.ForceUpdateCanvases();
            var door = GameObject.Find("DEV Door").GetComponent<Button>();
            var point = RectTransformUtility.WorldToScreenPoint(null, door.transform.position);
            var pointer = new PointerEventData(EventSystem.current) { position = point };
            var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
            Check(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(guide.panel.transform), "Real UI raycaster hits modal ahead of the door");
            Check(locked && !dialogue.IsVisible, "Room lease and dialogue entry points remain blocked");
            guide.Next(); guide.Close();
            Check(!locked && dialogue.IsVisible && dialogue.DisplayedText == before, "Closing preserves the current dialogue");
            yield return null;

            oldSettings = GameSettings.Current; hadPrefs = PlayerPrefs.HasKey(GameSettings.PlayerPrefsKey);
            oldPrefs = PlayerPrefs.GetString(GameSettings.PlayerPrefsKey, "");
            var owner = PresentationAudio.Instance;
            testClip = AudioClip.Create("DEV silent clip", 44100, 1, 44100, false);
            owner.PlayMusic(testClip); yield return null;
            Check(owner.music.isPlaying, "Configured BGM uses the live Unity AudioSource");
            var original = owner.music;
            new GameObject("DEV duplicate audio", typeof(PresentationAudio));
            yield return null;
            Check(PresentationAudio.Instance == owner && UnityEngine.Object.FindObjectsByType<PresentationAudio>(FindObjectsSortMode.None).Length == 1, "Duplicate scene audio owner is destroyed");
            owner.PlayMusic(testClip);
            Check(owner.music == original && owner.music.isPlaying, "Repeated same-track entry keeps a single source");
            var values = oldSettings.Copy(); values.masterVolume = 0.7f; values.musicVolume = 0.3f; values.effectsVolume = 0.2f;
            GameSettings.Apply(values);
            Check(Mathf.Approximately(owner.music.volume, 0.3f) && Mathf.Approximately(owner.effects.volume, 0.2f) && Mathf.Approximately(AudioListener.volume, 0.7f), "Committed settings apply master/music/effects exactly once");
            owner.PlayMusic(null); Check(!owner.music.isPlaying, "Missing media remains silent");
            report.AppendLine("TOTAL " + passed + " passed. Synthetic scene, no server/real progress saves.");
        }
        private static void Tick()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("D Play checks timed out.");
                if (checks != null && checks.MoveNext()) return;
                Finish(0);
            }
            catch (Exception error) { report.AppendLine(error.ToString()); Finish(1); }
        }
        private static void Finish(int code)
        {
            EditorApplication.update -= Tick;
            Directory.CreateDirectory("artifacts/d-presentation");
            File.WriteAllText("artifacts/d-presentation/play-checks.txt", report.ToString());
            Cleanup();
            if (code != 0 && Application.isBatchMode) EditorApplication.Exit(code);
            else EditorApplication.isPlaying = false;
        }
        private static void Cleanup()
        {
            EditorApplication.update -= Tick; checks = null;
            if (oldSettings != null)
            {
                GameSettings.Apply(oldSettings);
                if (hadPrefs) PlayerPrefs.SetString(GameSettings.PlayerPrefsKey, oldPrefs); else PlayerPrefs.DeleteKey(GameSettings.PlayerPrefsKey);
                PlayerPrefs.Save(); oldSettings = null;
            }
            if (testClip != null) { UnityEngine.Object.DestroyImmediate(testClip); testClip = null; }
        }
    }
}
