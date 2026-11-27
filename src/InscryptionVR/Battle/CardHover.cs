using DiskCardGame;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class CardHover
    {
        internal const float KeepFactor = 1.25f;
        private const float SwitchMeters = 0.01f;
        private const float SwitchSeconds = 0.06f;
        private const float LookbackSeconds = 0.09f;
        private const int HistorySize = 32;

        private PlayableCard _current;
        private PlayableCard _candidate;
        private float _candidateSince;
        private readonly PlayableCard[] _cards = new PlayableCard[HistorySize];
        private readonly float[] _times = new float[HistorySize];
        private int _head;
        private int _count;

        internal PlayableCard Current { get { return _current; } }

        internal PlayableCard Update(PlayableCard best, float bestSqr, float currentSqr, float reach, float scale, float now)
        {
            float keep = reach * KeepFactor;
            bool currentValid = _current != null && currentSqr <= keep * keep;
            if (!currentValid)
            {
                _current = best != null && bestSqr <= reach * reach ? best : null;
                _candidate = null;
            }
            else if (best != null && best != _current && Mathf.Sqrt(bestSqr) + SwitchMeters * scale < Mathf.Sqrt(currentSqr))
            {
                if (_candidate != best)
                {
                    _candidate = best;
                    _candidateSince = now;
                }
                else if (now - _candidateSince >= SwitchSeconds)
                {
                    _current = best;
                    _candidate = null;
                }
            }
            else _candidate = null;
            Record(now, _current);
            return _current;
        }

        internal PlayableCard Intended(float now)
        {
            PlayableCard earlier = At(now - LookbackSeconds);
            return earlier != null ? earlier : _current;
        }

        internal void Clear()
        {
            _current = null;
            _candidate = null;
            _count = 0;
            _head = 0;
            for (int i = 0; i < HistorySize; i++) _cards[i] = null;
        }

        private void Record(float now, PlayableCard card)
        {
            _cards[_head] = card;
            _times[_head] = now;
            _head = (_head + 1) % HistorySize;
            if (_count < HistorySize) _count++;
        }

        private PlayableCard At(float time)
        {
            PlayableCard oldest = null;
            for (int n = 1; n <= _count; n++)
            {
                int index = (_head - n + HistorySize) % HistorySize;
                if (_times[index] <= time) return _cards[index];
                oldest = _cards[index];
            }
            return oldest;
        }
    }
}
