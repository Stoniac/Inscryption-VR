using DiskCardGame;
using Pixelplacement;
using UnityEngine;

namespace InscryptionVR
{
    internal static class HandReach
    {
        private static int _mask = int.MinValue;

        internal static bool Active(Component component)
        {
            return component != null && component.gameObject.activeInHierarchy;
        }

        internal static bool Live(InteractableBase target)
        {
            if (!Active(target)) return false;
            Collider collider = target.GetComponent<Collider>();
            return collider != null && collider.enabled;
        }

        internal static float InteractableSqrDistance(Component target, Vector3 point)
        {
            Collider collider = target.GetComponent<Collider>();
            if (collider != null && collider.enabled) return collider.bounds.SqrDistance(point);
            return RendererSqrDistance(target, point);
        }

        internal static float RendererSqrDistance(Component target, Vector3 point)
        {
            Bounds bounds;
            if (RendererBounds(target, out bounds)) return bounds.SqrDistance(point);
            return (target.transform.position - point).sqrMagnitude;
        }

        internal static bool CardBounds(Component card, out Bounds bounds)
        {
            bounds = new Bounds();
            if (card == null) return false;
            BoxCollider box = card.GetComponent<BoxCollider>();
            if (box != null)
            {
                Transform t = box.transform;
                Vector3 half = box.size * 0.5f;
                bool any = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                    Vector3 world = t.TransformPoint(box.center + corner);
                    if (!any) { bounds = new Bounds(world, Vector3.zero); any = true; }
                    else bounds.Encapsulate(world);
                }
                return true;
            }
            Collider collider = card.GetComponent<Collider>();
            if (collider != null && collider.enabled)
            {
                bounds = collider.bounds;
                return true;
            }
            return RendererBounds(card, out bounds);
        }

        internal static bool RendererBounds(Component target, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any;
        }

        internal static float Size(Component target)
        {
            Collider collider = target.GetComponent<Collider>();
            Bounds bounds;
            if (collider != null && collider.enabled) bounds = collider.bounds;
            else if (!RendererBounds(target, out bounds)) return 0f;
            Vector3 size = bounds.size;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        }

        internal static Collider Pointed(VrHand state, float scale)
        {
            if (_mask == int.MinValue) _mask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("NonInteractable", "AlternateInteractable");
            RaycastHit hit;
            if (!Physics.Raycast(state.PointerRay, out hit, 10f * scale, _mask, QueryTriggerInteraction.UseGlobal)) return null;
            return hit.collider;
        }

        internal static bool PointsAtBounds(VrHand state, Component target, float scale)
        {
            Bounds bounds;
            if (!RendererBounds(target, out bounds)) return false;
            float distance;
            return bounds.IntersectRay(state.PointerRay, out distance) && distance <= 10f * scale;
        }
    }

    internal sealed class Carry
    {
        private const float PullSeconds = 0.16f;

        internal readonly Transform Target;
        internal readonly VrHand Hand;
        internal readonly Vector3 RestLocalPosition;
        internal readonly Quaternion RestLocalRotation;
        private readonly Transform _parent;
        private readonly bool _rotate;
        private readonly Vector3 _localPosition;
        private readonly Quaternion _localRotation;
        private readonly Vector3 _pullStart;
        private readonly Quaternion _pullStartRotation;
        private float _pull;

        internal Carry(Transform target, VrHand hand, bool touch, Vector3 heldOffset, bool rotate)
        {
            Target = target;
            Hand = hand;
            _rotate = rotate;
            _parent = target.parent;
            RestLocalPosition = target.localPosition;
            RestLocalRotation = target.localRotation;
            Tween.Stop(target.GetInstanceID());
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            _localRotation = inverse * target.rotation;
            _pullStart = target.position;
            _pullStartRotation = target.rotation;
            if (touch)
            {
                _localPosition = inverse * (target.position - hand.Position);
                _pull = 1f;
            }
            else
            {
                _localPosition = heldOffset;
                _pull = 0f;
            }
        }

        internal Vector3 RestPosition
        {
            get { return _parent != null ? _parent.TransformPoint(RestLocalPosition) : RestLocalPosition; }
        }

        internal bool Holds(Component target)
        {
            return Target != null && target != null && target.transform.IsChildOf(Target);
        }

        internal void Update()
        {
            if (Target == null) return;
            Vector3 position = Hand.Position + Hand.Rotation * _localPosition;
            Quaternion rotation = Hand.Rotation * _localRotation;
            if (_pull < 1f)
            {
                _pull = Mathf.Min(1f, _pull + Time.unscaledDeltaTime / PullSeconds);
                float k = Mathf.SmoothStep(0f, 1f, _pull);
                position = Vector3.Lerp(_pullStart, position, k);
                rotation = Quaternion.Slerp(_pullStartRotation, rotation, k);
            }
            if (_rotate) Target.SetPositionAndRotation(position, rotation);
            else Target.position = position;
        }

        internal void Lift(float dy)
        {
            if (Target != null) Target.position += Vector3.up * dy;
        }

        internal void PutBack(float seconds)
        {
            if (Target == null) return;
            Tween.Stop(Target.GetInstanceID());
            Tween.LocalPosition(Target, RestLocalPosition, seconds, 0f, Tween.EaseOut);
            if (_rotate) Tween.LocalRotation(Target, RestLocalRotation.eulerAngles, seconds, 0f, Tween.EaseOut);
        }

        internal void Restore()
        {
            if (Target == null) return;
            Tween.Stop(Target.GetInstanceID());
            Target.localPosition = RestLocalPosition;
            if (_rotate) Target.localRotation = RestLocalRotation;
        }
    }
}
