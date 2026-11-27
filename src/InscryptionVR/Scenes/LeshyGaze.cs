using System;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class LeshyGaze : MonoBehaviour
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo LookTargetField = typeof(OpponentAnimationController).GetField("lookTarget", Members);
        private static readonly FieldInfo DefaultTargetField = typeof(OpponentAnimationController).GetField("defaultLookTarget", Members);
        private static readonly FieldInfo LockedField = typeof(OpponentAnimationController).GetField("headLookLocked", Members);

        private const float HoldSeconds = 0.6f;
        private const float FaceDegrees = 8f;
        private const float FaceDwellSeconds = 0.5f;
        private const float FaceHoldSeconds = 1.2f;
        private const float FixRadiusMeters = 0.07f;
        private const float TableDwellSeconds = 0.8f;
        private const float TableGapSeconds = 0.4f;
        private const float TableMaxSeconds = 6f;
        private const float TableCooldownSeconds = 4f;
        private const float SpotFollowRate = 8f;
        private const float MaxGazeMeters = 3f;

        private enum Mode { None, Talk, Face, Table }

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private bool _enabled;
        private bool _notice;
        private bool _watch;
        private bool _watching;
        private bool _loggedWatch;
        private bool _failed;
        private GameObject _eye;
        private GameObject _spot;
        private OpponentAnimationController _owner;
        private float _lastTalk = -10f;
        private bool _logged;
        private bool _loggedFace;
        private bool _loggedTable;

        private float _faceSince = -1f;
        private float _faceLast = -10f;
        private bool _faceEngaged;

        private Vector3 _fixPoint;
        private float _fixSince = -1f;
        private float _tableLast = -10f;
        private float _tableStart = -1f;
        private float _tableCooldownUntil;
        private bool _tableEngaged;
        private Vector3 _spotPoint;

        internal static bool Available { get { return LookTargetField != null && DefaultTargetField != null && LockedField != null; } }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, bool enabled, bool notice, bool watchSacrifice)
        {
            _logger = logger;
            _renderer = renderer;
            _enabled = enabled;
            _notice = notice;
            _watch = watchSacrifice;
            if (!Available) _logger.LogWarning("Leshy's gaze unavailable: OpponentAnimationController look fields not found.");
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!Any) LetGo();
        }

        public void SetNotice(bool notice)
        {
            _notice = notice;
            if (!notice) ResetStare();
            if (!Any) LetGo();
        }

        public void SetWatchSacrifice(bool watch)
        {
            _watch = watch;
            if (!Any) LetGo();
        }

        private bool Any { get { return _enabled || _notice || _watch; } }

        private void LateUpdate()
        {
            if (_logger == null || _failed || !Available) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                try { LetGo(); } catch (Exception) { }
                _logger.LogWarning("Leshy's gaze disabled after an error: " + exception);
            }
        }

        private void Tick()
        {
            Vector3 head = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            float scale = 1f;
            OpponentAnimationController opponent = OpponentAnimationController.Instance;
            bool ready = Any && VrScenes.InCabin && !_renderer.Walking && !GameHooks.IsPaused && Time.timeScale > 0f &&
                opponent != null && opponent.isActiveAndEnabled && _renderer.TryGetHeadPose(out head, out rotation, out scale);
            if (!ready)
            {
                ResetStare();
                LetGo();
                return;
            }
            if (_eye == null)
            {
                _eye = new GameObject("InscryptionVR Player Eyes");
                DontDestroyOnLoad(_eye);
            }
            _eye.transform.SetPositionAndRotation(head, rotation);
            if (_owner != null && _owner != opponent) LetGo();

            if (_enabled && Talking()) _lastTalk = Time.unscaledTime;
            Mode mode = _enabled && Time.unscaledTime - _lastTalk < HoldSeconds ? Mode.Talk : Mode.None;
            if (_notice) UpdateStare(opponent, scale);
            else ResetStare();
            PlayableCard victim = _watch ? SacrificeGaze.Looked : null;
            if (mode == Mode.None && victim != null)
            {
                Vector3 spot = victim.transform.position;
                _spotPoint = _watching ? Vector3.Lerp(_spotPoint, spot, 1f - Mathf.Exp(-SpotFollowRate * Time.unscaledDeltaTime)) : spot;
                _watching = true;
                mode = Mode.Table;
            }
            else _watching = false;
            if (mode == Mode.None && _faceEngaged) mode = Mode.Face;
            if (mode == Mode.None && _tableEngaged) mode = Mode.Table;

            if (mode == Mode.None)
            {
                Release(opponent);
                return;
            }
            Transform target = _eye.transform;
            if (mode == Mode.Table)
            {
                if (_spot == null)
                {
                    _spot = new GameObject("InscryptionVR Player Gaze Spot");
                    DontDestroyOnLoad(_spot);
                }
                _spot.transform.position = _spotPoint;
                target = _spot.transform;
            }
            if (Take(opponent, target))
            {
                if (_watching && !_loggedWatch)
                {
                    _loggedWatch = true;
                    _logger.LogInfo("Leshy watches the creature you are thinking of sacrificing.");
                }
                else if (!_watching) LogMode(mode);
            }
            else if (mode == Mode.Table && !IsMine(LookTargetField.GetValue(opponent) as Transform)) _tableEngaged = false;
        }

        private void UpdateStare(OpponentAnimationController opponent, float scale)
        {
            float now = Time.unscaledTime;
            Vector3 origin, direction;
            bool eyes = EyeGaze.TryGetWorldGaze(out origin, out direction);

            bool onFace = false;
            if (eyes && opponent.Head != null)
            {
                Vector3 toFace = opponent.Head.position - origin;
                onFace = toFace.sqrMagnitude > 1e-6f && Vector3.Angle(direction, toFace) < FaceDegrees;
            }
            if (onFace)
            {
                if (_faceSince < 0f) _faceSince = now;
                _faceLast = now;
                if (now - _faceSince >= FaceDwellSeconds) _faceEngaged = true;
            }
            else
            {
                _faceSince = -1f;
                if (_faceEngaged && now - _faceLast > FaceHoldSeconds) _faceEngaged = false;
            }

            Vector3 point = Vector3.zero;
            bool onTable = eyes && !onFace && TablePoint(origin, direction, scale, out point);
            if (onTable)
            {
                _tableLast = now;
                float radius = FixRadiusMeters * scale;
                if (_fixSince < 0f || (point - _fixPoint).sqrMagnitude > radius * radius)
                {
                    _fixPoint = point;
                    _fixSince = now;
                }
                if (_tableEngaged)
                    _spotPoint = Vector3.Lerp(_spotPoint, point, 1f - Mathf.Exp(-SpotFollowRate * Time.unscaledDeltaTime));
                else if (now >= _tableCooldownUntil && now - _fixSince >= TableDwellSeconds)
                {
                    _tableEngaged = true;
                    _tableStart = now;
                    _spotPoint = _fixPoint;
                }
            }
            else _fixSince = -1f;

            if (_tableEngaged && (now - _tableLast > TableGapSeconds || now - _tableStart > TableMaxSeconds))
            {
                _tableEngaged = false;
                _tableCooldownUntil = now + TableCooldownSeconds;
                _fixSince = -1f;
            }
        }

        private static bool TablePoint(Vector3 origin, Vector3 direction, float scale, out Vector3 point)
        {
            point = Vector3.zero;
            CardGrabber grabber = CardGrabber.Instance;
            if (grabber == null || !grabber.HaveTable || direction.y > -0.05f) return false;
            float distance = (grabber.TableY - origin.y) / direction.y;
            if (distance <= 0f || distance > MaxGazeMeters * scale) return false;
            point = origin + direction * distance;
            return grabber.InsideTable(point);
        }

        private void ResetStare()
        {
            _faceSince = -1f;
            _faceEngaged = false;
            _fixSince = -1f;
            if (_tableEngaged) _tableCooldownUntil = Time.unscaledTime + TableCooldownSeconds;
            _tableEngaged = false;
        }

        private bool IsMine(Transform current)
        {
            return current != null && ((_eye != null && current == _eye.transform) || (_spot != null && current == _spot.transform));
        }

        private bool Take(OpponentAnimationController opponent, Transform target)
        {
            Transform current = LookTargetField.GetValue(opponent) as Transform;
            if (current == target) return true;
            if (!IsMine(current))
            {
                Transform fallback = DefaultTargetField.GetValue(opponent) as Transform;
                if (current != fallback || (bool)LockedField.GetValue(opponent)) return false;
            }
            opponent.SetLookTarget(target, Vector3.zero);
            _owner = opponent;
            return true;
        }

        private void Release(OpponentAnimationController opponent)
        {
            if (IsMine(LookTargetField.GetValue(opponent) as Transform)) opponent.ClearLookTarget();
            _owner = null;
        }

        private void LogMode(Mode mode)
        {
            if (mode == Mode.Talk && !_logged)
            {
                _logged = true;
                _logger.LogInfo("Leshy looks at you while he talks.");
            }
            else if (mode == Mode.Face && !_loggedFace)
            {
                _loggedFace = true;
                _logger.LogInfo("Leshy noticed you staring at him and looks back.");
            }
            else if (mode == Mode.Table && !_loggedTable)
            {
                _loggedTable = true;
                _logger.LogInfo("Leshy follows your eyes to " + _spotPoint.ToString("F2") + " on the table.");
            }
        }

        private static bool Talking()
        {
            TextDisplayer text = Singleton<TextDisplayer>.Instance;
            return text != null && text.Displaying;
        }

        private void LetGo()
        {
            OpponentAnimationController owner = _owner;
            _owner = null;
            if (owner == null || LookTargetField == null) return;
            if (IsMine(LookTargetField.GetValue(owner) as Transform)) owner.ClearLookTarget();
        }

        private void OnDestroy()
        {
            try { LetGo(); } catch (Exception) { }
            if (_eye != null) Destroy(_eye);
            if (_spot != null) Destroy(_spot);
        }
    }
}
