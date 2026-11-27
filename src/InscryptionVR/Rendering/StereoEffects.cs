using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace InscryptionVR
{
    internal sealed class StereoEffects
    {
        private const string VignetteType = "UnityStandardAssets.ImageEffects.VignetteAndChromaticAberration";
        private const string BlurType = "UnityStandardAssets.ImageEffects.BlurOptimized";
        private const float EyelidOpenHeight = 3.5f;
        private const float MaxBlurDim = 0.4f;
        private const float FullBlinkLids = 0.9f;

        private static readonly HashSet<string> PerEyeTypes = new HashSet<string>
        {
            "UnityStandardAssets.ImageEffects.ColorCorrectionCurves",
            "UnityStandardAssets.ImageEffects.ColorCorrectionLookup",
            "UnityStandardAssets.ImageEffects.ColorCorrectionRamp",
            "UnityStandardAssets.ImageEffects.Grayscale",
            "UnityStandardAssets.ImageEffects.SepiaTone",
            "UnityStandardAssets.ImageEffects.Tonemapping",
            "UnityStandardAssets.ImageEffects.Bloom",
            "UnityStandardAssets.ImageEffects.BloomOptimized",
            "UnityStandardAssets.ImageEffects.NoiseAndGrain",
            VignetteType,
            "PosterizeWithThreshold"
        };

        private static readonly string[] VignetteComfortFields = { "chromaticAberration", "axialAberration", "blur" };
        private static readonly FieldInfo ScreenColorMaskField =
            typeof(DiskCardGame.ScreenColorEffect).GetField("mask", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TopLidField =
            typeof(DiskCardGame.EyelidMaskEffect).GetField("topLid", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Mirror
        {
            internal MonoBehaviour Source;
            internal MonoBehaviour Left;
            internal MonoBehaviour Right;
            internal FieldInfo[] Fields;
            internal FieldInfo[] ComfortFields;
        }

        private readonly ManualLogSource _logger;
        private readonly Camera _source;
        private readonly Camera _left;
        private readonly Camera _right;
        private readonly List<Mirror> _mirrors = new List<Mirror>();
        private readonly Material _material;
        private readonly Camera _subtitleCamera;
        private List<MonoBehaviour> _blurs = new List<MonoBehaviour>();
        private static Type _blurType;
        private static FieldInfo _blurSize;
        private Camera _effectsCamera;
        private SpriteRenderer _screenMask;
        private Transform _topLid;
        private string _signature;
        private float _nextScan;
        private bool _failed;
        private Color _fill;
        private float _lids;
        private float _dim;

        internal bool Letterbox = true;
        internal DesktopCameras Desktop;
        private bool _colorEffects = true;

        internal bool ColorEffects
        {
            get { return _colorEffects; }
            set
            {
                if (_colorEffects == value) return;
                _colorEffects = value;
                _signature = null;
                _nextScan = 0f;
            }
        }

        internal StereoEffects(ManualLogSource logger, Camera source, Camera left, Camera right)
        {
            _logger = logger;
            _source = source;
            _left = left;
            _right = right;
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _material.SetInt("_Cull", (int)CullMode.Off);
                _material.SetInt("_ZWrite", 0);
                _material.SetInt("_ZTest", (int)CompareFunction.Always);
            }
            else
                _logger.LogWarning("Stereo overlays unavailable: built-in colored shader not found. Per-eye color effects still run.");
            var subtitleObject = new GameObject("InscryptionVR Subtitle Overlay Camera");
            subtitleObject.transform.SetParent(left.transform.parent, false);
            _subtitleCamera = subtitleObject.AddComponent<Camera>();
            _subtitleCamera.enabled = false;
            _subtitleCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _subtitleCamera.clearFlags = CameraClearFlags.Nothing;
            _subtitleCamera.cullingMask = 1 << VrDialogue.SubtitleLayer;
            _subtitleCamera.useOcclusionCulling = false;
        }

        internal int ExtraCullingMask
        {
            get { return !_failed && _effectsCamera != null && Live(_effectsCamera) ? _effectsCamera.cullingMask : 0; }
        }

        private bool Live(Camera camera)
        {
            if (camera.isActiveAndEnabled) return true;
            return Desktop != null && camera.gameObject.activeInHierarchy && Desktop.IsHidden(camera);
        }

        internal bool HasOverlay
        {
            get { return !_failed && _material != null && (_fill.a > 0.003f || _lids > 0.001f || _dim > 0.001f); }
        }

        internal void BeforeRender()
        {
            if (_failed) return;
            try
            {
                if (Time.unscaledTime >= _nextScan) Scan();
                foreach (Mirror mirror in _mirrors)
                {
                    if (mirror.Source == null || mirror.Left == null || mirror.Right == null)
                    {
                        _nextScan = 0f;
                        continue;
                    }
                    Copy(mirror, mirror.Left);
                    Copy(mirror, mirror.Right);
                }
                ReadOverlays();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        internal void DrawOverlays(RenderTexture target)
        {
            if (!HasOverlay) return;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadOrtho();
            _material.SetPass(0);
            GL.Begin(GL.QUADS);
            if (_fill.a > 0.003f) Quad(0f, 1f, _fill);
            if (_dim > 0.001f) Quad(0f, 1f, new Color(0f, 0f, 0f, _dim));
            if (_lids > 0.001f)
            {
                float height = _lids * 0.52f;
                Quad(1f - height, 1f, Color.black);
                Quad(0f, height, Color.black);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = previous;
        }

        internal void RenderSubtitlesOver(Camera eye, RenderTexture target)
        {
            _subtitleCamera.transform.position = eye.transform.position;
            _subtitleCamera.transform.rotation = eye.transform.rotation;
            _subtitleCamera.nearClipPlane = eye.nearClipPlane;
            _subtitleCamera.farClipPlane = eye.farClipPlane;
            _subtitleCamera.targetTexture = target;
            _subtitleCamera.projectionMatrix = eye.projectionMatrix;
            _subtitleCamera.Render();
        }

        internal void Dispose()
        {
            DestroyMirrors();
            if (_subtitleCamera != null) UnityEngine.Object.Destroy(_subtitleCamera.gameObject);
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }

        private static void Quad(float bottom, float top, Color color)
        {
            GL.Color(color);
            GL.Vertex3(0f, bottom, 0f);
            GL.Vertex3(1f, bottom, 0f);
            GL.Vertex3(1f, top, 0f);
            GL.Vertex3(0f, top, 0f);
        }

        private void Scan()
        {
            _nextScan = Time.unscaledTime + 2f;
            var hosts = new List<GameObject>();
            var cameras = new List<Camera>(Camera.allCameras);
            if (Desktop != null)
                foreach (Camera hidden in Desktop.Hidden)
                    if (hidden != null && !cameras.Contains(hidden)) cameras.Add(hidden);
            cameras.RemoveAll(delegate(Camera camera) { return camera.name == DesktopCameras.ProxyName; });
            cameras.Sort((a, b) => a.depth.CompareTo(b.depth));
            foreach (Camera camera in cameras)
                if (camera != _left && camera != _right && camera != _subtitleCamera && camera.targetTexture == null &&
                    camera.gameObject.activeInHierarchy && !hosts.Contains(camera.gameObject))
                    hosts.Add(camera.gameObject);
            var cameraEffects = UnityEngine.Object.FindObjectOfType<DiskCardGame.CameraEffects>();
            if (cameraEffects != null && !hosts.Contains(cameraEffects.gameObject)) hosts.Add(cameraEffects.gameObject);

            var sources = new List<MonoBehaviour>();
            var blurs = new List<MonoBehaviour>();
            var seen = new HashSet<string>();
            string signature = string.Empty;
            foreach (GameObject host in hosts)
                foreach (MonoBehaviour behaviour in host.GetComponents<MonoBehaviour>())
                {
                    if (behaviour == null) continue;
                    string typeName = behaviour.GetType().FullName;
                    if (typeName == BlurType) blurs.Add(behaviour);
                    else if (_colorEffects && PerEyeTypes.Contains(typeName) && seen.Add(typeName))
                    {
                        sources.Add(behaviour);
                        signature += behaviour.GetInstanceID() + ";";
                    }
                }
            _blurs = blurs;

            Camera effectsCamera = cameraEffects != null ? cameraEffects.GetComponent<Camera>() : null;
            if (effectsCamera == null || effectsCamera == _source || effectsCamera.orthographic || effectsCamera.targetTexture != null)
            {
                effectsCamera = null;
                foreach (Camera camera in cameras)
                    if (camera != _source && !camera.orthographic && camera.targetTexture == null && camera.name == "Effects Camera")
                    {
                        effectsCamera = camera;
                        break;
                    }
            }
            if (effectsCamera != _effectsCamera)
            {
                _effectsCamera = effectsCamera;
                if (effectsCamera != null)
                    _logger.LogInfo("Stereo effects: merging effects camera '" + effectsCamera.name + "' layers 0x" +
                        effectsCamera.cullingMask.ToString("X8") + " into the eyes.");
            }

            FindOverlays();
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(sources);
            }
        }

        private void FindOverlays()
        {
            _screenMask = null;
            _topLid = null;
            var ui = UnityEngine.Object.FindObjectOfType<DiskCardGame.UIManager>();
            ScreenEffectsLayer layer = ui != null ? ui.Effects : null;
            if (layer == null) return;
            var color = layer.GetEffect<DiskCardGame.ScreenColorEffect>();
            if (color != null && ScreenColorMaskField != null) _screenMask = ScreenColorMaskField.GetValue(color) as SpriteRenderer;
            var eyelids = layer.GetEffect<DiskCardGame.EyelidMaskEffect>();
            if (eyelids != null && TopLidField != null) _topLid = TopLidField.GetValue(eyelids) as Transform;
        }

        private void ReadOverlays()
        {
            _fill = Color.clear;
            if (_screenMask != null && _screenMask.enabled && _screenMask.gameObject.activeInHierarchy)
                _fill = _screenMask.color;
            _lids = 0f;
            if (_topLid != null && _topLid.gameObject.activeInHierarchy)
                _lids = Mathf.Clamp01(1f - _topLid.localPosition.y / EyelidOpenHeight);
            if (!Letterbox && _lids < FullBlinkLids) _lids = 0f;
            _dim = 0f;
            foreach (MonoBehaviour blur in _blurs)
            {
                if (blur == null || !blur.isActiveAndEnabled) continue;
                FieldInfo size = BlurSizeField(blur.GetType());
                float amount = size != null ? (float)size.GetValue(blur) : 3f;
                _dim = Mathf.Max(_dim, Mathf.Clamp01(amount / 6f) * MaxBlurDim);
            }
        }

        private static FieldInfo BlurSizeField(Type type)
        {
            if (type != _blurType)
            {
                _blurType = type;
                FieldInfo field = type.GetField("blurSize", BindingFlags.Instance | BindingFlags.Public);
                _blurSize = field != null && field.FieldType == typeof(float) ? field : null;
            }
            return _blurSize;
        }

        private void Rebuild(List<MonoBehaviour> sources)
        {
            DestroyMirrors();
            if (sources.Count == 0) return;
            bool leftActive = _left.gameObject.activeSelf;
            bool rightActive = _right.gameObject.activeSelf;
            _left.gameObject.SetActive(false);
            _right.gameObject.SetActive(false);
            var names = new List<string>();
            try
            {
                foreach (MonoBehaviour source in sources)
                {
                    Type type = source.GetType();
                    var mirror = new Mirror { Source = source, Fields = SerializedFields(type) };
                    if (type.FullName == VignetteType)
                    {
                        var comfort = new List<FieldInfo>();
                        foreach (string name in VignetteComfortFields)
                        {
                            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                            if (field != null && field.FieldType == typeof(float)) comfort.Add(field);
                        }
                        mirror.ComfortFields = comfort.ToArray();
                    }
                    try
                    {
                        mirror.Left = (MonoBehaviour)_left.gameObject.AddComponent(type);
                        mirror.Right = (MonoBehaviour)_right.gameObject.AddComponent(type);
                        Copy(mirror, mirror.Left);
                        Copy(mirror, mirror.Right);
                        _mirrors.Add(mirror);
                        names.Add(type.Name + "@" + source.gameObject.name);
                    }
                    catch (Exception exception)
                    {
                        if (mirror.Left != null) UnityEngine.Object.Destroy(mirror.Left);
                        if (mirror.Right != null) UnityEngine.Object.Destroy(mirror.Right);
                        _logger.LogWarning("Stereo effects: skipped " + type.Name + ": " + exception.Message);
                    }
                }
            }
            finally
            {
                _left.gameObject.SetActive(leftActive);
                _right.gameObject.SetActive(rightActive);
            }
            _logger.LogInfo("Stereo effects mirrored per eye: " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())) + ".");
        }

        private void DestroyMirrors()
        {
            foreach (Mirror mirror in _mirrors)
            {
                if (mirror.Left != null) UnityEngine.Object.Destroy(mirror.Left);
                if (mirror.Right != null) UnityEngine.Object.Destroy(mirror.Right);
            }
            _mirrors.Clear();
        }

        private static void Copy(Mirror mirror, MonoBehaviour target)
        {
            foreach (FieldInfo field in mirror.Fields)
                field.SetValue(target, field.GetValue(mirror.Source));
            if (mirror.ComfortFields != null)
                foreach (FieldInfo field in mirror.ComfortFields)
                    field.SetValue(target, 0f);
            bool enabled = mirror.Source.isActiveAndEnabled;
            if (target.enabled != enabled) target.enabled = enabled;
        }

        private static FieldInfo[] SerializedFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized || field.IsInitOnly) continue;
                    if (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField))) continue;
                    if (typeof(Component).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(GameObject)) continue;
                    fields.Add(field);
                }
            return fields.ToArray();
        }

        private void Fail(Exception exception)
        {
            _failed = true;
            _logger.LogWarning("Stereo effects disabled after an error; rendering continues without them: " + exception.Message);
            try { DestroyMirrors(); } catch (Exception) { }
        }
    }
}
