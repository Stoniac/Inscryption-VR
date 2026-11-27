using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class EyeGaze : MonoBehaviour
    {
        internal const string HelperName = "InscryptionVR.Eyes.exe";
        private const uint Magic = 0x45594531;
        private const int FlagValid = 2;
        private const int FlagHeadSpace = 8;
        private const int SharedSize = 84;
        private const float StaleSeconds = 0.25f;
        private const float LiveSeconds = 2f;
        private const float NoGazeWarnSeconds = 20f;
        private const float RetrySeconds = 10f;
        private const int MaxStarts = 3;

        private static EyeGaze _instance;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private bool _enabled;
        private bool _failed;
        private bool _missing;
        private string _directory;
        private Process _helper;
        private IntPtr _mapping;
        private IntPtr _view;
        private int _starts;
        private float _nextStart;
        private int _lastStatus = int.MinValue;
        private uint _lastSamples;
        private float _lastSampleTime = -100f;
        private float _runningSince = -1f;
        private bool _warnedNoGaze;
        private bool _loggedSample;
        private bool _gaveUp;
        private Vector3 _localOrigin;
        private Vector3 _localDirection = Vector3.forward;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, bool enabled)
        {
            _logger = logger;
            _renderer = renderer;
            _enabled = enabled;
            _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _instance = this;
            if (!File.Exists(Path.Combine(_directory, HelperName)))
            {
                _logger.LogInfo("Eye tracking unavailable: " + HelperName + " is not installed next to the plugin.");
                _failed = true;
                _missing = true;
            }
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) StopHelper("turned off in settings");
            else
            {
                _starts = 0;
                _gaveUp = false;
                _nextStart = 0f;
            }
        }

        internal static bool Live
        {
            get
            {
                EyeGaze gaze = _instance;
                return gaze != null && gaze._enabled && Time.unscaledTime - gaze._lastSampleTime < LiveSeconds;
            }
        }

        internal static string Status
        {
            get
            {
                EyeGaze gaze = _instance;
                if (gaze == null || gaze._missing) return "not installed";
                if (gaze._failed) return "error, see log";
                if (!gaze._enabled) return "off";
                if (Live) return "eyes found";
                if (gaze._helper == null) return gaze._gaveUp ? "unavailable" : "waiting";
                return "no eyes";
            }
        }

        internal static bool TryGetWorldGaze(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            EyeGaze gaze = _instance;
            if (gaze == null || !gaze._enabled || Time.unscaledTime - gaze._lastSampleTime > StaleSeconds) return false;
            Vector3 head;
            Quaternion rotation;
            float scale;
            if (!gaze._renderer.TryGetHeadPose(out head, out rotation, out scale)) return false;
            origin = head + rotation * (gaze._localOrigin * scale);
            direction = rotation * gaze._localDirection;
            return true;
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                StopHelper("error");
                _logger.LogWarning("Eye tracking disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            if (!_enabled) return;
            if (_helper == null)
            {
                Vector3 anchor;
                Quaternion yaw;
                Vector3 origin;
                Quaternion trackingYaw;
                float scale;
                if (_gaveUp || Time.unscaledTime < _nextStart || !_renderer.TryGetTrackingSpace(out anchor, out yaw, out origin, out trackingYaw, out scale)) return;
                StartHelper();
                return;
            }
            Read();
            bool exited;
            try { exited = _helper.HasExited; }
            catch (Exception) { exited = true; }
            if (!exited) return;
            int code = 0;
            try { code = _helper.ExitCode; }
            catch (Exception) { }
            Read();
            _logger.LogInfo("Eye tracking helper exited (code " + code + ", status " + Describe(_lastStatus) + ").");
            CloseHelper();
            if (_lastStatus == -7)
            {
                _gaveUp = true;
                return;
            }
            if (_starts >= MaxStarts)
            {
                _gaveUp = true;
                _logger.LogInfo("Eye tracking stays off for this session after " + _starts + " attempts; Leshy and gaze checks use your head direction.");
            }
            _nextStart = Time.unscaledTime + RetrySeconds;
        }

        private void StartHelper()
        {
            _starts++;
            int pid = Process.GetCurrentProcess().Id;
            string name = "Local\\InscryptionVR.Eyes." + pid;
            if (_view == IntPtr.Zero)
            {
                _mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04, 0, SharedSize, name);
                if (_mapping == IntPtr.Zero) throw new InvalidOperationException("CreateFileMapping failed: " + Marshal.GetLastWin32Error());
                _view = MapViewOfFile(_mapping, 0x0002 | 0x0004, 0, 0, new UIntPtr(SharedSize));
                if (_view == IntPtr.Zero) throw new InvalidOperationException("MapViewOfFile failed: " + Marshal.GetLastWin32Error());
            }
            for (int offset = 0; offset < SharedSize; offset += 4) Marshal.WriteInt32(_view, offset, 0);
            _lastStatus = int.MinValue;
            _lastSamples = 0;
            _runningSince = -1f;
            _warnedNoGaze = false;
            var info = new ProcessStartInfo(Path.Combine(_directory, HelperName));
            info.Arguments = Quote(Path.GetFullPath(Path.Combine(_directory, "openvr_api.dll"))) + " " + Quote(name) + " " + pid;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = _directory;
            try { _helper = Process.Start(info); }
            catch (Exception exception)
            {
                _logger.LogWarning("Eye tracking helper could not start: " + exception.Message);
                _helper = null;
                _nextStart = Time.unscaledTime + RetrySeconds;
                if (_starts >= MaxStarts) _gaveUp = true;
                return;
            }
            _logger.LogInfo("Eye tracking helper started (attempt " + _starts + ").");
        }

        private static string Quote(string value) { return "\"" + value + "\""; }

        private void Read()
        {
            if (_view == IntPtr.Zero) return;
            int status = 0;
            int flags = 0;
            uint samples = 0;
            var values = new float[15];
            bool ok = false;
            for (int attempt = 0; attempt < 4 && !ok; attempt++)
            {
                int before = Marshal.ReadInt32(_view, 4);
                if ((before & 1) != 0) { Thread.SpinWait(20); continue; }
                Thread.MemoryBarrier();
                if ((uint)Marshal.ReadInt32(_view, 0) != Magic) return;
                status = Marshal.ReadInt32(_view, 8);
                flags = Marshal.ReadInt32(_view, 12);
                for (int i = 0; i < 6; i++) values[i] = ReadFloat(16 + i * 4);
                samples = (uint)Marshal.ReadInt32(_view, 40);
                for (int i = 0; i < 9; i++) values[6 + i] = ReadFloat(48 + i * 4);
                Thread.MemoryBarrier();
                ok = Marshal.ReadInt32(_view, 4) == before;
            }
            if (!ok) return;
            if (status != _lastStatus)
            {
                _lastStatus = status;
                string text = "Eye tracking helper status: " + Describe(status) + (status == -2 ? " (OpenVR error " + flags + ")" : "") + ".";
                if (status < 0) _logger.LogInfo(text);
                else VrLog.Chatter(_logger, text);
                if (status == 1 && _runningSince < 0f) _runningSince = Time.unscaledTime;
            }
            if (status == 1 && (flags & FlagValid) != 0 && samples != _lastSamples)
            {
                _lastSamples = samples;
                var origin = new Vector3(values[0], values[1], -values[2]);
                var direction = new Vector3(values[3], values[4], -values[5]);
                if (!IsFinite(origin) || !IsFinite(direction) || direction.sqrMagnitude < 0.25f) return;
                _localOrigin = origin;
                _localDirection = direction.normalized;
                _lastSampleTime = Time.unscaledTime;
                if (!_loggedSample)
                {
                    _loggedSample = true;
                    _logger.LogInfo("Eye tracking found: first gaze sample " + ((flags & FlagHeadSpace) != 0 ? "(head space)" : "(standing space)") +
                        ", raw origin " + Vec(values, 6) + ", raw target " + Vec(values, 9) + ", head " + Vec(values, 12) +
                        ", head-local origin " + origin.ToString("F3") + ", direction " + _localDirection.ToString("F3") + ".");
                }
            }
            if (status == 1 && !_warnedNoGaze && !_loggedSample && _runningSince >= 0f && Time.unscaledTime - _runningSince > NoGazeWarnSeconds)
            {
                _warnedNoGaze = true;
                _logger.LogInfo("Eye tracking helper is running but has had no valid gaze for " + NoGazeWarnSeconds + " s (flags " + flags +
                    ", raw origin " + Vec(values, 6) + ", raw target " + Vec(values, 9) + "). Check that eye tracking is on in SteamVR and that sharing eye data with PC apps is allowed.");
            }
        }

        private float ReadFloat(int offset)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(_view, offset)), 0);
        }

        private static string Vec(float[] values, int start)
        {
            return "(" + values[start].ToString("F3") + ", " + values[start + 1].ToString("F3") + ", " + values[start + 2].ToString("F3") + ")";
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        private static string Describe(int status)
        {
            switch (status)
            {
                case 0: return "starting";
                case 1: return "running";
                case -1: return "openvr_api.dll missing";
                case -2: return "OpenVR init failed";
                case -3: return "no IVRSystem";
                case -4: return "SteamVR too old for eye tracking (no IVRInput_011)";
                case -5: return "action manifest rejected";
                case -6: return "eye action missing";
                case -7: return "SteamVR quit";
                case -8: return "game exited";
                case -9: return "could not write the action manifest";
                case int.MinValue: return "never reported";
                default: return status.ToString();
            }
        }

        private void StopHelper(string reason)
        {
            if (_helper == null) return;
            try { if (!_helper.HasExited) _helper.Kill(); }
            catch (Exception) { }
            if (_logger != null) _logger.LogInfo("Eye tracking helper stopped (" + reason + ").");
            CloseHelper();
        }

        private void CloseHelper()
        {
            try { if (_helper != null) _helper.Dispose(); }
            catch (Exception) { }
            _helper = null;
            _lastSampleTime = -100f;
        }

        private void OnApplicationQuit() { StopHelper("game closing"); }

        private void OnDestroy()
        {
            StopHelper("plugin unloading");
            if (_view != IntPtr.Zero) UnmapViewOfFile(_view);
            if (_mapping != IntPtr.Zero) CloseHandle(_mapping);
            _view = IntPtr.Zero;
            _mapping = IntPtr.Zero;
            if (_instance == this) _instance = null;
        }
    }
}
