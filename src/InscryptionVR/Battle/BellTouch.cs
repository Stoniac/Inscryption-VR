using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class BellTouch
    {
        private readonly CardGrabber _grabber;
        private readonly ManualLogSource _logger;
        private readonly float _reachMeters;
        private float _minSlapSpeed;
        private CombatBell _bell;
        private Collider[] _colliders = new Collider[0];
        private float _nextSearch;
        private float _cooldownUntil;
        private readonly System.Collections.Generic.HashSet<int> _sounds = new System.Collections.Generic.HashSet<int>();
        private const float SoftSpeed = 0.2f;
        private const float HardSpeed = 1.6f;
        private const float SoftVolume = 0.35f;
        private const float FloorSpeed = 0.15f;
        private const float ConeRatio = 1f;
        private readonly Track _left = new Track();
        private readonly Track _right = new Track();

        private sealed class Track
        {
            internal VrHand Hand;
            internal Vector3 Palm;
            internal Vector3 Finger;
            internal bool HadPalm;
            internal bool HadFinger;
        }

        internal BellTouch(CardGrabber grabber, ManualLogSource logger, float reachMeters, float minSlapSpeed)
        {
            _grabber = grabber;
            _logger = logger;
            _reachMeters = Mathf.Clamp(reachMeters, 0f, 0.3f);
            _minSlapSpeed = Mathf.Clamp(minSlapSpeed, 0f, 5f);
        }

        internal void SetMinSlapSpeed(float metersPerSecond) { _minSlapSpeed = Mathf.Clamp(metersPerSecond, 0f, 5f); }

        internal void Forget()
        {
            _bell = null;
            _colliders = new Collider[0];
        }

        internal bool Refresh()
        {
            if (_bell == null || !_bell.gameObject.activeInHierarchy)
            {
                if (Time.unscaledTime < _nextSearch) return false;
                _nextSearch = Time.unscaledTime + 1f;
                _bell = Object.FindObjectOfType<CombatBell>();
                if (_bell == null) return false;
                _colliders = _bell.GetComponentsInChildren<Collider>();
            }
            return _colliders.Length > 0;
        }

        internal void Process(VrHand state, PlayerHand hand, bool canRing, float scale)
        {
            if (!canRing || !state.Ready)
            {
                state.InBell = false;
                Track idle = For(state);
                idle.HadPalm = false;
                idle.HadFinger = false;
                return;
            }
            float reach = _reachMeters * scale;
            Track track = For(state);
            bool palm = !state.Grip.Held && Contains(state.Position, reach);
            bool finger = state.HasIndexTip && (!state.IndexTouch || !state.Grip.Held) && Contains(state.IndexTip, reach);
            bool inside = palm || finger;
            bool entered = inside && !state.InBell;
            state.InBell = inside;
            bool fromAbove = (palm && track.HadPalm && Above(track.Palm)) || (finger && track.HadFinger && Above(track.Finger));
            track.Palm = state.Position;
            track.HadPalm = true;
            track.HadFinger = state.HasIndexTip;
            if (state.HasIndexTip) track.Finger = state.IndexTip;
            if (!entered || state.Busy || Time.unscaledTime < _cooldownUntil) return;
            if (!Ready(hand)) return;
            if (!fromAbove) { Refuse(state, "came in from the side or below", scale); return; }
            if (!Downward(state.Velocity, scale)) { Refuse(state, "not moving down fast or steeply enough", scale); return; }

            _cooldownUntil = Time.unscaledTime + 1f;
            float speed = Mathf.Max(0f, -state.Velocity.y) / Mathf.Max(scale, 1e-4f);
            float force = Mathf.Clamp01((speed - SoftSpeed) / (HardSpeed - SoftSpeed));
            VrSounds.Snapshot(_sounds);
            _bell.CursorSelectStart();
            _bell.CursorSelectEnd();
            VrSounds.ScaleNew(_sounds, Mathf.Lerp(SoftVolume, 1f, force));
            state.Play(Feel.Thunk, Mathf.Lerp(0.4f, 1f, force));
            VrTips.Done("bell");
            _logger.LogInfo("Combat bell rung by the " + state.Name + " hand at " + speed.ToString("0.00") + " m/s (" + Mathf.RoundToInt(force * 100f) + "% force).");
        }

        private Track For(VrHand state)
        {
            if (_left.Hand == state) return _left;
            if (_right.Hand == state) return _right;
            Track track = _left.Hand == null ? _left : _right;
            track.Hand = state;
            track.HadPalm = false;
            track.HadFinger = false;
            return track;
        }

        private bool Above(Vector3 previous)
        {
            Bounds bounds;
            return Bounds(out bounds) && previous.y > bounds.max.y;
        }

        private bool Downward(Vector3 velocity, float scale)
        {
            float down = -velocity.y;
            float sideways = new Vector2(velocity.x, velocity.z).magnitude;
            return down >= Mathf.Max(FloorSpeed, _minSlapSpeed) * scale && down >= sideways * ConeRatio;
        }

        private void Refuse(VrHand state, string reason, float scale)
        {
            float speed = -state.Velocity.y / Mathf.Max(scale, 1e-4f);
            VrLog.Chatter(_logger, "Combat bell touch by the " + state.Name + " hand ignored: " + reason + " (" + speed.ToString("0.00") + " m/s down).");
        }

        private bool Contains(Vector3 point, float reach)
        {
            for (int i = 0; i < _colliders.Length; i++)
            {
                Collider collider = _colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (collider.bounds.SqrDistance(point) <= reach * reach) return true;
            }
            return false;
        }

        internal bool Ready(PlayerHand hand)
        {
            if (_bell == null) return false;
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.Transitioning || flow.CurrentGameState != GameState.CardBattle) return false;
            TurnManager turn = Singleton<TurnManager>.Instance;
            if (turn == null || !turn.PlayerCanInitiateCombat || turn.GameEnding || turn.GameEnded) return false;
            if (_grabber.PlayInProgress || hand.ChoosingSlot || !_bell.Enabled) return false;
            BoardManager board = Singleton<BoardManager>.Instance;
            if (board != null && (board.ChoosingSlot || board.ChoosingSacrifices)) return false;
            InteractionCursor cursor = Singleton<InteractionCursor>.Instance;
            return cursor != null && !cursor.InteractionDisabled;
        }

        internal bool Bounds(out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            for (int i = 0; i < _colliders.Length; i++)
            {
                Collider collider = _colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = collider.bounds; any = true; }
                else bounds.Encapsulate(collider.bounds);
            }
            return any;
        }
    }
}
