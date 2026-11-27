using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    public sealed class HapticMixer : MonoBehaviour
    {
        private ManualLogSource _logger;
        private VrInput _input;
        private readonly System.Random _noise = new System.Random();
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();
        private bool _failed;

        internal void Initialize(ManualLogSource logger, VrInput input)
        {
            _logger = logger;
            _input = input;
            StartCoroutine(Flush());
        }

        private IEnumerator Flush()
        {
            while (!_failed)
            {
                yield return _endOfFrame;
                try
                {
                    float now = Time.unscaledTime;
                    _input.Left.FlushHaptics(now, _noise);
                    _input.Right.FlushHaptics(now, _noise);
                }
                catch (Exception exception)
                {
                    _failed = true;
                    _logger.LogWarning("Controller haptics stopped: " + exception);
                }
            }
        }
    }
}
