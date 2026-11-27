using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class CabinDials
    {
        private sealed class Dial
        {
            internal MainInputInteractable Target;
            internal SpinningLockInteractable Lock;
            internal CuckooClock Clock;
            internal int Knob;
            internal Transform Turner;
            internal float StepDegrees;
        }

        private sealed class Turn
        {
            internal Dial Dial;
            internal VrHand Hand;
            internal Quaternion Previous;
            internal float Accumulated;
            internal bool Touch;
            internal int Steps;
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PositionField = typeof(SpinningLockInteractable).GetField("currentPosition", Members);
        private static readonly FieldInfo CountField = typeof(SpinningLockInteractable).GetField("numPositions", Members);
        private static readonly FieldInfo SpinnerField = typeof(SpinningLockInteractable).GetField("spinner", Members);
        private static readonly FieldInfo SubSpinnerField = typeof(SpinningLockInteractable).GetField("subSpinner", Members);
        private static readonly FieldInfo KnobsField = typeof(CuckooClock).GetField("clockHandKnobs", Members);
        private static readonly FieldInfo HandPositionsField = typeof(CuckooClock).GetField("handPositions", Members);
        private static readonly MethodInfo UpdateHandsMethod = typeof(CuckooClock).GetMethod("UpdateClockHandPositions", Members);
        private static readonly MethodInfo TrySolveMethod = typeof(CuckooClock).GetMethod("TrySolvePuzzles", Members);

        private const float ClockStepDegrees = 30f;
        private const float MaxStepDegrees = 45f;
        private const float AimMeters = 3f;
        private const float LetGoMeters = 0.3f;

        private readonly ManualLogSource _logger;
        private readonly List<Dial> _dials = new List<Dial>();
        private float _nextRefresh;
        private Turn _left;
        private Turn _right;
        private Dial _leftHover;
        private Dial _rightHover;

        internal CabinDials(ManualLogSource logger)
        {
            _logger = logger;
            if (!LocksAvailable) _logger.LogWarning("Safe dials by hand unavailable: SpinningLockInteractable internals not found; click the dials instead.");
            if (!ClockAvailable) _logger.LogWarning("Clock knobs by hand unavailable: CuckooClock internals not found; click the knobs instead.");
        }

        private static bool LocksAvailable { get { return PositionField != null && CountField != null && SpinnerField != null; } }

        private static bool ClockAvailable { get { return KnobsField != null && HandPositionsField != null && UpdateHandsMethod != null && TrySolveMethod != null; } }

        internal bool Hovering(VrHand hand)
        {
            return (hand.IsLeft ? _leftHover : _rightHover) != null || (hand.IsLeft ? _left : _right) != null;
        }

        internal void Tick(VrHand left, VrHand right, float reachMeters, float scale)
        {
            Refresh();
            Keep(ref _left, scale);
            Keep(ref _right, scale);
            Offer(left, ref _left, ref _leftHover, reachMeters, scale);
            Offer(right, ref _right, ref _rightHover, reachMeters, scale);
        }

        private void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            _dials.Clear();
            if (LocksAvailable)
            {
                SpinningLockInteractable[] locks = Object.FindObjectsOfType<SpinningLockInteractable>();
                for (int i = 0; i < locks.Length; i++)
                {
                    Transform spinner = SpinnerField.GetValue(locks[i]) as Transform;
                    int count = (int)CountField.GetValue(locks[i]);
                    if (spinner == null || count <= 0) continue;
                    var dial = new Dial();
                    dial.Target = locks[i];
                    dial.Lock = locks[i];
                    dial.Turner = spinner;
                    dial.StepDegrees = 360f / count;
                    _dials.Add(dial);
                }
            }
            if (ClockAvailable)
            {
                CuckooClock[] clocks = Object.FindObjectsOfType<CuckooClock>();
                for (int c = 0; c < clocks.Length; c++)
                {
                    List<MainInputInteractable> knobs = KnobsField.GetValue(clocks[c]) as List<MainInputInteractable>;
                    if (knobs == null) continue;
                    for (int k = 0; k < knobs.Count; k++)
                    {
                        if (knobs[k] == null) continue;
                        var dial = new Dial();
                        dial.Target = knobs[k];
                        dial.Clock = clocks[c];
                        dial.Knob = k;
                        dial.Turner = knobs[k].transform;
                        dial.StepDegrees = ClockStepDegrees;
                        _dials.Add(dial);
                    }
                }
            }
        }

        private static bool Usable(Dial dial)
        {
            if (dial.Target == null || dial.Turner == null || !dial.Target.gameObject.activeInHierarchy) return false;
            if (dial.Lock != null) return !dial.Lock.Disabled;
            return dial.Clock != null && dial.Clock.isActiveAndEnabled;
        }

        private void Offer(VrHand hand, ref Turn turn, ref Dial hover, float reachMeters, float scale)
        {
            Dial target = null;
            bool touch = false;
            if (turn == null && hand.Tracked && !hand.Busy) target = Nearest(hand, reachMeters, scale, out touch);
            if (target != null)
            {
                hand.Highlight();
                if (target != hover) hand.Play(Feel.Tick, 0.3f);
            }
            hover = target;
            if (target == null || hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            hover = null;
            CabinInteractable gated = target.Target as CabinInteractable;
            if (target.Clock != null && gated != null && !gated.PrerequisitesMet)
            {
                hand.Play(Feel.Reject, 0.5f);
                VrLog.Chatter(_logger, "Clock knob out of reach from here; stand in front of the clock and face it.");
                return;
            }
            var started = new Turn();
            started.Dial = target;
            started.Hand = hand;
            started.Previous = hand.Rotation;
            started.Touch = touch;
            turn = started;
            hand.Lifted = target.Target;
            hand.Play(Feel.Tap, 0.6f);
            VrTips.Show("cabin.dial", target.Turner.position + Vector3.up * (0.05f * scale), "Twist your wrist to turn it.", 40);
            _logger.LogInfo((target.Lock != null ? "Safe dial " : "Clock knob ") + target.Target.name + " gripped with the " + hand.Name + " hand (" + (touch ? "touch" : "aim") + ").");
        }

        private Dial Nearest(VrHand hand, float reachMeters, float scale, out bool touch)
        {
            touch = false;
            float reach = reachMeters * scale;
            float best = reach * reach;
            Dial chosen = null;
            Vector3 point = CardGrabber.CardPoint(hand);
            for (int i = 0; i < _dials.Count; i++)
            {
                Dial dial = _dials[i];
                if (!Usable(dial) || Taken(dial)) continue;
                Bounds bounds;
                if (!HandReach.RendererBounds(dial.Turner, out bounds)) continue;
                float distance = Mathf.Min(bounds.SqrDistance(point), bounds.SqrDistance(hand.Position));
                if (distance >= best) continue;
                best = distance;
                chosen = dial;
            }
            if (chosen != null)
            {
                touch = true;
                return chosen;
            }
            float nearest = AimMeters * scale;
            Ray ray = hand.PointerRay;
            for (int i = 0; i < _dials.Count; i++)
            {
                Dial dial = _dials[i];
                if (!Usable(dial) || Taken(dial)) continue;
                Bounds bounds;
                float distance;
                if (!HandReach.RendererBounds(dial.Turner, out bounds) || !bounds.IntersectRay(ray, out distance) || distance >= nearest) continue;
                nearest = distance;
                chosen = dial;
            }
            return chosen;
        }

        private bool Taken(Dial dial)
        {
            return (_left != null && _left.Dial.Target == dial.Target) || (_right != null && _right.Dial.Target == dial.Target);
        }

        private void Keep(ref Turn turn, float scale)
        {
            Turn t = turn;
            if (t == null) return;
            VrHand hand = t.Hand;
            bool far = false;
            if (t.Touch && Usable(t.Dial))
            {
                Bounds bounds;
                if (HandReach.RendererBounds(t.Dial.Turner, out bounds))
                    far = bounds.SqrDistance(hand.Position) > LetGoMeters * LetGoMeters * scale * scale;
            }
            if (!hand.Tracked || !hand.Grip.Held || !Usable(t.Dial) || far)
            {
                Finish(t);
                turn = null;
                return;
            }
            hand.SetHoldPose(t.Dial.Lock != null ? HandHold.Wrap : HandHold.Pinch);
            Quaternion now = hand.Rotation;
            Transform pivot = t.Dial.Turner.parent != null ? t.Dial.Turner.parent : t.Dial.Turner;
            Vector3 axis = pivot.TransformDirection(Vector3.forward).normalized;
            float step = Mathf.Clamp(TotemHands.Twist(now * Quaternion.Inverse(t.Previous), axis), -MaxStepDegrees, MaxStepDegrees);
            t.Previous = now;
            t.Accumulated += step;
            float size = t.Dial.StepDegrees;
            hand.Grit(Mathf.Clamp01(Mathf.Abs(step) / Mathf.Max(Time.unscaledDeltaTime, 0.0001f) / 400f) * 0.12f);
            while (t.Accumulated >= size)
            {
                t.Accumulated -= size;
                if (!Advance(t, 1)) t.Accumulated = 0f;
            }
            while (t.Accumulated <= -size)
            {
                t.Accumulated += size;
                if (!Advance(t, -1)) t.Accumulated = 0f;
            }
        }

        private bool Advance(Turn t, int direction)
        {
            Dial dial = t.Dial;
            if (!Usable(dial)) return false;
            bool moved = dial.Lock != null ? StepLock(dial.Lock, direction) : StepClock(dial, direction);
            if (!moved)
            {
                t.Hand.Play(Feel.Reject, 0.4f);
                return false;
            }
            t.Steps += direction;
            t.Hand.Play(dial.Lock != null ? Feel.Thud : Feel.Tick, dial.Lock != null ? 0.7f : 0.9f);
            VrTips.Done("cabin.dial");
            return true;
        }

        private static bool StepLock(SpinningLockInteractable dial, int direction)
        {
            if (dial.Disabled) return false;
            if (direction > 0)
            {
                dial.CursorSelectStart();
                if (dial != null) dial.CursorSelectEnd();
                return true;
            }
            int count = (int)CountField.GetValue(dial);
            int position = ((int)PositionField.GetValue(dial) - 1 + count) % count;
            PositionField.SetValue(dial, position);
            Vector3 eulers = new Vector3(0f, 0f, 360f / count * position);
            Transform spinner = SpinnerField.GetValue(dial) as Transform;
            if (spinner != null)
            {
                Tween.Stop(spinner.GetInstanceID());
                Tween.LocalRotation(spinner, eulers, 0.15f, 0f, Tween.EaseInOut);
            }
            Transform sub = SubSpinnerField != null ? SubSpinnerField.GetValue(dial) as Transform : null;
            if (sub != null)
            {
                Tween.Stop(sub.GetInstanceID());
                Tween.LocalRotation(sub, eulers, 0.25f, 0f, Tween.EaseInOut);
            }
            VrSounds.Play("dial_heavy", MixerGroup.ExplorationSFX, dial.transform.position, 0.5f);
            return true;
        }

        private static bool StepClock(Dial dial, int direction)
        {
            CabinInteractable gated = dial.Target as CabinInteractable;
            if (gated != null && !gated.PrerequisitesMet) return false;
            if (direction > 0)
            {
                dial.Target.CursorSelectStart();
                if (dial.Target != null) dial.Target.CursorSelectEnd();
                return true;
            }
            int[] positions = HandPositionsField.GetValue(dial.Clock) as int[];
            if (positions == null || dial.Knob >= positions.Length) return false;
            positions[dial.Knob] = (positions[dial.Knob] + 11) % 12;
            UpdateHandsMethod.Invoke(dial.Clock, new object[] { false });
            TrySolveMethod.Invoke(dial.Clock, null);
            VrSounds.Play("knob_tick", MixerGroup.ExplorationSFX, dial.Clock.transform.position, 1f);
            return true;
        }

        private void Finish(Turn t)
        {
            if (t.Hand.Lifted == (Component)t.Dial.Target) t.Hand.Lifted = null;
            if (t.Steps != 0)
                _logger.LogInfo((t.Dial.Lock != null ? "Safe dial " : "Clock knob ") + (t.Dial.Target != null ? t.Dial.Target.name : "?") + " turned " +
                    t.Steps + " step(s) with the " + t.Hand.Name + " hand.");
        }

        internal void Release()
        {
            if (_left != null) Finish(_left);
            if (_right != null) Finish(_right);
            _left = null;
            _right = null;
            _leftHover = null;
            _rightHover = null;
        }
    }
}
