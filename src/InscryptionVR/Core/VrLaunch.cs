using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InscryptionVR
{
    internal static class VrLaunch
    {
        internal const string Auto = "Auto";
        internal const string FlagOnly = "FlagOnly";
        internal static readonly string[] Modes = { Auto, FlagOnly };

        internal const string VrFlag = "vr";
        internal const string FlatFlag = "novr";

        private static readonly string[] SteamVrProcesses = { "vrserver", "vrcompositor" };

        internal enum Reason { VrFlag, FlatFlag, SteamVrRunning, SteamVrClosed, FlagOnlyNoFlag }

        internal static bool ShouldStartVr(string mode, out Reason reason)
        {
            if (HasFlag(FlatFlag)) { reason = Reason.FlatFlag; return false; }
            if (HasFlag(VrFlag)) { reason = Reason.VrFlag; return true; }
            if (!string.Equals(mode, Auto, StringComparison.OrdinalIgnoreCase)) { reason = Reason.FlagOnlyNoFlag; return false; }
            if (SteamVrRunning()) { reason = Reason.SteamVrRunning; return true; }
            reason = Reason.SteamVrClosed;
            return false;
        }

        internal static string Describe(Reason reason)
        {
            switch (reason)
            {
                case Reason.VrFlag: return "launched with -" + VrFlag;
                case Reason.FlatFlag: return "launched with -" + FlatFlag;
                case Reason.SteamVrRunning: return "SteamVR is running";
                case Reason.SteamVrClosed: return "SteamVR isn't running (" + LastProbe + ")";
                default: return "launched without -" + VrFlag + " (Rendering.LaunchMode = " + FlagOnly + ")";
            }
        }

        private static bool HasFlag(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                string trimmed = arg.TrimStart('-', '/');
                if (trimmed.Length < arg.Length && string.Equals(trimmed, flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static string LastProbe = "";

        private static bool SteamVrRunning()
        {
            try
            {
                if (SnapshotHasProcess()) return true;
            }
            catch (Exception exception) { LastProbe = "snapshot failed: " + exception.Message; }
            foreach (string name in SteamVrProcesses)
            {
                Process[] found = null;
                try
                {
                    found = Process.GetProcessesByName(name);
                    if (found.Length > 0) return true;
                }
                catch (Exception exception) { LastProbe += "; managed lookup failed: " + exception.Message; }
                finally
                {
                    if (found != null) foreach (Process p in found) p.Dispose();
                }
            }
            return false;
        }

        private const uint Th32csSnapProcess = 0x2;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static bool SnapshotHasProcess()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
            {
                LastProbe = "snapshot unavailable (error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            try
            {
                var entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                int count = 0;
                for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                {
                    count++;
                    string exe = entry.szExeFile ?? "";
                    if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
                    foreach (string name in SteamVrProcesses)
                        if (string.Equals(exe, name, StringComparison.OrdinalIgnoreCase)) return true;
                }
                LastProbe = "snapshot listed " + count + " processes, none of them SteamVR";
                return false;
            }
            finally { CloseHandle(snapshot); }
        }
    }
}
