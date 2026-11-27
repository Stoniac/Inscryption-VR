using System;
using System.Globalization;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using Valve.VR;

namespace InscryptionVR
{
    public sealed class RenderTimingDiagnostics : MonoBehaviour
    {
        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private float _nextSample;
        private bool _failed;
        private bool _havePrevious;
        private Compositor_CumulativeStats _previous;
        private uint _frameSize;
        private uint _statsSize;

        private bool _enabled;
        private Func<string> _state;

        public void Initialize(ManualLogSource logger, StereoRenderer renderer, bool enabled, Func<string> state)
        {
            _logger = logger;
            _renderer = renderer;
            _state = state;
            _frameSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming));
            _statsSize = (uint)Marshal.SizeOf(typeof(Compositor_CumulativeStats));
            if (_frameSize != 192 || _statsSize != 112)
            {
                _failed = true;
                _logger.LogWarning("VR timing diagnostics disabled: unexpected ABI sizes " + _frameSize + "/" + _statsSize + ".");
                return;
            }
            SetEnabled(enabled);
        }

        public void SetEnabled(bool enabled)
        {
            if (_failed || enabled == _enabled) return;
            _enabled = enabled;
            PerfProbe.Enabled = enabled;
            if (enabled) PerfProbe.Report();
            _nextSample = Time.realtimeSinceStartup + 5f;
            _logger.LogInfo(enabled ? "VR timing log on: SteamVR and main-thread timings every 5 seconds; sampled flags do not cover intervening frames."
                : "VR timing log off.");
        }

        private void Update()
        {
            if (_failed || !_enabled || _logger == null || _renderer == null) return;
            PerfProbe.Frame(Time.unscaledDeltaTime);
            if (Time.realtimeSinceStartup < _nextSample) return;
            Vector3 position;
            Quaternion rotation;
            float scale;
            if (!_renderer.TryGetHeadPose(out position, out rotation, out scale)) return;
            _nextSample = Time.realtimeSinceStartup + 5f;
            _logger.LogInfo("VR CPU sample (" + (_state != null ? _state() : "?") + "): " + PerfProbe.Report() + ".");
            try
            {
                CVRCompositor compositor = OpenVR.Compositor;
                if (compositor == null) return;
                var timing = new Compositor_FrameTiming();
                timing.m_nSize = _frameSize;
                bool available = compositor.GetFrameTiming(ref timing, 0);
                var stats = new Compositor_CumulativeStats();
                compositor.GetCumulativeStats(ref stats, _statsSize);
                uint processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                bool ownStats = stats.m_nPid == processId;
                bool supported = compositor.IsMotionSmoothingSupported();
                bool smoothingEnabled = supported && compositor.IsMotionSmoothingEnabled();
                string sample = available ? string.Format(CultureInfo.InvariantCulture,
                    "lastFrame={0}, flags=0x{1:X}, presents={2}, dropped={3}, mispresented={4}, clientIntervalMs={5:F2}, renderGpuMs={6:F2}, submitMs={7:F2}, transferMs={8:F2}",
                    timing.m_nFrameIndex, timing.m_nReprojectionFlags, timing.m_nNumFramePresents,
                    timing.m_nNumDroppedFrames, timing.m_nNumMisPresented, timing.m_flClientFrameIntervalMs,
                    timing.m_flTotalRenderGpuMs, timing.m_flSubmitFrameMs, timing.m_flTransferLatencyMs)
                    : "lastFrame=unavailable";
                string cumulative = "cumulativePid=" + stats.m_nPid + ", ownProcess=" + ownStats;
                if (ownStats)
                {
                    cumulative += ", presents=" + stats.m_nNumFramePresents + ", submits=" + stats.m_nNumFrameSubmits +
                        ", dropped=" + stats.m_nNumDroppedFrames + ", reprojected=" + stats.m_nNumReprojectedFrames +
                        ", depthFrames=" + stats.m_nNumFramesWithDepth;
                    if (_havePrevious && _previous.m_nPid == stats.m_nPid &&
                        stats.m_nNumFramePresents >= _previous.m_nNumFramePresents &&
                        stats.m_nNumDroppedFrames >= _previous.m_nNumDroppedFrames &&
                        stats.m_nNumReprojectedFrames >= _previous.m_nNumReprojectedFrames)
                        cumulative += ", intervalPresents=" + (stats.m_nNumFramePresents - _previous.m_nNumFramePresents) +
                            ", intervalDropped=" + (stats.m_nNumDroppedFrames - _previous.m_nNumDroppedFrames) +
                            ", intervalReprojected=" + (stats.m_nNumReprojectedFrames - _previous.m_nNumReprojectedFrames);
                }
                _previous = stats;
                _havePrevious = ownStats;
                _logger.LogInfo("VR timing sample: " + sample + "; " + cumulative +
                    "; motionSmoothingSupported=" + supported + ", motionSmoothingEnabled=" + smoothingEnabled + ".");
            }
            catch (Exception exception)
            {
                _failed = true;
                _logger.LogWarning("VR timing diagnostics disabled after read failure: " + exception.Message);
            }
        }
    }
}
