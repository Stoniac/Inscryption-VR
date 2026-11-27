using UnityEngine;

namespace InscryptionVR
{
    internal sealed class TableTouch
    {
        private const float DeepestMeters = 0.15f;
        private const float FullTapMetersPerSecond = 1f;

        private bool _leftContact;
        private bool _rightContact;

        internal void Tick(CardGrabber grabber, VrHand hand, bool active, float scale)
        {
            bool contact = active && Press(grabber, hand, scale);
            bool was = hand.IsLeft ? _leftContact : _rightContact;
            if (contact && !was)
            {
                float speed = Mathf.Max(0f, -hand.Velocity.y) / Mathf.Max(scale, 1e-4f);
                hand.Play(Feel.Tap, Mathf.Clamp(speed / FullTapMetersPerSecond, 0.25f, 1f));
            }
            if (hand.IsLeft) _leftContact = contact;
            else _rightContact = contact;
        }

        private static bool Press(CardGrabber grabber, VrHand hand, float scale)
        {
            if (!hand.Tracked || !hand.HasLowest || hand.Pinned || !grabber.HaveTable) return false;
            Vector3 lowest = hand.GripPosition + hand.GripRotation * (hand.LocalLowest * scale);
            float surface = grabber.TableY;
            if (lowest.y >= surface || lowest.y < surface - DeepestMeters * scale || !grabber.InsideTable(lowest)) return false;
            Vector3 lifted = hand.GripPosition + Vector3.up * (surface - lowest.y);
            hand.Pin(hand.GripPosition, hand.GripRotation, lifted, hand.GripRotation);
            return true;
        }
    }
}
