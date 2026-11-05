#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Gamsamo.Dialogue;
using Gamsamo.Networking;
using Gamsamo.Progression;
using Gamsamo.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Real PlayMode lifecycle checks in an isolated product/save folder. No mock AI or reward provider.</summary>
[InitializeOnLoad]
public static class BProgressPlayChecks
{
    private const string Key = "Gamsamo.B.PlayChecks.";
    private static double nextTick;
    static BProgressPlayChecks()
    {
        Debug.Log("B_PLAY_HARNESS reload running=" + Running + " stage=" + Stage);
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += Changed;
        EditorApplication.quitting += Restore;
    }
    private static int Stage { get => SessionState.GetInt(Key + "stage", 0); set => SessionState.SetInt(Key + "stage", value); }
    private static bool Running => SessionState.GetBool(Key + "running", false);
    private static string Output => Path.GetFullPath("artifacts/b-progression/playmode-results.txt");

    [MenuItem("Gamsamo/B/Run Isolated PlayMode Checks %#F10")]
    public static void Run()
    {
        if (EditorApplication.isPlaying || Running) throw new InvalidOperationException("Stop PlayMode before starting checks.");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save your scene changes before checks.");
        Directory.CreateDirectory(Path.GetDirectoryName(Output)); File.WriteAllText(Output, "B real PlayMode checks\n");
        SessionState.SetString(Key + "product", PlayerSettings.productName);
        SessionState.SetString(Key + "originalPath", Application.persistentDataPath);
        SessionState.SetBool(Key + "running", true); Stage = 0;
        PlayerSettings.productName = "GC_gamsamo_B_PlayTest_" + Guid.NewGuid().ToString("N");
        try
        {
            Check(Application.persistentDataPath != SessionState.GetString(Key + "originalPath", ""), "isolated persistentDataPath");
            SessionState.SetString(Key + "testPath", Application.persistentDataPath);
            File.AppendAllText(Output, "Test saves: " + Application.persistentDataPath + "\n");
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/StartMenu.unity");
            SetDeadline(); EditorApplication.EnterPlaymode();
        }
        catch (Exception e) { Fail(e); }
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        File.AppendAllText(Output, "PASS " + label + "\n"); Debug.Log("B_PLAY_PASS " + label);
    }
    private static void SetDeadline() => SessionState.SetString(Key + "deadline", DateTime.UtcNow.AddSeconds(90).ToString("O"));
    private static void Advance(int value) { Stage = value; SetDeadline(); nextTick = EditorApplication.timeSinceStartup + 1; }
    private static T Find<T>() where T : UnityEngine.Object => UnityEngine.Object.FindFirstObjectByType<T>();
    private static void Tick()
    {
        if (!Running || !EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.timeSinceStartup < nextTick) return;
        nextTick = EditorApplication.timeSinceStartup + .25;
        try
        {
            if (DateTime.UtcNow > DateTime.Parse(SessionState.GetString(Key + "deadline", DateTime.UtcNow.ToString("O"))))
                throw new TimeoutException("PlayMode stage " + Stage);
            CheckPath();
            var menu = Find<StartMenuController>(); var runtime = Find<GameProgressRuntime>(); var visits = Find<PatientVisitController>();
            switch (Stage)
            {
                case 0:
                    if (menu == null || menu.IsBusy) return;
                    Check(!menu.loadButton.interactable, "fresh menu has no continue");
                    menu.newGameButton.onClick.Invoke(); Advance(1); break;
                case 1:
                    if (runtime == null || !visits.ProgressReady) return;
                    Check(runtime.Progress.State.Phase == ProgressPhase.Waiting, "new game waits for door");
                    Check(!visits.useDemoDialogue, "production demo dialogue disabled");
                    SessionState.SetString(Key + "progress", runtime.Progress.State.ProgressId);
                    visits.room.OpenRecord(); Advance(2); break;
                case 2:
                    Check(visits.room.IsOverlayBlocking && !visits.TryAdmitNextPatient(), "record modal blocks admission");
                    visits.room.CloseRecord(); Advance(3); break;
                case 3:
                    if (visits.room.IsOverlayBlocking) return;
                    Check(visits.TryAdmitNextPatient(), "door admits first patient"); Advance(4); break;
                case 4:
                    if (visits.State != PatientVisitController.VisitState.Consulting) return;
                    var ai = Find<PatientAiAdapter>(); if (ai == null || string.IsNullOrEmpty(ai.LastError)) return;
                    Check(!string.IsNullOrEmpty(ai.CaseId) && !string.IsNullOrEmpty(ai.SessionId), "A case and round bound");
                    Check(runtime.Progress.State.ClearedCount == 0 && runtime.Progress.State.Economy.Balance == 0 && !visits.IsAwaitingDeparture,
                        "missing connection is not a clear or departure");
                    Check(!visits.TryAdmitNextPatient(), "active consultation blocks another admission");
                    SessionState.SetString(Key + "case", ai.CaseId); SessionState.SetString(Key + "round", ai.SessionId);
                    ScreenCapture.CaptureScreenshot(Path.GetFullPath("artifacts/b-progression/playmode-room.png"));
                    Advance(5); break;
                case 5:
                    Advance(6); EditorApplication.ExitPlaymode(); break;
                case 7:
                    if (menu == null || menu.IsBusy) return;
                    Check(menu.loadButton.interactable, "continue enabled after stop/restart");
                    menu.loadButton.onClick.Invoke(); Advance(8); break;
                case 8:
                    if (runtime == null || !visits.ProgressReady || visits.State != PatientVisitController.VisitState.Consulting) return;
                    var restored = Find<PatientAiAdapter>(); if (restored == null || string.IsNullOrEmpty(restored.CaseId)) return;
                    Check(runtime.Progress.State.ProgressId == SessionState.GetString(Key + "progress", ""), "same progress after PlayMode restart");
                    Check(restored.CaseId == SessionState.GetString(Key + "case", "") && restored.SessionId == SessionState.GetString(Key + "round", ""), "same AI case and round after restart");
                    // A failed connection without any outstanding request is safe to leave once draft and B state are saved.
                    Check(runtime.ReturnToStartMenu(), "safe title return after connection error"); Advance(9); break;
                case 9:
                    if (menu == null || menu.IsBusy) return;
                    menu.newGameButton.onClick.Invoke(); Advance(10); break;
                case 10:
                    if (runtime == null || !visits.ProgressReady) return;
                    Check(runtime.Progress.State.ProgressId != SessionState.GetString(Key + "progress", ""), "new game receives new progress ID");
                    Check(GameProgressSession.SavedRuns().Any(r => r.ProgressId == SessionState.GetString(Key + "progress", "") && !string.IsNullOrEmpty(r.Patients[0].AiSnapshot)), "previous run and AI history retained");
                    visits.room.OpenRecord(); Advance(11); break;
                case 11:
                    Check(visits.room.IsRecordOpen && visits.room.recordView != null, "history opens without patient admission");
                    visits.room.CloseRecord(); Advance(12); break;
                case 12:
                    if (visits.room.IsOverlayBlocking) return;
                    Check(runtime.ReturnToStartMenu(), "idle title return"); Advance(13); break;
                case 13:
                    if (menu == null || menu.IsBusy) return;
                    // Only the isolated test pointer is corrupted; real save files are never touched.
                    File.WriteAllText(Path.Combine(GameProgressSession.Root, "active.txt"), "test-corruption");
                    UnityEngine.SceneManagement.SceneManager.LoadScene("StartMenu"); Advance(14); break;
                case 14:
                    if (menu == null || menu.IsBusy) return;
                    Check(!menu.loadButton.interactable && !menu.newGameButton.interactable, "corruption disables continue and overwrite");
                    Check(File.ReadAllText(Path.Combine(GameProgressSession.Root, "active.txt")) == "test-corruption", "corruption remains available for recovery");
                    ScreenCapture.CaptureScreenshot(Path.GetFullPath("artifacts/b-progression/playmode-corruption.png"));
                    File.AppendAllText(Output, "B_PLAYMODE_PASS\n"); Debug.Log("B_PLAYMODE_PASS");
                    Advance(15); break;
                case 15: SessionState.SetBool(Key + "running", false); EditorApplication.ExitPlaymode(); break;
            }
        }
        catch (Exception e) { Fail(e); }
    }
    private static void CheckPath()
    {
        if (Application.persistentDataPath != SessionState.GetString(Key + "testPath", ""))
            throw new InvalidOperationException("Save isolation changed; aborting.");
    }
    private static void Changed(PlayModeStateChange state)
    {
        Debug.Log("B_PLAY_HARNESS state=" + state + " running=" + Running + " stage=" + Stage);
        if (state != PlayModeStateChange.EnteredEditMode) return;
        if (Running && Stage == 6)
        {
            Advance(7);
            EditorApplication.delayCall += () => { EditorSceneManager.OpenScene("Assets/_Project/Scenes/StartMenu.unity"); EditorApplication.EnterPlaymode(); };
        }
        else Restore();
    }
    private static void Fail(Exception error)
    {
        File.AppendAllText(Output, "FAIL stage " + Stage + ": " + error + "\n"); Debug.LogError("B_PLAYMODE_FAIL " + error);
        SessionState.SetBool(Key + "running", false);
        if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode(); else Restore();
    }
    private static void Restore()
    {
        string original = SessionState.GetString(Key + "product", "");
        if (string.IsNullOrEmpty(original)) return;
        PlayerSettings.productName = original;
        SessionState.EraseString(Key + "product"); SessionState.SetBool(Key + "running", false);
    }
}
#endif
