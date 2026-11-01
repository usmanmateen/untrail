using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Untrail
{
    public enum UninstallState
    {
        Running,    // the uninstaller (or something it started) is still running
        Completed,  // finished, and the app's entry AND its program are gone
        NoChange,   // finished, but nothing changed: cancelled, failed, or still working in another program (Steam)
        Partial,    // finished, and only some of the app went away
    }

    /// <summary>Each signal on its own; none of them alone is taken as "uninstalled".</summary>
    public class UninstallStatus
    {
        public bool TreeRunning;
        public int? ExitCode;
        public bool ExitCodeReliable;      // false when the uninstaller handed off to a copy of itself
        public bool EntryPresent, ProgramPresent, InstallDirPresent;
        public long InstallDirBytes;
        public bool HandsOff;              // Steam & co.: the real work happens in another, already-running program
        public UninstallState State;

        static readonly int[] SuccessCodes = { 0, 3010, 1641 }; // MSI: 3010/1641 = success, restart needed

        public bool ExitCodeSaysFailed { get { return ExitCode.HasValue && ExitCodeReliable && !SuccessCodes.Contains(ExitCode.Value); } }

        public string Describe(string app)
        {
            var parts = new List<string>();
            if (EntryPresent) parts.Add("it's still in Installed apps");
            if (ProgramPresent) parts.Add("its program file is still there");
            else if (InstallDirPresent && InstallDirBytes > 0) parts.Add(Util.FormatSize(InstallDirBytes) + " is left in its program folder");
            string code = ExitCode.HasValue && ExitCodeReliable && ExitCode.Value != 0
                ? " The uninstaller ended with code " + ExitCode.Value + (ExitCode.Value == 1602 ? " (cancelled)" : ExitCode.Value == 1603 ? " (failed)" : "") + "."
                : "";
            switch (State)
            {
                case UninstallState.Running: return "The uninstaller is still running.";
                case UninstallState.Completed:
                    return "The uninstaller finished and " + app + " is no longer installed." +
                           (InstallDirPresent && InstallDirBytes > 0 ? " It left " + Util.FormatSize(InstallDirBytes) + " in the program folder." : "");
                case UninstallState.Partial:
                    return "The uninstaller finished, but only partly: " + string.Join(", ", parts) + "." + code;
                default:
                    return HandsOff
                        ? "Waiting for " + app + " to be removed in the other program's window (Steam, for example)."
                        : "The uninstaller closed without removing " + app + " (" + string.Join(", ", parts) + "). It was probably cancelled or failed." + code;
            }
        }
    }

    /// <summary>
    /// Runs an app's own uninstaller and works out when it has really finished. Many uninstallers (NSIS, Inno,
    /// Squirrel) copy themselves to Temp, start that copy and exit at once; MSI runs in msiexec; Steam hands the
    /// job to the Steam client. So we follow the uninstaller's whole process tree, and judge the result by the
    /// app itself: its Installed-apps entry and its program must both be gone.
    /// </summary>
    public class UninstallRunner
    {
        public readonly AppInfo App;
        readonly Process main;
        readonly DateTime started;
        readonly bool handsOff;
        int? exitCode;
        long dirBytes;
        DateTime sizedAt = DateTime.MinValue;
        readonly Dictionary<int, DateTime?> tracked = new Dictionary<int, DateTime?>();

        UninstallRunner(AppInfo app, Process p, bool handsOff)
        {
            App = app;
            main = p;
            this.handsOff = handsOff;
            started = DateTime.Now.AddSeconds(-2);
            tracked[p.Id] = StartTime(p.Id);
        }

        public static void Command(UninstallEntry e, out string file, out string args)
        {
            string cmd = e.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd) && e.WindowsInstaller && e.KeyName.StartsWith("{")) cmd = "MsiExec.exe /X" + e.KeyName;
            Util.SplitCommand(cmd, out file, out args);
            if (Path.GetFileName(file).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                file = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                args = Regex.Replace(args, @"/I\s*\{", "/X{", RegexOptions.IgnoreCase);
            }
            else if (!File.Exists(file))
                throw new FileNotFoundException("its uninstaller is missing (" + file + ")");
        }

        /// <summary>Starts the uninstaller. Throws Win32Exception 1223 if its admin prompt is declined.</summary>
        public static UninstallRunner Start(AppInfo app)
        {
            string file, args;
            Command(app.Entry, out file, out args);
            string exe = Path.GetFileName(file).ToLowerInvariant();
            bool handsOff = args.IndexOf("steam://", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            new[] { "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe", "battle.net.exe", "upc.exe", "eadesktop.exe" }.Contains(exe);
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            try { psi.WorkingDirectory = Path.GetDirectoryName(file); } catch { }
            Process p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("the uninstaller didn't start");
            return new UninstallRunner(app, p, handsOff);
        }

        public UninstallStatus Status()
        {
            var s = new UninstallStatus { TreeRunning = TreeRunning(), HandsOff = handsOff };
            s.ExitCode = exitCode;
            s.ExitCodeReliable = tracked.Count == 1; // no hand-off copy: the first process's exit code is the real one
            s.EntryPresent = App.Entry != null && Reg.Exists(App.Entry.KeyPath);
            s.ProgramPresent = App.ExePath != null && File.Exists(App.ExePath);
            s.InstallDirPresent = App.InstallDir != null && Directory.Exists(App.InstallDir);
            if (!s.TreeRunning && s.InstallDirPresent && !s.ProgramPresent)
            {
                if (DateTime.Now - sizedAt > TimeSpan.FromSeconds(5)) { dirBytes = Util.DirSize(App.InstallDir, null); sizedAt = DateTime.Now; }
                s.InstallDirBytes = dirBytes;
            }

            // Gone means both: no Installed-apps entry, and no program (or, without a known program file, no program folder).
            bool entryGone = App.Entry == null || !s.EntryPresent;
            bool programGone = App.ExePath != null ? !s.ProgramPresent : (App.InstallDir != null && !s.InstallDirPresent);
            bool anyGone = (App.Entry != null && !s.EntryPresent) || (App.ExePath != null && !s.ProgramPresent) || (App.InstallDir != null && !s.InstallDirPresent);
            if (s.TreeRunning) s.State = UninstallState.Running;
            else if (entryGone && programGone && App.Entry != null) s.State = UninstallState.Completed;
            else if (anyGone) s.State = UninstallState.Partial;
            else s.State = UninstallState.NoChange;
            return s;
        }

        bool TreeRunning()
        {
            List<KeyValuePair<int, int>> procs = Snapshot();   // pid, parent pid
            var alive = new HashSet<int>();
            foreach (var kv in procs) alive.Add(kv.Key);

            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var kv in procs)
                {
                    if (tracked.ContainsKey(kv.Key) || !tracked.ContainsKey(kv.Value)) continue;
                    DateTime? st = StartTime(kv.Key);
                    if (st.HasValue && st.Value < started) continue; // PID reuse: older than our uninstall
                    tracked[kv.Key] = st;
                    grew = true;
                }
            }
            if (!exitCode.HasValue && main.HasExited) { try { exitCode = main.ExitCode; } catch { } }

            foreach (var kv in tracked)
            {
                if (!alive.Contains(kv.Key)) continue;
                DateTime? now = StartTime(kv.Key);
                if (kv.Value.HasValue && now.HasValue && now.Value != kv.Value.Value) continue; // reused PID
                return true;
            }
            return false;
        }

        /// <summary>
        /// Headless use: waits until the uninstall completes or clearly won't. After the process tree ends, results
        /// get a few seconds to settle (MSI and others finish their bookkeeping late); hand-off uninstallers
        /// (Steam) are given until the timeout.
        /// </summary>
        public UninstallStatus Wait(TimeSpan timeout)
        {
            DateTime end = DateTime.Now + timeout;
            DateTime? treeDone = null;
            UninstallStatus s = Status();
            while (DateTime.Now < end)
            {
                s = Status();
                if (s.State == UninstallState.Completed) return s;
                if (s.State != UninstallState.Running)
                {
                    if (!treeDone.HasValue) treeDone = DateTime.Now;
                    if (!handsOff && DateTime.Now - treeDone.Value > TimeSpan.FromSeconds(4)) return s;
                }
                else treeDone = null;
                System.Threading.Thread.Sleep(500);
            }
            return s;
        }

        static DateTime? StartTime(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return p.StartTime; }
            catch { return null; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        static List<KeyValuePair<int, int>> Snapshot()
        {
            var list = new List<KeyValuePair<int, int>>();
            IntPtr snap = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32FirstW(snap, ref e)) return list;
                do list.Add(new KeyValuePair<int, int>((int)e.th32ProcessID, (int)e.th32ParentProcessID));
                while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return list;
        }
    }
}
