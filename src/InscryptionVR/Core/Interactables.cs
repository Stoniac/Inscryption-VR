using DiskCardGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    internal static class Interactables
    {
        private const float RefreshSeconds = 0.5f;
        private static MainInputInteractable[] _all = new MainInputInteractable[0];
        private static float _next = -1f;
        private static int _scene = -1;
        private static int _excludedLayers = int.MinValue;

        internal static MainInputInteractable[] All
        {
            get
            {
                int scene = SceneManager.GetActiveScene().handle;
                if (scene != _scene || Time.unscaledTime >= _next)
                {
                    _scene = scene;
                    _next = Time.unscaledTime + RefreshSeconds;
                    _all = Object.FindObjectsOfType<MainInputInteractable>();
                }
                return _all;
            }
        }

        internal static bool Usable(MainInputInteractable target)
        {
            return HandReach.Live(target) && target.Enabled && !OnExcludedLayer(target);
        }

        internal static bool OnExcludedLayer(Component target)
        {
            if (_excludedLayers == int.MinValue) _excludedLayers = LayerMask.GetMask("NonInteractable", "AlternateInteractable");
            return ((1 << target.gameObject.layer) & _excludedLayers) != 0;
        }

        internal static void SetHover(ref MainInputInteractable mine, MainInputInteractable target, MainInputInteractable other)
        {
            if (mine == target) return;
            MainInputInteractable previous = mine;
            mine = target;
            if (previous != null && previous != other && HandReach.Live(previous)) previous.CursorExit();
            if (target != null && target != other) target.CursorEnter();
        }
    }
}
