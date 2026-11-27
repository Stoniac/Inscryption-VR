using System;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class BoardViewReturn : MonoBehaviour
    {
        private const float IdleSeconds = 0.6f;

        private ManualLogSource _logger;
        private float _idleSince = -1f;
        private bool _logged;

        internal void Initialize(ManualLogSource logger)
        {
            _logger = logger;
        }

        private void Update()
        {
            bool idle;
            try { idle = VrScenes.InCabin && IdleOnBoard(); }
            catch (Exception) { idle = false; }
            if (!idle)
            {
                _idleSince = -1f;
                return;
            }
            if (_idleSince < 0f)
            {
                _idleSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _idleSince < IdleSeconds) return;
            _idleSince = -1f;
            try
            {
                ViewManager views = Singleton<ViewManager>.Instance;
                View from = views.CurrentView;
                views.SwitchToView(View.Default);
                if (!_logged)
                {
                    _logged = true;
                    _logger.LogInfo("Board view returned to the default view after " + from + " (the flat game does this with the down key; tutorials wait for it).");
                }
                else VrLog.Chatter(_logger, "Board view returned to default from " + from + ".");
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Board view return failed: " + exception.Message);
                enabled = false;
            }
        }

        private static bool IdleOnBoard()
        {
            TurnManager turns = Singleton<TurnManager>.Instance;
            ViewManager views = Singleton<ViewManager>.Instance;
            BoardManager board = Singleton<BoardManager>.Instance;
            if (turns == null || views == null || board == null || views.Controller == null) return false;
            if (!turns.IsPlayerMainPhase) return false;
            View current = views.CurrentView;
            if (current != View.Board && current != View.OpponentQueue) return false;
            if (views.Controller.LockState == ViewLockState.Locked) return false;
            if (!views.Controller.CurrentControlModeAllowsView(View.Default)) return false;
            if (board.ChoosingSlot || board.ChoosingSacrifices) return false;
            PlayerHand hand = Singleton<PlayerHand>.Instance;
            if (hand != null && hand.ChoosingSlot) return false;
            ItemsManager items = Singleton<ItemsManager>.Instance;
            if (items != null && items.ActivatingItem) return false;
            return true;
        }
    }
}
