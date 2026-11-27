using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class EyeballHands
    {
        private sealed class Hold
        {
            internal EyeballInteractable Eye;
            internal Rigidbody Body;
            internal VrHand Hand;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal float Radius;
        }

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo EyeballsField = typeof(ChooseEyeballSequencer).GetField("eyeballs", Members);
        private static readonly FieldInfo BoxField = typeof(ChooseEyeballSequencer).GetField("box", Members);

        private const float GrabReachMeters = 0.05f;
        private const float SocketMeters = 0.06f;
        private const float EyeSideMeters = 0.032f;
        private const float EyeForwardMeters = 0.01f;
        private const float MaxThrowMetersPerSecond = 3f;
        private const float LostMeters = 0.5f;

        private readonly ManualLogSource _logger;
        private ChooseEyeballSequencer _sequencer;
        private float _nextFind;
        private List<EyeballInteractable> _eyes;
        private Hold _left;
        private Hold _right;
        private EyeballInteractable _leftHover;
        private EyeballInteractable _rightHover;

        internal EyeballHands(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal static bool Available { get { return EyeballsField != null; } }

        internal bool Holds(Component target)
        {
            if (target == null || _eyes == null) return false;
            Transform t = target.transform;
            for (int i = 0; i < _eyes.Count; i++)
                if (_eyes[i] != null && t.IsChildOf(_eyes[i].transform)) return true;
            return false;
        }

        internal void Tick(CardGrabber grabber, VrHand left, VrHand right, bool shown, bool input, float scale)
        {
            List<EyeballInteractable> eyes = shown ? FindEyes() : null;
            if (eyes == null)
            {
                Release();
                return;
            }
            _eyes = eyes;
            Vector3 head = Vector3.zero;
            Quaternion view = Quaternion.identity;
            float unitsPerMeter = scale;
            bool haveHead = grabber.Renderer != null && grabber.Renderer.TryGetHeadPose(out head, out view, out unitsPerMeter);
            if (unitsPerMeter <= 0f) unitsPerMeter = scale;
            Keep(ref _left, input, haveHead, head, view, unitsPerMeter, scale);
            Keep(ref _right, input, haveHead, head, view, unitsPerMeter, scale);
            Offer(left, ref _left, ref _leftHover, input, scale);
            Offer(right, ref _right, ref _rightHover, input, scale);
            Recover(scale);
            if (_left == null && _right == null)
            {
                EyeballInteractable first = FirstSelectable();
                if (first != null) VrTips.Show("events.eyeball", first.transform.position + Vector3.up * (0.06f * scale), "Pick an eye out of the box and press it into your empty socket.", 70);
            }
        }

        private List<EyeballInteractable> FindEyes()
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
                _sequencer = Object.FindObjectOfType<ChooseEyeballSequencer>();
            }
            if (_sequencer == null || !_sequencer.isActiveAndEnabled) return null;
            List<EyeballInteractable> eyes = EyeballsField.GetValue(_sequencer) as List<EyeballInteractable>;
            if (eyes == null) return null;
            for (int i = 0; i < eyes.Count; i++)
                if (Selectable(eyes[i])) return eyes;
            return _left != null || _right != null ? eyes : null;
        }

        private static bool Selectable(EyeballInteractable eye)
        {
            return eye != null && eye.gameObject.activeInHierarchy && eye.CursorSelectStarted != null && eye.GetComponent<Rigidbody>() != null;
        }

        private EyeballInteractable FirstSelectable()
        {
            for (int i = 0; i < _eyes.Count; i++) if (Selectable(_eyes[i])) return _eyes[i];
            return null;
        }

        private void Keep(ref Hold hold, bool input, bool haveHead, Vector3 head, Quaternion view, float unitsPerMeter, float scale)
        {
            Hold h = hold;
            if (h == null) return;
            if (h.Eye == null || h.Body == null || !h.Eye.gameObject.activeInHierarchy)
            {
                Unclaim(h);
                hold = null;
                return;
            }
            if (!input || !h.Hand.Ready || !h.Hand.Grip.Held)
            {
                Drop(h, scale);
                hold = null;
                return;
            }
            Transform t = h.Eye.transform;
            Tween.Stop(t.GetInstanceID());
            VrHand hand = h.Hand;
            Vector3 velocity = hand.Velocity;
            float speed = velocity.magnitude;
            if (speed > 1e-4f && h.Radius > 1e-5f)
            {
                Vector3 axis = Vector3.Cross(Vector3.up, velocity / speed);
                if (axis.sqrMagnitude > 1e-6f)
                {
                    float degrees = speed / h.Radius * Time.unscaledDeltaTime * Mathf.Rad2Deg * 0.35f;
                    Quaternion world = Quaternion.AngleAxis(degrees, axis.normalized) * (hand.Rotation * h.LocalRotation);
                    h.LocalRotation = Quaternion.Inverse(hand.Rotation) * world;
                }
            }
            Vector3 position = hand.Position + hand.Rotation * h.LocalPosition;
            t.SetPositionAndRotation(position, hand.Rotation * h.LocalRotation);
            hand.SetHoldPose(HandHold.Pinch);
            if (!haveHead || h.Eye.CursorSelectStarted == null) return;
            Vector3 right = view * Vector3.right;
            Vector3 forward = view * Vector3.forward;
            Vector3 leftEye = head + (-right * EyeSideMeters + forward * EyeForwardMeters) * unitsPerMeter;
            Vector3 rightEye = head + (right * EyeSideMeters + forward * EyeForwardMeters) * unitsPerMeter;
            float socket = SocketMeters * unitsPerMeter;
            float nearest = Mathf.Min((position - leftEye).sqrMagnitude, (position - rightEye).sqrMagnitude);
            if (nearest > socket * socket) return;
            Socket(h);
            hold = null;
        }

        private void Socket(Hold h)
        {
            Unclaim(h);
            EyeballInteractable eye = h.Eye;
            string name = eye.name;
            Renderer[] renderers = eye.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = false;
            h.Body.isKinematic = true;
            h.Hand.Play(Feel.Thud);
            h.Hand.Rumble(0.4f);
            VrTips.Done("events.eyeball");
            _logger.LogInfo("Eyeball " + name + " pressed into the socket with the " + h.Hand.Name + " hand.");
            eye.CursorSelectStart();
            if (eye != null) eye.CursorSelectEnd();
        }

        private void Drop(Hold h, float scale)
        {
            Unclaim(h);
            h.Body.isKinematic = false;
            h.Body.velocity = Vector3.ClampMagnitude(h.Hand.Velocity, MaxThrowMetersPerSecond * scale);
            h.Body.angularVelocity = Random.onUnitSphere * 6f;
            h.Body.WakeUp();
            h.Hand.Play(Feel.Tick, 0.4f);
        }

        private static void Unclaim(Hold h)
        {
            if (h.Hand != null && h.Hand.Lifted == (Component)h.Eye) h.Hand.Lifted = null;
        }

        private void Offer(VrHand hand, ref Hold hold, ref EyeballInteractable hover, bool input, float scale)
        {
            EyeballInteractable target = null;
            if (hold == null && input && hand.Ready && !hand.Busy) target = Nearest(hand, scale);
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
            hold = Grab(hand, target, scale);
        }

        private EyeballInteractable Nearest(VrHand hand, float scale)
        {
            float reach = GrabReachMeters * scale;
            float best = reach * reach;
            EyeballInteractable result = null;
            Vector3 point = CardGrabber.CardPoint(hand);
            Vector3 pinch = hand.Position + hand.Rotation * hand.PinchOffset(scale);
            for (int i = 0; i < _eyes.Count; i++)
            {
                EyeballInteractable eye = _eyes[i];
                if (!Selectable(eye) || HeldByAnyone(eye)) continue;
                Bounds bounds;
                if (!HandReach.RendererBounds(eye, out bounds)) continue;
                float distance = Mathf.Min(bounds.SqrDistance(point), bounds.SqrDistance(pinch));
                if (distance >= best) continue;
                best = distance;
                result = eye;
            }
            return result;
        }

        private bool HeldByAnyone(EyeballInteractable eye)
        {
            return (_left != null && _left.Eye == eye) || (_right != null && _right.Eye == eye);
        }

        private Hold Grab(VrHand hand, EyeballInteractable eye, float scale)
        {
            Rigidbody body = eye.GetComponent<Rigidbody>();
            if (body == null) return null;
            Transform t = eye.transform;
            Tween.Stop(t.GetInstanceID());
            body.isKinematic = true;
            var hold = new Hold();
            hold.Eye = eye;
            hold.Body = body;
            hold.Hand = hand;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            hold.LocalPosition = inverse * (t.position - hand.Position);
            hold.LocalRotation = inverse * t.rotation;
            Bounds bounds;
            hold.Radius = HandReach.RendererBounds(eye, out bounds) ? Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)) : 0.012f * scale;
            hand.Lifted = eye;
            hand.Play(Feel.Tick);
            VrSounds.Play("eyeball_squish#1", MixerGroup.TableObjectsSFX, t.position, 0.35f);
            _logger.LogInfo("Picked up eyeball " + eye.name + " with the " + hand.Name + " hand.");
            return hold;
        }

        private void Recover(float scale)
        {
            Transform box = BoxField != null ? BoxField.GetValue(_sequencer) as Transform : null;
            if (box == null) return;
            float floor = box.position.y - LostMeters * scale;
            for (int i = 0; i < _eyes.Count; i++)
            {
                EyeballInteractable eye = _eyes[i];
                if (eye == null || !eye.gameObject.activeInHierarchy || HeldByAnyone(eye) || eye.transform.position.y >= floor) continue;
                Rigidbody body = eye.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic) continue;
                Vector3 back = box.position + Vector3.up * (0.05f * scale);
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.position = back;
                eye.transform.position = back;
                VrLog.Chatter(_logger, "An eyeball rolled off the table; it's back in the box.");
            }
        }

        internal void Release()
        {
            Let(ref _left);
            Let(ref _right);
            _leftHover = null;
            _rightHover = null;
            _eyes = null;
        }

        private static void Let(ref Hold hold)
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
