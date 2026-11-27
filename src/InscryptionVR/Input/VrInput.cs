using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using Valve.VR;

namespace InscryptionVR
{
    internal enum Feel { Tick, Tap, Thud, Thunk, Snap, Reject, Shatter }

    internal sealed class VrHand
    {
        private struct Shot
        {
            internal Feel Feel;
            internal float Start;
            internal float Strength;
            internal bool Emitted;
        }

        private readonly List<Shot> _shots = new List<Shot>(4);
        private float _raw;
        private float _smooth;
        private float _grit;
        private int _smoothFrame = -10;
        private int _gritFrame = -10;
        private HandHold _holdPose;
        private int _holdFrame = -10;

        private const int HistorySize = 10;
        private const float VelocityWindow = 0.1f;

        internal readonly bool IsLeft;
        internal readonly string Name;
        internal readonly ETrackedControllerRole Role;
        internal readonly ControllerTriggerState Grip = new ControllerTriggerState();
        internal readonly ControllerTriggerState Trigger = new ControllerTriggerState();

        internal uint Index = OpenVR.k_unTrackedDeviceIndexInvalid;
        internal int GripAxis = -1;
        internal int TriggerAxis = -1;
        internal int StickAxis = -1;
        internal string ModelName = "";
        internal Vector2 Stick;
        internal Vector2 StickFlick;
        internal bool StickCentered = true;

        internal bool Tracked;
        internal Vector3 RawPosition;
        internal Quaternion Rotation = Quaternion.identity;
        internal Vector3 Position;
        internal Vector3 GripPosition;
        internal Quaternion GripRotation = Quaternion.identity;
        internal Vector3 AimOrigin;
        internal Vector3 AimDirection = Vector3.forward;
        internal Vector3 IndexTip;
        internal bool HasIndexTip;
        internal Vector3 FistPosition;
        internal Quaternion FistRotation = Quaternion.identity;
        internal bool HasFist;
        internal Vector3 LocalIndexTip;
        internal Vector3 LocalFist;
        internal Quaternion LocalFistRotation = Quaternion.identity;
        private int _pinFrame = -10;
        private Quaternion _pinTurn = Quaternion.identity;
        private Vector3 _pinFrom;
        private Vector3 _pinTo;

        internal float GripValue;
        internal float TriggerValue;
        internal bool TriggerDigital;
        internal bool ThumbTouch = true;
        internal bool IndexTouch = true;
        internal bool TouchSensing;
        internal int GripEdge;
        internal float RestGripSince = -1f;
        private int _noAimGrabFrame = -10;
        internal bool RestGripLogged;
        internal int TriggerEdge;
        internal bool MenuPressed;
        internal bool MenuHeld = true;
        internal Vector3 Velocity;

        internal Vector3 LocalGripPosition;
        internal Quaternion LocalGripRotation = Quaternion.identity;
        internal Vector3 LocalAimPosition;
        internal Quaternion LocalAimRotation = Quaternion.identity;
        internal Vector3 LocalHoldPosition;

        internal bool Ready;
        internal bool Claimed;
        internal CardGrabber.CardBody Held;
        internal PileGrab Pile;
        internal bool InBell;
        internal Component HoverCard;
        internal Component Lifted;
        internal Vector3 LocalLowest;
        internal bool HasLowest;
        internal Vector3 LocalKnuckle;
        internal Vector3 LocalForward = Vector3.forward;
        internal Vector3 LocalBack = Vector3.up;
        internal float HandLength = 0.18f;
        internal bool HasFrame;
        internal bool GameHandShown;

        private readonly Vector3[] _positions = new Vector3[HistorySize];
        private readonly float[] _times = new float[HistorySize];
        private int _historyCount;
        private int _historyHead;
        private int _highlightFrame = -10;

        internal VrHand(bool left)
        {
            IsLeft = left;
            Name = left ? "left" : "right";
            Role = left ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
        }

        internal bool Busy { get { return Claimed || Held != null || Pile != null || Lifted != null; } }

        internal bool Highlighted { get { return Time.frameCount - _highlightFrame <= 1; } }

        internal void Highlight() { _highlightFrame = Time.frameCount; }

        internal void BlockAimGrab() { _noAimGrabFrame = Time.frameCount; }

        internal bool AimGrabBlocked { get { return _noAimGrabFrame == Time.frameCount; } }

        internal Vector3 LocalAimDirection { get { return LocalAimRotation * Vector3.forward; } }

        internal Ray PointerRay { get { return new Ray(AimOrigin, AimDirection); } }

        internal Vector3 HoldOffset(float forwardMeters, float scale)
        {
            return LocalAimDirection * (forwardMeters * scale);
        }

        internal Vector3 LocalPinch;

        internal Vector3 PinchOffset(float scale) { return LocalPinch * scale; }

        internal void SetHoldPose(HandHold pose)
        {
            _holdPose = pose;
            _holdFrame = Time.frameCount;
        }

        internal HandHold HoldPose { get { return Time.frameCount - _holdFrame <= 1 ? _holdPose : HandHold.None; } }

        internal void Pin(Vector3 heldPosition, Quaternion heldRotation, Vector3 actualPosition, Quaternion actualRotation)
        {
            _pinTurn = actualRotation * Quaternion.Inverse(heldRotation);
            _pinFrom = heldPosition;
            _pinTo = actualPosition;
            _pinFrame = Time.frameCount;
        }

        internal bool Pinned { get { return _pinFrame == Time.frameCount; } }

        internal Vector3 Displayed(Vector3 point) { return Pinned ? _pinTo + _pinTurn * (point - _pinFrom) : point; }

        internal Quaternion Displayed(Quaternion rotation) { return Pinned ? _pinTurn * rotation : rotation; }

        internal void UpdateModelFrames(float scale)
        {
            IndexTip = GripPosition + GripRotation * (LocalIndexTip * scale);
            FistPosition = GripPosition + GripRotation * (LocalFist * scale);
            FistRotation = GripRotation * LocalFistRotation;
        }

        internal void Pulse(int microseconds)
        {
            _raw = Mathf.Max(_raw, Mathf.Clamp01(microseconds / 3999f));
        }

        internal void Play(Feel feel, float strength = 1f)
        {
            if (!Tracked || strength <= 0f) return;
            if (_shots.Count >= 8) _shots.RemoveAt(0);
            var shot = new Shot();
            shot.Feel = feel;
            shot.Start = Time.unscaledTime;
            shot.Strength = Mathf.Clamp01(strength);
            _shots.Add(shot);
        }

        internal void Rumble(float amplitude)
        {
            if (_smoothFrame != Time.frameCount) { _smoothFrame = Time.frameCount; _smooth = 0f; }
            _smooth = Mathf.Max(_smooth, Mathf.Clamp01(amplitude));
        }

        internal void Grit(float amplitude)
        {
            if (_gritFrame != Time.frameCount) { _gritFrame = Time.frameCount; _grit = 0f; }
            _grit = Mathf.Max(_grit, Mathf.Clamp01(amplitude));
        }

        internal void FlushHaptics(float now, System.Random noise)
        {
            float amplitude = _raw;
            _raw = 0f;
            if (_smoothFrame == Time.frameCount) amplitude = Mathf.Max(amplitude, _smooth);
            if (_gritFrame == Time.frameCount && _grit > 0f) amplitude = Mathf.Max(amplitude, GritSample(_grit, noise));
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                Shot shot = _shots[i];
                float t = shot.Emitted ? now - shot.Start : 0f;
                float length;
                float value = Envelope(shot.Feel, t, noise, out length) * shot.Strength;
                if (shot.Emitted && t >= length)
                {
                    _shots.RemoveAt(i);
                    continue;
                }
                shot.Emitted = true;
                _shots[i] = shot;
                amplitude = Mathf.Max(amplitude, value);
            }
            if (amplitude < 0.03f || !Tracked || Index == OpenVR.k_unTrackedDeviceIndexInvalid) return;
            CVRSystem system = OpenVR.System;
            if (system != null) system.TriggerHapticPulse(Index, 0, (ushort)Mathf.Clamp(200f + amplitude * 3799f, 0f, 3999f));
        }

        internal void ClearHaptics()
        {
            _shots.Clear();
            _raw = 0f;
        }

        private static float GritSample(float strength, System.Random noise)
        {
            if (noise.NextDouble() < 0.35) return 0f;
            return strength * (0.35f + 0.65f * (float)noise.NextDouble());
        }

        private static float Envelope(Feel feel, float t, System.Random noise, out float length)
        {
            switch (feel)
            {
                case Feel.Tick:
                    length = 0.012f;
                    return 0.3f;
                case Feel.Tap:
                    length = 0.04f;
                    return 0.65f * (1f - t / length);
                case Feel.Thud:
                    length = 0.07f;
                    return 0.85f * Mathf.Pow(Mathf.Clamp01(1f - t / length), 1.5f);
                case Feel.Thunk:
                    length = 0.16f;
                    return t < 0.03f ? 1f : Mathf.Exp(-(t - 0.03f) / 0.035f);
                case Feel.Snap:
                    length = 0.02f;
                    return 1f;
                case Feel.Reject:
                    length = 0.1f;
                    return t < 0.016f || (t >= 0.075f && t < 0.091f) ? 0.45f : 0f;
                case Feel.Shatter:
                    length = 0.2f;
                    return t < 0.02f ? 1f : GritSample(0.8f * (1f - t / length), noise);
                default:
                    length = 0f;
                    return 0f;
            }
        }

        internal void Lose()
        {
            Tracked = false;
            HasIndexTip = false;
            HasFist = false;
            HasLowest = false;
            GripEdge = 0;
            TriggerEdge = 0;
            Stick = Vector2.zero;
            StickFlick = Vector2.zero;
            StickCentered = false;
            GripValue = 0f;
            TriggerValue = 0f;
            TriggerDigital = false;
            MenuPressed = false;
            MenuHeld = true;
            Velocity = Vector3.zero;
            _historyCount = 0;
            Grip.Cancel();
            Trigger.Cancel();
            ClearHaptics();
        }

        internal void RecordPosition(float time)
        {
            _historyHead = (_historyHead + 1) % HistorySize;
            _positions[_historyHead] = Position;
            _times[_historyHead] = time;
            if (_historyCount < HistorySize) _historyCount++;
            Velocity = ComputeVelocity();
        }

        internal void ResetHistory() { _historyCount = 0; Velocity = Vector3.zero; }

        private Vector3 ComputeVelocity()
        {
            if (_historyCount < 2) return Vector3.zero;
            int newest = _historyHead;
            float now = _times[newest];
            int oldest = newest;
            for (int i = 1; i < _historyCount; i++)
            {
                int index = (newest - i + HistorySize) % HistorySize;
                if (now - _times[index] > VelocityWindow) break;
                oldest = index;
            }
            float span = now - _times[oldest];
            if (span < 0.005f) return Vector3.zero;
            return (_positions[newest] - _positions[oldest]) / span;
        }
    }

    internal sealed class VrInput
    {
        private const ulong MenuButtons = (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu) | (1UL << (int)EVRButtonId.k_EButton_System);
        private const ulong ThumbButtons = (1UL << (int)EVRButtonId.k_EButton_Axis0) | (1UL << (int)EVRButtonId.k_EButton_A) |
            (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu);
        private const ulong IndexButtons = 1UL << (int)EVRButtonId.k_EButton_Axis1;
        private const ulong GripButton = 1UL << (int)EVRButtonId.k_EButton_Grip;
        private const ulong TriggerButton = 1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger;
        private const float StickCenterRadius = 0.3f;
        private const float StickFlickRadius = 0.7f;

        private static readonly Vector3 FallbackGripPosition = new Vector3(-0.007f, -0.00183f, -0.10195f);
        private static readonly Quaternion FallbackGripRotation = Quaternion.Euler(-20.6f, 0f, 0f);
        private static readonly Vector3 FallbackAimPosition = new Vector3(-0.007f, -0.03895f, -0.0095f);
        private static readonly Quaternion FallbackAimRotation = Quaternion.Euler(39.4f, 0f, 0f);
        private static readonly Vector3 FallbackHoldPosition = new Vector3(-0.043f, -0.0158f, -0.0377f);

        internal readonly VrHand Left = new VrHand(true);
        internal readonly VrHand Right = new VrHand(false);

        private readonly ManualLogSource _logger;
        private readonly StereoRenderer _renderer;
        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly float[] _matrix = new float[12];
        private readonly StringBuilder _text = new StringBuilder(256);
        private int _frame = -1;
        private float _displayFrame = 1f / 90f;
        private float _photons;
        private float _nextDisplayQuery;

        internal bool Active { get; private set; }
        internal Quaternion SeatRotation { get; private set; }
        internal bool InputAvailable { get; private set; }
        internal float Scale { get; private set; }

        internal VrInput(ManualLogSource logger, StereoRenderer renderer)
        {
            _logger = logger;
            _renderer = renderer;
            Scale = 1f;
        }

        internal VrHand Other(VrHand hand) { return hand == Left ? Right : Left; }

        internal bool LeftHanded { get; private set; }

        internal VrHand Main { get { return LeftHanded ? Left : Right; } }

        internal VrHand Off { get { return LeftHanded ? Right : Left; } }

        internal event System.Action HandednessChanged;

        internal void SetLeftHanded(bool leftHanded)
        {
            if (LeftHanded == leftHanded) return;
            LeftHanded = leftHanded;
            _logger.LogInfo(leftHanded ? "Left-handed: cards, the laser and turning are on the left hand; the knife, walking and pause are on the right."
                : "Right-handed: cards, the laser and turning are on the right hand; the knife, walking and pause are on the left.");
            System.Action changed = HandednessChanged;
            if (changed != null) changed();
        }

        internal void Refresh()
        {
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;
            Sample();
        }

        private void Sample()
        {
            Vector3 anchor = Vector3.zero, origin = Vector3.zero;
            Quaternion anchorYaw = Quaternion.identity, trackingYaw = Quaternion.identity;
            float scale = 1f;
            Active = _renderer != null && _renderer.TryGetTrackingSpace(out anchor, out anchorYaw, out origin, out trackingYaw, out scale);
            CVRSystem system = Active ? OpenVR.System : null;
            Active = system != null;
            if (!Active)
            {
                InputAvailable = false;
                Left.Lose();
                Right.Lose();
                return;
            }
            Scale = scale;
            SeatRotation = anchorYaw;
            InputAvailable = system.IsInputAvailable();
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseSeated, PredictionSeconds(system), _poses);
            Quaternion basis = anchorYaw * Quaternion.Inverse(trackingYaw);
            float now = Time.unscaledTime;
            SampleHand(system, Left, anchor, basis, origin, scale, now);
            SampleHand(system, Right, anchor, basis, origin, scale, now);
        }

        private void SampleHand(CVRSystem system, VrHand hand, Vector3 anchor, Quaternion basis, Vector3 origin, float scale, float now)
        {
            hand.GripEdge = 0;
            hand.TriggerEdge = 0;
            hand.StickFlick = Vector2.zero;
            hand.MenuPressed = false;
            uint index = system.GetTrackedDeviceIndexForControllerRole(hand.Role);
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid || index >= _poses.Length) { hand.Lose(); return; }
            TrackedDevicePose_t pose = _poses[index];
            if (!pose.bDeviceIsConnected || !pose.bPoseIsValid) { hand.Lose(); return; }
            CopyMatrix(pose.mDeviceToAbsoluteTracking, _matrix);
            if (!StereoPose.IsPoseValid(_matrix)) { hand.Lose(); return; }
            if (hand.Index != index)
            {
                hand.Index = index;
                Configure(system, hand);
                hand.Grip.Cancel();
                hand.Trigger.Cancel();
                hand.MenuHeld = true;
                hand.ResetHistory();
            }

            hand.RawPosition = anchor + basis * ((StereoPose.Position(_matrix) - origin) * scale);
            hand.Rotation = basis * StereoPose.Rotation(_matrix);
            hand.GripPosition = hand.RawPosition + hand.Rotation * (hand.LocalGripPosition * scale);
            hand.GripRotation = hand.Rotation * hand.LocalGripRotation;
            hand.AimOrigin = hand.RawPosition + hand.Rotation * (hand.LocalAimPosition * scale);
            hand.AimDirection = hand.Rotation * hand.LocalAimDirection;
            hand.Position = hand.RawPosition + hand.Rotation * (hand.LocalHoldPosition * scale);
            hand.UpdateModelFrames(scale);
            hand.Tracked = true;
            hand.RecordPosition(now);

            var state = new VRControllerState_t();
            if (!system.GetControllerState(index, ref state, (uint)Marshal.SizeOf(typeof(VRControllerState_t))))
                state = new VRControllerState_t();
            ulong pressed = state.ulButtonPressed;
            ulong touched = state.ulButtonTouched;

            float gripAnalog = ReadAxis(state, hand.GripAxis);
            bool gripDigital = (pressed & GripButton) != 0;
            hand.GripValue = Mathf.Clamp01(Mathf.Max(gripAnalog, gripDigital ? 1f : 0f));
            hand.GripEdge = hand.Grip.Sample(hand.Grip.IsPressed(gripDigital, gripAnalog));
            WatchRestingGrip(hand, gripAnalog, gripDigital, now);

            float triggerAnalog = ReadAxis(state, hand.TriggerAxis);
            hand.TriggerDigital = (pressed & TriggerButton) != 0;
            hand.TriggerValue = Mathf.Clamp01(Mathf.Max(triggerAnalog, hand.TriggerDigital ? 1f : 0f));
            hand.TriggerEdge = hand.Trigger.Sample(hand.Trigger.IsPressed(hand.TriggerDigital, triggerAnalog));

            hand.Stick = ReadStick(state, hand.StickAxis);
            float tilt = hand.Stick.magnitude;
            if (tilt < StickCenterRadius) hand.StickCentered = true;
            else if (tilt > StickFlickRadius && hand.StickCentered)
            {
                hand.StickCentered = false;
                hand.StickFlick = hand.Stick / tilt;
            }

            if (touched != 0) hand.TouchSensing = true;
            hand.ThumbTouch = !hand.TouchSensing || ((touched | pressed) & ThumbButtons) != 0;
            hand.IndexTouch = !hand.TouchSensing || ((touched | pressed) & IndexButtons) != 0 || hand.TriggerValue > 0.05f;

            ulong menu = pressed & MenuButtons;
            hand.MenuPressed = menu != 0 && !hand.MenuHeld;
            hand.MenuHeld = menu != 0;
        }

        private void WatchRestingGrip(VrHand hand, float analog, bool digital, float now)
        {
            if (hand.RestGripLogged || hand.Grip.Held || (analog < 0.15f && !digital))
            {
                hand.RestGripSince = -1f;
                return;
            }
            if (hand.RestGripSince < 0f) hand.RestGripSince = now;
            if (now - hand.RestGripSince < 3f) return;
            hand.RestGripLogged = true;
            _logger.LogInfo("VR " + hand.Name + " controller: grip reads " + analog.ToString("F2") + (digital ? " with the grip button reported pressed" : "") +
                " while released (axis " + hand.GripAxis + "); the hologram fingers curl that much.");
        }

        private void Configure(CVRSystem system, VrHand hand)
        {
            hand.GripAxis = FindAxis(system, hand.Index, 2);
            hand.TriggerAxis = FindAxis(system, hand.Index, 1);
            if (hand.TriggerAxis < 0) hand.TriggerAxis = 1;
            hand.StickAxis = FindAxisOfType(system, hand.Index, EVRControllerAxisType.k_eControllerAxis_Joystick);
            if (hand.StickAxis < 0) hand.StickAxis = FindAxisOfType(system, hand.Index, EVRControllerAxisType.k_eControllerAxis_TrackPad);
            if (hand.StickAxis < 0) hand.StickAxis = 0;
            hand.StickCentered = false;
            hand.ModelName = DeviceString(system, hand.Index, ETrackedDeviceProperty.Prop_RenderModelName_String);
            float mirror = hand.IsLeft ? -1f : 1f;

            Vector3 position;
            Quaternion rotation;
            bool grip = TryComponent(hand.ModelName, "openxr_grip", out position, out rotation);
            hand.LocalGripPosition = grip ? position : Mirror(FallbackGripPosition, mirror);
            hand.LocalGripRotation = grip ? rotation : FallbackGripRotation;
            bool aim = TryComponent(hand.ModelName, "openxr_aim", out position, out rotation);
            hand.LocalAimPosition = aim ? position : Mirror(FallbackAimPosition, mirror);
            hand.LocalAimRotation = aim ? rotation : FallbackAimRotation;
            bool hold = TryComponent(hand.ModelName, "openxr_pinch", out position, out rotation);
            hand.LocalHoldPosition = hold ? position : Mirror(FallbackHoldPosition, mirror);

            _logger.LogInfo("VR " + hand.Name + " controller: device " + hand.Index + ", model '" + hand.ModelName + "', grip axis " + hand.GripAxis +
                ", trigger axis " + hand.TriggerAxis + ", stick axis " + hand.StickAxis + "; hand frames from " + (grip && aim && hold ? "the controller's render model" :
                "built-in Quest Touch values (grip " + grip + ", aim " + aim + ", pinch " + hold + ")") + ".");
        }

        private bool TryComponent(string model, string component, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            CVRRenderModels models = OpenVR.RenderModels;
            if (models == null || string.IsNullOrEmpty(model)) return false;
            var controller = new VRControllerState_t();
            var mode = new RenderModel_ControllerMode_State_t();
            var state = new RenderModel_ComponentState_t();
            try
            {
                if (!models.GetComponentState(model, component, ref controller, ref mode, ref state)) return false;
            }
            catch (System.Exception exception)
            {
                _logger.LogWarning("Render model component '" + component + "' unavailable: " + exception.Message);
                return false;
            }
            CopyMatrix(state.mTrackingToComponentLocal, _matrix);
            if (!StereoPose.IsPoseValid(_matrix)) return false;
            position = StereoPose.Position(_matrix);
            if (position.sqrMagnitude > 0.3f * 0.3f) return false;
            rotation = StereoPose.Rotation(_matrix);
            return true;
        }

        private string DeviceString(CVRSystem system, uint index, ETrackedDeviceProperty property)
        {
            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            _text.Length = 0;
            system.GetStringTrackedDeviceProperty(index, property, _text, (uint)_text.Capacity, ref error);
            return error == ETrackedPropertyError.TrackedProp_Success ? _text.ToString() : "";
        }

        private float PredictionSeconds(CVRSystem system)
        {
            float frames = _renderer.PredictionFrames;
            if (frames <= 0f) return 0f;
            if (Time.unscaledTime >= _nextDisplayQuery)
            {
                _nextDisplayQuery = Time.unscaledTime + 2f;
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                float frequency = system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
                if (error == ETrackedPropertyError.TrackedProp_Success && frequency >= 20f && frequency <= 1000f) _displayFrame = 1f / frequency;
                float photons = system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref error);
                _photons = error == ETrackedPropertyError.TrackedProp_Success && photons >= 0f && photons <= 0.05f ? photons : 0f;
            }
            float sinceVsync = 0f;
            ulong counter = 0;
            if (!system.GetTimeSinceLastVsync(ref sinceVsync, ref counter)) return 0f;
            if (!(sinceVsync >= 0f && sinceVsync <= _displayFrame)) sinceVsync = 0f;
            return Mathf.Clamp(frames * _displayFrame - sinceVsync + _photons, 0f, 0.1f);
        }

        private static int FindAxis(CVRSystem system, uint index, int occurrence)
        {
            int found = 0;
            for (int axis = 0; axis < 5; axis++)
            {
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                int type = system.GetInt32TrackedDeviceProperty(index, (ETrackedDeviceProperty)((int)ETrackedDeviceProperty.Prop_Axis0Type_Int32 + axis), ref error);
                if (error != ETrackedPropertyError.TrackedProp_Success || type != (int)EVRControllerAxisType.k_eControllerAxis_Trigger) continue;
                found++;
                if (found == occurrence) return axis;
            }
            return -1;
        }

        private static int FindAxisOfType(CVRSystem system, uint index, EVRControllerAxisType wanted)
        {
            for (int axis = 0; axis < 5; axis++)
            {
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                int type = system.GetInt32TrackedDeviceProperty(index, (ETrackedDeviceProperty)((int)ETrackedDeviceProperty.Prop_Axis0Type_Int32 + axis), ref error);
                if (error == ETrackedPropertyError.TrackedProp_Success && type == (int)wanted) return axis;
            }
            return -1;
        }

        private static Vector2 ReadStick(VRControllerState_t state, int axis)
        {
            Vector2 value;
            switch (axis)
            {
                case 0: value = new Vector2(state.rAxis0.x, state.rAxis0.y); break;
                case 1: value = new Vector2(state.rAxis1.x, state.rAxis1.y); break;
                case 2: value = new Vector2(state.rAxis2.x, state.rAxis2.y); break;
                case 3: value = new Vector2(state.rAxis3.x, state.rAxis3.y); break;
                case 4: value = new Vector2(state.rAxis4.x, state.rAxis4.y); break;
                default: return Vector2.zero;
            }
            if (float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.x) || float.IsInfinity(value.y)) return Vector2.zero;
            return Vector2.ClampMagnitude(value, 1f);
        }

        private static float ReadAxis(VRControllerState_t state, int axis)
        {
            switch (axis)
            {
                case 0: return state.rAxis0.x;
                case 1: return state.rAxis1.x;
                case 2: return state.rAxis2.x;
                case 3: return state.rAxis3.x;
                case 4: return state.rAxis4.x;
                default: return 0f;
            }
        }

        private static Vector3 Mirror(Vector3 value, float mirror) { return new Vector3(value.x * mirror, value.y, value.z); }

        internal static void CopyMatrix(HmdMatrix34_t matrix, float[] values)
        {
            values[0] = matrix.m0; values[1] = matrix.m1; values[2] = matrix.m2; values[3] = matrix.m3;
            values[4] = matrix.m4; values[5] = matrix.m5; values[6] = matrix.m6; values[7] = matrix.m7;
            values[8] = matrix.m8; values[9] = matrix.m9; values[10] = matrix.m10; values[11] = matrix.m11;
        }
    }
}
