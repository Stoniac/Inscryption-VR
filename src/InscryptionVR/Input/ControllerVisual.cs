using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    internal sealed class ControllerVisual
    {
        private static readonly Color Idle = new Color(0.35f, 0.8f, 1f, 0.35f);
        private static readonly Color Active = new Color(0.55f, 1f, 0.85f, 0.85f);

        private readonly GameObject _root;
        private readonly LineRenderer _line;
        private readonly GameObject _marker;
        private readonly Material _material;
        private readonly Material _markerMaterial;

        internal ControllerVisual(Transform parent)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader == null) throw new System.InvalidOperationException("No shader available for the pointer laser");
            _root = new GameObject("InscryptionVR Pointer Laser");
            _root.transform.SetParent(parent, false);
            _root.layer = HologramHands.Layer;
            _line = _root.AddComponent<LineRenderer>();
            _material = new Material(shader);
            _line.sharedMaterial = _material;
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _marker.name = "InscryptionVR Pointer Target";
            _marker.transform.SetParent(_root.transform, false);
            _marker.layer = HologramHands.Layer;
            Collider collider = _marker.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Object.Destroy(collider);
            }
            _markerMaterial = new Material(shader);
            Renderer markerRenderer = _marker.GetComponent<Renderer>();
            markerRenderer.sharedMaterial = _markerMaterial;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            SetVisible(false);
        }

        internal void Draw(Vector3 start, Vector3 end, bool interactable, float scale)
        {
            SetVisible(true);
            Color color = interactable ? Active : Idle;
            _line.startColor = color;
            _line.endColor = new Color(color.r, color.g, color.b, color.a * 0.5f);
            _line.startWidth = 0.0025f * scale;
            _line.endWidth = 0.001f * scale;
            _line.SetPosition(0, start);
            _line.SetPosition(1, end);
            _marker.transform.position = end;
            _marker.transform.localScale = Vector3.one * ((interactable ? 0.012f : 0.007f) * scale);
            _markerMaterial.color = color;
        }

        internal void SetVisible(bool visible) { if (_root != null && _root.activeSelf != visible) _root.SetActive(visible); }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_material != null) Object.Destroy(_material);
            if (_markerMaterial != null) Object.Destroy(_markerMaterial);
        }
    }
}
