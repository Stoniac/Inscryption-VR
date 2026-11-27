namespace InscryptionVR
{
    internal sealed class ControllerTriggerState
    {
        private bool _initialized;
        private bool _armed;
        internal bool Held { get; private set; }

        internal bool IsPressed(bool digital, float analog)
        {
            float threshold = !_initialized || Held ? 0.3f : 0.65f;
            return digital || (!float.IsNaN(analog) && !float.IsInfinity(analog) && analog >= threshold);
        }

        internal int Sample(bool pressed)
        {
            if (!_initialized)
            {
                _initialized = true;
                Held = pressed;
                _armed = !pressed;
                return 0;
            }
            bool wasHeld = Held;
            Held = pressed;
            if (!pressed)
            {
                bool release = wasHeld && _armed;
                _armed = true;
                return release ? -1 : 0;
            }
            return !wasHeld && _armed ? 1 : 0;
        }

        internal void Cancel()
        {
            _initialized = false;
            _armed = false;
            Held = false;
        }
    }
}
