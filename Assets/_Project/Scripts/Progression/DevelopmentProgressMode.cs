using System;
using System.Linq;
using UnityEngine;

namespace Gamsamo.Progression
{
    /// <summary>Opt-in, frozen for the Play session. Release players always return false.</summary>
    public static class DevelopmentProgressMode
    {
        private static bool? active;
        public static bool Enabled => active ?? (active = Requested()).Value;

        private static bool Requested()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorPrefs.GetBool(PreferenceKey, false);
#elif DEVELOPMENT_BUILD
            return Environment.GetCommandLineArgs().Contains("--gamsamo-dev-skip");
#else
            return false;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { active = null; GameProgressSession.NewGameRequested = false; }

#if UNITY_EDITOR
        private const string MenuPath = "Gamsamo/Development/70-70 Skip Preview (separate save)";
        private static string PreferenceKey => "Gamsamo.DevelopmentSkip." + Application.dataPath;

        [UnityEditor.MenuItem(MenuPath)]
        private static void Toggle()
        {
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            UnityEditor.EditorPrefs.SetBool(PreferenceKey, !UnityEditor.EditorPrefs.GetBool(PreferenceKey, false));
            active = null;
        }

        [UnityEditor.MenuItem(MenuPath, true)]
        private static bool ValidateMenu()
        {
            UnityEditor.Menu.SetChecked(MenuPath, UnityEditor.EditorPrefs.GetBool(PreferenceKey, false));
            return !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode;
        }
#endif
    }
}
