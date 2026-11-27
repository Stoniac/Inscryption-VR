using System;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class SkipFootageMenu : MonoBehaviour
    {
        private const int BrowsingFootage = 1;
        private const int PlayingFootage = 2;
        private const int LogoScreen = 6;
        private const int LogoBatch = 0;
        private const float SearchInterval = 0.5f;
        private const float ExitDelay = 0.25f;

        private ManualLogSource _logger;
        private FieldInfo _state;
        private FieldInfo _runnerField;
        private VideoCameraRig _rig;
        private CustomCoroutine _ownRunner;
        private CustomCoroutine _gameRunner;
        private float _nextSearch;
        private float _browsingSince = -1f;

        internal void Initialize(ManualLogSource logger)
        {
            _logger = logger;
            _state = typeof(VideoCameraRig).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
            _runnerField = typeof(CustomCoroutine).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            if (_state == null)
            {
                logger.LogWarning("Footage menu skip unavailable: VideoCameraRig.state not found.");
                enabled = false;
                return;
            }
            if (_runnerField == null) logger.LogWarning("Camcorder logo skip unavailable: CustomCoroutine.instance not found.");
        }

        private void Update()
        {
            if (!VrScenes.InCabin)
            {
                Reset();
                return;
            }
            if (_rig == null)
            {
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + SearchInterval;
                _rig = FindObjectOfType<VideoCameraRig>();
                if (_rig == null) return;
            }
            int state;
            try { state = Convert.ToInt32(_state.GetValue(_rig)); }
            catch (Exception) { return; }

            if (state == PlayingFootage) CaptureLogoTimer();
            else if (state == LogoScreen && _ownRunner != null) SkipLogo();
            else ReleaseRunner(false);

            if (state != BrowsingFootage)
            {
                _browsingSince = -1f;
                return;
            }
            if (_browsingSince < 0f)
            {
                _browsingSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _browsingSince < ExitDelay) return;
            _browsingSince = -1f;
            try
            {
                _rig.ExitFootageMenu();
                _logger.LogInfo("Skipped the camcorder footage menu.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Footage menu skip failed: " + exception.Message);
                enabled = false;
            }
        }

        private void CaptureLogoTimer()
        {
            if (_ownRunner != null || _runnerField == null) return;
            try
            {
                _gameRunner = _runnerField.GetValue(null) as CustomCoroutine;
                _ownRunner = new GameObject("InscryptionVR Logo Timer").AddComponent<CustomCoroutine>();
                _runnerField.SetValue(null, _ownRunner);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Camcorder logo skip unavailable: " + exception.Message);
                _runnerField = null;
                ReleaseRunner(false);
            }
        }

        private void SkipLogo()
        {
            ReleaseRunner(true);
            try
            {
                _rig.EnterFootageMenu(LogoBatch);
                _logger.LogInfo("Skipped the camcorder logo screen.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Camcorder logo skip failed: " + exception.Message);
            }
        }

        private void ReleaseRunner(bool cancelPending)
        {
            if (_ownRunner == null) return;
            try
            {
                if (ReferenceEquals(_runnerField.GetValue(null), _ownRunner)) _runnerField.SetValue(null, _gameRunner);
            }
            catch (Exception) { }
            if (cancelPending)
            {
                _ownRunner.StopAllCoroutines();
                Destroy(_ownRunner.gameObject);
            }
            _ownRunner = null;
            _gameRunner = null;
        }

        private void Reset()
        {
            ReleaseRunner(false);
            _rig = null;
            _browsingSince = -1f;
        }
    }
}
