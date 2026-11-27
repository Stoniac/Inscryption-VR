using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    internal sealed class MoonStars
    {
        private const int StarCount = 1100;
        private const float PixelDegrees = 0.12f;
        private const float TwinkleHz = 12f;
        private const float TwinkleShare = 0.4f;
        private const int Queue = 999;
        private const float LowestElevation = -80f;

        private readonly StereoRenderer _renderer;
        private GameObject _root;
        private Mesh _mesh;
        private Material _material;
        private Color32[] _colors;
        private Color[] _tint;
        private float[] _strength;
        private float[] _speed;
        private float[] _phase;
        private float _radius;
        private float _nextTwinkle;
        private bool _shown;

        internal MoonStars(StereoRenderer renderer)
        {
            _renderer = renderer;
            _renderer.BeforeEyes += Place;
        }

        internal void Show(float radius)
        {
            Build();
            _radius = radius;
            _shown = true;
        }

        internal void Hide()
        {
            _shown = false;
            if (_root != null && _root.activeSelf) _root.SetActive(false);
        }

        internal void Dispose()
        {
            Hide();
            _renderer.BeforeEyes -= Place;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }

        private void Place()
        {
            if (!_shown || _root == null) return;
            try
            {
                Vector3 head;
                Quaternion rotation;
                float unitsPerMeter;
                if (!_renderer.TryGetHeadPose(out head, out rotation, out unitsPerMeter)) return;
                _root.transform.position = head;
                _root.transform.rotation = Quaternion.identity;
                _root.transform.localScale = Vector3.one * _radius;
                if (!_root.activeSelf) _root.SetActive(true);
                Twinkle();
            }
            catch (Exception)
            {
                Hide();
            }
        }

        private void Build()
        {
            if (_root != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) throw new InvalidOperationException("the built-in colored shader is missing");
            _material = new Material(shader);
            _material.name = "InscryptionVR Moon Stars";
            _material.hideFlags = HideFlags.HideAndDontSave;
            _material.SetInt("_SrcBlend", (int)BlendMode.One);
            _material.SetInt("_DstBlend", (int)BlendMode.Zero);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetColor("_Color", Color.white);
            _material.renderQueue = Queue;

            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();
            _tint = new Color[StarCount];
            _strength = new float[StarCount];
            _speed = new float[StarCount];
            _phase = new float[StarCount];
            var random = new System.Random(4211);
            float lowSin = Mathf.Sin(LowestElevation * Mathf.Deg2Rad);
            Color cool = new Color(0.78f, 0.84f, 1f, 1f);
            Color warm = new Color(1f, 0.93f, 0.82f, 1f);
            Color violet = new Color(0.92f, 0.78f, 1f, 1f);
            for (int i = 0; i < StarCount; i++)
            {
                float sin = Mathf.Lerp(lowSin, 1f, (float)random.NextDouble());
                float azimuth = (float)random.NextDouble() * Mathf.PI * 2f;
                float cos = Mathf.Sqrt(Mathf.Max(0f, 1f - sin * sin));
                Vector3 direction = new Vector3(cos * Mathf.Sin(azimuth), sin, cos * Mathf.Cos(azimuth));
                float roll = (float)random.NextDouble();
                float pixels = roll < 0.7f ? 1f : roll < 0.94f ? 2f : 3f;
                float strength = pixels >= 3f ? Mathf.Lerp(0.85f, 1f, (float)random.NextDouble())
                    : 0.25f + 0.75f * Mathf.Pow((float)random.NextDouble(), 2f) + (pixels >= 2f ? 0.15f : 0f);
                double hue = random.NextDouble();
                _tint[i] = hue < 0.15 ? violet : Color.Lerp(cool, warm, (float)random.NextDouble());
                _strength[i] = Mathf.Clamp01(strength);
                _speed[i] = random.NextDouble() < TwinkleShare ? Mathf.Lerp(0.8f, 2.8f, (float)random.NextDouble()) : 0f;
                _phase[i] = (float)random.NextDouble() * Mathf.PI * 2f;
                Vector3 east = Vector3.Cross(Vector3.up, direction);
                if (east.sqrMagnitude < 1e-6f) east = Vector3.right;
                east.Normalize();
                Vector3 north = Vector3.Cross(direction, east).normalized;
                float half = Mathf.Tan(pixels * PixelDegrees * 0.5f * Mathf.Deg2Rad);
                int start = vertices.Count;
                vertices.Add(direction - east * half - north * half);
                vertices.Add(direction - east * half + north * half);
                vertices.Add(direction + east * half + north * half);
                vertices.Add(direction + east * half - north * half);
                Color32 star = ToVertex(StarColor(i, 1f));
                for (int k = 0; k < 4; k++) colors.Add(star);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }
            _mesh = new Mesh();
            _mesh.name = "InscryptionVR Moon Stars";
            _mesh.hideFlags = HideFlags.HideAndDontSave;
            _mesh.MarkDynamic();
            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _colors = colors.ToArray();
            _mesh.colors32 = _colors;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);

            _root = new GameObject("InscryptionVR Moon Stars");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.layer = HologramHands.Layer;
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _root.SetActive(false);
        }

        private void Twinkle()
        {
            if (_colors == null || Time.unscaledTime < _nextTwinkle) return;
            _nextTwinkle = Time.unscaledTime + 1f / TwinkleHz;
            float time = Time.unscaledTime;
            for (int i = 0; i < StarCount; i++)
            {
                if (_speed[i] <= 0f) continue;
                Color32 color = ToVertex(StarColor(i, 0.55f + 0.45f * Mathf.Sin(time * _speed[i] + _phase[i])));
                int start = i * 4;
                for (int k = 0; k < 4; k++) _colors[start + k] = color;
            }
            _mesh.colors32 = _colors;
        }

        private Color StarColor(int index, float glow)
        {
            return _tint[index] * (_strength[index] * glow);
        }

        private static Color32 ToVertex(Color color)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            color.a = 1f;
            return color;
        }
    }
}
