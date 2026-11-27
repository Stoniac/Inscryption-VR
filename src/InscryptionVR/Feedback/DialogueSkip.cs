using System;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class DialogueSkip : MonoBehaviour
    {
        private const float WaveMetersPerSecond = 0.9f;
        private const float FlapRadiansPerSecond = 4f;
        private const float SpinSmoothingSeconds = 0.04f;
        private const int Axes = 5;
        private const int WaveReversals = 2;
        private const float WaveWindowSeconds = 1f;
        private const float StrokeGapSeconds = 0.45f;
        private const float BelowEyeMeters = 0.5f;
        private const float QuietSeconds = 0.6f;
        private const float MaxSkipSeconds = 90f;
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo AudioField = typeof(TextDisplayer).GetField("currentAudio", Members);
        private static DialogueSkip _instance;

        private sealed class Waver
        {
            internal readonly int[] Sign = new int[Axes];
            internal readonly float[] LastStroke = new float[Axes];
            internal readonly int[] Reversals = new int[Axes];
            internal readonly float[] FirstReversal = new float[Axes];
            internal Quaternion LastRotation = Quaternion.identity;
            internal bool HasRotation;
            internal float LastSample;
            internal Vector3 Spin;
        }

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private readonly Waver _left = new Waver();
        private readonly Waver _right = new Waver();
        private bool _enabled;
        private bool _skipping;
        private bool _throughSetup;
        private bool _wavedEvent;
        private float _skipStart;
        private float _lastDialogue;
        private bool _failed;

        internal static bool Active { get { return _instance != null && _instance._skipping; } }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, bool enabled)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _enabled = enabled;
            _instance = this;
        }

        internal void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) Stop("turned off");
        }

        private void Update()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                _skipping = false;
                _logger.LogError("Wave-to-skip dialogue disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            TextDisplayer display = Singleton<TextDisplayer>.Instance;
            bool talking = display != null && (display.Displaying || display.PlayingEvent);
            float now = Time.unscaledTime;
            if (talking) _lastDialogue = now;

            if (_skipping)
            {
                Skip(display, talking, now);
                return;
            }
            if (!_enabled || !talking || Time.timeScale <= 0f || _input == null) return;
            _input.Refresh();
            Vector3 eye;
            Quaternion view;
            float scale;
            if (_renderer == null || !_renderer.TryGetHeadPose(out eye, out view, out scale)) return;
            if (scale <= 0f) scale = _input.Scale;
            VrHand hand = null;
            if (Waved(_input.Left, _left, eye, view, scale, now)) hand = _input.Left;
            else if (Waved(_input.Right, _right, eye, view, scale, now)) hand = _input.Right;
            if (hand == null) return;
            hand.Play(Feel.Snap, 0.8f);
            Begin(display, now);
        }

        private bool Waved(VrHand hand, Waver waver, Vector3 eye, Quaternion view, float scale, float now)
        {
            bool flapped = Flap(hand, waver);
            if (!hand.Ready || hand.Busy || hand.Grip.Held || hand.Pinned || hand.Position.y < eye.y - BelowEyeMeters * scale)
            {
                Reset(waver);
                return false;
            }
            Vector3 right = view * Vector3.right;
            right.y = 0f;
            Vector3 forward = view * Vector3.forward;
            forward.y = 0f;
            if (right.sqrMagnitude < 1e-6f || forward.sqrMagnitude < 1e-6f) return false;
            right.Normalize();
            forward.Normalize();
            bool waved = Stroke(waver, 0, Vector3.Dot(hand.Velocity, right) / scale, WaveMetersPerSecond, now);
            waved |= Stroke(waver, 1, Vector3.Dot(hand.Velocity, forward) / scale, WaveMetersPerSecond, now);
            if (flapped)
            {
                for (int axis = 0; axis < 3; axis++)
                    waved |= Stroke(waver, 2 + axis, waver.Spin[axis], FlapRadiansPerSecond, now);
            }
            if (!waved) return false;
            Reset(waver);
            return true;
        }

        private static bool Flap(VrHand hand, Waver waver)
        {
            float dt = Time.unscaledDeltaTime;
            if (!hand.Tracked || dt <= 0f)
            {
                waver.HasRotation = false;
                waver.Spin = Vector3.zero;
                return false;
            }
            Quaternion rotation = hand.GripRotation;
            float now = Time.unscaledTime;
            bool stale = now - waver.LastSample > 0.1f;
            waver.LastSample = now;
            if (!waver.HasRotation || stale)
            {
                waver.LastRotation = rotation;
                waver.HasRotation = true;
                waver.Spin = Vector3.zero;
                return false;
            }
            Quaternion delta = Quaternion.Inverse(waver.LastRotation) * rotation;
            waver.LastRotation = rotation;
            float angle;
            Vector3 axis;
            delta.ToAngleAxis(out angle, out axis);
            if (angle > 180f) angle -= 360f;
            Vector3 spin = Mathf.Abs(angle) < 1e-3f || float.IsNaN(axis.x) ? Vector3.zero : axis * (angle * Mathf.Deg2Rad / dt);
            waver.Spin = Vector3.Lerp(waver.Spin, spin, Mathf.Clamp01(dt / SpinSmoothingSeconds));
            return true;
        }

        private static bool Stroke(Waver waver, int axis, float speed, float threshold, float now)
        {
            if (now - waver.LastStroke[axis] > StrokeGapSeconds)
            {
                waver.Sign[axis] = 0;
                waver.Reversals[axis] = 0;
            }
            if (Mathf.Abs(speed) < threshold) return false;
            int sign = speed > 0f ? 1 : -1;
            waver.LastStroke[axis] = now;
            if (sign == waver.Sign[axis]) return false;
            if (waver.Sign[axis] != 0)
            {
                if (waver.Reversals[axis] == 0 || now - waver.FirstReversal[axis] > WaveWindowSeconds)
                {
                    waver.Reversals[axis] = 0;
                    waver.FirstReversal[axis] = now;
                }
                waver.Reversals[axis]++;
            }
            waver.Sign[axis] = sign;
            return waver.Reversals[axis] >= WaveReversals;
        }

        private static void Reset(Waver waver)
        {
            for (int axis = 0; axis < Axes; axis++)
            {
                waver.Sign[axis] = 0;
                waver.Reversals[axis] = 0;
            }
        }

        private void Begin(TextDisplayer display, float now)
        {
            _skipping = true;
            _skipStart = now;
            TurnManager turns = Singleton<TurnManager>.Instance;
            _throughSetup = turns != null && turns.IsSetupPhase && !turns.GameEnding && !turns.GameEnded;
            _wavedEvent = display != null && display.PlayingEvent;
            _logger.LogInfo("You waved Leshy away; skipping the rest of this dialogue" + (_throughSetup ? " until the battle starts." : "."));
            Skip(display, true, now);
        }

        private void Skip(TextDisplayer display, bool talking, float now)
        {
            if (now - _skipStart > MaxSkipSeconds)
            {
                Stop("timed out");
                return;
            }
            if (_throughSetup)
            {
                TurnManager turns = Singleton<TurnManager>.Instance;
                if (turns == null || !turns.IsSetupPhase || turns.GameEnded) _throughSetup = false;
            }
            if (!_throughSetup)
            {
                if (_wavedEvent && (display == null || !display.PlayingEvent))
                {
                    Stop("the waved dialogue ended");
                    return;
                }
                if (!talking && now - _lastDialogue > QuietSeconds)
                {
                    Stop("dialogue over");
                    return;
                }
            }
            if (display == null || !talking) return;
            if (display.Displaying)
            {
                Silence(display);
                if (display.CurrentAdvanceMode == TextDisplayer.MessageAdvanceMode.Auto) display.Clear();
            }
            display.Interrupt();
        }

        private static void Silence(TextDisplayer display)
        {
            if (AudioField == null) return;
            AudioSource voice = AudioField.GetValue(display) as AudioSource;
            if (voice != null && voice.isPlaying) voice.Stop();
        }

        private void Stop(string reason)
        {
            if (!_skipping) return;
            _skipping = false;
            _throughSetup = false;
            _wavedEvent = false;
            _logger.LogInfo("Dialogue skip ended (" + reason + ") after " + (Time.unscaledTime - _skipStart).ToString("0.0") + " s.");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
