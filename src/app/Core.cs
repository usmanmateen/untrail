using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Untrail
{
    public enum ItemKind { Folder, File, Registry, RegistryValue }

    // Ordered best-first so Math.Max gives the weaker of two.
    public enum Confidence { Sure = 0, Likely = 1, Maybe = 2 }

    public class FoundItem
    {
        public string Path;
        public ItemKind Kind;
        public long Size;
        public Confidence Conf;
        public string Category;
        public string Reason;
        public bool NeedsAdmin;
        public bool IsLink;

        public string DisplayPath
        {
            get
            {
                if (Kind == ItemKind.RegistryValue)
                {
                    int bar = Path.LastIndexOf('|');
                    return Path.Substring(0, bar) + "  →  " + Path.Substring(bar + 1);
                }
                return Path;
            }
        }
    }

    public class UninstallEntry
    {
        public string KeyPath;      // e.g. HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}
        public string KeyName;
        public string DisplayName = "", Publisher = "", DisplayVersion = "";
        public string InstallLocation, UninstallString, QuietUninstallString, DisplayIcon, InstallDate;
        public long EstimatedSizeKB;
        public bool WindowsInstaller;
        public bool Hidden;
    }

    public class AppInfo
    {
        public string Name = "", Publisher = "", Version = "";
        public string SourcePath, ExePath, InstallDir, ShortcutPath, ExtraDir;
        public UninstallEntry Entry;
        public List<UninstallEntry> SameApp = new List<UninstallEntry>();
        public List<string> Strong = new List<string>();
        public List<string> Weak = new List<string>();
        public List<string> Pub = new List<string>();
        public List<string> Notes = new List<string>();
        public string InstallDirWarning;
        public DateTime? InstalledAt;   // set by the first scan

        public bool HasUninstaller
        {
            get { return Entry != null && (!string.IsNullOrEmpty(Entry.UninstallString) || Entry.WindowsInstaller); }
        }
    }

    public static class Util
    {
        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string LocalLow = System.IO.Path.Combine(UserProfile, @"AppData\LocalLow");
        public static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static readonly string ProgramFiles = Env("ProgramW6432", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        public static readonly string ProgramFilesX86 = Env("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        public static readonly string CommonFiles = Env("CommonProgramW6432", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        public static readonly string CommonFilesX86 = Env("CommonProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        public static readonly string Temp = NormDir(System.IO.Path.GetTempPath());
        public static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static readonly string PublicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        public static readonly string StartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        public static readonly string CommonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        public static readonly string SavedGames = System.IO.Path.Combine(UserProfile, "Saved Games");
        public static readonly string QuickLaunch = System.IO.Path.Combine(RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch");
        public static readonly string SelfExe = Process.GetCurrentProcess().MainModule.FileName;

        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static Util()
        {
            string users = System.IO.Path.GetDirectoryName(UserProfile);
            string[] list =
            {
                WinDir, ProgramFiles, ProgramFilesX86, CommonFiles, CommonFilesX86,
                System.IO.Path.Combine(ProgramFiles, "WindowsApps"), System.IO.Path.Combine(ProgramFiles, "Microsoft"),
                System.IO.Path.Combine(ProgramFilesX86, "Microsoft"), System.IO.Path.Combine(ProgramFiles, "Windows Defender"),
                ProgramData, System.IO.Path.Combine(ProgramData, "Microsoft"), System.IO.Path.Combine(ProgramData, "Packages"),
                users, System.IO.Path.Combine(users, "Public"), System.IO.Path.Combine(users, "Default"), UserProfile,
                System.IO.Path.Combine(UserProfile, "AppData"), LocalAppData, RoamingAppData, LocalLow,
                System.IO.Path.Combine(LocalAppData, "Programs"), System.IO.Path.Combine(LocalAppData, "Microsoft"),
                System.IO.Path.Combine(LocalAppData, "Packages"), System.IO.Path.Combine(LocalAppData, "Temp"),
                System.IO.Path.Combine(RoamingAppData, "Microsoft"), System.IO.Path.Combine(LocalLow, "Microsoft"),
                Temp, Documents, Desktop, PublicDesktop, StartMenu, CommonStartMenu,
                System.IO.Path.Combine(StartMenu, "Programs"), System.IO.Path.Combine(CommonStartMenu, "Programs"),
                System.IO.Path.Combine(StartMenu, @"Programs\Startup"), System.IO.Path.Combine(CommonStartMenu, @"Programs\Startup"),
                System.IO.Path.Combine(UserProfile, "Downloads"), System.IO.Path.Combine(UserProfile, "Pictures"),
                System.IO.Path.Combine(UserProfile, "Music"), System.IO.Path.Combine(UserProfile, "Videos"),
                System.IO.Path.Combine(UserProfile, "OneDrive"), System.IO.Path.Combine(UserProfile, "Favorites"),
                SavedGames, QuickLaunch, Environment.GetEnvironmentVariable("OneDrive"),
            };
            foreach (string p in list)
            {
                string n = NormDir(p);
                if (n != null) Protected.Add(n);
            }
            foreach (string p in Protected.ToList())
            {
                // Also protect the real target of any redirected (e.g. OneDrive-backed) folder.
                try { if (Directory.Exists(p)) Protected.Add(NormDir(new DirectoryInfo(p).FullName)); } catch { }
            }
        }

        static string Env(string name, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        public static bool IsAdmin
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        public static string NormDir(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"').Trim());
            if (p.Length == 0) return null;
            try { p = System.IO.Path.GetFullPath(p); } catch { return null; }
            return p.TrimEnd('\\');
        }

        public static bool SamePath(string a, string b)
        {
            return a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when path is strictly inside root.</summary>
        public static bool IsUnder(string path, string root)
        {
            if (path == null || root == null) return false;
            root = root.TrimEnd('\\');
            return path.Length > root.Length + 1 && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAtOrUnder(string path, string root)
        {
            return SamePath(path, root) || IsUnder(path, root);
        }

        public static bool IsProtected(string path)
        {
            if (path == null) return true;
            path = path.TrimEnd('\\');
            if (path.Length <= 3) return true;                    // drive roots
            if (IsAtOrUnder(path, WinDir)) return true;
            return Protected.Contains(path);
        }

        /// <summary>A folder/file we would ever be willing to remove.</summary>
        public static bool IsSafeToDelete(string path)
        {
            if (IsProtected(path)) return false;
            foreach (string p in Protected) if (IsUnder(p, path)) return false;
            if (IsAtOrUnder(SelfExe, path)) return false;
            return true;
        }

        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.Ordinal)
        {
            "app","apps","application","applications","program","programs","setup","install","installer","uninstall",
            "update","updater","launcher","bin","data","common","shared","temp","tmp","cache","caches","config","settings",
            "windows","microsoft","system","service","services","client","desktop","tools","tool","helper","runtime",
            "x64","x86","code","main","core","plugin","plugins","default","user","users","local","roaming","programdata",
            "package","packages","squirrel","electron","node","java","web","browser","documents","logs","log","crash",
            "crashpad","crashreports","files","downloads","new","test","demo","free","pro","lite","portable","beta",
            "unins000","uninst","start","run","win","win64","win32","driver","drivers","sdk","support","assets","resources",
        };

        /// <summary>Real words other software uses for its folders too: a name match alone isn't proof of ownership.</summary>
        public static readonly HashSet<string> CommonWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "notes","note","notepad","music","photos","photo","pictures","mail","email","chat","camera","video","videos",
            "player","studio","editor","office","cloud","sync","drive","backup","backups","viewer","reader","scanner",
            "recorder","manager","monitor","terminal","console","assistant","translator","calculator","calendar","weather",
            "maps","news","store","shop","games","game","paint","draw","sketch","explorer","finder","search","messenger",
            "meet","share","files","home","work","notebook","journal","tasks","todo","clock","timer","radio","tv","books",
            "library","media","stream","capture","screenshot","clipboard","password","passwords","vault","wallet","vpn",
            "proxy","server","agent","daemon","tray","widget","widgets","theme","themes","fonts","icons","profiles","profile",
        };

        public static string Key(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        public static bool UsableKey(string k)
        {
            return k != null && k.Length >= 3 && !Generic.Contains(k) && !Regex.IsMatch(k, @"^\d+$");
        }

        /// <summary>"Python 3.12.1 (64-bit)" -> "Python", "7-Zip 23.01 (x64)" -> "7-Zip".</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = Regex.Replace(name, @"\s*[\(\[].*?[\)\]]", "");
            s = Regex.Replace(s, @"\s+(v|version\s*)?\d+(\.\d+)+\S*.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit|amd64|arm64)$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+-\s+shortcut$", "", RegexOptions.IgnoreCase);
            return s.Trim(' ', '-', '_', '.');
        }

        public static string CleanPublisher(string pub)
        {
            if (string.IsNullOrWhiteSpace(pub)) return "";
            string s = Regex.Replace(pub, @"[,\.]?\s+(inc|llc|ltd|limited|corp|corporation|co|gmbh|s\.?a|ag|b\.?v|srl|pty|plc|oy|ab|technologies|software|studios?)\.?$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[,\.]?\s+(inc|llc|ltd|limited|corp|corporation|co)\.?$", "", RegexOptions.IgnoreCase);
            return s.Trim(' ', ',', '.');
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString(mb < 10 ? "0.0" : "0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }

        public static void SplitCommand(string cmd, out string file, out string args)
        {
            cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
            file = cmd; args = "";
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { file = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                return;
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) { file = cmd.Substring(0, i + 4); args = cmd.Substring(i + 4).Trim(); }
        }

        public static string[] SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        public static string[] SafeFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern); } catch { return new string[0]; }
        }

        public static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return false; }
        }

        public static long DirSize(string dir, Func<bool> cancelled)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                if (cancelled != null && cancelled()) break;
                string d = stack.Pop();
                try
                {
                    foreach (FileSystemInfo f in new DirectoryInfo(d).EnumerateFileSystemInfos())
                    {
                        if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var fi = f as FileInfo;
                        if (fi != null) total += fi.Length; else stack.Push(f.FullName);
                    }
                }
                catch { }
            }
            return total;
        }

        [ThreadStatic] static object wsh;

        public static string ResolveShortcut(string lnk)
        {
            string args;
            return ResolveShortcut(lnk, out args);
        }

        public static string ResolveShortcut(string lnk, out string args)
        {
            args = "";
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (wsh == null) wsh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, wsh, new object[] { lnk });
                string target = (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                args = (string)sc.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null) ?? "";
                Marshal.FinalReleaseComObject(sc);
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }

    public static class Reg
    {
        public static RegistryKey Base(string hive)
        {
            RegistryHive h = hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            return RegistryKey.OpenBaseKey(h, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        }

        public static void Split(string path, out string hive, out string sub)
        {
            int i = path.IndexOf('\\');
            hive = path.Substring(0, i);
            sub = path.Substring(i + 1);
        }

        public static RegistryKey Open(string path, bool writable)
        {
            string hive, sub;
            Split(path, out hive, out sub);
            using (RegistryKey root = Base(hive)) return root.OpenSubKey(sub, writable);
        }

        public static bool Exists(string path)
        {
            try
            {
                using (RegistryKey k = Open(path, false)) return k != null;
            }
            catch { return false; }
        }

        public static bool ValueExists(string keyPath, string name)
        {
            try
            {
                using (RegistryKey k = Open(keyPath, false)) return k != null && k.GetValue(name) != null;
            }
            catch { return false; }
        }

        public static string Str(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            return v == null ? null : v.ToString();
        }

        public static long Num(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            if (v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
    }
}
