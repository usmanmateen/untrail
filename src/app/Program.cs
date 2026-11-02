using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Untrail
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Paths over 260 characters (deep app folders). Must run before anything touches System.IO;
            // together with longPathAware in app.manifest.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            if (args.Length >= 3 && args[0] == "--elevated-helper") return Deleter.RunHelper(args[1], args[2]);
            if (args.Contains("--hide-user")) { Util.HideUser = true; args = args.Where(a => a != "--hide-user").ToArray(); }
            if (args.Contains("--no-update-check")) { Updater.Disabled = true; args = args.Where(a => a != "--no-update-check").ToArray(); }
            if (args.Length >= 2 && args[0] == "--update-selftest") return UpdateSelfTest(args[1]);
            if (args.Length >= 3 && args[0] == "--helper-test") return HelperTest(args[1], args[2]);
            if (args.Length >= 2 && args[0] == "--scan") return CliScan(args, false);
            if (args.Length >= 2 && args[0] == "--purge") return CliScan(args, true);
            if (args.Length >= 2 && args[0] == "--selftest") return SelfTest(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 1 && args[0] == "--uninstall-self") return UninstallSelf(args.Contains("--quiet"));
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
            return 0;
        }

        /// <summary>Called by Windows' Settings > Apps > Untrail > Uninstall.</summary>
        static int UninstallSelf(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall Untrail from this PC?\n\nYour backups in Documents\\Untrail Backups are kept.",
                    "Uninstall Untrail", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 1;
            InstallInfo.Uninstall(Util.SelfExe);
            if (!quiet) MessageBox.Show("Untrail has been removed.", "Untrail", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>
        /// Headless modes (used by the integration tests too):
        ///   Untrail.exe --scan  "path\to\app.lnk" --out report.txt [--deep]
        ///       Read-only: lists what the window would show.
        ///   Untrail.exe --purge "path\to\app.lnk" --yes --out report.txt [--run-uninstaller] [--permanent] [--via-helper]
        ///       Removes only the "Safe to remove" items, exactly as the window would, then re-scans.
        ///       --via-helper sends the delete through the admin helper's pipe even when it isn't needed.
        /// Exit codes: 0 ok, 1 couldn't identify, 2 some items failed, 3 leftovers remain, 4 uninstaller didn't finish.
        /// </summary>
        static int CliScan(string[] args, bool purge)
        {
            string path = args[1];
            string outFile = Arg(args, "--out");
            bool deep = args.Contains("--deep");
            var sb = new StringBuilder();
            List<UninstallEntry> entries = Scanner.GetUninstallEntries();
            AppInfo app;
            string error = null;
            if (path.StartsWith("entry:"))
            {
                string name = path.Substring(6);
                UninstallEntry e = entries.FirstOrDefault(x => x.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                app = e == null ? null : Scanner.FromEntry(e, entries);
                if (e == null) error = "No installed app called " + name;
            }
            else app = Scanner.Identify(path, entries, out error);

            int code = 0;
            if (app == null)
            {
                sb.AppendLine("ERROR: " + error);
                code = 1;
            }
            else
            {
                sb.AppendLine("App:         " + app.Name);
                sb.AppendLine("Publisher:   " + app.Publisher + "   Version: " + app.Version);
                sb.AppendLine("Exe:         " + app.ExePath);
                sb.AppendLine("Entry:       " + (app.Entry != null ? app.Entry.KeyPath : "(none)"));
                sb.AppendLine("Uninstaller: " + (app.Entry != null ? app.Entry.UninstallString : ""));
                sb.AppendLine("Keys:        strong=[" + string.Join(",", app.Strong) + "] weak=[" + string.Join(",", app.Weak) + "] pub=[" + string.Join(",", app.Pub) + "]");
                foreach (string n in app.Notes) sb.AppendLine("Note:        " + n);
                List<FoundItem> items = Scanner.Scan(app, entries, deep, null, null);
                sb.AppendLine("Install dir: " + app.InstallDir);
                sb.AppendLine();
                Report(sb, items);
                sb.AppendLine("Sure total: " + Util.FormatSize(items.Where(i => i.Conf == Confidence.Sure).Sum(i => i.Size)) +
                              "   All: " + Util.FormatSize(items.Sum(i => i.Size)));
                if (purge) code = Purge(args, app, items, sb);
            }
            if (outFile != null) File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            else Console.Write(sb.ToString());
            return code;
        }

        static void Report(StringBuilder sb, IEnumerable<FoundItem> items)
        {
            foreach (FoundItem it in items.OrderBy(i => Array.IndexOf(Cat.Order, i.Category)).ThenBy(i => i.Conf))
                sb.AppendLine(string.Format("[{0,-6}] {1,-13} {2,10}  {3}   <- {4}{5}", it.Conf, it.Kind, it.Kind >= ItemKind.Registry ? "-" : Util.FormatSize(it.Size),
                    it.DisplayPath, it.Reason, it.NeedsAdmin ? " (admin)" : ""));
        }

        static int Purge(string[] args, AppInfo app, List<FoundItem> items, StringBuilder sb)
        {
            if (!args.Contains("--yes")) { sb.AppendLine("Refusing to delete without --yes."); return 1; }
            sb.AppendLine();
            // Exactly what the window does with its default selection: only "Safe to remove" items.
            // --include-review also selects "Review"/"Keep" items, like ticking everything in the window.
            bool all = args.Contains("--include-review");
            var session = new RemovalSession(app, items, items.Where(i => all || i.Conf == Confidence.Sure).ToList(), new DeleteOptions
            {
                Recycle = !args.Contains("--permanent"),
                BackupDir = Deleter.NewBackupDir(app.Name),
                SimulateBackupFailure = args.Contains("--simulate-backup-failure"),
            });
            session.Measure();
            if (args.Contains("--run-uninstaller") && app.HasUninstaller)
            {
                session.Uninstall = UninstallRunner.Start(app).Wait(TimeSpan.FromMinutes(5));
                if (session.Uninstall.State != UninstallState.Completed)
                {
                    if (!args.Contains("--clean-anyway"))
                    {
                        // A cancelled, failed or partial uninstall never leads to cleanup on its own.
                        session.Stopped = true;
                        sb.Append(session.ToText());
                        sb.AppendLine("STOPPED   the uninstaller didn't complete, so nothing was deleted");
                        return 4;
                    }
                    session.ManualCleanup = true;
                }
            }

            List<FoundItem> toDelete = session.AfterUninstaller();
            bool needAdmin = toDelete.Any(i => i.NeedsAdmin) && !Util.IsAdmin;
            session.Record(toDelete.Count == 0 ? new List<DeleteResult>()
                : needAdmin || args.Contains("--via-helper") ? Deleter.RunElevated(toDelete, session.Options, session.Scope, null, needAdmin)
                : Deleter.Run(toDelete, session.Options, session.Scope, null));
            session.Verify(null);
            sb.Append(session.ToText());
            if (session.Count(Outcome.Failed) > 0) return 2;
            if (session.SafeRemaining > 0 || !session.Verified) return 3;
            if (session.Count(Outcome.PendingRestart) > 0) return 5;
            return 0;
        }

        /// <summary>Checks the delete allowlist (Guard) against paths that must never be deleted. Nothing is touched.</summary>
        static int SelfTest(string outFile)
        {
            string win = Util.WinDir, pf = Util.ProgramFiles, cv = @"\Microsoft\Windows\CurrentVersion\";
            var refuse = new List<FoundItem>
            {
                F(win), F(Path.Combine(win, "System32")), F(pf), F(Util.ProgramFilesX86), F(Util.ProgramData), F(Util.UserProfile),
                F(Util.LocalAppData), F(Util.RoamingAppData), F(Util.Documents), F(Util.Desktop), F(Path.GetPathRoot(win)),
                F(Path.GetDirectoryName(Util.UserProfile)), F(Path.Combine(pf, "WindowsApps")), F(Util.SelfExe), F(@"\\server\share\app"),
                F(Path.Combine(Util.LocalAppData, "Programs")), F(Path.Combine(Util.StartMenu, "Programs")),
                K(@"HKLM\SOFTWARE\Microsoft"), K(@"HKLM\SOFTWARE\Microsoft\Windows"), K(@"HKLM\SYSTEM\CurrentControlSet\Services\Foo"),
                K(@"HKCU\Software\Classes\Foo"), K(@"HKLM\SOFTWARE\Policies\Foo"), K(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Foo"),
                K(@"HKCU\Software"), K(@"HKLM\SOFTWARE"), K(@"HKCU\Software\Vendor\App\Deep"), K(@"HKLM\SOFTWARE" + cv + "Uninstall"),
                K(@"HKLM\SOFTWARE" + cv + "Run"), K(@"HKCU\Software" + cv + "Uninstall\\App\\Sub"), K(@"HKCU\Environment"),
                K(@"HKCU\Software\Foo\\Bar"), V(@"HKLM\SYSTEM\Setup|CmdLine"), V(@"HKCU\Software\Vendor|Value"),
            };
            var allow = new List<FoundItem>
            {
                F(Path.Combine(pf, "Some Vendor App")), F(Path.Combine(Util.RoamingAppData, "SomeApp")), F(Path.Combine(Util.LocalAppData, @"Programs\SomeApp")),
                K(@"HKCU\Software\SomeApp"), K(@"HKCU\Software\Vendor\SomeApp"), K(@"HKLM\SOFTWARE\WOW6432Node\Vendor"),
                K(@"HKLM\SOFTWARE" + cv + "Uninstall\\{GUID}"), K(@"HKLM\SOFTWARE\WOW6432Node" + cv + "Uninstall\\App"),
                K(@"HKCU\Software" + cv + "App Paths\\app.exe"), V(@"HKCU\Software" + cv + "Run|SomeApp"),
            };
            var sb = new StringBuilder();
            int bad = 0;
            foreach (FoundItem it in refuse)
            {
                bool ok = Guard.Check(it) != null;
                if (!ok) bad++;
                sb.AppendLine((ok ? "PASS refuse " : "FAIL allowed ") + it.Path);
            }
            foreach (FoundItem it in allow)
            {
                string why = Guard.Check(it);
                if (why != null) bad++;
                sb.AppendLine((why == null ? "PASS allow  " : "FAIL refused ") + it.Path + (why != null ? "  -- " + why : ""));
            }
            sb.AppendLine(bad == 0 ? "ALL PASSED" : bad + " FAILED");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            return bad == 0 ? 0 : 1;
        }

        /// <summary>
        /// Test (offline): version comparison, reading a GitHub release, refusing download links outside this repo,
        /// and reading SHA256SUMS.txt. Writes PASS/FAIL lines to outFile.
        /// </summary>
        static int UpdateSelfTest(string outFile)
        {
            var sb = new StringBuilder();
            int bad = 0;
            Action<bool, string> check = (ok, what) => { if (!ok) bad++; sb.AppendLine((ok ? "PASS " : "FAIL ") + what); };

            check(Updater.IsNewer("1.1.4", "1.1.3"), "1.1.4 is newer than 1.1.3");
            check(Updater.IsNewer("v1.10.0", "1.9.9"), "1.10.0 is newer than 1.9.9 (numeric, not text)");
            check(!Updater.IsNewer("1.1.3", "1.1.3"), "same version is not newer");
            check(!Updater.IsNewer("1.1.2", "1.1.3"), "older version is not newer");
            check(!Updater.IsNewer("nightly", "1.1.3"), "a tag that isn't a version is ignored");

            string repo = InstallInfo.RepoUrl;
            string json = "{\"tag_name\":\"v9.8.7\",\"html_url\":\"" + repo + "/releases/tag/v9.8.7\",\"assets\":["
                + "{\"name\":\"UntrailSetup.exe\",\"browser_download_url\":\"" + repo + "/releases/download/v9.8.7/UntrailSetup.exe\"},"
                + "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"" + repo + "/releases/download/v9.8.7/SHA256SUMS.txt\"}]}";
            Updater.Release r = Updater.Parse(json);
            check(r.Version == "9.8.7", "release version read from the tag");
            check(r.SetupUrl != null && r.SetupUrl.EndsWith("/v9.8.7/UntrailSetup.exe"), "installer link found");
            check(r.SumsUrl != null && r.SumsUrl.EndsWith("/v9.8.7/SHA256SUMS.txt"), "checksum file link found");

            string evil = json.Replace(repo + "/releases/download/v9.8.7/UntrailSetup.exe", "https://example.com/UntrailSetup.exe")
                              .Replace(repo + "/releases/tag/v9.8.7", "https://example.com/notes");
            Updater.Release e = Updater.Parse(evil);
            check(e.SetupUrl == null, "installer link outside this repo's releases is refused");
            check(e.NotesUrl == repo + "/releases/latest", "release-notes link outside this repo is replaced");

            string hash = new string('a', 64);
            string sums = "1111111111111111111111111111111111111111111111111111111111111111  Untrail.exe\r\n" + hash + "  UntrailSetup.exe\r\n";
            check(Updater.ExpectedHash(sums, "UntrailSetup.exe") == hash, "installer hash read from SHA256SUMS.txt (CRLF lines)");
            check(Updater.ExpectedHash(sums, "UntrailSetup.zip") == null, "no hash for a file that isn't listed");

            sb.AppendLine(bad == 0 ? "ALL PASSED" : bad + " FAILED");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            return bad == 0 ? 0 : 1;
        }

        /// <summary>
        /// Test: sends the admin helper items that don't belong to the app (a decoy folder and registry key) under a
        /// made-up app identity, and checks it refuses them. Writes PASS/FAIL lines to outFile.
        /// </summary>
        static int HelperTest(string decoyDir, string outFile)
        {
            var scope = new AppScope { Name = "Zephyr Scope Test", Strong = { "zephyrscopetest" }, Pub = { "zephyrlabs" } };
            var items = new List<FoundItem>
            {
                new FoundItem { Kind = ItemKind.Folder, Path = decoyDir, RealPath = Util.FinalPath(decoyDir) },
                new FoundItem { Kind = ItemKind.Registry, Path = @"HKCU\Software\Zephyr Decoy Vendor" },
            };
            var opts = new DeleteOptions { Recycle = false, BackupDir = Deleter.NewBackupDir("Zephyr Scope Test") };
            List<DeleteResult> results = Deleter.RunElevated(items, opts, scope, null, false);
            var sb = new StringBuilder();
            int bad = 0;
            foreach (DeleteResult r in results)
            {
                bool refused = r.Outcome == Outcome.Failed && r.Error != null && r.Error.StartsWith("Refused") && Scanner.StillExists(r.Item);
                if (!refused) bad++;
                sb.AppendLine((refused ? "PASS refused " : "FAIL not refused ") + r.Item.Path + "  -- " + r.Error);
            }
            try { Directory.Delete(opts.BackupDir, true); } catch { }
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            return bad == 0 ? 0 : 1;
        }

        static FoundItem F(string p) { return new FoundItem { Kind = ItemKind.Folder, Path = p }; }
        static FoundItem K(string p) { return new FoundItem { Kind = ItemKind.Registry, Path = p }; }
        static FoundItem V(string p) { return new FoundItem { Kind = ItemKind.RegistryValue, Path = p }; }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
