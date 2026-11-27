using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    internal static class VrSounds
    {
        internal static void Play(string soundId, MixerGroup group, Vector3 position, float volume)
        {
            AudioController audio = AudioController.Instance;
            if (audio == null) return;
            AudioSource source = audio.PlaySound3D(soundId, group, position, volume);
            if (source != null) source.spatialBlend = 1f;
        }

        private static List<AudioSource> Active() { return GameHooks.ActiveSfx(); }

        internal static void Snapshot(HashSet<int> ids)
        {
            ids.Clear();
            List<AudioSource> sources = Active();
            if (sources == null) return;
            for (int i = 0; i < sources.Count; i++) if (sources[i] != null) ids.Add(sources[i].GetInstanceID());
        }

        internal static int ScaleNew(HashSet<int> before, float factor)
        {
            List<AudioSource> sources = Active();
            if (sources == null) return 0;
            int count = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                AudioSource source = sources[i];
                if (source == null || before.Contains(source.GetInstanceID())) continue;
                source.volume *= factor;
                count++;
            }
            return count;
        }
    }

    public sealed class SpatialAudio : MonoBehaviour
    {
        private const float HandSoundMeters = 0.3f;
        private const float ScanSeconds = 0.5f;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private VrInput _input;
        private bool _enabled;
        private bool _failed;
        private GameObject _head;
        private AudioListener _listener;
        private readonly List<AudioListener> _muted = new List<AudioListener>();
        private readonly HashSet<int> _known = new HashSet<int>();
        private readonly HashSet<int> _present = new HashSet<int>();
        private float _nextScan;
        private bool _announced;

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer, VrInput input, bool enabled)
        {
            _logger = logger;
            _renderer = renderer;
            _input = input;
            _enabled = enabled;
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled) Restore();
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                try { Restore(); } catch (Exception) { }
                _logger.LogWarning("Head-tracked audio disabled after an error; the game's own listener is back: " + exception);
            }
        }

        private void Tick()
        {
            Vector3 head;
            Quaternion rotation;
            float unitsPerMeter;
            if (!_enabled || !VrScenes.InVrScene || !_renderer.TryGetHeadPose(out head, out rotation, out unitsPerMeter))
            {
                Restore();
                return;
            }
            if (_head == null)
            {
                _head = new GameObject("InscryptionVR Head Listener");
                DontDestroyOnLoad(_head);
                _listener = _head.AddComponent<AudioListener>();
                _listener.enabled = false;
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanSeconds;
                MuteGameListeners();
            }
            _head.transform.SetPositionAndRotation(head, rotation);
            if (!_listener.enabled) _listener.enabled = true;
            SpatializeHandSounds(unitsPerMeter);
        }

        private void MuteGameListeners()
        {
            AudioListener[] listeners = FindObjectsOfType<AudioListener>();
            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener listener = listeners[i];
                if (listener == null || listener == _listener || !listener.enabled) continue;
                listener.enabled = false;
                if (!_muted.Contains(listener)) _muted.Add(listener);
                if (!_announced)
                {
                    _announced = true;
                    _logger.LogInfo("Audio now follows the headset: the game's listener on '" + listener.name + "' is paused while in VR" + Filters(listener.gameObject) + ".");
                }
            }
        }

        private static string Filters(GameObject owner)
        {
            var names = new StringBuilder();
            Behaviour[] behaviours = owner.GetComponents<Behaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null || !(behaviour is AudioBehaviour) || behaviour is AudioListener) continue;
                names.Append(names.Length == 0 ? "" : ", ").Append(behaviour.GetType().Name);
            }
            return names.Length == 0 ? "" : " (it also carries " + names + ", which stay on the old listener)";
        }

        private void SpatializeHandSounds(float unitsPerMeter)
        {
            List<AudioSource> sources = GameHooks.ActiveSfx();
            if (sources == null) return;
            _input.Refresh();
            VrHand left = _input.Left, right = _input.Right;
            float reach = HandSoundMeters * unitsPerMeter;
            float limit = reach * reach;
            _present.Clear();
            for (int i = 0; i < sources.Count; i++)
            {
                AudioSource source = sources[i];
                if (source == null) continue;
                int id = source.GetInstanceID();
                _present.Add(id);
                if (!_known.Add(id) || source.spatialBlend <= 0f || source.spatialBlend >= 1f) continue;
                Vector3 position = source.transform.position;
                bool near = (left.Tracked && (left.Position - position).sqrMagnitude <= limit) ||
                    (right.Tracked && (right.Position - position).sqrMagnitude <= limit);
                if (near) source.spatialBlend = 1f;
            }
            _known.IntersectWith(_present);
        }

        private void Restore()
        {
            if (_listener != null && _listener.enabled) _listener.enabled = false;
            for (int i = 0; i < _muted.Count; i++)
                if (_muted[i] != null) _muted[i].enabled = true;
            _muted.Clear();
            _known.Clear();
            _nextScan = 0f;
        }

        private void OnDestroy()
        {
            Restore();
            if (_head != null) Destroy(_head);
        }
    }
}
