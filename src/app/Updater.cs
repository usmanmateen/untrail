using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Untrail
{
    /// <summary>
    /// Update check against GitHub's public releases API: one HTTPS request, at most once a day, that sends nothing
    /// about the user or their apps. It's Untrail's only network use, and can be turned off from the window's
    /// settings menu (stored as HKCU\Software\Untrail\CheckForUpdates = 0) or with --no-update-check.
    /// </summary>
    static class Updater
    {
        const string Api = "https://api.github.com/repos/usmanmateen/untrail/releases/latest";
        const string DownloadPrefix = InstallInfo.RepoUrl + "/releases/download/";
        static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        /// <summary>Set by --no-update-check (the demo recorder uses it): no request for the whole run.</summary>
        public static bool Disabled;

        public class Release
        {
            public string Version, NotesUrl, SetupUrl, SumsUrl;
        }

        static string UserAgent { get { return "Untrail/" + BuildInfo.Version + " (+" + InstallInfo.RepoUrl + ")"; } }

        /// <summary>Local builds are "0.0.0-dev": they never look for updates.</summary>
        public static bool IsDevBuild { get { return BuildInfo.Version.StartsWith("0.0.0"); } }

        public static bool AutoCheck
        {
            get { return Convert.ToInt32(Get("CheckForUpdates", 1)) != 0; }
            set { Set("CheckForUpdates", value ? 1 : 0); }
        }

        public static string SkippedVersion
        {
            get { return Get("SkippedVersion", null) as string; }
            set { Set("SkippedVersion", value ?? ""); }
        }

        /// <summary>True when this is the installed copy, so the installer can replace it in place.</summary>
        public static bool CanUpdateInPlace
        {
            get { return string.Equals(Util.SelfExe, InstallInfo.Exe, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// The once-a-day check at start-up. Calls <paramref name="onNewer"/> (on a worker thread) when a newer release
        /// exists that the user hasn't skipped. Failures stay silent: an offline PC just checks again next time.
        /// </summary>
        public static void CheckInBackground(Action<Release> onNewer)
        {
            if (Disabled || IsDevBuild || !AutoCheck) return;
            DateTime last;
            if (DateTime.TryParse(Get("LastUpdateCheck", "") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)
                && DateTime.UtcNow - last < Interval)
                return;
            Task.Run(() =>
            {
                try
                {
                    Release r = Fetch(15000);
                    Set("LastUpdateCheck", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    if (IsNewer(r.Version, BuildInfo.Version) && r.Version != SkippedVersion) onNewer(r);
                }
                catch { }
            });
        }

        public static Release Fetch(int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json";
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Parse(sr.ReadToEnd());
        }

        /// <summary>Reads the fields we need from a GitHub release. Download links must point at this repo's releases.</summary>
        public static Release Parse(string json)
        {
            var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var r = new Release { Version = ((string)o["tag_name"]).TrimStart('v', 'V'), NotesUrl = o["html_url"] as string };
            object assets;
            if (o.TryGetValue("assets", out assets) && assets is IEnumerable)
            {
                foreach (object a in (IEnumerable)assets)
                {
                    var d = a as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = d["name"] as string, url = d["browser_download_url"] as string;
                    if (url == null || !url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name == "UntrailSetup.exe") r.SetupUrl = url;
                    else if (name == "SHA256SUMS.txt") r.SumsUrl = url;
                }
            }
            if (r.NotesUrl == null || !r.NotesUrl.StartsWith(InstallInfo.RepoUrl + "/", StringComparison.OrdinalIgnoreCase))
                r.NotesUrl = InstallInfo.RepoUrl + "/releases/latest";
            return r;
        }

        public static bool IsNewer(string candidate, string current)
        {
            Version a = Numeric(candidate), b = Numeric(current);
            return a != null && b != null && a > b;
        }

        static Version Numeric(string v)
        {
            Match m = Regex.Match(v ?? "", @"^v?(\d+)\.(\d+)\.(\d+)");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        /// <summary>The SHA-256 listed for <paramref name="file"/> in a SHA256SUMS.txt ("hash  name" lines), or null.</summary>
        public static string ExpectedHash(string sums, string file)
        {
            foreach (string line in sums.Split('\n'))
            {
                Match m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                if (m.Success && m.Groups[2].Value.Trim() == file) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Downloads the new installer and the release's SHA256SUMS.txt, checks the installer against it, then starts a
        /// silent install that reopens Untrail. The caller must exit straight after, so the installer can replace this exe.
        /// Throws (and runs nothing) if anything doesn't check out.
        /// </summary>
        public static void DownloadAndInstall(Release r)
        {
            if (r.SetupUrl == null || r.SumsUrl == null) throw new InvalidOperationException("This release doesn't include an installer to download.");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string dir = Path.Combine(Path.GetTempPath(), "Untrail-update-" + r.Version);
            Directory.CreateDirectory(dir);
            string setup = Path.Combine(dir, "UntrailSetup.exe");
            string sums;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                wc.DownloadFile(r.SetupUrl, setup);
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                sums = wc.DownloadString(r.SumsUrl);
            }
            string expected = ExpectedHash(sums, "UntrailSetup.exe");
            if (expected == null || expected != Sha256(setup))
            {
                try { File.Delete(setup); } catch { }
                throw new InvalidDataException("The downloaded installer didn't match the checksum published with the release, so it wasn't run.");
            }
            string args = "/S /launch" + (File.Exists(InstallInfo.DesktopLink) ? " /desktop" : "");
            Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = false, WorkingDirectory = dir });
        }

        /// <summary>Removes installers left in Temp by earlier updates (Untrail shouldn't leave its own leftovers).</summary>
        public static void CleanUp()
        {
            try
            {
                foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "Untrail-update-*"))
                    try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        static object Get(string name, object fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(InstallInfo.SettingsKey))
                    return k == null ? fallback : (k.GetValue(name, fallback) ?? fallback);
            }
            catch { return fallback; }
        }

        static void Set(string name, object value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(InstallInfo.SettingsKey))
                    k.SetValue(name, value);
            }
            catch { }
        }
    }
}
