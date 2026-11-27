using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    internal static class Optimization
    {
        internal const string Off = "Off";
        internal const string Balanced = "Balanced";
        internal const string Standalone = "Standalone";
        internal static readonly string[] Levels = { Off, Balanced, Standalone };

        internal const string Auto = "Auto";
        internal const string On = "On";
        internal static readonly string[] HalfRateModes = { Auto, On, Off };

        internal const int LevelOff = 0;
        internal const int LevelBalanced = 1;
        internal const int LevelStandalone = 2;

        internal static int Level(string name)
        {
            if (name == Standalone) return LevelStandalone;
            if (name == Balanced) return LevelBalanced;
            return LevelOff;
        }

        internal static bool HalfRate(int level, string mode)
        {
            if (mode == On) return true;
            if (mode == Off) return false;
            return level >= LevelStandalone;
        }

        internal static bool ColorGrading(int level)
        {
            return level < LevelStandalone;
        }

        internal static float RenderScaleCap(int level)
        {
            if (level >= LevelStandalone) return 0.5f;
            if (level >= LevelBalanced) return 0.6f;
            return 0f;
        }
    }

    internal sealed class QualityOverride
    {
        private const float BalancedShadowDistance = 40f;
        private const int BalancedPixelLights = 2;
        private const int StandalonePixelLights = 1;
        private const float StandaloneLodBias = 0.6f;

        private readonly ManualLogSource _logger;
        private bool _saved;
        private ShadowQuality _shadows;
        private float _shadowDistance;
        private int _shadowCascades;
        private int _pixelLights;
        private float _lodBias;
        private AnisotropicFiltering _anisotropic;
        private bool _softParticles;
        private bool _reflectionProbes;
        private int _applied;

        internal QualityOverride(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal void Apply(int level)
        {
            if (level <= Optimization.LevelOff)
            {
                Restore();
                return;
            }
            if (!_saved)
            {
                _saved = true;
                _shadows = QualitySettings.shadows;
                _shadowDistance = QualitySettings.shadowDistance;
                _shadowCascades = QualitySettings.shadowCascades;
                _pixelLights = QualitySettings.pixelLightCount;
                _lodBias = QualitySettings.lodBias;
                _anisotropic = QualitySettings.anisotropicFiltering;
                _softParticles = QualitySettings.softParticles;
                _reflectionProbes = QualitySettings.realtimeReflectionProbes;
            }
            bool standalone = level >= Optimization.LevelStandalone;
            QualitySettings.shadows = standalone ? ShadowQuality.Disable : _shadows;
            QualitySettings.shadowDistance = Mathf.Min(_shadowDistance, BalancedShadowDistance);
            QualitySettings.shadowCascades = 1;
            QualitySettings.pixelLightCount = Mathf.Min(_pixelLights, standalone ? StandalonePixelLights : BalancedPixelLights);
            QualitySettings.lodBias = standalone ? _lodBias * StandaloneLodBias : _lodBias;
            QualitySettings.anisotropicFiltering = standalone ? AnisotropicFiltering.Disable : _anisotropic;
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            if (_applied != level)
                _logger.LogInfo("Optimization quality: shadows " + QualitySettings.shadows + " (was " + _shadows + "), shadow distance " +
                    QualitySettings.shadowDistance.ToString("0.#") + " (was " + _shadowDistance.ToString("0.#") + "), cascades 1 (was " + _shadowCascades +
                    "), pixel lights " + QualitySettings.pixelLightCount + " (was " + _pixelLights + "), LOD bias " + QualitySettings.lodBias.ToString("0.##") +
                    " (was " + _lodBias.ToString("0.##") + "), anisotropic " + QualitySettings.anisotropicFiltering + " (was " + _anisotropic + ").");
            _applied = level;
        }

        internal void Restore()
        {
            if (!_saved) return;
            _saved = false;
            QualitySettings.shadows = _shadows;
            QualitySettings.shadowDistance = _shadowDistance;
            QualitySettings.shadowCascades = _shadowCascades;
            QualitySettings.pixelLightCount = _pixelLights;
            QualitySettings.lodBias = _lodBias;
            QualitySettings.anisotropicFiltering = _anisotropic;
            QualitySettings.softParticles = _softParticles;
            QualitySettings.realtimeReflectionProbes = _reflectionProbes;
            if (_applied != Optimization.LevelOff) _logger.LogInfo("Optimization quality: game settings restored.");
            _applied = Optimization.LevelOff;
        }
    }

    internal sealed class DesktopCameras
    {
        private const float ScanSeconds = 0.5f;
        internal const string ProxyName = "InscryptionVR Main Camera Stand-in";

        private readonly ManualLogSource _logger;
        private readonly List<Camera> _hidden = new List<Camera>();
        private Camera _proxy;
        private Camera _proxied;
        private float _nextScan;
        private bool _logged;

        internal DesktopCameras(ManualLogSource logger)
        {
            _logger = logger;
        }

        internal List<Camera> Hidden { get { return _hidden; } }

        internal bool Active { get { return _hidden.Count > 0; } }

        internal bool IsHidden(Camera camera)
        {
            return camera != null && _hidden.Contains(camera);
        }

        internal void Hide(Camera source)
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanSeconds;
                Scan(source);
            }
            for (int i = 0; i < _hidden.Count; i++)
            {
                Camera camera = _hidden[i];
                if (camera != null && camera.enabled) camera.enabled = false;
            }
            UpdateProxy();
        }

        private void Scan(Camera source)
        {
            _hidden.RemoveAll(delegate(Camera camera) { return camera == null; });
            if (source != null && source.enabled) Add(source);
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null || camera == _proxy || camera.targetTexture != null) continue;
                Add(camera);
            }
            if (!_logged && _hidden.Count > 0)
            {
                _logged = true;
                var names = new StringBuilder();
                for (int i = 0; i < _hidden.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(_hidden[i].name);
                }
                _logger.LogInfo("Optimization: the monitor window stops drawing the game while you're at the table (" + names +
                    (_proxy != null ? "; a stand-in main camera keeps the game's cursor working" : "") + ").");
            }
        }

        private void Add(Camera camera)
        {
            if (_hidden.Contains(camera)) return;
            if (_proxied == null && camera.CompareTag("MainCamera")) MakeProxy(camera);
            _hidden.Add(camera);
            camera.enabled = false;
        }

        private void MakeProxy(Camera main)
        {
            var proxyObject = new GameObject(ProxyName);
            proxyObject.tag = "MainCamera";
            proxyObject.layer = main.gameObject.layer;
            proxyObject.transform.SetParent(main.transform, false);
            _proxy = proxyObject.AddComponent<Camera>();
            _proxy.CopyFrom(main);
            _proxy.cullingMask = 0;
            _proxy.clearFlags = CameraClearFlags.Nothing;
            _proxy.useOcclusionCulling = false;
            _proxy.allowHDR = false;
            _proxy.allowMSAA = false;
            _proxy.stereoTargetEye = StereoTargetEyeMask.None;
            _proxy.enabled = true;
            _proxied = main;
        }

        private void UpdateProxy()
        {
            if (_proxy == null) return;
            if (_proxied == null)
            {
                Object.Destroy(_proxy.gameObject);
                _proxy = null;
                return;
            }
            Transform t = _proxy.transform;
            if (t.localPosition != Vector3.zero) t.localPosition = Vector3.zero;
            if (t.localRotation != Quaternion.identity) t.localRotation = Quaternion.identity;
            _proxy.fieldOfView = _proxied.fieldOfView;
            _proxy.nearClipPlane = _proxied.nearClipPlane;
            _proxy.farClipPlane = _proxied.farClipPlane;
            _proxy.orthographic = _proxied.orthographic;
            _proxy.orthographicSize = _proxied.orthographicSize;
            _proxy.rect = _proxied.rect;
            if (_proxy.targetTexture != _proxied.targetTexture) _proxy.targetTexture = _proxied.targetTexture;
            if (_proxy.cullingMask != 0) _proxy.cullingMask = 0;
        }

        internal void Restore()
        {
            for (int i = 0; i < _hidden.Count; i++)
                if (_hidden[i] != null) _hidden[i].enabled = true;
            _hidden.Clear();
            if (_proxy != null) Object.Destroy(_proxy.gameObject);
            _proxy = null;
            _proxied = null;
            _nextScan = 0f;
        }
    }

    internal static class PerfProbe
    {
        internal const int Eyes = 0;
        internal const int Hands = 1;
        private const int Count = 2;
        private static readonly string[] Names = { "eye rendering", "hand interactions" };
        private static readonly long[] Totals = new long[Count];
        private static readonly long[] Starts = new long[Count];
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _frames;
        private static double _frameSeconds;
        private static float _worstFrame;

        internal static bool Enabled;

        internal static void Begin(int section)
        {
            if (Enabled) Starts[section] = Clock.ElapsedTicks;
        }

        internal static void End(int section)
        {
            if (Enabled) Totals[section] += Clock.ElapsedTicks - Starts[section];
        }

        internal static void Frame(float seconds)
        {
            if (!Enabled) return;
            _frames++;
            _frameSeconds += seconds;
            if (seconds > _worstFrame) _worstFrame = seconds;
        }

        internal static string Report()
        {
            if (_frames == 0) return "no frames sampled";
            double average = _frameSeconds / _frames;
            var text = new StringBuilder();
            text.Append("frame ").Append((average * 1000.0).ToString("0.0")).Append(" ms avg (")
                .Append((1.0 / System.Math.Max(average, 1e-4)).ToString("0")).Append(" fps), worst ")
                .Append((_worstFrame * 1000f).ToString("0.0")).Append(" ms");
            for (int i = 0; i < Count; i++)
            {
                double ms = Totals[i] * 1000.0 / Stopwatch.Frequency / _frames;
                text.Append("; ").Append(Names[i]).Append(' ').Append(ms.ToString("0.00")).Append(" ms/frame on the main thread");
                Totals[i] = 0;
            }
            _frames = 0;
            _frameSeconds = 0;
            _worstFrame = 0f;
            return text.ToString();
        }
    }
}
