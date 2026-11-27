using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rewired;
using UnityEngine;

namespace InscryptionVR
{
    internal static class GameKeys
    {
        private const uint InputKeyboard = 1;
        private const uint KeyUp = 0x0002;
        private const uint ExtendedKey = 0x0001;
        private const float HoldSeconds = 0.15f;

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint Type;
            public InputUnion Data;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        private sealed class Held
        {
            internal ushort VirtualKey;
            internal bool Extended;
            internal float ReleaseAt;
        }

        private static readonly List<Held> Pressed = new List<Held>();
        private static int _processId;

        internal static bool PressAction(string action, KeyCode fallback, out string pressed)
        {
            pressed = null;
            if (!GameWindowFocused()) return false;
            KeyCode key = KeyForAction(action, fallback);
            ushort virtualKey;
            bool extended;
            if (!VirtualKeyFor(key, out virtualKey, out extended)) return false;
            if (!Send(virtualKey, extended, false)) return false;
            Held held = new Held();
            held.VirtualKey = virtualKey;
            held.Extended = extended;
            held.ReleaseAt = Time.realtimeSinceStartup + HoldSeconds;
            Pressed.Add(held);
            pressed = key.ToString();
            return true;
        }

        internal static void Tick()
        {
            if (Pressed.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = Pressed.Count - 1; i >= 0; i--)
            {
                if (now < Pressed[i].ReleaseAt) continue;
                Send(Pressed[i].VirtualKey, Pressed[i].Extended, true);
                Pressed.RemoveAt(i);
            }
        }

        internal static void ReleaseAll()
        {
            for (int i = 0; i < Pressed.Count; i++) Send(Pressed[i].VirtualKey, Pressed[i].Extended, true);
            Pressed.Clear();
        }

        private static KeyCode KeyForAction(string action, KeyCode fallback)
        {
            try
            {
                if (!ReInput.isReady) return fallback;
                Player player = ReInput.players.GetPlayer(0);
                if (player == null) return fallback;
                KeyCode best = KeyCode.None;
                foreach (ActionElementMap map in player.controllers.maps.ButtonMapsWithAction(ControllerType.Keyboard, action, true))
                {
                    if (map == null || map.keyCode == KeyCode.None) continue;
                    ushort virtualKey;
                    bool extended;
                    if (!VirtualKeyFor(map.keyCode, out virtualKey, out extended)) continue;
                    if (!extended) return map.keyCode;
                    if (best == KeyCode.None) best = map.keyCode;
                }
                return best != KeyCode.None ? best : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool VirtualKeyFor(KeyCode key, out ushort virtualKey, out bool extended)
        {
            extended = false;
            virtualKey = 0;
            if (key >= KeyCode.A && key <= KeyCode.Z) virtualKey = (ushort)(0x41 + (key - KeyCode.A));
            else if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) virtualKey = (ushort)(0x30 + (key - KeyCode.Alpha0));
            else if (key == KeyCode.Space) virtualKey = 0x20;
            else if (key == KeyCode.Return) virtualKey = 0x0D;
            else if (key == KeyCode.UpArrow) { virtualKey = 0x26; extended = true; }
            else if (key == KeyCode.DownArrow) { virtualKey = 0x28; extended = true; }
            else if (key == KeyCode.LeftArrow) { virtualKey = 0x25; extended = true; }
            else if (key == KeyCode.RightArrow) { virtualKey = 0x27; extended = true; }
            return virtualKey != 0;
        }

        private static bool GameWindowFocused()
        {
            try
            {
                if (_processId == 0) _processId = Process.GetCurrentProcess().Id;
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                return owner == (uint)_processId;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Send(ushort virtualKey, bool extended, bool up)
        {
            try
            {
                NativeInput[] inputs = new NativeInput[1];
                inputs[0].Type = InputKeyboard;
                inputs[0].Data.Keyboard.VirtualKey = virtualKey;
                inputs[0].Data.Keyboard.ScanCode = (ushort)MapVirtualKey(virtualKey, 0);
                inputs[0].Data.Keyboard.Flags = (up ? KeyUp : 0u) | (extended ? ExtendedKey : 0u);
                return SendInput(1, inputs, Marshal.SizeOf(typeof(NativeInput))) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
