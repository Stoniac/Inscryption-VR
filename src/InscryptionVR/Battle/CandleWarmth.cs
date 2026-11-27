using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class CandleWarmth
    {
        private sealed class Flame
        {
            internal Transform Transform;
            internal Vector3 Base;
            internal Vector3 Written;
            internal Vector3 Lean;
            internal bool Leaning;
        }

        private static readonly FieldInfo ActiveFlamesField = typeof(CandleHolder).GetField("activeFlames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private const float WarmMeters = 0.12f;
        private const float HotMeters = 0.025f;
        private const float ColdMeters = 0.05f;
        private const float LeanMeters = 0.006f;
        private const float LeanRate = 10f;

        private readonly ManualLogSource _logger;
        private readonly List<Flame> _flames = new List<Flame>();
        private CandleHolder _holder;
        private bool _leftCold;
        private bool _rightCold;
        private bool _logged;

        internal CandleWarmth(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available { get { return ActiveFlamesField != null; } }

        internal void Tick(VrHand left, VrHand right, bool input, float scale)
        {
            if (!input || !Resolve())
            {
                Release();
                return;
            }
            float dt = Time.unscaledDeltaTime;
            bool leftCold = false, rightCold = false;
            for (int i = 0; i < _flames.Count; i++)
            {
                Flame flame = _flames[i];
                if (flame.Transform == null) continue;
                Vector3 position = flame.Transform.position;
                bool lit = flame.Transform.gameObject.activeInHierarchy;
                Vector3 push = Vector3.zero;
                Sense(left, flame, position, lit, scale, ref push, ref leftCold);
                Sense(right, flame, position, lit, scale, ref push, ref rightCold);
                Lean(flame, lit ? push : Vector3.zero, scale, dt);
            }
            if (leftCold && !_leftCold) left.Play(Feel.Tick, 0.25f);
            if (rightCold && !_rightCold) right.Play(Feel.Tick, 0.25f);
            _leftCold = leftCold;
            _rightCold = rightCold;
        }

        private void Sense(VrHand hand, Flame flame, Vector3 position, bool lit, float scale, ref Vector3 push, ref bool cold)
        {
            if (!hand.Ready) return;
            Vector3 nearest;
            float meters = Distance(hand, position, out nearest) / Mathf.Max(scale, 1e-4f);
            if (!lit)
            {
                if (meters < ColdMeters) cold = true;
                return;
            }
            if (meters >= WarmMeters) return;
            float closeness = 1f - meters / WarmMeters;
            hand.Rumble(0.05f + 0.2f * closeness * closeness);
            if (meters < HotMeters) hand.Grit(0.35f);
            Vector3 away = position - nearest;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-8f) push += away.normalized * closeness;
            if (!_logged)
            {
                _logged = true;
                _logger.LogInfo("Candle warmth: the " + hand.Name + " hand is near a lit candle.");
            }
        }

        private static float Distance(VrHand hand, Vector3 position, out Vector3 nearest)
        {
            nearest = hand.Position;
            float best = (hand.Position - position).sqrMagnitude;
            Check(hand.GripPosition, position, ref best, ref nearest);
            if (hand.HasIndexTip) Check(hand.IndexTip, position, ref best, ref nearest);
            if (hand.HasFist) Check(hand.FistPosition, position, ref best, ref nearest);
            return Mathf.Sqrt(best);
        }

        private static void Check(Vector3 point, Vector3 position, ref float best, ref Vector3 nearest)
        {
            float distance = (point - position).sqrMagnitude;
            if (distance >= best) return;
            best = distance;
            nearest = point;
        }

        private static void Lean(Flame flame, Vector3 push, float scale, float dt)
        {
            Transform t = flame.Transform;
            if (flame.Leaning && (t.localPosition - flame.Written).sqrMagnitude > 1e-10f) flame.Base = t.localPosition;
            if (!flame.Leaning)
            {
                if (push.sqrMagnitude < 1e-6f) return;
                flame.Base = t.localPosition;
                flame.Lean = Vector3.zero;
                flame.Leaning = true;
            }
            Vector3 target = Vector3.ClampMagnitude(push, 1f) * (LeanMeters * scale);
            target += new Vector3(Mathf.PerlinNoise(Time.unscaledTime * 9f, 0.3f) - 0.5f, 0f, Mathf.PerlinNoise(0.7f, Time.unscaledTime * 9f) - 0.5f) * (push.magnitude * 0.004f * scale);
            flame.Lean = Vector3.Lerp(flame.Lean, target, 1f - Mathf.Exp(-LeanRate * dt));
            if (push.sqrMagnitude < 1e-6f && flame.Lean.sqrMagnitude < 1e-10f)
            {
                t.localPosition = flame.Base;
                flame.Leaning = false;
                return;
            }
            Transform parent = t.parent;
            Vector3 local = parent != null ? parent.InverseTransformVector(flame.Lean) : flame.Lean;
            flame.Written = flame.Base + local;
            t.localPosition = flame.Written;
        }

        private bool Resolve()
        {
            CandleHolder holder = Singleton<CandleHolder>.Instance;
            if (holder == null || !holder.gameObject.activeInHierarchy) return false;
            List<Animator> active = ActiveFlamesField.GetValue(holder) as List<Animator>;
            if (active == null) return false;
            if (holder != _holder || active.Count != _flames.Count || Changed(active))
            {
                Restore();
                _holder = holder;
                for (int i = 0; i < active.Count; i++)
                {
                    var flame = new Flame();
                    flame.Transform = active[i] != null ? active[i].transform : null;
                    _flames.Add(flame);
                }
            }
            return true;
        }

        private bool Changed(List<Animator> active)
        {
            for (int i = 0; i < active.Count; i++)
            {
                Transform t = active[i] != null ? active[i].transform : null;
                if (_flames[i].Transform != t) return true;
            }
            return false;
        }

        private void Restore()
        {
            for (int i = 0; i < _flames.Count; i++)
            {
                Flame flame = _flames[i];
                if (flame.Leaning && flame.Transform != null) flame.Transform.localPosition = flame.Base;
            }
            _flames.Clear();
        }

        internal void Release()
        {
            Restore();
            _holder = null;
            _leftCold = false;
            _rightCold = false;
        }
    }
}
