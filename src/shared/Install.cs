using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Untrail
{
    /// <summary>Where Untrail installs itself (per user, no admin) and how it registers in Installed apps.</summary>
    static class InstallInfo
    {
        public const string RepoUrl = "https://github.com/usmanmateen/untrail";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Untrail";
        /// <summary>Untrail's own settings (the update check); removed when Untrail is uninstalled.</summary>
        public const string SettingsKey = @"Software\Untrail";

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Untrail");
        public static readonly string Exe = Path.Combine(Dir, "Untrail.exe");
        public static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Untrail.lnk");
        public static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Untrail.lnk");

        public static bool IsInstalled { get { return File.Exists(Exe); } }

        public static void CreateShortcut(string lnk, string target)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Uninstall apps completely" });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(sh); }
        }

        public static void Register(string version)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Untrail");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "Untrail");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall-self");
                k.SetValue("QuietUninstallString", "\"" + Exe + "\" --uninstall-self --quiet");
                k.SetValue("URLInfoAbout", RepoUrl);
                k.SetValue("HelpLink", RepoUrl + "/issues");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>Closes any running Untrail.exe from the install folder (other than this process).</summary>
        public static void CloseRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("Untrail"))
            {
                try
                {
                    if (p.Id != me && string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        public static bool IsRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("Untrail"))
            {
                try { if (p.Id != me && string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        /// <summary>Removes shortcuts and the Installed-apps entry, then deletes the install folder once this process has exited.</summary>
        public static void Uninstall(string selfExe)
        {
            CloseRunning();
            foreach (string lnk in new[] { StartMenuLink, DesktopLink })
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false); } catch { }

            // A running exe can't delete itself: a hidden cmd waits for us to exit, then removes the folder.
            if (string.Equals(Path.GetDirectoryName(selfExe), Dir, StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + Dir + "\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
                };
                Process.Start(psi);
            }
            else if (Directory.Exists(Dir))
            {
                try { Directory.Delete(Dir, true); } catch { }
            }
        }
    }
}
