using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class CampfireWarmth
    {
        private static readonly FieldInfo LightField = typeof(CardStatBoostSequencer).GetField("campfireLight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private const float WarmMeters = 0.3f;
        private const float HotMeters = 0.08f;
        private const float LitIntensity = 0.05f;

        private readonly ManualLogSource _logger;
        private CardStatBoostSequencer _sequencer;
        private float _nextFind;
        private bool _leftHot;
        private bool _rightHot;
        private bool _logged;

        internal CampfireWarmth(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available { get { return LightField != null; } }

        internal void Tick(VrHand left, VrHand right, bool input, float scale)
        {
            Light fire = input ? FindFire() : null;
            if (fire == null)
            {
                _leftHot = false;
                _rightHot = false;
                return;
            }
            Vector3 position = fire.transform.position;
            float flicker = 0.75f + 0.25f * Mathf.PerlinNoise(Time.unscaledTime * 7f, 0.4f);
            Sense(left, position, flicker, scale, ref _leftHot);
            Sense(right, position, flicker, scale, ref _rightHot);
        }

        private Light FindFire()
        {
            GameFlowManager flow = Singleton<GameFlowManager>.Instance;
            if (flow == null || flow.CurrentGameState != GameState.SpecialCardSequence)
            {
                _sequencer = null;
                return null;
            }
            if (_sequencer == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 0.5f;
                _sequencer = Object.FindObjectOfType<CardStatBoostSequencer>();
            }
            if (_sequencer == null || !_sequencer.isActiveAndEnabled) return null;
            Light fire = LightField.GetValue(_sequencer) as Light;
            if (fire == null || !fire.enabled || !fire.gameObject.activeInHierarchy || fire.intensity < LitIntensity) return null;
            return fire;
        }

        private void Sense(VrHand hand, Vector3 fire, float flicker, float scale, ref bool hot)
        {
            if (!hand.Ready)
            {
                hot = false;
                return;
            }
            float meters = Nearest(hand, fire) / Mathf.Max(scale, 1e-4f);
            if (meters >= WarmMeters)
            {
                hot = false;
                return;
            }
            float closeness = 1f - meters / WarmMeters;
            hand.Rumble((0.06f + 0.3f * closeness * closeness) * flicker);
            if (!_logged)
            {
                _logged = true;
                _logger.LogInfo("Campfire warmth: the " + hand.Name + " hand is " + meters.ToString("0.00") + " m from the fire light.");
            }
            bool burning = meters < HotMeters;
            if (burning)
            {
                hand.Grit(0.5f);
                if (!hot) hand.Play(Feel.Shatter, 0.6f);
            }
            hot = burning;
        }

        private static float Nearest(VrHand hand, Vector3 fire)
        {
            float best = (hand.Position - fire).sqrMagnitude;
            best = Mathf.Min(best, (hand.GripPosition - fire).sqrMagnitude);
            if (hand.HasIndexTip) best = Mathf.Min(best, (hand.IndexTip - fire).sqrMagnitude);
            if (hand.HasFist) best = Mathf.Min(best, (hand.FistPosition - fire).sqrMagnitude);
            return Mathf.Sqrt(best);
        }

        internal void Release()
        {
            _sequencer = null;
            _leftHot = false;
            _rightHot = false;
        }
    }
}
