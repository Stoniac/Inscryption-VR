using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class ScaleTouch
    {
        private sealed class Tooth
        {
            internal GameObject Weight;
            internal Rigidbody Body;
            internal VrHand Hand;
            internal bool PlayerSide;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 From;
            internal Quaternion FromRotation;
            internal float Progress;
            internal bool Returning;
            internal bool WasKinematic;
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo PlayerSideField = typeof(Scales3D).GetField("playerSide", Members);
        private static readonly FieldInfo OpponentSideField = typeof(Scales3D).GetField("opponentSide", Members);
        private static readonly FieldInfo RotatorField = typeof(Scales3D).GetField("rotator", Members);
        private static readonly FieldInfo PlayerWeightsField = typeof(Scales3D).GetField("playerWeights", Members);
        private static readonly FieldInfo OpponentWeightsField = typeof(Scales3D).GetField("opponentWeights", Members);
        private static readonly FieldInfo PlayerWeightField = typeof(Scales).GetField("playerWeight", Members);
        private static readonly FieldInfo OpponentWeightField = typeof(Scales).GetField("opponentWeight", Members);

        private const float PanReachMeters = 0.012f;
        private const float ToothReachMeters = 0.04f;
        private const float ReturnSeconds = 0.25f;
        private const float DropMeters = 0.04f;
        private const float WobbleDecaySeconds = 0.35f;
        private const float WobblePeriodSeconds = 0.42f;
        private const float WobbleSeconds = 1.4f;

        private readonly ManualLogSource _logger;
        private readonly List<Tooth> _teeth = new List<Tooth>();
        private Scales3D _scales;
        private Transform _playerSide;
        private Transform _opponentSide;
        private Transform _rotator;
        private List<GameObject> _playerWeights;
        private List<GameObject> _opponentWeights;
        private bool _leftInPan;
        private bool _rightInPan;
        private GameObject _leftHover;
        private GameObject _rightHover;

        private float _wobbleDegrees;
        private float _wobbleStart = -10f;
        private bool _wobbling;
        private Quaternion _base;
        private Quaternion _written;

        internal ScaleTouch(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available
        {
            get
            {
                return PlayerSideField != null && OpponentSideField != null && RotatorField != null && PlayerWeightsField != null && OpponentWeightsField != null;
            }
        }

        internal bool Holds(Component target)
        {
            if (target == null) return false;
            for (int i = 0; i < _teeth.Count; i++)
                if (_teeth[i].Weight != null && target.transform.IsChildOf(_teeth[i].Weight.transform)) return true;
            return false;
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool input, float scale)
        {
            if (!Resolve())
            {
                Release();
                return;
            }
            Prune();
            Poke(left, input, scale);
            Poke(right, input, scale);
            UpdateHand(grabber, left, input, scale);
            UpdateHand(grabber, right, input, scale);
            AnimateTeeth(scale);
            Wobble();
        }

        private bool Resolve()
        {
            LifeManager life = Singleton<LifeManager>.Instance;
            Scales3D scales = life != null ? life.Scales3D : null;
            if (scales == null || !scales.gameObject.activeInHierarchy) return false;
            if (scales != _scales)
            {
                Release();
                _scales = scales;
                _playerSide = PlayerSideField.GetValue(scales) as Transform;
                _opponentSide = OpponentSideField.GetValue(scales) as Transform;
                _rotator = RotatorField.GetValue(scales) as Transform;
                _playerWeights = PlayerWeightsField.GetValue(scales) as List<GameObject>;
                _opponentWeights = OpponentWeightsField.GetValue(scales) as List<GameObject>;
            }
            return _playerSide != null && _opponentSide != null && _rotator != null && _playerWeights != null && _opponentWeights != null;
        }

        private void Poke(VrHand hand, bool input, float scale)
        {
            bool playerPan = false;
            bool inside = input && hand.Ready && InPan(hand, scale, out playerPan);
            bool was = hand.IsLeft ? _leftInPan : _rightInPan;
            if (hand.IsLeft) _leftInPan = inside;
            else _rightInPan = inside;
            if (!inside || was || hand.Busy || hand.Grip.Held) return;
            float speed = Mathf.Max(0f, -hand.Velocity.y) / Mathf.Max(scale, 1e-4f);
            if (speed < 0.1f) return;
            float degrees = Mathf.Clamp(2.5f + speed * 6f, 2.5f, 9f);
            StartWobble(playerPan ? degrees : -degrees);
            int teeth = Count(playerPan);
            hand.Play(Feel.Tap, Mathf.Clamp(0.35f + 0.08f * teeth, 0.35f, 1f));
            VrSounds.Play("scale_tick", MixerGroup.TableObjectsSFX, _rotator.position, 0.5f);
            VrLog.Chatter(_logger, "Scale pan poked by the " + hand.Name + " hand (" + (playerPan ? "your" : "Leshy's") + " side, " + teeth + " teeth).");
        }

        private bool InPan(VrHand hand, float scale, out bool playerPan)
        {
            playerPan = false;
            float reach = PanReachMeters * scale;
            Vector3 point = CardGrabber.CardPoint(hand);
            float player = PanSqrDistance(_playerSide, point);
            float opponent = PanSqrDistance(_opponentSide, point);
            if (Mathf.Min(player, opponent) > reach * reach) return false;
            playerPan = player <= opponent;
            return true;
        }

        private static float PanSqrDistance(Transform side, Vector3 point)
        {
            Transform pan = side.parent != null ? side.parent : side;
            Bounds bounds;
            if (!HandReach.RendererBounds(pan, out bounds)) return float.MaxValue;
            return bounds.SqrDistance(point);
        }

        private int Count(bool playerPan)
        {
            FieldInfo field = playerPan ? PlayerWeightField : OpponentWeightField;
            if (field != null) return (int)field.GetValue(_scales);
            return playerPan ? _playerWeights.Count : _opponentWeights.Count;
        }

        private void StartWobble(float degrees)
        {
            _wobbleDegrees = degrees;
            _wobbleStart = Time.unscaledTime;
            if (!_wobbling)
            {
                _base = _rotator.localRotation;
                _written = _base;
                _wobbling = true;
            }
        }

        private void Wobble()
        {
            if (!_wobbling || _rotator == null) return;
            Quaternion current = _rotator.localRotation;
            if (Quaternion.Angle(current, _written) > 0.01f) _base = current;
            float t = Time.unscaledTime - _wobbleStart;
            if (t >= WobbleSeconds)
            {
                _rotator.localRotation = _base;
                _wobbling = false;
                return;
            }
            float angle = _wobbleDegrees * Mathf.Exp(-t / WobbleDecaySeconds) * Mathf.Sin(t * Mathf.PI * 2f / WobblePeriodSeconds);
            _written = Quaternion.Euler(0f, angle, 0f) * _base;
            _rotator.localRotation = _written;
        }

        private void UpdateHand(CardGrabber grabber, VrHand hand, bool input, float scale)
        {
            Tooth held = HeldBy(hand);
            if (held != null)
            {
                SetHover(hand, null);
                if (!input || !hand.Ready || !hand.Grip.Held)
                {
                    StartReturn(held);
                    return;
                }
                hand.SetHoldPose(HandHold.Pinch);
                return;
            }
            if (!input || !hand.Ready || hand.Busy || hand.HoverCard != null)
            {
                SetHover(hand, null);
                return;
            }
            GameObject hover = Nearest(hand, scale);
            if (hover != null)
            {
                hand.Highlight();
                GrabGlow.Mark(hover.transform);
                if (hover != Hover(hand)) hand.Play(Feel.Tick, 0.4f);
            }
            SetHover(hand, hover);
            if (hover != null && hand.GripEdge == 1) Grab(hand, hover);
        }

        private GameObject Hover(VrHand hand) { return hand.IsLeft ? _leftHover : _rightHover; }

        private void SetHover(VrHand hand, GameObject weight)
        {
            if (hand.IsLeft) _leftHover = weight;
            else _rightHover = weight;
        }

        private GameObject Nearest(VrHand hand, float scale)
        {
            float reach = ToothReachMeters * scale;
            float best = reach * reach;
            GameObject result = null;
            Vector3 point = CardGrabber.CardPoint(hand);
            Vector3 pinch = hand.Position + hand.Rotation * hand.PinchOffset(scale);
            Consider(_playerWeights, point, pinch, ref best, ref result);
            Consider(_opponentWeights, point, pinch, ref best, ref result);
            return result;
        }

        private void Consider(List<GameObject> weights, Vector3 point, Vector3 pinch, ref float best, ref GameObject result)
        {
            for (int i = 0; i < weights.Count; i++)
            {
                GameObject weight = weights[i];
                if (!Grabbable(weight)) continue;
                Tooth tooth = Find(weight);
                if (tooth != null && !tooth.Returning) continue;
                Collider collider = weight.GetComponent<Collider>();
                Bounds bounds = collider.bounds;
                float distance = Mathf.Min(bounds.SqrDistance(point), bounds.SqrDistance(pinch));
                if (distance >= best) continue;
                best = distance;
                result = weight;
            }
        }

        private bool Grabbable(GameObject weight)
        {
            if (weight == null || !weight.activeInHierarchy || weight.transform.parent != _scales.transform) return false;
            Collider collider = weight.GetComponent<Collider>();
            return collider != null && collider.enabled && weight.GetComponent<Rigidbody>() != null;
        }

        private void Grab(VrHand hand, GameObject weight)
        {
            Tooth tooth = Find(weight);
            if (tooth == null)
            {
                tooth = new Tooth();
                tooth.Weight = weight;
                tooth.Body = weight.GetComponent<Rigidbody>();
                tooth.WasKinematic = tooth.Body.isKinematic;
                _teeth.Add(tooth);
            }
            tooth.PlayerSide = _playerWeights.Contains(weight);
            tooth.Hand = hand;
            tooth.Returning = false;
            tooth.Body.isKinematic = true;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            tooth.LocalPosition = inverse * (weight.transform.position - hand.Position);
            tooth.LocalRotation = inverse * weight.transform.rotation;
            hand.Lifted = weight.transform;
            SetHover(hand, null);
            hand.Play(Feel.Tick);
            _logger.LogInfo("Picked a tooth off " + (tooth.PlayerSide ? "your" : "Leshy's") + " side of the scale with the " + hand.Name + " hand.");
        }

        private void StartReturn(Tooth tooth)
        {
            if (tooth.Hand != null && tooth.Hand.Lifted == (Component)tooth.Weight.transform) tooth.Hand.Lifted = null;
            tooth.Hand = null;
            tooth.Returning = true;
            tooth.Progress = 0f;
            tooth.From = tooth.Weight.transform.position;
            tooth.FromRotation = tooth.Weight.transform.rotation;
        }

        private void AnimateTeeth(float scale)
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = _teeth.Count - 1; i >= 0; i--)
            {
                Tooth tooth = _teeth[i];
                Transform t = tooth.Weight.transform;
                if (!tooth.Returning)
                {
                    VrHand hand = tooth.Hand;
                    t.SetPositionAndRotation(hand.Position + hand.Rotation * tooth.LocalPosition, hand.Rotation * tooth.LocalRotation);
                    continue;
                }
                Transform side = tooth.PlayerSide ? _playerSide : _opponentSide;
                Transform marker = side.Find("DropMarker");
                Vector3 drop = (marker != null ? marker.position : side.position) + Vector3.up * (DropMeters * scale);
                tooth.Progress = Mathf.Min(1f, tooth.Progress + dt / ReturnSeconds);
                float k = Mathf.SmoothStep(0f, 1f, tooth.Progress);
                t.position = Vector3.Lerp(tooth.From, drop, k);
                t.rotation = Quaternion.Slerp(tooth.FromRotation, tooth.FromRotation * Quaternion.Euler(0f, 90f, 0f), k);
                if (tooth.Progress < 1f) continue;
                Settle(tooth);
                _teeth.RemoveAt(i);
            }
        }

        private static void Settle(Tooth tooth)
        {
            tooth.Body.isKinematic = tooth.WasKinematic;
            if (!tooth.Body.isKinematic)
            {
                tooth.Body.velocity = Vector3.zero;
                tooth.Body.angularVelocity = Vector3.zero;
                tooth.Body.WakeUp();
            }
        }

        private void Prune()
        {
            for (int i = _teeth.Count - 1; i >= 0; i--)
            {
                Tooth tooth = _teeth[i];
                if (tooth.Weight != null && tooth.Body != null && Grabbable(tooth.Weight)) continue;
                if (tooth.Hand != null && tooth.Weight != null && tooth.Hand.Lifted == (Component)tooth.Weight.transform) tooth.Hand.Lifted = null;
                else if (tooth.Hand != null && tooth.Weight == null) tooth.Hand.Lifted = null;
                _teeth.RemoveAt(i);
            }
        }

        private Tooth HeldBy(VrHand hand)
        {
            for (int i = 0; i < _teeth.Count; i++)
                if (!_teeth[i].Returning && _teeth[i].Hand == hand) return _teeth[i];
            if (hand.Lifted is Transform) hand.Lifted = null;
            return null;
        }

        private Tooth Find(GameObject weight)
        {
            for (int i = 0; i < _teeth.Count; i++) if (_teeth[i].Weight == weight) return _teeth[i];
            return null;
        }

        internal void Release()
        {
            for (int i = 0; i < _teeth.Count; i++)
            {
                Tooth tooth = _teeth[i];
                if (tooth.Hand != null) tooth.Hand.Lifted = null;
                if (tooth.Weight == null || tooth.Body == null) continue;
                Settle(tooth);
            }
            _teeth.Clear();
            if (_wobbling && _rotator != null) _rotator.localRotation = _base;
            _wobbling = false;
            _leftHover = null;
            _rightHover = null;
            _leftInPan = false;
            _rightInPan = false;
            _scales = null;
            _rotator = null;
        }
    }
}
