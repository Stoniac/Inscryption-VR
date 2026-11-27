using UnityEngine;
using UnityEngine.UI;

namespace InscryptionVR
{
    public sealed class LetterJitter : BaseMeshEffect
    {
        private const float TicksPerSecond = 12f;
        private const float WavyTicksPerSecond = 24f;
        private const float WaveSpeed = 7f;
        private const float WaveSpacing = 0.55f;
        private const float WaveHeight = 1.2f;
        private DiskCardGame.TextDisplayer.LetterAnimation _animation = DiskCardGame.TextDisplayer.LetterAnimation.None;
        private int _tick = -1;
        private float _amplitude = 1f;

        internal int Version { get; private set; }

        internal DiskCardGame.TextDisplayer.LetterAnimation Animation
        {
            get { return _animation; }
            set
            {
                if (_animation == value) return;
                _animation = value;
                Dirty();
            }
        }

        internal float Amplitude
        {
            get { return _amplitude; }
            set
            {
                if (Mathf.Approximately(_amplitude, value)) return;
                _amplitude = value;
                Dirty();
            }
        }

        private void Dirty()
        {
            Version++;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private void Update()
        {
            if (_animation == DiskCardGame.TextDisplayer.LetterAnimation.None || graphic == null) return;
            bool wavy = _animation == DiskCardGame.TextDisplayer.LetterAnimation.WavyJitter;
            int tick = Mathf.FloorToInt(Time.unscaledTime * (wavy ? WavyTicksPerSecond : TicksPerSecond));
            if (tick == _tick) return;
            _tick = tick;
            Dirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || _animation == DiskCardGame.TextDisplayer.LetterAnimation.None) return;
            int count = vh.currentVertCount;
            bool wavy = _animation == DiskCardGame.TextDisplayer.LetterAnimation.WavyJitter;
            int jitterTick = wavy ? Mathf.FloorToInt(_tick * TicksPerSecond / WavyTicksPerSecond) : _tick;
            float time = _tick / (wavy ? WavyTicksPerSecond : TicksPerSecond);
            UIVertex vertex = new UIVertex();
            for (int start = 0; start + 3 < count; start += 4)
            {
                int letter = start / 4;
                Vector3 offset = new Vector3(Step(letter, jitterTick, 0x2F6B), Step(letter, jitterTick, 0x51C3), 0f);
                if (wavy) offset.y += Mathf.Round(Mathf.Sin(time * WaveSpeed - letter * WaveSpacing) * WaveHeight);
                offset *= _amplitude;
                for (int i = start; i < start + 4; i++)
                {
                    vh.PopulateUIVertex(ref vertex, i);
                    vertex.position += offset;
                    vh.SetUIVertex(vertex, i);
                }
            }
        }

        private static float Step(int letter, int tick, int salt)
        {
            unchecked
            {
                uint h = (uint)(letter * 73856093) ^ (uint)(tick * 19349663) ^ (uint)salt;
                h ^= h >> 13;
                h *= 0x5BD1E995;
                h ^= h >> 15;
                uint roll = h % 10u;
                if (roll < 6u) return 0f;
                return roll % 2u == 0u ? -1f : 1f;
            }
        }
    }
}
