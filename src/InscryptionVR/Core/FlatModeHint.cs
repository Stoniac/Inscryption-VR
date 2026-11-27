using UnityEngine;
using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    internal sealed class FlatModeHint : MonoBehaviour
    {
        private const string AutoMessage = "Start SteamVR before the game to play in VR";
        private const string FlagMessage = "Add \"-" + VrLaunch.VrFlag + "\" in Steam's launch options for VR";
        internal bool Auto;
        private GUIStyle _style;

        private void OnGUI()
        {
            if (SceneManager.GetActiveScene().name != VrScenes.StartMenu) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box);
                _style.alignment = TextAnchor.MiddleCenter;
                _style.fontSize = Mathf.Max(12, Screen.height / 60);
                _style.normal.textColor = new Color(0.9f, 0.85f, 0.75f);
                _style.padding = new RectOffset(12, 12, 6, 6);
            }
            string message = Auto ? AutoMessage : FlagMessage;
            Vector2 size = _style.CalcSize(new GUIContent(message));
            var rect = new Rect(16f, Screen.height - size.y - 16f, size.x, size.y);
            GUI.Box(rect, message, _style);
        }
    }
}
