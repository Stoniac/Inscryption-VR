using System;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class FinaleTrial : MonoBehaviour
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo TrialAnchorField = typeof(DeckTrialSequencer).GetField("trialCardsAnchor", Members);
        private static readonly FieldInfo DrawnAnchorField = typeof(DeckTrialSequencer).GetField("drawnCardsAnchor", Members);

        private const float CardSpacing = 1.5f;
        private const float BackMeters = 0.42f;
        private const float AboveMeters = 0.32f;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private bool _failed;
        private bool _mine;
        private bool _logged;
        private FinaleDeckTrialSequencer _trial;
        private float _nextSearch;

        internal static bool Active { get; private set; }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer)
        {
            _logger = logger;
            _renderer = renderer;
            if (TrialAnchorField == null) _logger.LogWarning("Finale deck trial: DeckTrialSequencer.trialCardsAnchor not found; the seat stays where you stand at the giant's table.");
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                Active = false;
                try { Release(); } catch (Exception) { }
                _logger.LogError("Finale deck trial seat turned off after an error; the view stays standing: " + exception);
            }
        }

        private void Tick()
        {
            if (!VrScenes.InFinale)
            {
                Active = false;
                _trial = null;
                _logged = false;
                _mine = false;
                return;
            }
            ViewManager views = Singleton<ViewManager>.Instance;
            Active = views != null && views.CurrentView == View.GiantHandTable;
            if (!Active)
            {
                Release();
                return;
            }
            Vector3 eye;
            float yaw;
            if (!Seat(out eye, out yaw)) return;
            _renderer.SetFixedSeat(eye, yaw);
            if (!_mine) _logger.LogInfo("Finale deck trial: seat glides up to the giant's table; grab a card to choose it.");
            _mine = true;
        }

        private void Release()
        {
            if (!_mine) return;
            _mine = false;
            _renderer.ClearFixedSeat();
            _logger.LogInfo("Finale deck trial over: seat glides back to where you stood.");
        }

        private bool Seat(out Vector3 eye, out float yaw)
        {
            eye = Vector3.zero;
            yaw = 0f;
            if (TrialAnchorField == null) return false;
            if (_trial == null && Time.unscaledTime >= _nextSearch)
            {
                _nextSearch = Time.unscaledTime + 0.5f;
                _trial = FindObjectOfType<FinaleDeckTrialSequencer>();
            }
            if (_trial == null) return false;
            Transform anchor = TrialAnchorField.GetValue(_trial) as Transform;
            if (anchor == null) return false;
            Vector3 focus = anchor.TransformPoint(new Vector3(CardSpacing, 0f, 0f));
            FirstPersonController walker = Singleton<FirstPersonController>.Instance;
            Vector3 forward = walker != null ? focus - walker.transform.position : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1f && walker != null) forward = walker.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            float scale = _renderer.UnitsPerMeter;
            eye = focus - forward * (BackMeters * scale) + Vector3.up * (AboveMeters * scale);
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            if (!_logged)
            {
                _logged = true;
                Transform drawn = DrawnAnchorField != null ? DrawnAnchorField.GetValue(_trial) as Transform : null;
                _logger.LogInfo("Finale deck trial probe: trial cards centre " + focus.ToString("F2") + " (anchor " + anchor.position.ToString("F2") +
                    ", scale " + anchor.lossyScale.ToString("F2") + "), drawn cards " + (drawn != null ? drawn.position.ToString("F2") : "?") +
                    ", walker " + (walker != null ? walker.transform.position.ToString("F2") + " yaw " + walker.transform.eulerAngles.y.ToString("0") : "?") +
                    ", seat " + eye.ToString("F2") + " facing " + yaw.ToString("0") + " degrees (" + BackMeters + " m back, " + AboveMeters + " m up plus seat height).");
            }
            return true;
        }
    }
}
