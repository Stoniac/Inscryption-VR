using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DiskCardGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace InscryptionVR
{
    public sealed class GameHaptics : MonoBehaviour
    {
        [Flags]
        private enum Hands { None = 0, Left = 1, Right = 2, Both = 3 }

        private struct Buzz
        {
            internal float Start;
            internal float End;
            internal float Strength;
            internal bool Fade;
        }

        private sealed class TrackedCard
        {
            internal int Damage;
            internal bool Dead;
            internal Hands Side;
        }

        private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const float VoiceGain = 8f;
        private const float VoiceGate = 0.01f;

        private ManualLogSource _logger;
        private VrInput _input;
        private float _strength;
        private float _voiceStrength;
        private bool _failed;

        private readonly List<Buzz> _left = new List<Buzz>();
        private readonly List<Buzz> _right = new List<Buzz>();
        private readonly Dictionary<PlayableCard, TrackedCard> _cards = new Dictionary<PlayableCard, TrackedCard>();
        private readonly HashSet<PlayableCard> _seen = new HashSet<PlayableCard>();
        private readonly List<PlayableCard> _gone = new List<PlayableCard>();

        private FieldInfo _playerWeight;
        private FieldInfo _opponentWeight;
        private Scales _scales;
        private int _lastPlayerWeight;
        private int _lastOpponentWeight;

        private FieldInfo _currentAudio;
        private FieldInfo _voicePrefix;
        private FieldInfo _defaultStyle;
        private FieldInfo _alternateStyles;
        private FieldInfo _styleVoicePrefix;
        private FieldInfo _styleSpeaker;
        private AudioSource _voiceSource;
        private bool _voiceIsLeshy;
        private readonly float[] _samples = new float[256];

        internal void Initialize(ManualLogSource logger, VrInput input, float strength, float voiceStrength)
        {
            _logger = logger;
            _input = input;
            _strength = Mathf.Clamp01(strength);
            _voiceStrength = Mathf.Clamp01(voiceStrength);
            try
            {
                Type scales = typeof(Scales);
                _playerWeight = scales.GetField("playerWeight", InstanceMembers);
                _opponentWeight = scales.GetField("opponentWeight", InstanceMembers);
                if (_playerWeight == null || _opponentWeight == null)
                    _logger.LogWarning("Scale haptics unavailable: weight fields not found.");
            }
            catch (Exception exception) { _logger.LogWarning("Scale haptics unavailable: " + exception.Message); _playerWeight = null; }
            try
            {
                Type text = typeof(TextDisplayer);
                _currentAudio = text.GetField("currentAudio", InstanceMembers);
                _voicePrefix = text.GetField("voiceSoundIdPrefix", InstanceMembers);
                _defaultStyle = text.GetField("defaultStyle", InstanceMembers);
                _alternateStyles = text.GetField("alternateSpeakerStyles", InstanceMembers);
                Type style = text.GetNestedType("SpeakerTextStyle", BindingFlags.Public | BindingFlags.NonPublic);
                if (style != null)
                {
                    _styleVoicePrefix = style.GetField("voiceSoundIdPrefix", InstanceMembers);
                    _styleSpeaker = style.GetField("speaker", InstanceMembers);
                }
                if (_currentAudio == null || _voicePrefix == null || _defaultStyle == null || _styleVoicePrefix == null)
                {
                    _currentAudio = null;
                    _logger.LogWarning("Voice haptics unavailable: dialogue voice fields not found.");
                }
            }
            catch (Exception exception) { _logger.LogWarning("Voice haptics unavailable: " + exception.Message); _currentAudio = null; }
            _logger.LogInfo("Game haptics ready (strength " + _strength.ToString("0.00") + ", voice " + _voiceStrength.ToString("0.00") + ").");
        }

        public void SetStrength(float strength) { _strength = Mathf.Clamp01(strength); }

        public void SetVoiceStrength(float voiceStrength) { _voiceStrength = Mathf.Clamp01(voiceStrength); }

        private void LateUpdate()
        {
            if (_logger == null || _failed) return;
            try { Tick(); }
            catch (Exception exception)
            {
                _failed = true;
                _logger.LogWarning("Game haptics stopped: " + exception);
            }
        }

        private void Tick()
        {
            bool active = SceneManager.GetActiveScene().name == "Part1_Cabin" && Time.timeScale > 0f;
            if (active)
            {
                _input.Refresh();
                active = _input.Active;
            }
            if (!active)
            {
                Forget();
                return;
            }

            float now = Time.unscaledTime;
            ObserveCards(_input.SeatRotation * Vector3.right, now);
            ObserveScales(now);
            float voice = ObserveVoice();

            Emit(_input.Left, Mathf.Max(Evaluate(_left, now), voice));
            Emit(_input.Right, Mathf.Max(Evaluate(_right, now), voice));
        }

        private void ObserveCards(Vector3 across, float now)
        {
            BoardManager board = Singleton<BoardManager>.Instance;
            if (board == null) { _cards.Clear(); return; }
            List<CardSlot> slots = board.PlayerSlotsCopy;
            Vector3 center = Vector3.zero;
            int count = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] == null) continue;
                center += slots[i].transform.position;
                count++;
            }
            if (count == 0) { _cards.Clear(); return; }
            center /= count;
            float lane = slots.Count > 1 && slots[0] != null && slots[1] != null
                ? Vector3.Distance(slots[0].transform.position, slots[1].transform.position) : 1f;

            _seen.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                CardSlot slot = slots[i];
                if (slot == null) continue;
                PlayableCard card = slot.Card;
                if (card == null || card.Status == null) continue;
                _seen.Add(card);
                Hands side = SideOf(slot.transform.position - center, across, lane);
                TrackedCard tracked;
                if (!_cards.TryGetValue(card, out tracked))
                {
                    _cards[card] = new TrackedCard { Damage = card.Status.damageTaken, Dead = card.Dead, Side = side };
                    continue;
                }
                tracked.Side = side;
                int damage = card.Status.damageTaken;
                if (damage > tracked.Damage) Add(side, 0.6f, 0.14f, true, now);
                tracked.Damage = damage;
                if (card.Dead && !tracked.Dead) Died(side, now);
                tracked.Dead = card.Dead;
            }

            _gone.Clear();
            foreach (KeyValuePair<PlayableCard, TrackedCard> pair in _cards)
            {
                if (_seen.Contains(pair.Key)) continue;
                if (pair.Key != null && pair.Key.Dead && !pair.Value.Dead) Died(pair.Value.Side, now);
                _gone.Add(pair.Key);
            }
            for (int i = 0; i < _gone.Count; i++) _cards.Remove(_gone[i]);
        }

        private static Hands SideOf(Vector3 offset, Vector3 across, float lane)
        {
            float d = Vector3.Dot(offset, across);
            if (Mathf.Abs(d) < lane * 0.3f) return Hands.Both;
            return d < 0f ? Hands.Left : Hands.Right;
        }

        private void Died(Hands side, float now)
        {
            Add(side, 1f, 0.12f, false, now);
            Add(side, 0.7f, 0.45f, true, now);
        }

        private void ObserveScales(float now)
        {
            if (_playerWeight == null || _opponentWeight == null) return;
            LifeManager life = Singleton<LifeManager>.Instance;
            Scales scales = life != null ? life.Scales : null;
            if (scales == null) { _scales = null; return; }
            int player = (int)_playerWeight.GetValue(scales);
            int opponent = (int)_opponentWeight.GetValue(scales);
            if (scales != _scales)
            {
                _scales = scales;
                _lastPlayerWeight = player;
                _lastOpponentWeight = opponent;
                return;
            }
            bool playerUp = player > _lastPlayerWeight;
            bool opponentUp = opponent > _lastOpponentWeight;
            bool playerChanged = player != _lastPlayerWeight;
            bool opponentChanged = opponent != _lastOpponentWeight;
            _lastPlayerWeight = player;
            _lastOpponentWeight = opponent;
            if (playerChanged && opponentChanged) return;
            if (playerUp) Add(Hands.Both, 1f, 0.22f, true, now);
            else if (opponentUp) Add(Hands.Both, 0.4f, 0.1f, true, now);
        }

        private float ObserveVoice()
        {
            if (_currentAudio == null || _voiceStrength <= 0f) return 0f;
            TextDisplayer text = Singleton<TextDisplayer>.Instance;
            if (text == null) { _voiceSource = null; return 0f; }
            AudioSource source = _currentAudio.GetValue(text) as AudioSource;
            if (source != _voiceSource)
            {
                _voiceSource = source;
                _voiceIsLeshy = source != null && IsLeshyStyle(text);
            }
            if (source == null || !_voiceIsLeshy || !source.isPlaying) return 0f;
            source.GetOutputData(_samples, 0);
            float sum = 0f;
            for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / _samples.Length);
            if (rms < VoiceGate) return 0f;
            return Mathf.Clamp01(rms * VoiceGain) * _voiceStrength;
        }

        private bool IsLeshyStyle(TextDisplayer text)
        {
            string current = _voicePrefix.GetValue(text) as string ?? string.Empty;
            object fallback = _defaultStyle.GetValue(text);
            if (fallback != null && string.Equals(current, _styleVoicePrefix.GetValue(fallback) as string ?? string.Empty)) return true;
            IEnumerable styles = _alternateStyles != null ? _alternateStyles.GetValue(text) as IEnumerable : null;
            if (styles == null || _styleSpeaker == null) return false;
            foreach (object style in styles)
            {
                if (style == null) continue;
                object speaker = _styleSpeaker.GetValue(style);
                if (speaker == null || (DialogueEvent.Speaker)speaker != DialogueEvent.Speaker.Leshy) continue;
                if (string.Equals(current, _styleVoicePrefix.GetValue(style) as string ?? string.Empty)) return true;
            }
            return false;
        }

        private void Add(Hands hands, float strength, float seconds, bool fade, float now)
        {
            var buzz = new Buzz { Start = now, End = now + seconds, Strength = strength, Fade = fade };
            if ((hands & Hands.Left) != 0) _left.Add(buzz);
            if ((hands & Hands.Right) != 0) _right.Add(buzz);
        }

        private static float Evaluate(List<Buzz> buzzes, float now)
        {
            float strength = 0f;
            for (int i = buzzes.Count - 1; i >= 0; i--)
            {
                Buzz buzz = buzzes[i];
                if (now >= buzz.End) { buzzes.RemoveAt(i); continue; }
                float value = buzz.Strength;
                if (buzz.Fade) value *= 1f - (now - buzz.Start) / (buzz.End - buzz.Start);
                if (value > strength) strength = value;
            }
            return strength;
        }

        private void Emit(VrHand hand, float strength)
        {
            strength *= _strength;
            if (strength < 0.02f || !hand.Tracked) return;
            hand.Rumble(0.08f + strength * 0.92f);
        }

        private void Forget()
        {
            _left.Clear();
            _right.Clear();
            _cards.Clear();
            _scales = null;
            _voiceSource = null;
        }
    }
}
