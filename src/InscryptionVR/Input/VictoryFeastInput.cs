using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace InscryptionVR
{
    internal sealed class VictoryFeastBindings
    {
        internal readonly FieldInfo AnyKeyWait;

        internal VictoryFeastBindings()
        {
            Type closures = typeof(DiskCardGame.VictoryFeastSequencer).GetNestedType("<>c", BindingFlags.Public | BindingFlags.NonPublic);
            if (closures == null) throw new MissingMemberException("DiskCardGame.VictoryFeastSequencer", "<>c");
            AnyKeyWait = closures.GetField("<>9__4_0", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (AnyKeyWait == null || AnyKeyWait.FieldType != typeof(Func<bool>))
                throw new MissingFieldException(closures.FullName, "<>9__4_0");
        }
    }

    internal static class VictoryFeastInput
    {
        private static VrInput _input;

        internal static void Install(ManualLogSource logger, VrInput input)
        {
            _input = input;
            try
            {
                var bindings = new VictoryFeastBindings();
                bindings.AnyKeyWait.SetValue(null, new Func<bool>(Pressed));
                logger.LogInfo("Victory feast: either trigger continues where the game waits for any key.");
            }
            catch (Exception exception)
            {
                logger.LogWarning("Victory feast controller input unavailable; press a keyboard key there instead: " + exception.Message);
            }
        }

        private static bool Pressed()
        {
            if (Input.anyKeyDown) return true;
            if (_input == null) return false;
            _input.Refresh();
            return _input.Active && (_input.Left.TriggerEdge == 1 || _input.Right.TriggerEdge == 1);
        }
    }
}
