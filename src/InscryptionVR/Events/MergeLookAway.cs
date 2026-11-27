using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    public sealed class MergeLookAway : MonoBehaviour
    {
        private const string EventId = "DuplicateMergeLookAway";
        private const float AwayDegrees = 70f;
        private const float AwaySeconds = 0.4f;
        private const float ShownSeconds = 0.6f;
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ContinueField = typeof(TextDisplayer).GetField("continuePressed", Members);
        private static readonly FieldInfo SlotField = typeof(DuplicateMergeSequencer).GetField("selectionSlot", Members);
        private static MergeLookAway _instance;

        private ManualLogSource _logger;
        private StereoRenderer _renderer;
        private TextDisplayer _display;
        private Action<DialogueEvent.Line> _onLine;
        private Action _onEnd;
        private readonly HashSet<DialogueEvent.Line> _finalLines = new HashSet<DialogueEvent.Line>();
        private bool _loadedLines;
        private bool _gated;
        private bool _released;
        private float _lineAt;
        private float _awayFor;
        private Transform _spot;
        private bool _failed;

        internal static bool Holding { get { return _instance != null && _instance._gated && !_instance._released; } }

        internal void Initialize(ManualLogSource logger, StereoRenderer renderer)
        {
            _logger = logger;
            _renderer = renderer;
            _instance = this;
            _onLine = OnLine;
            _onEnd = OnEnd;
            if (ContinueField == null) _logger.LogWarning("Mycologist look-away unavailable: TextDisplayer.continuePressed not found; the merge line advances with the trigger.");
        }

        private void LateUpdate()
        {
            if (_logger == null || _failed || ContinueField == null) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                _gated = false;
                _logger.LogError("Mycologist look-away disabled after an error; the merge line advances with the trigger: " + exception);
            }
        }

        private void Tick()
        {
            if (SceneManager.GetActiveScene().name != "Part1_Cabin")
            {
                Detach();
                _gated = false;
                return;
            }
            TextDisplayer display = Singleton<TextDisplayer>.Instance;
            if (display != _display) Attach(display);
            if (!_gated || _display == null) return;
            if (EventStyle.Simplified || DialogueSkip.Active)
            {
                _gated = false;
                return;
            }

            Vector3 eye;
            Quaternion view;
            float scale;
            if (!_renderer.TryGetHeadPose(out eye, out view, out scale))
            {
                _released = true;
                Push();
                return;
            }
            bool away = LookingAway(eye, view * Vector3.forward);
            _awayFor = away ? _awayFor + Time.unscaledDeltaTime : 0f;
            bool shown = _display.Displaying && Time.time - _lineAt >= ShownSeconds;
            if (!_released && shown && _awayFor >= AwaySeconds)
            {
                _released = true;
                _logger.LogInfo("You looked away; the mycologists begin the merge.");
            }
            if (_released) Push();
            else ContinueField.SetValue(_display, false);
        }

        private void Push()
        {
            if (_display != null) ContinueField.SetValue(_display, true);
        }

        private bool LookingAway(Vector3 eye, Vector3 forward)
        {
            if (_spot != null && _spot.gameObject.activeInHierarchy && Angle(forward, _spot.position - eye) < AwayDegrees) return false;
            OpponentAnimationController opponent = OpponentAnimationController.Instance;
            Transform head = opponent != null && opponent.isActiveAndEnabled ? opponent.Head : null;
            if (head != null && head.gameObject.activeInHierarchy && Angle(forward, head.position - eye) < AwayDegrees) return false;
            return true;
        }

        private static float Angle(Vector3 forward, Vector3 toward)
        {
            return toward.sqrMagnitude < 1e-8f ? 180f : Vector3.Angle(forward, toward);
        }

        private void OnLine(DialogueEvent.Line line)
        {
            LoadLines();
            bool gate = line != null && !EventStyle.Simplified && _finalLines.Contains(line);
            if (gate && !_gated)
            {
                _spot = FindSpot();
                _logger.LogInfo("The mycologists wait until you look away from the table before merging.");
            }
            _gated = gate;
            _released = false;
            _awayFor = 0f;
            _lineAt = Time.time;
        }

        private void OnEnd()
        {
            _gated = false;
            _released = false;
        }

        private void LoadLines()
        {
            if (_loadedLines) return;
            _loadedLines = true;
            DialogueEvent dialogue = DialogueDataUtil.Data != null ? DialogueDataUtil.Data.GetEvent(EventId) : null;
            if (dialogue == null)
            {
                _logger.LogWarning("Mycologist look-away: dialogue '" + EventId + "' not found; the merge line advances with the trigger.");
                return;
            }
            AddFinal(dialogue.mainLines);
            if (dialogue.repeatLines != null)
                for (int i = 0; i < dialogue.repeatLines.Count; i++) AddFinal(dialogue.repeatLines[i]);
        }

        private void AddFinal(DialogueEvent.LineSet set)
        {
            if (set == null || set.lines == null || set.lines.Count == 0) return;
            for (int i = set.lines.Count - 1; i >= 0; i--)
            {
                DialogueEvent.Line line = set.lines[i];
                if (line == null) continue;
                _finalLines.Add(line);
                if (line.storyCondition == StoryEvent.BasicTutorialCompleted) return;
            }
        }

        private static Transform FindSpot()
        {
            DuplicateMergeSequencer sequencer = FindObjectOfType<DuplicateMergeSequencer>();
            if (sequencer == null) return null;
            Component slot = SlotField != null ? SlotField.GetValue(sequencer) as Component : null;
            return slot != null ? slot.transform : sequencer.transform;
        }

        private void Attach(TextDisplayer display)
        {
            Detach();
            _display = display;
            if (_display == null) return;
            _display.newDialogueEventLine = (Action<DialogueEvent.Line>)Delegate.Combine(_display.newDialogueEventLine, _onLine);
            _display.endOfDialogueLines = (Action)Delegate.Combine(_display.endOfDialogueLines, _onEnd);
        }

        private void Detach()
        {
            if (_display != null)
            {
                _display.newDialogueEventLine = (Action<DialogueEvent.Line>)Delegate.Remove(_display.newDialogueEventLine, _onLine);
                _display.endOfDialogueLines = (Action)Delegate.Remove(_display.endOfDialogueLines, _onEnd);
            }
            _display = null;
            _spot = null;
        }

        private void OnDestroy()
        {
            Detach();
            if (_instance == this) _instance = null;
        }
    }
}
