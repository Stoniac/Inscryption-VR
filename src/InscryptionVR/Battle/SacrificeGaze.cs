using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class SacrificeGaze
    {
        internal const string ShakeGame = "Game";
        internal const string ShakeKnife = "Look at knife";
        internal const string ShakeCreature = "Look at creature";
        internal const string ShakeBoth = "Knife or creature";
        internal const string ShakeOff = "Off";
        internal static readonly string[] ShakeModes = { ShakeGame, ShakeKnife, ShakeCreature, ShakeBoth, ShakeOff };

        internal const string MarkerKnife = "Knife";
        internal const string MarkerBoth = "Knife and eyes";
        internal const string MarkerEyes = "Eyes";
        internal static readonly string[] MarkerModes = { MarkerKnife, MarkerBoth, MarkerEyes };

        private const float CreatureDegrees = 5f;
        private const float StickyDegrees = 2.5f;
        private const float CardMarginMeters = 0.015f;
        private const float LookHoldSeconds = 0.2f;
        private const float KnifeDegrees = 7f;
        private const float KnifeHoldSeconds = 0.3f;
        private const float ShakeSettleSeconds = 0.12f;
        private const float HeartNearMeters = 0.02f;
        private const float HeartFarMeters = 0.3f;
        private const float HeartSlowBpm = 60f;
        private const float HeartFastBpm = 140f;
        private const float HeartDubPhase = 0.18f;
        private const int KnifeSamples = 6;

        private sealed class ShakeState
        {
            internal bool Shaking = true;
            internal bool Want = true;
            internal float WantSince;
        }

        private static PlayableCard _looked;

        private readonly ManualLogSource _logger;
        private string _shake = ShakeGame;
        private string _marker = MarkerKnife;
        private bool _heartbeat;
        private readonly Dictionary<PlayableCard, ShakeState> _shakes = new Dictionary<PlayableCard, ShakeState>();
        private readonly List<PlayableCard> _stale = new List<PlayableCard>();
        private float _lookedLast = -10f;
        private float _knifeLast = -10f;
        private float _heartPhase;
        private bool _dubDone;
        private bool _loggedMode;
        private bool _loggedNoEyes;

        internal SacrificeGaze(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static PlayableCard Looked { get { return _looked; } }

        internal static bool NeedsEyes(string shake)
        {
            return shake == ShakeKnife || shake == ShakeCreature || shake == ShakeBoth;
        }

        internal void Configure(string shake, string marker, bool heartbeat)
        {
            _shake = Array.IndexOf(ShakeModes, shake) >= 0 ? shake : ShakeGame;
            _marker = Array.IndexOf(MarkerModes, marker) >= 0 ? marker : MarkerKnife;
            _heartbeat = heartbeat;
            _loggedMode = false;
            _loggedNoEyes = false;
        }

        internal PlayableCard Tick(List<CardSlot> slots, Predicate<CardSlot> marked, bool open, bool holding, VrHand holder,
            Vector3 pommel, Vector3 tip, PlayableCard knifeCard, float scale)
        {
            if (!open)
            {
                Reset();
                return null;
            }
            Vector3 origin, direction;
            bool eyes = EyeGaze.TryGetWorldGaze(out origin, out direction);
            float now = Time.unscaledTime;

            PlayableCard seen = eyes ? Pick(slots, origin, direction, scale) : null;
            if (seen != null)
            {
                _looked = seen;
                _lookedLast = now;
            }
            else if (_looked != null && (!eyes || now - _lookedLast > LookHoldSeconds)) _looked = null;

            if (eyes && holding && KnifeAngle(origin, direction, pommel, tip) < KnifeDegrees) _knifeLast = now;
            bool atKnife = eyes && holding && now - _knifeLast < KnifeHoldSeconds;

            bool live = EyeGaze.Live;
            ApplyShakes(slots, marked, live, atKnife, now);
            if (live && !_loggedMode)
            {
                _loggedMode = true;
                _logger.LogInfo("Sacrifice with eye tracking: shake '" + _shake + "', marker '" + _marker + "', heartbeat " + (_heartbeat ? "on" : "off") + ".");
            }
            else if (!live && !_loggedNoEyes && NeedsEyes(_shake))
            {
                _loggedNoEyes = true;
                _logger.LogInfo("Sacrifice shake '" + _shake + "' needs eye tracking, but SteamVR is sending no gaze (eye tracking: " + EyeGaze.Status +
                    "), so every creature trembles as in the flat game.");
            }

            if (_heartbeat && holding && holder != null && _looked != null) Heartbeat(holder, tip, scale);
            else
            {
                _heartPhase = 0f;
                _dubDone = false;
            }

            if (!live || _marker == MarkerKnife) return knifeCard;
            if (!holding) return null;
            if (_marker == MarkerEyes) return _looked;
            return knifeCard != null && knifeCard == _looked ? knifeCard : null;
        }

        private void ApplyShakes(List<CardSlot> slots, Predicate<CardSlot> marked, bool live, bool atKnife, float now)
        {
            bool game = !live || _shake == ShakeGame;
            for (int i = 0; i < slots.Count; i++)
            {
                CardSlot slot = slots[i];
                if (slot == null || !slot.IsPlayerSlot || slot.Card == null || !slot.Card.CanBeSacrificed || slot.Card.Anim == null) continue;
                PlayableCard card = slot.Card;
                ShakeState state;
                if (!_shakes.TryGetValue(card, out state))
                {
                    state = new ShakeState();
                    state.WantSince = now;
                    _shakes[card] = state;
                }
                bool want;
                if (game || marked(slot)) want = true;
                else if (_shake == ShakeKnife) want = atKnife;
                else if (_shake == ShakeCreature) want = card == _looked;
                else if (_shake == ShakeBoth) want = atKnife || card == _looked;
                else want = false;
                if (want != state.Want)
                {
                    state.Want = want;
                    state.WantSince = now;
                }
                bool settle = game || marked(slot) || now - state.WantSince >= ShakeSettleSeconds;
                if (state.Shaking == state.Want || !settle) continue;
                state.Shaking = state.Want;
                card.Anim.SetShaking(state.Shaking);
            }
            _stale.Clear();
            foreach (KeyValuePair<PlayableCard, ShakeState> entry in _shakes)
                if (entry.Key == null || entry.Key.Dead) _stale.Add(entry.Key);
            for (int i = 0; i < _stale.Count; i++) _shakes.Remove(_stale[i]);
        }

        private void Heartbeat(VrHand holder, Vector3 tip, float scale)
        {
            Bounds bounds;
            float meters = HeartFarMeters;
            if (HandReach.CardBounds(_looked, out bounds)) meters = Mathf.Sqrt(bounds.SqrDistance(tip)) / Mathf.Max(scale, 1e-4f);
            float closeness = 1f - Mathf.Clamp01((meters - HeartNearMeters) / (HeartFarMeters - HeartNearMeters));
            float bpm = Mathf.Lerp(HeartSlowBpm, HeartFastBpm, closeness);
            float strength = Mathf.Lerp(0.25f, 0.6f, closeness);
            float before = _heartPhase;
            _heartPhase += Time.unscaledDeltaTime * bpm / 60f;
            if (before == 0f || _heartPhase >= 1f)
            {
                _heartPhase = before == 0f ? 1e-4f : _heartPhase - Mathf.Floor(_heartPhase);
                _dubDone = false;
                holder.Play(Feel.Tap, strength);
            }
            if (!_dubDone && _heartPhase >= HeartDubPhase)
            {
                _dubDone = true;
                holder.Play(Feel.Tick, strength);
            }
        }

        private static PlayableCard Pick(List<CardSlot> slots, Vector3 origin, Vector3 direction, float scale)
        {
            PlayableCard best = null;
            float bestScore = float.PositiveInfinity;
            float margin = CardMarginMeters * scale;
            for (int i = 0; i < slots.Count; i++)
            {
                CardSlot slot = slots[i];
                if (slot == null || !slot.IsPlayerSlot || slot.Card == null || !slot.Card.CanBeSacrificed) continue;
                Bounds bounds;
                if (!HandReach.CardBounds(slot.Card, out bounds)) continue;
                Vector3 toCard = bounds.center - origin;
                if (toCard.sqrMagnitude < 1e-8f) continue;
                float angle = Vector3.Angle(direction, toCard);
                bool inside = false;
                if (direction.y < -0.02f)
                {
                    float t = (bounds.max.y - origin.y) / direction.y;
                    if (t > 0f)
                    {
                        Vector3 hit = origin + direction * t;
                        inside = hit.x >= bounds.min.x - margin && hit.x <= bounds.max.x + margin && hit.z >= bounds.min.z - margin && hit.z <= bounds.max.z + margin;
                    }
                }
                if (!inside && angle > CreatureDegrees) continue;
                float score = inside ? angle * 0.5f : angle;
                if (slot.Card == _looked) score -= StickyDegrees;
                if (score >= bestScore) continue;
                bestScore = score;
                best = slot.Card;
            }
            return best;
        }

        private static float KnifeAngle(Vector3 origin, Vector3 direction, Vector3 pommel, Vector3 tip)
        {
            float best = 180f;
            for (int i = 0; i < KnifeSamples; i++)
            {
                Vector3 point = Vector3.Lerp(pommel, tip, i / (float)(KnifeSamples - 1));
                Vector3 toPoint = point - origin;
                if (toPoint.sqrMagnitude < 1e-8f) continue;
                best = Mathf.Min(best, Vector3.Angle(direction, toPoint));
            }
            return best;
        }

        internal void Reset()
        {
            _shakes.Clear();
            _looked = null;
            _heartPhase = 0f;
            _dubDone = false;
        }
    }
}
