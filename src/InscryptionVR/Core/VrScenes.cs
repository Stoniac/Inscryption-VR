using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    internal static class VrScenes
    {
        internal const string Cabin = "Part1_Cabin";
        internal const string Sanctum = "Part1_Sanctum";
        internal const string Finale = "Part1_Finale";
        internal const string AscensionMenu = "Ascension_Configure";
        internal const string AscensionCredits = "Ascension_Credits";
        internal const string StartMenu = "Start";
        internal const string Loading = "Loading";
        internal const string MenuCredits = "MenuCredits";

        internal static bool SanctumEnabled = true;
        internal static bool FinaleEnabled = true;
        internal static bool ScreenEnabled;
        internal static bool MainMenuEnabled;

        internal static string Current { get { return SceneManager.GetActiveScene().name; } }

        internal static bool InCabin { get { return Current == Cabin; } }

        internal static bool InSanctum { get { return SanctumEnabled && Current == Sanctum; } }

        internal static bool InFinale { get { return FinaleEnabled && Current == Finale; } }

        internal static bool InVrScene { get { return Supported(Current); } }

        internal static bool InScreen { get { return IsScreen(Current); } }

        internal static bool Supported(string scene)
        {
            return scene == Cabin || (SanctumEnabled && scene == Sanctum) || (FinaleEnabled && scene == Finale);
        }

        internal static bool IsScreen(string scene)
        {
            if (ScreenEnabled && (scene == AscensionMenu || scene == AscensionCredits)) return true;
            return MainMenuEnabled && (scene == StartMenu || scene == Loading || scene == MenuCredits);
        }

        internal static bool Renders(string scene)
        {
            return Supported(scene) || IsScreen(scene);
        }
    }
}
