using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Untrail
{
    /// <summary>
    /// Runs an app's own uninstaller and works out when it has really finished: many uninstallers
    /// (NSIS, Inno, Squirrel) copy themselves to Temp, start that copy and exit at once, and some
    /// (Steam) hand the job to another program. So we follow the uninstaller's whole process tree
    /// and also watch whether the app itself has gone.
    /// </summary>
    public class UninstallRunner
    {
        public readonly AppInfo App;
        public int? ExitCode { get; private set; }
        readonly Process main;
        readonly DateTime started;
        readonly Dictionary<int, DateTime?> tracked = new Dictionary<int, DateTime?>();

        UninstallRunner(AppInfo app, Process p)
        {
            App = app;
            main = p;
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
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            try { psi.WorkingDirectory = Path.GetDirectoryName(file); } catch { }
            Process p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("the uninstaller didn't start");
            return new UninstallRunner(app, p);
        }

        /// <summary>True while the uninstaller or anything it started is still running.</summary>
        public bool Running
        {
            get
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
                if (!ExitCode.HasValue && main.HasExited) { try { ExitCode = main.ExitCode; } catch { ExitCode = -1; } }

                foreach (var kv in tracked)
                {
                    if (!alive.Contains(kv.Key)) continue;
                    DateTime? now = StartTime(kv.Key);
                    if (kv.Value.HasValue && now.HasValue && now.Value != kv.Value.Value) continue; // reused PID
                    return true;
                }
                return false;
            }
        }

        /// <summary>The uninstaller has done its job: its Installed-apps entry, program file or folder has gone.</summary>
        public static bool AppGone(AppInfo app)
        {
            if (app.Entry != null && !Reg.Exists(app.Entry.KeyPath)) return true;
            if (app.ExePath != null && !File.Exists(app.ExePath)) return true;
            if (app.InstallDir != null && !Directory.Exists(app.InstallDir)) return true;
            return false;
        }

        /// <summary>Blocks until the uninstaller is finished and the app is gone, or the timeout passes. For headless use.</summary>
        public bool WaitDone(TimeSpan timeout)
        {
            DateTime end = DateTime.Now + timeout;
            while (DateTime.Now < end)
            {
                if (!Running && AppGone(App)) return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
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
