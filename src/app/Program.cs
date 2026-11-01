using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

            if (args.Length >= 2 && args[0] == "--job") return Deleter.RunJobFile(args[1]);
            if (args.Length >= 2 && args[0] == "--scan") return CliScan(args);

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
        /// Read-only report, for checking what a scan would pick up:
        ///   Untrail.exe --scan "path\to\app.lnk" --out report.txt [--deep] [--write-job job.txt]
        /// --write-job saves the "Sure" items as a job file that "--job" can run (used for testing).
        /// </summary>
        static int CliScan(string[] args)
        {
            string path = args[1];
            string outFile = Arg(args, "--out");
            string jobFile = Arg(args, "--write-job");
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

            if (app == null)
            {
                sb.AppendLine("ERROR: " + error);
            }
            else
            {
                sb.AppendLine("App:         " + app.Name);
                sb.AppendLine("Publisher:   " + app.Publisher + "   Version: " + app.Version);
                sb.AppendLine("Exe:         " + app.ExePath);
                sb.AppendLine("Install dir: " + app.InstallDir);
                sb.AppendLine("Entry:       " + (app.Entry != null ? app.Entry.KeyPath : "(none)"));
                sb.AppendLine("Uninstaller: " + (app.Entry != null ? app.Entry.UninstallString : ""));
                sb.AppendLine("Keys:        strong=[" + string.Join(",", app.Strong) + "] weak=[" + string.Join(",", app.Weak) + "] pub=[" + string.Join(",", app.Pub) + "]");
                foreach (string n in app.Notes) sb.AppendLine("Note:        " + n);
                List<FoundItem> items = Scanner.Scan(app, entries, deep, null, null);
                sb.AppendLine();
                foreach (FoundItem it in items.OrderBy(i => Array.IndexOf(Cat.Order, i.Category)).ThenBy(i => i.Conf))
                    sb.AppendLine(string.Format("[{0,-6}] {1,-9} {2,10}  {3}   <- {4}{5}", it.Conf, it.Kind, it.Kind >= ItemKind.Registry ? "-" : Util.FormatSize(it.Size),
                        it.DisplayPath, it.Reason, it.NeedsAdmin ? " (admin)" : ""));
                sb.AppendLine();
                sb.AppendLine("Sure total: " + Util.FormatSize(items.Where(i => i.Conf == Confidence.Sure).Sum(i => i.Size)) +
                              "   All: " + Util.FormatSize(items.Sum(i => i.Size)));
                if (jobFile != null)
                    Deleter.WriteJob(jobFile, items.Where(i => i.Conf == Confidence.Sure), false, Deleter.NewBackupDir(app.Name));
            }
            if (outFile != null) File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            else Console.Write(sb.ToString());
            return app == null ? 1 : 0;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
