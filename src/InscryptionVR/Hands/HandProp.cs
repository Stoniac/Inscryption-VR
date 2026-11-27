using System.Collections.Generic;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class HandProp
    {
        private static readonly List<HandProp> All = new List<HandProp>();

        private readonly GameObject _root;
        private readonly List<Mesh> _baked = new List<Mesh>();
        private VrHand _hand;
        private Vector3 _localPosition;
        private Quaternion _localRotation = Quaternion.identity;
        private HandHold _pose = HandHold.Wrap;
        private Vector3 _from;
        private Quaternion _fromRotation = Quaternion.identity;
        private Vector3 _to;
        private Quaternion _toRotation = Quaternion.identity;
        private float _glide = 1f;
        private float _glideSeconds = 0.2f;

        private HandProp(GameObject root)
        {
            _root = root;
        }

        internal Transform Root { get { return _root != null ? _root.transform : null; } }

        internal bool Alive { get { return _root != null; } }

        internal VrHand Hand { get { return _root != null ? _hand : null; } }

        internal static HandProp Copy(Transform source, string label)
        {
            if (source == null) return null;
            GameObject root = new GameObject("InscryptionVR Prop " + label);
            root.transform.SetPositionAndRotation(source.position, source.rotation);
            HandProp prop = new HandProp(root);
            Renderer[] renderers = source.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++) prop.AddPart(renderers[i]);
            All.Add(prop);
            return prop;
        }

        private void AddPart(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            if (renderer is ParticleSystemRenderer || GrabGlow.IsShell(renderer)) return;
            Mesh mesh;
            bool baked = false;
            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (renderer is MeshRenderer)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) return;
                mesh = filter.sharedMesh;
            }
            else if (skinned != null)
            {
                mesh = new Mesh();
                skinned.BakeMesh(mesh);
                _baked.Add(mesh);
                baked = true;
            }
            else return;

            GameObject part = new GameObject(renderer.name);
            part.layer = renderer.gameObject.layer;
            Transform t = part.transform;
            t.SetParent(_root.transform, false);
            t.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
            t.localScale = renderer.transform.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer copy = part.AddComponent<MeshRenderer>();
            copy.sharedMaterials = renderer.sharedMaterials;
            copy.shadowCastingMode = renderer.shadowCastingMode;
            copy.receiveShadows = renderer.receiveShadows;
            copy.lightProbeUsage = renderer.lightProbeUsage;
            copy.reflectionProbeUsage = renderer.reflectionProbeUsage;
            if (renderer.HasPropertyBlock())
            {
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                copy.SetPropertyBlock(block);
            }
            if (baked) FitBakedScale(t, copy, skinned.bounds);
        }

        private static void FitBakedScale(Transform part, Renderer copy, Bounds wanted)
        {
            float target = wanted.size.magnitude;
            if (target <= 0f) return;
            float scaled = copy.bounds.size.magnitude;
            Vector3 lossy = part.localScale;
            part.localScale = Vector3.one;
            float unscaled = copy.bounds.size.magnitude;
            if (Mathf.Abs(scaled - target) < Mathf.Abs(unscaled - target)) part.localScale = lossy;
        }

        internal void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        }

        internal void Follow(VrHand hand, HandHold pose)
        {
            if (_root == null || hand == null) return;
            Quaternion inverse = Quaternion.Inverse(hand.Rotation);
            Follow(hand, inverse * (_root.transform.position - hand.Position), inverse * _root.transform.rotation, pose);
        }

        internal void Follow(VrHand hand, Vector3 localPosition, Quaternion localRotation, HandHold pose)
        {
            _hand = hand;
            _localPosition = localPosition;
            _localRotation = localRotation;
            _pose = pose;
            _glide = 1f;
        }

        internal void GlideTo(Vector3 position, Quaternion rotation, float seconds)
        {
            if (_root == null) return;
            _hand = null;
            _from = _root.transform.position;
            _fromRotation = _root.transform.rotation;
            _to = position;
            _toRotation = rotation;
            _glideSeconds = Mathf.Max(seconds, 0.01f);
            _glide = 0f;
        }

        internal void Destroy()
        {
            All.Remove(this);
            if (_root != null) Object.Destroy(_root);
            for (int i = 0; i < _baked.Count; i++) if (_baked[i] != null) Object.Destroy(_baked[i]);
            _baked.Clear();
            _hand = null;
        }

        internal static void TickAll(float dt)
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                if (i >= All.Count) continue;
                All[i].Tick(dt);
            }
        }

        internal static void ClearAll()
        {
            List<HandProp> props = new List<HandProp>(All);
            for (int i = 0; i < props.Count; i++) props[i].Destroy();
            All.Clear();
        }

        private void Tick(float dt)
        {
            if (_root == null)
            {
                Destroy();
                return;
            }
            Transform t = _root.transform;
            if (_hand != null)
            {
                if (!_hand.Ready) return;
                if (!_hand.Grip.Held)
                {
                    Destroy();
                    return;
                }
                _hand.Claimed = true;
                _hand.SetHoldPose(_pose);
                t.SetPositionAndRotation(_hand.Position + _hand.Rotation * _localPosition, _hand.Rotation * _localRotation);
                return;
            }
            if (_glide >= 1f) return;
            _glide = Mathf.Min(1f, _glide + dt / _glideSeconds);
            float k = Mathf.SmoothStep(0f, 1f, _glide);
            t.SetPositionAndRotation(Vector3.Lerp(_from, _to, k), Quaternion.Slerp(_fromRotation, _toRotation, k));
        }
    }

    internal sealed class RendererHider
    {
        private readonly List<Renderer> _hidden = new List<Renderer>();

        internal bool Any { get { return _hidden.Count > 0; } }

        internal void Hide(Component root)
        {
            if (root == null) return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || GrabGlow.IsShell(renderer)) continue;
                renderer.enabled = false;
                if (!_hidden.Contains(renderer)) _hidden.Add(renderer);
            }
        }

        internal void Keep()
        {
            for (int i = _hidden.Count - 1; i >= 0; i--)
            {
                Renderer renderer = _hidden[i];
                if (renderer == null) _hidden.RemoveAt(i);
                else if (renderer.enabled) renderer.enabled = false;
            }
        }

        internal void Restore()
        {
            for (int i = 0; i < _hidden.Count; i++) if (_hidden[i] != null) _hidden[i].enabled = true;
            _hidden.Clear();
        }

        internal void Forget()
        {
            _hidden.Clear();
        }
    }
}
