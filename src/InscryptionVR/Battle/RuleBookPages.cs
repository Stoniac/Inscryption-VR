using System.Collections.Generic;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class RuleBookPages
    {
        private const float MaxZoom = 2.5f;
        private const float MarginUnits = 0.2f;
        private const float PageHeightFraction = 0.661f;
        private const float ScanSeconds = 0.2f;
        private const float ZoomRate = 10f;

        private sealed class Page
        {
            internal Camera Camera;
            internal bool HasContent;
            internal Vector2 Centre;
            internal Vector2 Half;
        }

        private readonly List<Page> _pages = new List<Page>();
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private float _nextScan;
        private float _lastApply;
        private float _zoom = 1f;
        private bool _applied;

        internal int Count { get { return _pages.Count; } }

        internal float Zoom { get { return _zoom; } }

        internal static bool IsPageCamera(Camera camera)
        {
            return camera != null && camera.orthographic && camera.targetTexture != null;
        }

        internal static bool UnderPageCamera(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (IsPageCamera(p.GetComponent<Camera>())) return true;
            }
            return false;
        }

        internal void Bind(Transform rig)
        {
            Reset();
            _pages.Clear();
            Camera[] cameras = rig.GetComponentsInChildren<Camera>(true);
            for (int i = 0; i < cameras.Length; i++)
            {
                if (IsPageCamera(cameras[i])) _pages.Add(new Page { Camera = cameras[i] });
            }
            _nextScan = 0f;
            _zoom = 1f;
        }

        internal void Clear()
        {
            Reset();
            _pages.Clear();
        }

        internal void Apply(float now)
        {
            if (_pages.Count == 0) return;
            if (!_applied || now >= _nextScan)
            {
                for (int i = 0; i < _pages.Count; i++) Scan(_pages[i]);
                _nextScan = now + ScanSeconds;
            }
            float target = TargetZoom();
            if (!_applied) _zoom = target;
            else _zoom = Mathf.Lerp(_zoom, target, 1f - Mathf.Exp(-ZoomRate * Mathf.Max(0f, now - _lastApply)));
            _lastApply = now;
            for (int i = 0; i < _pages.Count; i++) Project(_pages[i]);
            _applied = true;
        }

        internal void Reset()
        {
            if (!_applied) return;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Camera != null) _pages[i].Camera.ResetProjectionMatrix();
            }
            _applied = false;
        }

        private float TargetZoom()
        {
            float zoom = MaxZoom;
            bool any = false;
            for (int i = 0; i < _pages.Count; i++)
            {
                Page page = _pages[i];
                if (page.Camera == null || !page.HasContent) continue;
                float size = page.Camera.orthographicSize;
                float halfWidth = size * Mathf.Max(page.Camera.aspect, 1e-3f) - MarginUnits;
                float halfHeight = size * PageHeightFraction - MarginUnits;
                zoom = Mathf.Min(zoom, halfWidth / Mathf.Max(page.Half.x, 1e-3f));
                zoom = Mathf.Min(zoom, halfHeight / Mathf.Max(page.Half.y, 1e-3f));
                any = true;
            }
            return any ? Mathf.Clamp(zoom, 1f, MaxZoom) : 1f;
        }

        private void Project(Page page)
        {
            Camera camera = page.Camera;
            if (camera == null) return;
            float scale = Mathf.Abs(camera.transform.lossyScale.y);
            if (scale < 1e-5f) scale = 1e-5f;
            float half = camera.orthographicSize / _zoom * scale;
            float halfWidth = half * Mathf.Max(camera.aspect, 1e-3f);
            Vector2 centre = page.HasContent ? page.Centre * scale : Vector2.zero;
            float near = Mathf.Min(camera.nearClipPlane, 0.01f);
            camera.projectionMatrix = Matrix4x4.Ortho(centre.x - halfWidth, centre.x + halfWidth, centre.y - half, centre.y + half, near, camera.farClipPlane);
        }

        private void Scan(Page page)
        {
            page.HasContent = false;
            Camera camera = page.Camera;
            if (camera == null || !camera.gameObject.activeInHierarchy) return;
            Transform frame = camera.transform;
            camera.GetComponentsInChildren(false, _renderers);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < _renderers.Count; i++)
            {
                MeshRenderer renderer = _renderers[i] as MeshRenderer;
                if (renderer == null || !renderer.enabled || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Bounds local = filter.sharedMesh.bounds;
                if (local.size.x < 1e-4f && local.size.y < 1e-4f) continue;
                Transform t = renderer.transform;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = frame.InverseTransformPoint(t.TransformPoint(corner));
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
                page.HasContent = true;
            }
            _renderers.Clear();
            if (!page.HasContent) return;
            page.Centre = (min + max) * 0.5f;
            page.Half = (max - min) * 0.5f;
        }
    }
}
