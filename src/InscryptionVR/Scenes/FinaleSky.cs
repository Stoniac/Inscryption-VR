using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    public sealed class FinaleSky : MonoBehaviour
    {
        private const float SampleDelaySeconds = 2.5f;
        private const int SampleSize = 48;
        private const int SampleYaws = 8;
        private const float SamplePitch = 40f;
        private const float SampleFov = 90f;
        private const float VoidTolerance = 0.03f;
        private const int MinColumns = 12;
        private const float RadiusOfFar = 0.9f;
        private const float StarRadius = 0.99f;
        private const int Rings = 32;
        private const int Segments = 48;
        private const int StarCount = 260;
        private const float PixelDegrees = 0.12f;
        private const float TwinkleHz = 12f;
        private const float TwinkleShare = 0.35f;
        private const int Queue = 2450;
        private static readonly Color FallbackRim = new Color(0.06f, 0.08f, 0.14f, 1f);
        private const float FallbackEdge = 20f;

        private struct Reading
        {
            internal Color Rim;
            internal Color Void;
            internal float Edge;
            internal int Columns;
            internal bool Found;
        }

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private bool _failed;
        private GameObject _dome;
        private Mesh _mesh;
        private Material _material;
        private Color32[] _colors;
        private int _starStart;
        private Color[] _starSky;
        private Color[] _starTint;
        private float[] _starStrength;
        private float[] _twinkleSpeed;
        private float[] _twinklePhase;
        private float _enteredAt = -1f;
        private bool _ready;
        private float _nextTwinkle;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer)
        {
            _logger = logger;
            _renderer = renderer;
            _renderer.BeforeEyes += Place;
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception) { Fail(exception); }
        }

        private void Tick()
        {
            if (!VrScenes.InFinale)
            {
                _enteredAt = -1f;
                _ready = false;
                Hide();
                return;
            }
            Camera source = _renderer.SourceCamera;
            if (source == null || source.gameObject.scene.name != VrScenes.Finale) return;
            if (_enteredAt < 0f) _enteredAt = Time.unscaledTime;
            if (_ready || Time.unscaledTime - _enteredAt < SampleDelaySeconds) return;
            Reading reading = Sample(source);
            Build(reading);
            _ready = true;
            if (reading.Found)
                _logger.LogInfo("Finale sky roof: the backdrop ends about " + reading.Edge.ToString("0") + " degrees up; the roof continues its colour " +
                    Hex(reading.Rim) + " (from " + reading.Columns + " columns; the void above was " + Hex(reading.Void) + ", clear flags " + source.clearFlags +
                    ", far clip " + source.farClipPlane.ToString("0") + ").");
            else
                _logger.LogInfo("Finale sky roof: no backdrop edge found overhead (" + reading.Columns + " columns; void " + Hex(reading.Void) + ", clear flags " +
                    source.clearFlags + "); using a night blue roof from " + FallbackEdge.ToString("0") + " degrees up.");
        }

        private void Place()
        {
            if (_failed) return;
            try
            {
                if (_dome == null || !_ready || !VrScenes.InFinale)
                {
                    Hide();
                    return;
                }
                Vector3 head;
                Quaternion rotation;
                float unitsPerMeter;
                if (!_renderer.TryGetHeadPose(out head, out rotation, out unitsPerMeter)) return;
                Camera source = _renderer.SourceCamera;
                float far = source != null ? source.farClipPlane : 1000f;
                _dome.transform.position = head;
                _dome.transform.rotation = Quaternion.identity;
                _dome.transform.localScale = Vector3.one * (far * RadiusOfFar);
                if (!_dome.activeSelf) _dome.SetActive(true);
                Twinkle();
            }
            catch (Exception exception) { Fail(exception); }
        }

        private void Hide()
        {
            if (_dome != null && _dome.activeSelf) _dome.SetActive(false);
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            try { Hide(); } catch (Exception) { }
            _logger.LogError("Finale sky roof turned off after an error: " + exception);
        }

        private Reading Sample(Camera source)
        {
            var reading = new Reading();
            RenderTexture target = RenderTexture.GetTemporary(SampleSize, SampleSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var texture = new Texture2D(SampleSize, SampleSize, TextureFormat.RGB24, false);
            var probe = new GameObject("InscryptionVR Sky Probe Camera");
            try
            {
                Camera camera = probe.AddComponent<Camera>();
                camera.CopyFrom(source);
                camera.enabled = false;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.targetTexture = target;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.fieldOfView = SampleFov;
                camera.aspect = 1f;
                camera.cullingMask = source.cullingMask & ~(1 << HologramHands.Layer) & ~(1 << VrDialogue.SubtitleLayer);
                camera.transform.position = source.transform.position;
                var frames = new List<Color[]>();
                var rays = new List<Vector3[]>();
                for (int i = 0; i < SampleYaws; i++)
                {
                    camera.transform.rotation = Quaternion.Euler(-SamplePitch, i * 360f / SampleYaws, 0f);
                    camera.Render();
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = target;
                    texture.ReadPixels(new Rect(0f, 0f, SampleSize, SampleSize), 0, 0, false);
                    RenderTexture.active = previous;
                    frames.Add(texture.GetPixels());
                    var frameRays = new Vector3[SampleSize * SampleSize];
                    for (int y = 0; y < SampleSize; y++)
                        for (int x = 0; x < SampleSize; x++)
                            frameRays[y * SampleSize + x] = camera.ViewportPointToRay(new Vector3((x + 0.5f) / SampleSize, (y + 0.5f) / SampleSize, 0f)).direction;
                    rays.Add(frameRays);
                }
                var tops = new List<Color>();
                foreach (Color[] frame in frames) tops.Add(frame[(SampleSize - 1) * SampleSize + SampleSize / 2]);
                reading.Void = Median(tops);
                var rims = new List<Color>();
                var edges = new List<float>();
                for (int f = 0; f < frames.Count; f++)
                {
                    Color[] frame = frames[f];
                    for (int x = 0; x < SampleSize; x++)
                    {
                        if (Distance(frame[(SampleSize - 1) * SampleSize + x], reading.Void) > VoidTolerance) continue;
                        int edge = -1;
                        for (int y = SampleSize - 2; y >= 0; y--)
                        {
                            if (Distance(frame[y * SampleSize + x], reading.Void) <= VoidTolerance) continue;
                            edge = y;
                            break;
                        }
                        if (edge < 3) continue;
                        Color below = (frame[(edge - 1) * SampleSize + x] + frame[(edge - 2) * SampleSize + x]) * 0.5f;
                        rims.Add(below);
                        edges.Add(Mathf.Asin(Mathf.Clamp(rays[f][edge * SampleSize + x].y, -1f, 1f)) * Mathf.Rad2Deg);
                    }
                }
                reading.Columns = rims.Count;
                reading.Found = rims.Count >= MinColumns;
                if (reading.Found)
                {
                    reading.Rim = Median(rims);
                    edges.Sort();
                    reading.Edge = Mathf.Clamp(edges[edges.Count / 2], -10f, 80f);
                }
                else
                {
                    reading.Rim = FallbackRim;
                    reading.Edge = FallbackEdge;
                }
                reading.Rim.a = 1f;
            }
            finally
            {
                Destroy(probe);
                Destroy(texture);
                RenderTexture.ReleaseTemporary(target);
            }
            return reading;
        }

        private void Build(Reading reading)
        {
            EnsureObjects();
            Color rim = reading.Rim;
            Color zenith = new Color(rim.r * 0.42f, rim.g * 0.48f, rim.b * 0.62f, 1f);
            float edge = reading.Edge;
            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();
            for (int ring = 0; ring <= Rings; ring++)
            {
                float elevation = -90f + 180f * ring / Rings;
                Color color = SkyAt(elevation, edge, rim, zenith);
                float e = elevation * Mathf.Deg2Rad;
                for (int segment = 0; segment <= Segments; segment++)
                {
                    float a = segment * Mathf.PI * 2f / Segments;
                    vertices.Add(new Vector3(Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(a)));
                    colors.Add(ToVertex(color));
                }
            }
            for (int ring = 0; ring < Rings; ring++)
            {
                for (int segment = 0; segment < Segments; segment++)
                {
                    int a = ring * (Segments + 1) + segment;
                    int b = a + Segments + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }
            _starStart = vertices.Count;
            _starSky = new Color[StarCount];
            _starTint = new Color[StarCount];
            _starStrength = new float[StarCount];
            _twinkleSpeed = new float[StarCount];
            _twinklePhase = new float[StarCount];
            var random = new System.Random(1777);
            float lowest = Mathf.Min(edge + 4f, 85f);
            float lowSin = Mathf.Sin(lowest * Mathf.Deg2Rad);
            Color cool = new Color(0.8f, 0.86f, 1f, 1f);
            Color warm = new Color(1f, 0.95f, 0.84f, 1f);
            for (int i = 0; i < StarCount; i++)
            {
                float sin = Mathf.Lerp(lowSin, 1f, (float)random.NextDouble());
                float elevation = Mathf.Asin(sin) * Mathf.Rad2Deg;
                float azimuth = (float)random.NextDouble() * Mathf.PI * 2f;
                float cos = Mathf.Sqrt(Mathf.Max(0f, 1f - sin * sin));
                Vector3 direction = new Vector3(cos * Mathf.Sin(azimuth), sin, cos * Mathf.Cos(azimuth));
                float roll = (float)random.NextDouble();
                float pixels = roll < 0.72f ? 1f : roll < 0.95f ? 2f : 3f;
                float strength = pixels >= 3f ? Mathf.Lerp(0.85f, 1f, (float)random.NextDouble())
                    : 0.3f + 0.7f * Mathf.Pow((float)random.NextDouble(), 2f) + (pixels >= 2f ? 0.15f : 0f);
                strength = Mathf.Clamp01(strength) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lowest, lowest + 12f, elevation));
                _starSky[i] = SkyAt(elevation, edge, rim, zenith);
                _starTint[i] = Color.Lerp(cool, warm, (float)random.NextDouble());
                _starStrength[i] = strength;
                _twinkleSpeed[i] = random.NextDouble() < TwinkleShare ? Mathf.Lerp(0.8f, 2.6f, (float)random.NextDouble()) : 0f;
                _twinklePhase[i] = (float)random.NextDouble() * Mathf.PI * 2f;
                Vector3 east = Vector3.Cross(Vector3.up, direction);
                if (east.sqrMagnitude < 1e-6f) east = Vector3.right;
                east.Normalize();
                Vector3 north = Vector3.Cross(direction, east).normalized;
                float half = Mathf.Tan(pixels * PixelDegrees * 0.5f * Mathf.Deg2Rad) * StarRadius;
                Vector3 centre = direction * StarRadius;
                int start = vertices.Count;
                vertices.Add(centre - east * half - north * half);
                vertices.Add(centre - east * half + north * half);
                vertices.Add(centre + east * half + north * half);
                vertices.Add(centre + east * half - north * half);
                Color32 star = ToVertex(StarColor(i, 1f));
                for (int k = 0; k < 4; k++) colors.Add(star);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }
            _mesh.Clear();
            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _colors = colors.ToArray();
            _mesh.colors32 = _colors;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            _nextTwinkle = 0f;
        }

        private void EnsureObjects()
        {
            if (_dome != null) return;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) throw new InvalidOperationException("the built-in colored shader is missing");
            _material = new Material(shader);
            _material.name = "InscryptionVR Finale Sky Roof";
            _material.hideFlags = HideFlags.HideAndDontSave;
            _material.SetInt("_SrcBlend", (int)BlendMode.One);
            _material.SetInt("_DstBlend", (int)BlendMode.Zero);
            _material.SetInt("_ZWrite", 1);
            _material.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetColor("_Color", Color.white);
            _material.renderQueue = Queue;
            _mesh = new Mesh();
            _mesh.name = "InscryptionVR Finale Sky Roof";
            _mesh.hideFlags = HideFlags.HideAndDontSave;
            _mesh.MarkDynamic();
            _dome = new GameObject("InscryptionVR Finale Sky Roof");
            DontDestroyOnLoad(_dome);
            _dome.layer = HologramHands.Layer;
            _dome.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _dome.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _dome.SetActive(false);
        }

        private void Twinkle()
        {
            if (_colors == null || Time.unscaledTime < _nextTwinkle) return;
            _nextTwinkle = Time.unscaledTime + 1f / TwinkleHz;
            float time = Time.unscaledTime;
            bool changed = false;
            for (int i = 0; i < StarCount; i++)
            {
                if (_twinkleSpeed[i] <= 0f) continue;
                float glow = 0.6f + 0.4f * Mathf.Sin(time * _twinkleSpeed[i] + _twinklePhase[i]);
                Color32 color = ToVertex(StarColor(i, glow));
                int start = _starStart + i * 4;
                for (int k = 0; k < 4; k++) _colors[start + k] = color;
                changed = true;
            }
            if (changed) _mesh.colors32 = _colors;
        }

        private Color StarColor(int index, float glow)
        {
            return Color.Lerp(_starSky[index], _starTint[index], _starStrength[index] * glow);
        }

        private static Color SkyAt(float elevation, float edge, Color rim, Color zenith)
        {
            if (elevation <= edge) return rim;
            float t = Mathf.InverseLerp(edge, 90f, elevation);
            return Color.Lerp(rim, zenith, Mathf.SmoothStep(0f, 1f, t));
        }

        private static Color32 ToVertex(Color color)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.linear;
            color.a = 1f;
            return color;
        }

        private static float Distance(Color a, Color b)
        {
            return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
        }

        private static Color Median(List<Color> colors)
        {
            if (colors.Count == 0) return Color.black;
            var r = new List<float>();
            var g = new List<float>();
            var b = new List<float>();
            foreach (Color c in colors) { r.Add(c.r); g.Add(c.g); b.Add(c.b); }
            r.Sort(); g.Sort(); b.Sort();
            int m = colors.Count / 2;
            return new Color(r[m], g[m], b[m], 1f);
        }

        private static string Hex(Color color)
        {
            Color32 c = color;
            return "#" + c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2");
        }
    }
}
