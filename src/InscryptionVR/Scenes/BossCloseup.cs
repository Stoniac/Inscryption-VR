using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class BossCloseup : MonoBehaviour
    {
        internal const string MoveMe = "Move me";
        internal const string MoveLeshy = "Move Leshy";
        internal const string Off = "Off";
        internal static readonly string[] Modes = { MoveMe, MoveLeshy, Off };

        private const float InSeconds = 0.25f;
        private const float OutSeconds = 0.9f;
        private static readonly FieldInfo EyeBonesField = typeof(LeshyAnimationController).GetField("eyeBones", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private string _mode = MoveMe;
        private float _distanceMeters = 0.7f;
        private bool _failed;

        private bool _active;
        private bool _seen;
        private string _activeMode = Off;
        private Vector3 _target;
        private float _progress;
        private Vector3 _applied;

        private Transform _leshy;
        private bool _leshyMoved;
        private Vector3 _leshyBase;
        private Vector3 _leshySet;
        private readonly List<Transform> _arms = new List<Transform>();
        private readonly List<Vector3> _armBase = new List<Vector3>();
        private readonly List<Vector3> _armSet = new List<Vector3>();
        private readonly List<Vector3> _armWorld = new List<Vector3>();
        private bool _loggedArms;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, string mode, float distanceMeters)
        {
            _logger = logger;
            _renderer = renderer;
            SetMode(mode);
            SetDistance(distanceMeters);
        }

        public void SetMode(string mode)
        {
            _mode = Array.IndexOf(Modes, mode) >= 0 ? mode : MoveMe;
        }

        public void SetDistance(float meters)
        {
            _distanceMeters = Mathf.Clamp(meters, 0.2f, 3f);
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                try { ReleaseAll(); } catch (Exception) { }
                _logger.LogWarning("Boss closeup disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            if (!VrScenes.InCabin)
            {
                ReleaseAll();
                _seen = false;
                return;
            }
            bool closeup = !_renderer.Walking && InCloseupView();
            if (_active && (!closeup || _activeMode != _mode)) _active = false;
            if (!closeup) _seen = false;
            bool paused = GameHooks.IsPaused;
            if (!_active && closeup && !_seen && _mode != Off && !paused && InBossIntro()) Begin();

            if (!paused)
            {
                float step = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                _progress = _active ? Mathf.MoveTowards(_progress, 1f, step / InSeconds) : Mathf.MoveTowards(_progress, 0f, step / OutSeconds);
            }
            Vector3 offset = _target * Mathf.SmoothStep(0f, 1f, _progress);
            if (!_active && _progress <= 0f) offset = Vector3.zero;
            Apply(offset);
            if (!_active && _progress <= 0f)
            {
                _activeMode = Off;
                _target = Vector3.zero;
            }
        }

        private static bool InCloseupView()
        {
            ViewManager view = Singleton<ViewManager>.Instance;
            return view != null && view.CurrentView == View.BossCloseup;
        }

        private static bool InBossIntro()
        {
            TurnManager turns = Singleton<TurnManager>.Instance;
            return turns != null && turns.IsSetupPhase && turns.Opponent is Part1BossOpponent;
        }

        private void Begin()
        {
            Vector3 head;
            Quaternion rotation;
            float unitsPerMeter;
            if (!_renderer.TryGetHeadPose(out head, out rotation, out unitsPerMeter)) return;
            Vector3 face;
            if (!TryGetFace(out face)) return;
            if (_activeMode != _mode && _applied != Vector3.zero)
            {
                ReleaseAll();
                _progress = 0f;
            }
            if (_activeMode == MoveMe) head -= _applied;
            else if (_activeMode == MoveLeshy) face -= _applied;
            _activeMode = _mode;
            _active = true;
            _seen = true;
            Vector3 between = face - head;
            float distance = between.magnitude;
            float gap = _distanceMeters * unitsPerMeter;
            Vector3 travel = distance > gap ? between * ((distance - gap) / distance) : Vector3.zero;
            _target = _mode == MoveMe ? travel : -travel;
            _logger.LogInfo("Boss closeup: " + (_mode == MoveMe ? "moving you toward Leshy" : "bringing Leshy to you") + ", " +
                (distance / unitsPerMeter).ToString("F2") + " m face to face, " + (travel.magnitude / unitsPerMeter).ToString("F2") + " m travel, stopping at " +
                _distanceMeters.ToString("F2") + " m.");
        }

        private static bool TryGetFace(out Vector3 face)
        {
            face = Vector3.zero;
            LeshyAnimationController leshy = LeshyAnimationController.Instance;
            if (leshy != null && EyeBonesField != null)
            {
                List<Transform> bones = EyeBonesField.GetValue(leshy) as List<Transform>;
                if (bones != null)
                {
                    int count = 0;
                    Vector3 sum = Vector3.zero;
                    foreach (Transform bone in bones)
                    {
                        if (bone == null) continue;
                        sum += bone.position;
                        count++;
                    }
                    if (count > 0)
                    {
                        face = sum / count;
                        return true;
                    }
                }
            }
            OpponentAnimationController opponent = OpponentAnimationController.Instance;
            if (opponent == null || opponent.Head == null) return false;
            face = opponent.Head.position;
            return true;
        }

        private void Apply(Vector3 offset)
        {
            if (_activeMode == MoveMe)
            {
                if (offset != _applied || _renderer.SeatOffset != offset) _renderer.SetSeatOffset(offset);
            }
            else if (_activeMode == MoveLeshy)
            {
                MoveLeshyBy(offset);
            }
            _applied = offset;
        }

        private void MoveLeshyBy(Vector3 offset)
        {
            OpponentAnimationController opponent = OpponentAnimationController.Instance;
            Transform root = opponent != null ? opponent.transform : null;
            if (root != _leshy)
            {
                RestoreLeshy();
                _leshy = root;
                FindArms(opponent);
            }
            if (_leshy == null) return;
            RestoreLeshy();
            if (offset.sqrMagnitude < 1e-10f) return;
            _leshyBase = _leshy.position;
            for (int i = 0; i < _arms.Count; i++)
            {
                Transform arm = _arms[i];
                _armBase[i] = arm != null ? arm.localPosition : Vector3.zero;
                _armWorld[i] = arm != null ? arm.position : Vector3.zero;
            }
            _leshy.position = _leshyBase + offset;
            _leshySet = _leshy.position;
            for (int i = 0; i < _arms.Count; i++)
            {
                Transform arm = _arms[i];
                if (arm == null) continue;
                arm.position = _armWorld[i];
                _armSet[i] = arm.localPosition;
            }
            _leshyMoved = true;
        }

        private void FindArms(OpponentAnimationController opponent)
        {
            _arms.Clear();
            _armBase.Clear();
            _armSet.Clear();
            _armWorld.Clear();
            LeshyAnimationController leshy = opponent as LeshyAnimationController;
            if (leshy == null || _leshy == null) return;
            AddArm(leshy.LeftArm);
            AddArm(leshy.RightArm);
            if (_loggedArms) return;
            _loggedArms = true;
            _logger.LogInfo("Boss closeup: Leshy root '" + _leshy.name + "', " + _arms.Count + " arm(s) under it held in place" +
                (leshy.LeftArm != null ? "; left arm '" + leshy.LeftArm.name + "'" + (leshy.LeftArm.transform.IsChildOf(_leshy) ? "" : " (separate)") : "") +
                (leshy.RightArm != null ? "; right arm '" + leshy.RightArm.name + "'" + (leshy.RightArm.transform.IsChildOf(_leshy) ? "" : " (separate)") : "") + ".");
        }

        private void AddArm(OpponentArmController arm)
        {
            if (arm == null) return;
            Transform transform = arm.transform;
            if (transform == _leshy || !transform.IsChildOf(_leshy) || _arms.Contains(transform)) return;
            _arms.Add(transform);
            _armBase.Add(transform.localPosition);
            _armSet.Add(transform.localPosition);
            _armWorld.Add(transform.position);
        }

        private void RestoreLeshy()
        {
            if (!_leshyMoved) return;
            _leshyMoved = false;
            if (_leshy == null) return;
            if (_leshy.position == _leshySet) _leshy.position = _leshyBase;
            for (int i = 0; i < _arms.Count; i++)
            {
                Transform arm = _arms[i];
                if (arm != null && arm.localPosition == _armSet[i]) arm.localPosition = _armBase[i];
            }
        }

        private void ReleaseAll()
        {
            RestoreLeshy();
            if (_renderer != null && _renderer.SeatOffset != Vector3.zero) _renderer.SetSeatOffset(Vector3.zero);
            _applied = Vector3.zero;
            _target = Vector3.zero;
            _progress = 0f;
            _active = false;
            _activeMode = Off;
        }

        private void OnDestroy()
        {
            try { ReleaseAll(); } catch (Exception) { }
        }
    }
}
