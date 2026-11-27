using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class BoneTokens
    {
        private sealed class Hold
        {
            internal BoneTokenInteractable Token;
            internal Rigidbody Body;
            internal VrHand Hand;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo TokensField = typeof(Part1ResourcesManager).GetField("boneTokens", Members);
        private static readonly FieldInfo AreaField = typeof(Part1ResourcesManager).GetField("boneTokenArea", Members);

        private const float GrabReachMeters = 0.035f;
        private const float FingerRadiusMeters = 0.012f;
        private const float FlickMetersPerSecond = 0.15f;
        private const float FlickBoost = 1.2f;
        private const float FlickLift = 0.25f;
        private const float MaxThrowMetersPerSecond = 4f;
        private const float SpinRadiansPerSecond = 12f;
        private const float LostMeters = 0.6f;

        private readonly ManualLogSource _logger;
        private readonly HashSet<int> _touching = new HashSet<int>();
        private readonly HashSet<int> _touchingNow = new HashSet<int>();
        private Part1ResourcesManager _manager;
        private Hold _left;
        private Hold _right;
        private BoneTokenInteractable _leftHover;
        private BoneTokenInteractable _rightHover;
        private bool _announced;

        internal BoneTokens(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available { get { return TokensField != null; } }

        internal bool Holds(Component target)
        {
            return Holding(_left, target) || Holding(_right, target);
        }

        private static bool Holding(Hold hold, Component target)
        {
            return hold != null && hold.Token != null && target != null && target.transform.IsChildOf(hold.Token.transform);
        }

        internal void Tick(VrHand left, VrHand right, bool active, bool input, float scale)
        {
            Part1ResourcesManager manager = active ? Singleton<ResourcesManager>.Instance as Part1ResourcesManager : null;
            List<BoneTokenInteractable> tokens = manager != null ? TokensField.GetValue(manager) as List<BoneTokenInteractable> : null;
            if (tokens == null)
            {
                Release();
                return;
            }
            _manager = manager;
            Keep(ref _left, tokens, input, scale);
            Keep(ref _right, tokens, input, scale);
            _touchingNow.Clear();
            Touch(left, _left, tokens, input, scale);
            Touch(right, _right, tokens, input, scale);
            _touching.Clear();
            _touching.UnionWith(_touchingNow);
            Offer(left, ref _left, ref _leftHover, tokens, input, scale);
            Offer(right, ref _right, ref _rightHover, tokens, input, scale);
            Recover(manager, tokens, scale);
            if (!_announced && tokens.Count > 0)
            {
                _announced = true;
                _logger.LogInfo("Bone tokens are loose on the table: flick or push them with a fingertip, or grip one to pick it up and toss it.");
            }
        }

        private void Keep(ref Hold hold, List<BoneTokenInteractable> tokens, bool input, float scale)
        {
            Hold h = hold;
            if (h == null) return;
            if (h.Token == null || h.Body == null || !tokens.Contains(h.Token) || !h.Token.gameObject.activeInHierarchy)
            {
                Unclaim(h);
                hold = null;
                return;
            }
            if (!input || !h.Hand.Ready || !h.Hand.Grip.Held)
            {
                Throw(h, scale);
                hold = null;
                return;
            }
            Transform t = h.Token.transform;
            Tween.Stop(t.GetInstanceID());
            t.SetPositionAndRotation(h.Hand.Position + h.Hand.Rotation * h.LocalPosition, h.Hand.Rotation * h.LocalRotation);
            h.Hand.SetHoldPose(HandHold.Pinch);
        }

        private void Throw(Hold h, float scale)
        {
            Unclaim(h);
            Vector3 velocity = Vector3.ClampMagnitude(h.Hand.Velocity, MaxThrowMetersPerSecond * scale);
            h.Body.isKinematic = false;
            h.Body.velocity = velocity;
            h.Body.angularVelocity = Random.onUnitSphere * (SpinRadiansPerSecond * Mathf.Clamp01(velocity.magnitude / Mathf.Max(scale, 0.0001f)));
            h.Body.WakeUp();
            if (_manager != null) _manager.OnFlickedBoneToken();
            h.Hand.Play(Feel.Tick, 0.5f);
            VrLog.Chatter(_logger, "Bone token let go from the " + h.Hand.Name + " hand at " + (velocity.magnitude / Mathf.Max(scale, 0.0001f)).ToString("0.00") + " m/s.");
        }

        private static void Unclaim(Hold h)
        {
            if (h.Hand != null && h.Hand.Lifted == (Component)h.Token) h.Hand.Lifted = null;
        }

        private void Touch(VrHand hand, Hold hold, List<BoneTokenInteractable> tokens, bool input, float scale)
        {
            if (!input || !hand.Ready || hold != null || hand.Busy) return;
            float radius = FingerRadiusMeters * scale;
            Vector3 tip = CardGrabber.CardPoint(hand);
            Vector3 velocity = hand.Velocity;
            float speed = velocity.magnitude / Mathf.Max(scale, 0.0001f);
            for (int i = 0; i < tokens.Count; i++)
            {
                BoneTokenInteractable token = tokens[i];
                if (token == null || Holds(token)) continue;
                Rigidbody body = token.GetComponent<Rigidbody>();
                Collider collider = token.GetComponent<Collider>();
                if (body == null || body.isKinematic || collider == null || !collider.enabled) continue;
                Vector3 closest = collider.ClosestPoint(tip);
                Vector3 gap = closest - tip;
                float distance = gap.magnitude;
                bool inside = distance < 1e-5f;
                if (!inside && distance > radius) continue;
                int key = token.GetInstanceID() * 2 + (hand.IsLeft ? 0 : 1);
                _touchingNow.Add(key);
                bool entered = !_touching.Contains(key);
                Vector3 normal = inside ? body.worldCenterOfMass - tip : gap / distance;
                if (normal.sqrMagnitude < 1e-10f) normal = velocity.sqrMagnitude > 1e-10f ? velocity : Vector3.up;
                normal.Normalize();
                float approach = Vector3.Dot(velocity, normal);
                if (approach <= 0f) continue;
                if (entered && speed >= FlickMetersPerSecond)
                {
                    Vector3 flick = velocity * FlickBoost + Vector3.up * (velocity.magnitude * FlickLift);
                    body.velocity = Vector3.ClampMagnitude(flick, MaxThrowMetersPerSecond * scale);
                    body.angularVelocity = Random.onUnitSphere * SpinRadiansPerSecond;
                    body.WakeUp();
                    if (_manager != null) _manager.OnFlickedBoneToken();
                    hand.Play(Feel.Tick, Mathf.Clamp(0.3f + speed, 0.3f, 1f));
                    VrLog.Chatter(_logger, "Bone token flicked by the " + hand.Name + " hand at " + speed.ToString("0.00") + " m/s.");
                    continue;
                }
                float along = Vector3.Dot(body.velocity, normal);
                if (along < approach)
                {
                    body.velocity += normal * (approach - along);
                    body.WakeUp();
                }
                if (entered) hand.Play(Feel.Tick, 0.25f);
            }
        }

        private void Offer(VrHand hand, ref Hold hold, ref BoneTokenInteractable hover, List<BoneTokenInteractable> tokens, bool input, float scale)
        {
            BoneTokenInteractable target = null;
            if (hold == null && input && hand.Ready && !hand.Busy && hand.HoverCard == null) target = Nearest(hand, tokens, scale);
            if (target != null)
            {
                hand.Highlight();
                GrabGlow.Mark(target);
                if (target != hover) hand.Play(Feel.Tick, 0.3f);
            }
            hover = target;
            if (target == null || hand.GripEdge != 1) return;
            hand.GripEdge = 0;
            hover = null;
            hold = Grab(hand, target, tokens);
        }

        private BoneTokenInteractable Nearest(VrHand hand, List<BoneTokenInteractable> tokens, float scale)
        {
            float reach = GrabReachMeters * scale;
            float best = reach * reach;
            BoneTokenInteractable result = null;
            Vector3 point = CardGrabber.CardPoint(hand);
            Vector3 pinch = hand.Position + hand.Rotation * hand.PinchOffset(scale);
            for (int i = 0; i < tokens.Count; i++)
            {
                BoneTokenInteractable token = tokens[i];
                if (token == null || !token.gameObject.activeInHierarchy || Holds(token)) continue;
                Collider collider = token.GetComponent<Collider>();
                if (collider == null || !collider.enabled || token.GetComponent<Rigidbody>() == null) continue;
                Bounds bounds = collider.bounds;
                float distance = Mathf.Min(bounds.SqrDistance(point), bounds.SqrDistance(pinch));
                if (distance >= best) continue;
                best = distance;
                result = token;
            }
            return result;
        }

        private Hold Grab(VrHand hand, BoneTokenInteractable token, List<BoneTokenInteractable> tokens)
        {
            Rigidbody body = token.GetComponent<Rigidbody>();
            if (body == null) return null;
            Transform t = token.transform;
            Tween.Stop(t.GetInstanceID());
            body.isKinematic = true;
            for (int i = 0; i < tokens.Count; i++)
            {
                BoneTokenInteractable other = tokens[i];
                if (other == null || other == token || Holds(other)) continue;
                Rigidbody otherBody = other.GetComponent<Rigidbody>();
                Collider otherCollider = other.GetComponent<Collider>();
                if (otherBody == null || !otherBody.isKinematic || otherCollider == null || !otherCollider.enabled) continue;
                otherBody.isKinematic = false;
                otherBody.WakeUp();
            }
            var hold = new Hold();
            hold.Token = token;
            hold.Body = body;
            hold.Hand = hand;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            hold.LocalPosition = inverse * (t.position - hand.Position);
            hold.LocalRotation = inverse * t.rotation;
            hand.Lifted = token;
            if (_manager != null) _manager.OnFlickedBoneToken();
            hand.Play(Feel.Tick);
            VrSounds.Play("token_enter_higher", MixerGroup.TableObjectsSFX, t.position, 0.5f);
            VrLog.Chatter(_logger, "Bone token picked up with the " + hand.Name + " hand.");
            return hold;
        }

        private void Recover(Part1ResourcesManager manager, List<BoneTokenInteractable> tokens, float scale)
        {
            Transform area = AreaField != null ? AreaField.GetValue(manager) as Transform : null;
            if (area == null) return;
            float floor = area.position.y - LostMeters * scale;
            for (int i = 0; i < tokens.Count; i++)
            {
                BoneTokenInteractable token = tokens[i];
                if (token == null || Holds(token) || token.transform.position.y >= floor) continue;
                Rigidbody body = token.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic) continue;
                Vector3 landing = area.position + new Vector3(Random.value * 0.6f, 0f, Random.value * 0.6f) + Vector3.up;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.position = landing;
                token.transform.position = landing;
                body.WakeUp();
                VrLog.Chatter(_logger, "A bone token fell off the table; it's back on the pile.");
            }
        }

        internal void Release()
        {
            Drop(ref _left);
            Drop(ref _right);
            _touching.Clear();
            _touchingNow.Clear();
            _leftHover = null;
            _rightHover = null;
            _manager = null;
        }

        private static void Drop(ref Hold hold)
        {
            Hold h = hold;
            hold = null;
            if (h == null) return;
            Unclaim(h);
            if (h.Body == null) return;
            h.Body.isKinematic = false;
            h.Body.velocity = Vector3.zero;
            h.Body.WakeUp();
        }
    }
}
