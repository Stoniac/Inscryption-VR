using BepInEx.Logging;

namespace InscryptionVR
{
    internal static class VrLog
    {
        internal static bool Verbose;

        internal static void Chatter(ManualLogSource logger, string message)
        {
            if (logger == null) return;
            if (Verbose) logger.LogInfo(message);
            else logger.LogDebug(message);
        }
    }

    internal sealed class StatusLine
    {
        private readonly ManualLogSource _logger;
        private readonly string _prefix;
        private string _last;

        internal StatusLine(ManualLogSource logger, string prefix)
        {
            _logger = logger;
            _prefix = prefix;
        }

        internal void Set(string status)
        {
            if (_last == status) return;
            _last = status;
            VrLog.Chatter(_logger, _prefix + ": " + status + ".");
        }
    }
}
