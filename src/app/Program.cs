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
            if (args.Contains("--run-uninstaller") && app.HasUninstaller)
            {
                UninstallRunner run = UninstallRunner.Start(app);
                bool finished = run.WaitDone(TimeSpan.FromMinutes(5));
                sb.AppendLine("UNINSTALLER exit code " + run.ExitCode + (finished ? ", finished" : ", DID NOT FINISH"));
                if (!finished) return 4;
            }

            List<FoundItem> selected = items.Where(i => i.Conf == Confidence.Sure && Scanner.StillExists(i)).ToList();
            string backup = Deleter.NewBackupDir(app.Name);
            bool recycle = !args.Contains("--permanent");
            bool needAdmin = selected.Any(i => i.NeedsAdmin) && !Util.IsAdmin;
            List<DeleteResult> results = selected.Count == 0 ? new List<DeleteResult>()
                : needAdmin || args.Contains("--via-helper") ? Deleter.RunElevated(selected, recycle, backup, null, needAdmin)
                : Deleter.Run(selected, recycle, backup, null);
            foreach (DeleteResult r in results)
                sb.AppendLine((r.Ok ? "DELETED   " : "FAILED    ") + r.Item.DisplayPath + (r.Error != null ? "  -- " + r.Error : ""));
            sb.AppendLine("BACKUP    " + backup);

            // Verify: scan again with the same identity and report anything "Safe to remove" that is still there.
            List<FoundItem> again = Scanner.Scan(app, Scanner.GetUninstallEntries(), false, null, null);
            foreach (FoundItem it in again.Where(i => i.Conf == Confidence.Sure))
                sb.AppendLine("REMAINING " + it.DisplayPath);
            if (results.Any(r => !r.Ok)) return 2;
            return again.Any(i => i.Conf == Confidence.Sure) ? 3 : 0;
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
