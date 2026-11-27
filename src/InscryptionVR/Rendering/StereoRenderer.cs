using System;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Valve.VR;

namespace InscryptionVR
{
    public sealed class StereoRenderer : MonoBehaviour
    {
        private ManualLogSource _logger;
        private StereoBridge _bridge;
        private Camera _source;
        private DiskCardGame.ViewManager _tableView;
        private Camera _leftCamera;
        private Camera _rightCamera;
        private RenderTexture _leftTexture;
        private RenderTexture _rightTexture;
        private Vector3 _anchorPosition;
        private Quaternion _anchorRotation;
        private Vector3 _trackingOrigin;
        private Quaternion _trackingRotation;
        private float _worldUnitsPerMeter;
        private float _renderScale;
        private bool _renderScaleDirty;
        private uint _recommendedWidth;
        private uint _recommendedHeight;
        private readonly System.Collections.Generic.List<RenderTexture> _retired = new System.Collections.Generic.List<RenderTexture>();
        private readonly System.Collections.Generic.List<int> _retiredFrames = new System.Collections.Generic.List<int>();
        internal const float MinRenderScale = 0.25f;
        internal const float MaxRenderScale = 1.5f;
        private const int MaxEyePixels = 4096;
        private const int RetireFrames = 4;
        private float _seatHeightMeters;
        private Vector3 _seatBase;
        private Vector3 _seatOffset;
        private float _posePredictionFrames;
        private bool _disableDesktopVsync;
        private bool _imageEffects;
        private StereoEffects _effects;
        private bool _letterbox = true;
        private bool _vsyncOverridden;
        private int _savedVsyncCount;
        private int _savedTargetFrameRate;
        private int _loggedFrameCap;
        private int _optimization;
        private string _halfRateMode = Optimization.Auto;
        private bool _desktopMirror;
        private bool _loggedMirror;
        private QualityOverride _quality;
        private DesktopCameras _desktop;
        private float _displayHz = DefaultDisplayHz;
        private float _nextHzQuery;
        private const float DefaultDisplayHz = 90f;
        private const int UncappedFrameRate = 144;
        private VrDialogue _dialogue;
        private MenuScreen _screen;
        public int SeatRevision { get; private set; }
        internal event Action BeforeEyes;
        private bool _started;
        private bool _stopped;
        private bool _suspended;
        private bool _centered;
        private bool _trackingLost;
        private bool _focusLost;
        private bool _acknowledgedFrame;
        private bool _loggedFrustum;
        private bool _hasLastRawPose;
        private Vector3 _lastRawPosition;
        private Quaternion _lastRawRotation = Quaternion.identity;
        private float _lastRawTime;
        private VREvent_t _event;
        private uint _eventSize;
        private const float RecenterJumpMeters = 0.25f;
        private const float RecenterJumpDegrees = 40f;
        private const float RecenterMaxFrameSeconds = 0.1f;
        private const float RecenterDegreesPerSecond = 1200f;
        private const float RecenterMetersPerSecond = 6f;
        private string _waitingScene;
        private string _sourceScene;
        private Transform _seatSpace;
        private Vector3 _seatLocal;
        private bool _walking;
        private float _walkYaw;
        private bool _fixedSeat;
        private Vector3 _fixedPosition;
        private float _fixedYaw;
        private bool _glideNext;
        private Vector3 _glideOffset;
        private float _glideStart = -10f;
        private const float GlideSeconds = 0.6f;
        private readonly float[] _head = new float[12];
        private readonly float[] _leftEye = new float[12];
        private readonly float[] _rightEye = new float[12];
        private readonly float[] _leftFrustum = new float[4];
        private readonly float[] _rightFrustum = new float[4];

        public void Initialize(ManualLogSource logger, float worldUnitsPerMeter, float renderScale, float seatHeightMeters,
            float posePredictionFrames, bool disableDesktopVsync, bool imageEffects)
        {
            _logger = logger;
            _worldUnitsPerMeter = Mathf.Clamp(worldUnitsPerMeter, 1f, 40f);
            _renderScale = Mathf.Clamp(renderScale, MinRenderScale, MaxRenderScale);
            _seatHeightMeters = Mathf.Clamp(seatHeightMeters, -0.5f, 0.5f);
            _posePredictionFrames = Mathf.Clamp(posePredictionFrames, 0f, 3f);
            _disableDesktopVsync = disableDesktopVsync;
            _imageEffects = imageEffects;
            _eventSize = (uint)Marshal.SizeOf(typeof(VREvent_t));
            _quality = new QualityOverride(logger);
            _desktop = new DesktopCameras(logger);
            _logger.LogInfo("VR renderer waiting for the cabin. F8 recenters; F9 stops VR rendering.");
        }

        internal void SetOptimization(string level, string halfRateMode)
        {
            int next = Optimization.Level(level);
            bool changed = next != _optimization;
            _optimization = next;
            _halfRateMode = halfRateMode;
            if (_logger != null)
                _logger.LogInfo("Optimization " + Optimization.Levels[next] + ", half framerate " + (HalfRate ? "on" : "off") + " (" + halfRateMode +
                    "), color grading " + (ColorEffects ? "on" : "off") + ".");
            if (!_started || _stopped) return;
            _quality.Apply(_optimization);
            if (_optimization == Optimization.LevelOff) _desktop.Restore();
            if (changed && !_suspended && _source != null)
            {
                ConfigureEye(_leftCamera, _leftTexture);
                ConfigureEye(_rightCamera, _rightTexture);
            }
            if (_effects != null) _effects.ColorEffects = ColorEffects;
            EnforceVsync();
        }

        private bool HalfRate { get { return Optimization.HalfRate(_optimization, _halfRateMode); } }

        private bool ColorEffects { get { return Optimization.ColorGrading(_optimization); } }

        internal void SetDesktopMirror(bool enabled)
        {
            if (_desktopMirror == enabled && _loggedMirror) return;
            _desktopMirror = enabled;
            _loggedMirror = true;
            if (_logger != null)
                _logger.LogInfo(enabled ? "Monitor window mirrors the headset's left eye (the flat view returns in pause and menu screens)."
                    : "Monitor window shows the flat game camera.");
        }

        private void MirrorToDesktop(RenderTexture eye)
        {
            int width = Screen.width;
            int height = Screen.height;
            if (eye == null || width <= 0 || height <= 0) return;
            float screenAspect = (float)width / height;
            float eyeAspect = (float)eye.width / eye.height;
            Vector2 scale = Vector2.one;
            if (screenAspect > eyeAspect) scale.y = eyeAspect / screenAspect;
            else scale.x = screenAspect / eyeAspect;
            Vector2 offset = new Vector2((1f - scale.x) * 0.5f, (1f - scale.y) * 0.5f);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(eye, (RenderTexture)null, scale, offset);
            RenderTexture.active = previous;
        }

        public float PredictionFrames
        {
            get { return _bridge != null && _bridge.SupportsPrediction && _started && !_stopped ? _posePredictionFrames : 0f; }
        }

        public void SetDialogue(VrDialogue dialogue) { _dialogue = dialogue; }

        internal void SetMenuScreen(MenuScreen screen) { _screen = screen; }

        private bool OnScreen { get { return _screen != null && VrScenes.IsScreen(_sourceScene); } }

        private Camera FindSource(string scene)
        {
            if (!VrScenes.IsScreen(scene)) return FindTableCamera();
            _tableView = null;
            return _screen != null ? _screen.Activate() : null;
        }

        public void SetLetterbox(bool show)
        {
            _letterbox = show;
            if (_effects != null) _effects.Letterbox = show;
        }

        private StereoEffects CreateEffects()
        {
            var effects = new StereoEffects(_logger, _source, _leftCamera, _rightCamera);
            effects.Letterbox = _letterbox;
            effects.ColorEffects = ColorEffects;
            effects.Desktop = _desktop;
            return effects;
        }

        public void Recenter()
        {
            if (!_started || _stopped) return;
            _centered = false;
            _logger.LogInfo("Stereo recenter requested from the VR menu; next valid headset pose resets the table anchor.");
        }

        internal bool Walking { get { return _walking; } }

        internal void BeginWalk()
        {
            BeginWalk(_anchorRotation.eulerAngles.y, "VR seat leaves the table and follows you around the cabin.");
        }

        internal void BeginWalk(float yaw, string reason)
        {
            if (_walking) return;
            _walking = true;
            _walkYaw = Mathf.Repeat(yaw, 360f);
            _centered = false;
            if (_logger != null) _logger.LogInfo(reason);
        }

        internal bool FixedSeat { get { return _fixedSeat; } }

        internal void SetFixedSeat(Vector3 eye, float yaw)
        {
            if (_fixedSeat && (eye - _fixedPosition).sqrMagnitude < 1e-4f && Mathf.Abs(Mathf.DeltaAngle(yaw, _fixedYaw)) < 0.5f) return;
            bool first = !_fixedSeat;
            _fixedSeat = true;
            _fixedPosition = eye;
            _fixedYaw = Mathf.Repeat(yaw, 360f);
            if (first) _glideNext = true;
            _centered = false;
        }

        internal void ClearFixedSeat()
        {
            if (!_fixedSeat) return;
            Vector3 head;
            Quaternion view;
            float unitsPerMeter;
            if (_walking && TryGetHeadPose(out head, out view, out unitsPerMeter))
            {
                Vector3 forward = view * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 1e-4f) _walkYaw = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
            }
            _fixedSeat = false;
            _glideNext = true;
            _centered = false;
        }

        private float GlideWeight()
        {
            float t = (Time.unscaledTime - _glideStart) / GlideSeconds;
            if (t >= 1f) return 0f;
            t = Mathf.Clamp01(t);
            return 1f - t * t * (3f - 2f * t);
        }

        private bool Gliding { get { return GlideWeight() > 0f; } }

        internal void EndWalk()
        {
            if (!_walking) return;
            _walking = false;
            _centered = false;
            if (_logger != null) _logger.LogInfo("VR seat returns to the table.");
        }

        internal void TurnWalk(float degrees)
        {
            if (!_walking || !_centered) return;
            _walkYaw = Mathf.Repeat(_walkYaw + degrees, 360f);
            _anchorRotation = Quaternion.Euler(0f, _walkYaw, 0f);
            Vector3 head = StereoPose.Position(_head);
            _trackingOrigin = new Vector3(head.x, _trackingOrigin.y, head.z);
        }

        public void SetSeatHeight(float meters)
        {
            _seatHeightMeters = Mathf.Clamp(meters, -0.5f, 0.5f);
            ApplySeat();
        }

        public void SetWorldUnitsPerMeter(float unitsPerMeter)
        {
            _worldUnitsPerMeter = Mathf.Clamp(unitsPerMeter, 1f, 40f);
            if (_leftCamera != null) _leftCamera.nearClipPlane = 0.03f * _worldUnitsPerMeter;
            if (_rightCamera != null) _rightCamera.nearClipPlane = 0.03f * _worldUnitsPerMeter;
            ApplySeat();
        }

        internal Vector3 SeatOffset { get { return _seatOffset; } }

        internal float UnitsPerMeter { get { return _worldUnitsPerMeter; } }

        internal Camera SourceCamera { get { return _source; } }

        internal int ExtraEyeLayers;

        internal int BackdropLayers;

        internal float MinFarClip;

        internal bool BlackBackground;

        internal void SetSeatOffset(Vector3 offset)
        {
            _seatOffset = offset;
            ApplySeat();
        }

        private void ApplySeat()
        {
            _anchorPosition = _seatBase + Vector3.up * (_seatHeightMeters * _worldUnitsPerMeter) + _seatOffset + _glideOffset * GlideWeight();
        }

        public void SetRenderScale(float scale)
        {
            float clamped = Mathf.Clamp(scale, MinRenderScale, MaxRenderScale);
            if (Mathf.Approximately(clamped, _renderScale)) return;
            _renderScale = clamped;
            _renderScaleDirty = true;
        }

        private void EyeSize(out int width, out int height)
        {
            width = Mathf.Clamp(Mathf.RoundToInt(_recommendedWidth * _renderScale), 256, MaxEyePixels);
            height = Mathf.Clamp(Mathf.RoundToInt(_recommendedHeight * _renderScale), 256, MaxEyePixels);
        }

        private void ApplyRenderScale()
        {
            if (!_renderScaleDirty) return;
            _renderScaleDirty = false;
            int width, height;
            EyeSize(out width, out height);
            if (_leftTexture != null && _leftTexture.width == width && _leftTexture.height == height) return;
            RenderTexture left = null;
            RenderTexture right;
            try
            {
                left = MakeTexture("InscryptionVR Left Eye", width, height);
                right = MakeTexture("InscryptionVR Right Eye", width, height);
            }
            catch (Exception exception)
            {
                ReleaseTexture(left);
                _logger.LogWarning("Render quality change to " + width + "x" + height + " per eye failed; keeping the current size: " + exception.Message);
                return;
            }
            Retire(_leftTexture);
            Retire(_rightTexture);
            _leftTexture = left;
            _rightTexture = right;
            _leftCamera.targetTexture = left;
            _rightCamera.targetTexture = right;
            _logger.LogInfo("Render quality " + Mathf.RoundToInt(_renderScale * 100f) + "%: " + width + "x" + height + " per eye.");
        }

        private void Retire(RenderTexture texture)
        {
            if (texture == null) return;
            _retired.Add(texture);
            _retiredFrames.Add(Time.frameCount);
        }

        private void ReleaseRetired()
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if (Time.frameCount - _retiredFrames[i] < RetireFrames) continue;
                ReleaseTexture(_retired[i]);
                _retired.RemoveAt(i);
                _retiredFrames.RemoveAt(i);
            }
        }

        public void SetPosePrediction(float frames)
        {
            _posePredictionFrames = Mathf.Clamp(frames, 0f, 3f);
            if (_bridge != null && _started && !_stopped) _bridge.SetPrediction(_posePredictionFrames);
        }

        public bool TryGetHeadPose(out Vector3 position, out Quaternion rotation, out float unitsPerMeter)
        {
            unitsPerMeter = _worldUnitsPerMeter;
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!_started || _stopped || !_centered || _trackingLost) return false;
            Quaternion basis = _anchorRotation * Quaternion.Inverse(_trackingRotation);
            position = _anchorPosition + basis * ((StereoPose.Position(_head) - _trackingOrigin) * unitsPerMeter);
            rotation = basis * StereoPose.Rotation(_head);
            return true;
        }

        public bool TryGetTrackingSpace(out Vector3 anchorPosition, out Quaternion anchorRotation,
            out Vector3 trackingOrigin, out Quaternion trackingYaw, out float unitsPerMeter)
        {
            anchorPosition = _anchorPosition;
            anchorRotation = _anchorRotation;
            trackingOrigin = _trackingOrigin;
            trackingYaw = _trackingRotation;
            unitsPerMeter = _worldUnitsPerMeter;
            return _started && !_stopped && _centered && !_trackingLost;
        }

        private void Update()
        {
            if (_logger == null || _stopped)
                return;
            if (_started) PollEvents();
            if (Input.GetKeyDown(KeyCode.F9))
            {
                StopStereo("F9 emergency stop");
                return;
            }
            if (_started && _suspended)
            {
                TryResume();
                return;
            }
            if (_started) EnforceVsync();
            if (!_started)
            {
                string scene = SceneManager.GetActiveScene().name;
                if (!VrScenes.Renders(scene))
                {
                    if (_waitingScene != scene)
                    {
                        _waitingScene = scene;
                        _logger.LogInfo("Stereo waiting: scene '" + scene + "' has no supported table view. Use desktop menus.");
                    }
                    return;
                }
                _source = FindSource(scene);
                if (_source != null)
                {
                    _sourceScene = scene;
                    try { StartStereo(); }
                    catch (Exception exception) { StopStereo("Initialization failed: " + exception.Message); }
                }
            }
            else if (Input.GetKeyDown(KeyCode.F8))
            {
                _centered = false;
                _logger.LogInfo("Stereo recenter requested; next valid headset pose resets the table anchor.");
            }
        }

        private void StartStereo()
        {
            if (IntPtr.Size != 4 || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("This native stereo bridge requires x86 Direct3D11.");
            _bridge = new StereoBridge();
            uint width, height;
            int result = _bridge.Start(out width, out height);
            if (result != 0)
                throw new InvalidOperationException("Native OpenVR Scene initialization error " + result + ". Start SteamVR and connect the headset first.");
            _started = true;
            if (width == 0 || height == 0 || width > 16384 || height > 16384)
                throw new InvalidOperationException("Runtime returned invalid eye texture dimensions.");
            _recommendedWidth = width;
            _recommendedHeight = height;
            int eyeWidth, eyeHeight;
            EyeSize(out eyeWidth, out eyeHeight);
            _leftTexture = MakeTexture("InscryptionVR Left Eye", eyeWidth, eyeHeight);
            _rightTexture = MakeTexture("InscryptionVR Right Eye", eyeWidth, eyeHeight);
            _leftCamera = MakeCamera("InscryptionVR Left Eye Camera", _leftTexture);
            _rightCamera = MakeCamera("InscryptionVR Right Eye Camera", _rightTexture);
            _logger.LogInfo("VR rendering started from '" + CameraPath(_source.transform) + "': " + eyeWidth + "x" + eyeHeight +
                " per eye, " + _worldUnitsPerMeter + " world units per metre.");
            if (_bridge.SupportsPrediction)
            {
                _bridge.SetPrediction(_posePredictionFrames);
                _logger.LogInfo("Head pose prediction: " + (_posePredictionFrames > 0f ? _posePredictionFrames.ToString("F2") + " display frames ahead." : "off (current pose)."));
            }
            else if (_posePredictionFrames > 0f)
                _logger.LogWarning("Installed native renderer has no pose prediction; rendering with the current head pose. Install the rebuilt renderer for smoother movement.");
            if (_imageEffects && !OnScreen)
            {
                _effects = CreateEffects();
                _logger.LogInfo("Stereo image effects enabled: per-eye color effects plus head-locked fades and blinks; distortion effects stay off.");
            }
            _quality.Apply(_optimization);
            EnforceVsync();
            StartCoroutine(RenderFrames());
        }

        private RenderTexture MakeTexture(string textureName, int width, int height)
        {
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            texture.name = textureName;
            texture.antiAliasing = 1;
            texture.useMipMap = false;
            if (!texture.Create())
            {
                Destroy(texture);
                throw new InvalidOperationException("Could not allocate an eye render texture.");
            }
            return texture;
        }

        private Camera MakeCamera(string cameraName, RenderTexture texture)
        {
            var eyeObject = new GameObject(cameraName);
            eyeObject.transform.SetParent(transform, false);
            var eyeCamera = eyeObject.AddComponent<Camera>();
            ConfigureEye(eyeCamera, texture);
            return eyeCamera;
        }

        private void ConfigureEye(Camera eyeCamera, RenderTexture texture)
        {
            eyeCamera.CopyFrom(_source);
            eyeCamera.enabled = false;
            eyeCamera.stereoTargetEye = StereoTargetEyeMask.None;
            eyeCamera.targetTexture = texture;
            eyeCamera.rect = new Rect(0f, 0f, 1f, 1f);
            eyeCamera.cullingMask |= 1 << VrDialogue.SubtitleLayer | 1 << HologramHands.Layer;
            eyeCamera.nearClipPlane = 0.03f * _worldUnitsPerMeter;
            if (_optimization >= Optimization.LevelBalanced)
            {
                eyeCamera.allowHDR = false;
                eyeCamera.allowMSAA = false;
            }
        }

        private void Suspend(string scene)
        {
            if (_suspended) return;
            _suspended = true;
            _centered = false;
            _hasLastRawPose = false;
            _source = null;
            _tableView = null;
            _seatSpace = null;
            if (_effects != null) { _effects.Dispose(); _effects = null; }
            if (_screen != null) _screen.Deactivate();
            _desktop.Restore();
            RestoreVsync();
            _logger.LogInfo("VR paused: the game left '" + _sourceScene + "' for '" + scene + "'. " + (VrScenes.Renders(scene)
                ? "VR picks up again as soon as its camera is ready." : "Use the monitor there; VR picks up again in the cabin, the sanctum, the finale walk or a menu."));
        }

        private void TryResume()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (!VrScenes.Renders(scene)) return;
            try
            {
                Camera source = FindSource(scene);
                if (source == null) return;
                _source = source;
                _sourceScene = scene;
                _fixedSeat = false;
                _glideNext = false;
                _glideOffset = Vector3.zero;
                ConfigureEye(_leftCamera, _leftTexture);
                ConfigureEye(_rightCamera, _rightTexture);
                if (_imageEffects && !OnScreen) _effects = CreateEffects();
                _suspended = false;
                _centered = false;
                _trackingLost = false;
                _quality.Apply(_optimization);
                EnforceVsync();
                _logger.LogInfo("VR resumed in '" + scene + "' from '" + CameraPath(_source.transform) + "'; re-anchoring the seat.");
            }
            catch (Exception exception)
            {
                StopStereo("Could not resume VR in the cabin: " + exception.Message);
            }
        }

        private IEnumerator RenderFrames()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (!_stopped)
            {
                yield return endOfFrame;
                RenderFrame();
            }
        }

        private void RenderFrame()
        {
            if (!_started || _stopped || _suspended)
                return;
            try
            {
                string scene = SceneManager.GetActiveScene().name;
                if (_source == null || scene != _sourceScene || !VrScenes.Renders(scene))
                {
                    Suspend(scene);
                    return;
                }
                int leftError, rightError;
                uint frames;
                int status = _bridge.GetStatus(out leftError, out rightError, out frames);
                const int focusError = 101;
                if (status < 0 || (leftError != 0 && leftError != focusError) || (rightError != 0 && rightError != focusError))
                    throw new InvalidOperationException("Compositor submission failed: state=" + status + ", left=" + leftError + ", right=" + rightError + ".");
                bool focusLost = leftError == focusError || rightError == focusError;
                if (focusLost != _focusLost)
                {
                    _focusLost = focusLost;
                    _logger.LogInfo(focusLost ? "Stereo waiting for SteamVR focus; render events continue so it can recover."
                        : "SteamVR focus recovered.");
                }
                int valid = _bridge.GetFrame(_head, _leftEye, _rightEye, _leftFrustum, _rightFrustum,
                    _source.nearClipPlane, _source.farClipPlane);
                if (valid < 0)
                    throw new InvalidOperationException("Native pose query failed: " + valid + ".");
                if (valid == 0)
                {
                    _hasLastRawPose = false;
                    if (!_trackingLost)
                    {
                        _trackingLost = true;
                        _logger.LogWarning("Headset tracking unavailable; eye rendering/submission paused.");
                    }
                    return;
                }
                if (!StereoPose.IsPoseValid(_head) || !StereoPose.IsPoseValid(_leftEye) || !StereoPose.IsPoseValid(_rightEye) ||
                    !StereoPose.IsFrustumValid(_leftFrustum) || !StereoPose.IsFrustumValid(_rightFrustum))
                    throw new InvalidOperationException("Runtime returned invalid pose or projection data.");
                _trackingLost = false;
                DetectRuntimeRecenter();
                if (!_loggedFrustum)
                {
                    _loggedFrustum = true;
                    _logger.LogInfo("Eye projection tangents L=" + FormatFrustum(_leftFrustum) + " R=" + FormatFrustum(_rightFrustum) +
                        "; IPD " + (StereoPose.Position(_rightEye) - StereoPose.Position(_leftEye)).magnitude.ToString("F4") + " m.");
                }
                if (_centered && _seatSpace != null)
                {
                    _seatBase = _seatSpace.TransformPoint(_seatLocal);
                    ApplySeat();
                }
                else if (_centered && _glideOffset != Vector3.zero)
                {
                    ApplySeat();
                    if (!Gliding) _glideOffset = Vector3.zero;
                }
                if (!_centered)
                {
                    CaptureSeat();
                    _trackingOrigin = StereoPose.Position(_head);
                    _trackingRotation = Quaternion.Euler(0f, StereoPose.Rotation(_head).eulerAngles.y, 0f);
                    _centered = true;
                    SeatRevision++;
                    _logger.LogInfo("Seat anchored at " + _anchorPosition.ToString("F2") + " (seat height " + (_seatHeightMeters * 100f).ToString("+0;-0;0") + " cm).");
                    LogScaleCalibration();
                }
                ApplyRenderScale();
                if (_optimization >= Optimization.LevelBalanced && !OnScreen && !GameHooks.IsPaused)
                    _desktop.Hide(_source);
                else if (_desktop.Active)
                    _desktop.Restore();
                if (OnScreen) _screen.BeforeRender(_worldUnitsPerMeter);
                if (_dialogue != null) _dialogue.RefreshForRender();
                Action beforeEyes = BeforeEyes;
                if (beforeEyes != null) beforeEyes();
                int extraMask = 0;
                if (_effects != null)
                {
                    _effects.BeforeRender();
                    extraMask = _effects.ExtraCullingMask;
                }
                PerfProbe.Begin(PerfProbe.Eyes);
                RenderEye(_leftCamera, _leftEye, _leftFrustum, _leftTexture, extraMask);
                RenderEye(_rightCamera, _rightEye, _rightFrustum, _rightTexture, extraMask);
                PerfProbe.End(PerfProbe.Eyes);
                _bridge.SetTextures(_leftTexture.GetNativeTexturePtr(), _rightTexture.GetNativeTexturePtr());
                GL.IssuePluginEvent(_bridge.RenderEvent, 1);
                if (_desktopMirror && !OnScreen && !GameHooks.IsPaused) MirrorToDesktop(_leftTexture);
                ReleaseRetired();
                if (frames > 0 && !_acknowledgedFrame)
                {
                    _acknowledgedFrame = true;
                    _logger.LogInfo("SteamVR is receiving frames.");
                }
            }
            catch (Exception exception)
            {
                StopStereo("Stereo failed: " + exception.Message);
            }
        }

        private void RenderEye(Camera camera, float[] eye, float[] frustum, RenderTexture target, int extraMask)
        {
            Quaternion originInverse = Quaternion.Inverse(_trackingRotation);
            Quaternion headRotation = StereoPose.Rotation(_head);
            Vector3 headPosition = StereoPose.Position(_head);
            Vector3 localPosition = originInverse * (headPosition - _trackingOrigin + headRotation * StereoPose.Position(eye));
            camera.transform.position = _anchorPosition + _anchorRotation * (localPosition * _worldUnitsPerMeter);
            camera.transform.rotation = _anchorRotation * originInverse * headRotation * StereoPose.Rotation(eye);
            bool overlay = _effects != null && _effects.HasOverlay;
            camera.cullingMask = _source.cullingMask | extraMask | ExtraEyeLayers | BackdropLayers | 1 << HologramHands.Layer | (overlay ? 0 : 1 << VrDialogue.SubtitleLayer);
            camera.farClipPlane = Mathf.Max(_source.farClipPlane, MinFarClip);
            camera.clearFlags = BlackBackground ? CameraClearFlags.SolidColor : _source.clearFlags;
            camera.backgroundColor = BlackBackground ? Color.black : _source.backgroundColor;
            camera.projectionMatrix = StereoPose.Projection(frustum, camera.nearClipPlane, camera.farClipPlane);
            camera.Render();
            if (overlay)
            {
                _effects.DrawOverlays(target);
                _effects.RenderSubtitlesOver(camera, target);
            }
        }

        private void EnforceVsync()
        {
            if (_stopped) return;
            bool half = HalfRate;
            if (!_disableDesktopVsync && !half)
            {
                RestoreVsync();
                return;
            }
            if (!_vsyncOverridden)
            {
                _savedVsyncCount = QualitySettings.vSyncCount;
                _savedTargetFrameRate = Application.targetFrameRate;
                _vsyncOverridden = true;
                _logger.LogInfo("Desktop vsync off while in VR; SteamVR paces frames.");
            }
            int cap = half ? HalfFrameRate() : UncappedFrameRate;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != cap) Application.targetFrameRate = cap;
            if (cap != _loggedFrameCap)
            {
                _loggedFrameCap = cap;
                _logger.LogInfo(half ? "Half framerate: the game runs at " + cap + " fps on a " + _displayHz.ToString("0.#") + " Hz headset; SteamVR fills in the frames between."
                    : "Full framerate: SteamVR paces frames.");
            }
        }

        private int HalfFrameRate()
        {
            if (Time.unscaledTime >= _nextHzQuery)
            {
                _nextHzQuery = Time.unscaledTime + 5f;
                CVRSystem system = _started && !_stopped ? OpenVR.System : null;
                if (system != null)
                {
                    ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                    float hz = system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
                    if (error == ETrackedPropertyError.TrackedProp_Success && hz >= 40f && hz <= 480f) _displayHz = hz;
                }
            }
            return Mathf.Max(20, Mathf.RoundToInt(_displayHz * 0.5f));
        }

        private void RestoreVsync()
        {
            if (!_vsyncOverridden) return;
            _vsyncOverridden = false;
            _loggedFrameCap = 0;
            QualitySettings.vSyncCount = _savedVsyncCount;
            Application.targetFrameRate = _savedTargetFrameRate;
        }

        private void CaptureSeat()
        {
            if (OnScreen)
            {
                _seatBase = _screen.Seat;
                _seatLocal = _seatBase;
                _seatSpace = null;
                _anchorRotation = Quaternion.identity;
                ApplySeat();
                return;
            }
            Vector3 previous = _seatBase + _glideOffset * GlideWeight();
            bool glide = _glideNext;
            _glideNext = false;
            CaptureSceneSeat();
            if (!glide) return;
            _glideOffset = previous - _seatBase;
            _glideStart = Time.unscaledTime;
            ApplySeat();
        }

        private void CaptureSceneSeat()
        {
            if (_fixedSeat)
            {
                _seatBase = _fixedPosition;
                _seatLocal = _seatBase;
                _seatSpace = null;
                _anchorRotation = Quaternion.Euler(0f, _fixedYaw, 0f);
                ApplySeat();
                _logger.LogInfo("Seat moved to " + _seatBase.ToString("F2") + ", facing " + _fixedYaw.ToString("0") + " degrees.");
                return;
            }
            if (_tableView == null || _tableView.CameraParent == null)
                throw new InvalidOperationException("Table view camera parent unavailable for seated anchor.");
            Transform cameraParent = _tableView.CameraParent;
            bool sanctum = _sourceScene == VrScenes.Sanctum;
            bool finale = _sourceScene == VrScenes.Finale;
            bool standing = sanctum && DiskCardGame.RunState.Run != null && DiskCardGame.RunState.Run.completed;
            bool walking = (_walking || finale) && !sanctum;
            DiskCardGame.View view = walking || standing ? DiskCardGame.View.FirstPerson : !sanctum ? DiskCardGame.View.Default : DiskCardGame.View.SanctumFloorDown;
            DiskCardGame.ViewInfo seat = DiskCardGame.ViewManager.GetViewInfo(view);
            Transform space = cameraParent.parent;
            Vector3 cameraOffset = cameraParent.InverseTransformPoint(_source.transform.position);
            Vector3 localPosition = seat.camPosition + Quaternion.Euler(seat.camRotation) *
                Vector3.Scale(cameraOffset, cameraParent.localScale);
            if (walking)
            {
                localPosition.x = 0f;
                localPosition.z = 0f;
            }
            _seatBase = space == null ? localPosition : space.TransformPoint(localPosition);
            float yaw = walking ? _walkYaw : standing ? _source.transform.eulerAngles.y : seat.camRotation.y + (space == null ? 0f : space.eulerAngles.y);
            _anchorRotation = Quaternion.Euler(0f, yaw, 0f);
            _seatSpace = sanctum || walking ? space : null;
            if (walking)
                _logger.LogInfo((finale ? "Finale" : "Cabin") + " seat: following '" + (space != null ? space.name : "?") + "' at " + _seatBase.ToString("F2") + ", facing " + yaw.ToString("0") + " degrees.");
            _seatLocal = localPosition;
            ApplySeat();
            if (sanctum)
                _logger.LogInfo("Sanctum seat: " + (standing ? "standing where the victory sequence puts you" : "lying on the floor where Leshy makes your death card") +
                    ", facing " + yaw.ToString("0") + " degrees" + (space != null ? " in '" + space.name + "'" : "") + ".");
        }
        private static bool TryGetMapSurfaceHeight(out float height)
        {
            height = 0f;
            var map = UnityEngine.Object.FindObjectOfType<DiskCardGame.GameMap>();
            if (map != null) { height = map.transform.position.y; return true; }
            var board = UnityEngine.Object.FindObjectOfType<DiskCardGame.BoardManager>();
            if (board != null && board.PlayerSlotsCopy != null && board.PlayerSlotsCopy.Count > 0 && board.PlayerSlotsCopy[0] != null)
            {
                height = board.PlayerSlotsCopy[0].transform.position.y;
                return true;
            }
            return false;
        }

        private Camera FindTableCamera()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type viewType = assembly.GetType("DiskCardGame.ViewManager", false);
                if (viewType == null)
                    continue;
                FieldInfo field = viewType.GetField("pixelCamera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    break;
                foreach (UnityEngine.Object view in UnityEngine.Object.FindObjectsOfType(viewType))
                {
                    Camera candidate = field.GetValue(view) as Camera;
                    if (candidate != null && !candidate.orthographic && candidate.gameObject.activeInHierarchy)
                    {
                        _tableView = view as DiskCardGame.ViewManager;
                        return candidate;
                    }
                }
                break;
            }
            return null;
        }

        private void PollEvents()
        {
            CVRSystem system = OpenVR.System;
            if (system == null || _eventSize == 0) return;
            for (int guard = 0; guard < 64 && system.PollNextEvent(ref _event, _eventSize); guard++)
            {
                var type = (EVREventType)_event.eventType;
                if (type != EVREventType.VREvent_SeatedZeroPoseReset && type != EVREventType.VREvent_StandingZeroPoseReset &&
                    type != EVREventType.VREvent_ChaperoneUniverseHasChanged) continue;
                if (_centered) _logger.LogInfo("SteamVR recentered (" + type + "); re-anchoring the table to your new facing.");
                _centered = false;
            }
        }

        private void DetectRuntimeRecenter()
        {
            Vector3 position = StereoPose.Position(_head);
            Quaternion rotation = StereoPose.Rotation(_head);
            float now = Time.realtimeSinceStartup;
            float elapsed = Mathf.Max(now - _lastRawTime, 0.001f);
            if (_hasLastRawPose && _centered && elapsed < RecenterMaxFrameSeconds)
            {
                float moved = Vector3.Distance(position, _lastRawPosition);
                Quaternion delta = rotation * Quaternion.Inverse(_lastRawRotation);
                float angle;
                Vector3 axis;
                delta.ToAngleAxis(out angle, out axis);
                if (angle > 180f) angle = 360f - angle;
                bool yawJump = angle > RecenterJumpDegrees && angle / elapsed > RecenterDegreesPerSecond && Mathf.Abs(axis.y) > 0.9f;
                bool moveJump = moved > RecenterJumpMeters && moved / elapsed > RecenterMetersPerSecond;
                if (moveJump || yawJump)
                {
                    _centered = false;
                    _logger.LogInfo("Headset pose jumped " + moved.ToString("F2") + " m / " + angle.ToString("F0") +
                        " degrees between frames (headset recenter); re-anchoring the table to your new facing.");
                }
            }
            _lastRawPosition = position;
            _lastRawRotation = rotation;
            _lastRawTime = now;
            _hasLastRawPose = true;
        }

        private void LogScaleCalibration()
        {
            float surface;
            if (!TryGetMapSurfaceHeight(out surface)) return;
            float eyeMeters = (_anchorPosition.y - surface) / _worldUnitsPerMeter;
            _logger.LogInfo("Scale check: " + _worldUnitsPerMeter.ToString("F1") + " world units per metre; seated eye is " +
                eyeMeters.ToString("F2") + " m above the board surface (y=" + surface.ToString("F2") + ").");
        }

        private static string FormatFrustum(float[] raw)
        {
            return "(" + raw[0].ToString("F3") + ", " + raw[1].ToString("F3") + ", " + raw[2].ToString("F3") + ", " + raw[3].ToString("F3") + ")";
        }

        private static string CameraPath(Transform cameraTransform)
        {
            string path = cameraTransform.name;
            while (cameraTransform.parent != null)
            {
                cameraTransform = cameraTransform.parent;
                path = cameraTransform.name + "/" + path;
            }
            return path;
        }

        public void StopStereo(string reason)
        {
            if (_stopped)
                return;
            _stopped = true;
            if (_logger != null)
                _logger.LogWarning("Stereo stopped: " + reason);
            RestoreVsync();
            if (_desktop != null) _desktop.Restore();
            if (_quality != null) _quality.Restore();
            if (_effects != null) { _effects.Dispose(); _effects = null; }
            if (_leftCamera != null) Destroy(_leftCamera.gameObject);
            if (_rightCamera != null) Destroy(_rightCamera.gameObject);
            if (_bridge != null && _started)
            {
                _bridge.RequestShutdown();
                GL.IssuePluginEvent(_bridge.RenderEvent, 2);
                var cleanupObject = new GameObject("InscryptionVR Stereo Cleanup");
                DontDestroyOnLoad(cleanupObject);
                cleanupObject.AddComponent<StereoCleanup>().Initialize(_bridge, _leftTexture, _rightTexture, _logger);
            }
            else
            {
                ReleaseTexture(_leftTexture);
                ReleaseTexture(_rightTexture);
            }
            ReleaseRetired();
        }

        private void OnDestroy() { StopStereo("renderer destroyed"); }

        internal static void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            Destroy(texture);
        }
    }

    internal sealed class StereoCleanup : MonoBehaviour
    {
        private StereoBridge _bridge;
        private RenderTexture _left;
        private RenderTexture _right;
        private ManualLogSource _logger;
        internal void Initialize(StereoBridge bridge, RenderTexture left, RenderTexture right, ManualLogSource logger)
        { _bridge = bridge; _left = left; _right = right; _logger = logger; }
        private void Update()
        {
            int leftError, rightError;
            uint frames;
            if (_bridge == null || _bridge.GetStatus(out leftError, out rightError, out frames) != 2) return;
            StereoRenderer.ReleaseTexture(_left);
            StereoRenderer.ReleaseTexture(_right);
            if (_logger != null) _logger.LogInfo("Render-thread stereo shutdown acknowledged; eye textures released.");
            Destroy(gameObject);
        }
    }

    internal sealed class StereoBridge
    {
        private static IntPtr _library;
        private readonly StartDelegate _start;
        private readonly ShutdownDelegate _shutdown;
        private readonly FrameDelegate _frame;
        private readonly TexturesDelegate _textures;
        private readonly StatusDelegate _status;
        private readonly PredictionDelegate _prediction;
        internal readonly IntPtr RenderEvent;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PredictionDelegate(float frames);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartDelegate([MarshalAs(UnmanagedType.LPWStr)] string path, out uint width, out uint height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShutdownDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FrameDelegate([Out] float[] head, [Out] float[] leftEye, [Out] float[] rightEye, [Out] float[] leftFrustum, [Out] float[] rightFrustum, float near, float far);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TexturesDelegate(IntPtr left, IntPtr right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RenderEventDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StatusDelegate(out int left, out int right, out uint frames);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private readonly string _directory;
        internal StereoBridge()
        {
            _directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (_library == IntPtr.Zero)
            {
                string path = Path.GetFullPath(Path.Combine(_directory, "InscryptionVR.Native.dll"));
                if (!File.Exists(path)) throw new FileNotFoundException("Native stereo bridge missing", path);
                _library = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
                if (_library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Native stereo bridge load failed");
            }
            _start = Export<StartDelegate>("ivr_start");
            _shutdown = Export<ShutdownDelegate>("ivr_shutdown");
            _frame = Export<FrameDelegate>("ivr_get_frame");
            _textures = Export<TexturesDelegate>("ivr_set_textures");
            _status = Export<StatusDelegate>("ivr_get_submit_status");
            IntPtr prediction = GetProcAddress(_library, "ivr_set_pose_prediction");
            if (prediction != IntPtr.Zero)
                _prediction = (PredictionDelegate)Marshal.GetDelegateForFunctionPointer(prediction, typeof(PredictionDelegate));
            RenderEvent = Export<RenderEventDelegate>("ivr_get_render_event_func")();
            if (RenderEvent == IntPtr.Zero) throw new InvalidOperationException("Native bridge returned a null render callback");
        }
        internal int Start(out uint width, out uint height) { return _start(Path.GetFullPath(Path.Combine(_directory, "openvr_api.dll")), out width, out height); }
        internal int GetFrame(float[] head, float[] leftEye, float[] rightEye, float[] leftFrustum, float[] rightFrustum, float near, float far)
        { return _frame(head, leftEye, rightEye, leftFrustum, rightFrustum, near, far); }
        internal void SetTextures(IntPtr left, IntPtr right) { _textures(left, right); }
        internal int GetStatus(out int left, out int right, out uint frames) { return _status(out left, out right, out frames); }
        internal void RequestShutdown() { _shutdown(); }
        internal bool SupportsPrediction { get { return _prediction != null; } }
        internal void SetPrediction(float frames) { if (_prediction != null) _prediction(frames); }
        private static T Export<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(_library, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("Native bridge lacks " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }
    }
}
