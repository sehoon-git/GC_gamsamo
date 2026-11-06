using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Gamsamo.Progression;

// Real mode/session/file code with Unity API stubs. No rendering/Play claim.
static class RuntimeGuardChecks
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    static void Reset() => typeof(DevelopmentProgressMode).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    static void Main()
    {
        var path = Path.Combine(Path.GetTempPath(), "gamsamo-mode-check-" + Guid.NewGuid().ToString("N"));
        UnityEngine.Application.persistentDataPath = path;
        UnityEngine.Application.dataPath = Path.Combine(path, "Assets");
        var roster = new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" };
        bool expected = false;
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
        expected = Environment.GetCommandLineArgs().Contains("--gamsamo-dev-skip");
#endif
        try
        {
            Reset(); Check(DevelopmentProgressMode.Enabled == expected, "release ignores switch; development defaults off");
            var run = GameProgressSession.Create(roster);
            Check(run.State.DevelopmentMode == expected, "new save explicitly marks selected mode");
            Check(GameProgressSession.Open().State.ProgressId == run.State.ProgressId, "same mode restores same run");
            Check(GameProgressSession.Root.EndsWith(expected ? "game-progress-development" : "game-progress"), "mode selects isolated root");
#if UNITY_EDITOR
            var normalRoot = GameProgressSession.Root;
            var normalPointer = File.ReadAllBytes(Path.Combine(normalRoot, "active.txt"));
            UnityEditor.EditorPrefs.SetBool("Gamsamo.DevelopmentSkip." + UnityEngine.Application.dataPath, true);
            Check(!DevelopmentProgressMode.Enabled, "mid-session changes cannot redirect writes");
            Reset(); Check(DevelopmentProgressMode.Enabled && GameProgressSession.Open() == null, "next Play uses empty separate development selection");
            var dev = GameProgressSession.Create(roster);
            Check(dev.State.DevelopmentMode && dev.State.ProgressId != run.State.ProgressId, "development has a distinct progress identity");
            Check(normalPointer.SequenceEqual(File.ReadAllBytes(Path.Combine(normalRoot, "active.txt"))), "normal pointer preserved");
            UnityEditor.EditorPrefs.SetBool("Gamsamo.DevelopmentSkip." + UnityEngine.Application.dataPath, false);
            Reset(); Check(GameProgressSession.Open().State.ProgressId == run.State.ProgressId, "disabling restores normal game");
#endif
            Console.WriteLine("PASS " + checks + " mode/file checks (Unity API stubs; no Play/rendering)");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
