using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Untrail
{
    /// <summary>What happened on one drive. File sizes are logical sizes; FreeChange is the measured change in free space.</summary>
    public class DriveReport
    {
        public string Drive;
        public long ByUninstaller, Deleted, Recycled, Pending, Left;
        public long? FreeBefore, FreeAfter;
        public long? FreeChange { get { return FreeBefore.HasValue && FreeAfter.HasValue ? FreeAfter.Value - FreeBefore.Value : (long?)null; } }
    }

    /// <summary>
    /// One removal, start to finish: measure, run the app's uninstaller, delete what's left, scan again.
    /// Shared by the window and the headless --purge mode so both report exactly the same way.
    /// Nothing is counted twice: what the uninstaller removed is measured before Untrail deletes anything.
    /// </summary>
    public class RemovalSession
    {
        public readonly AppInfo App;
        public readonly List<FoundItem> Scanned, Selected;
        public readonly DeleteOptions Options;
        public readonly AppScope Scope;
        public readonly Dictionary<string, DriveReport> Drives = new Dictionary<string, DriveReport>(StringComparer.OrdinalIgnoreCase);
        public UninstallStatus Uninstall;          // null when the uninstaller wasn't run
        public bool ManualCleanup;                 // the user chose to clean up although the uninstaller didn't complete
        public List<DeleteResult> Results = new List<DeleteResult>();
        public List<FoundItem> Remaining = new List<FoundItem>();
        public bool Verified;                      // the follow-up scan ran to the end
        public bool Stopped;                       // the uninstaller didn't complete and nothing was deleted
        Dictionary<FoundItem, long> before;

        public RemovalSession(AppInfo app, List<FoundItem> scanned, List<FoundItem> selected, DeleteOptions options)
        {
            App = app;
            Scanned = scanned;
            Selected = selected;
            Options = options;
            Scope = AppScope.From(app);
        }

        DriveReport D(string path)
        {
            string d = Util.DriveOf(path);
            if (d == null) return null;
            DriveReport r;
            if (!Drives.TryGetValue(d, out r)) Drives[d] = r = new DriveReport { Drive = d };
            return r;
        }

        static long SizeNow(FoundItem it)
        {
            try
            {
                if (it.Kind == ItemKind.Folder) return Directory.Exists(it.Path) && !Util.IsReparse(it.Path) ? Util.DirSize(it.Path, null) : 0;
                if (it.Kind == ItemKind.File) return File.Exists(it.Path) ? new FileInfo(it.Path).Length : 0;
            }
            catch { }
            return 0;
        }

        /// <summary>Step 1: sizes of everything found, and free space, before anything changes.</summary>
        public void Measure()
        {
            before = Scanned.ToDictionary(i => i, SizeNow);
            foreach (FoundItem it in Scanned) D(it.Path);
            Dictionary<string, long> free = Util.FreeSpace(Drives.Keys);
            foreach (DriveReport d in Drives.Values) { long f; if (free.TryGetValue(d.Drive, out f)) d.FreeBefore = f; }
        }

        /// <summary>Step 2 (after the app's uninstaller): what it removed, and which selected items are still there to delete.</summary>
        public List<FoundItem> AfterUninstaller()
        {
            foreach (FoundItem it in Scanned)
            {
                long now = SizeNow(it);
                DriveReport d = D(it.Path);
                if (d != null) d.ByUninstaller += Math.Max(0, before[it] - now);
                it.Size = now;
            }
            return Selected.Where(Scanner.StillExists).ToList();
        }

        /// <summary>Step 3: what Untrail itself did.</summary>
        public void Record(List<DeleteResult> results)
        {
            Results = results;
            foreach (DeleteResult r in results)
            {
                DriveReport d = D(r.Item.Path);
                if (d == null) continue;
                d.Deleted += r.Deleted; d.Recycled += r.Recycled; d.Pending += r.Pending; d.Left += r.Left;
            }
        }

        /// <summary>Step 4: scan again with the same identity, and measure free space again.</summary>
        public void Verify(Action<string> progress)
        {
            try
            {
                Remaining = Scanner.Scan(App, Scanner.GetUninstallEntries(), false, progress, null);
                Verified = true;
            }
            catch
            {
                Remaining = Selected.Where(Scanner.StillExists).ToList();
                Verified = false;
            }
            Dictionary<string, long> free = Util.FreeSpace(Drives.Keys);
            foreach (DriveReport d in Drives.Values) { long f; if (free.TryGetValue(d.Drive, out f)) d.FreeAfter = f; }
        }

        public int Count(Outcome o) { return Results.Count(r => r.Outcome == o); }
        public int SafeRemaining { get { return Remaining.Count(i => i.Conf == Confidence.Sure); } }
        public int ReviewRemaining { get { return Remaining.Count(i => i.Conf != Confidence.Sure); } }
        public bool Clean { get { return Verified && Count(Outcome.Failed) == 0 && Count(Outcome.PendingRestart) == 0 && SafeRemaining == 0; } }

        /// <summary>The one-line outcome. Only claims "nothing left" when a finished scan supports it.</summary>
        public string Headline()
        {
            if (Stopped) return "Stopped: the app's uninstaller didn't complete, so nothing was deleted.";
            if (!Verified) return "Cleanup finished, but the follow-up scan didn't complete, so Untrail can't confirm what's left.";
            int failed = Count(Outcome.Failed), pending = Count(Outcome.PendingRestart);
            if (failed > 0) return "Cleanup finished with problems: " + failed + " item(s) couldn't be removed.";
            if (pending > 0) return "Cleanup almost complete: " + pending + " item(s) will be removed when you restart.";
            if (SafeRemaining > 0) return "Cleanup finished, but " + SafeRemaining + " item(s) are still there.";
            if (ReviewRemaining > 0) return "Cleanup complete. " + ReviewRemaining + " other item(s) were found that need your review.";
            return "Cleanup complete. No additional leftovers detected in scanned locations.";
        }

        public string UninstallLine()
        {
            if (Uninstall == null) return "The app's own uninstaller wasn't run.";
            return Uninstall.Describe(App.Name) + (ManualCleanup ? " You chose to clean up anyway." : "");
        }

        /// <summary>Machine-readable report for --purge (the integration tests read it).</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            if (Uninstall != null)
                sb.AppendLine("UNINSTALLER state=" + Uninstall.State + " exit=" + (Uninstall.ExitCode.HasValue ? Uninstall.ExitCode.Value.ToString() : "none") +
                              " exit_reliable=" + Uninstall.ExitCodeReliable + " entry_present=" + Uninstall.EntryPresent + " program_present=" + Uninstall.ProgramPresent);
            foreach (DeleteResult r in Results)
                sb.AppendLine("RESULT " + r.Outcome.ToString().PadRight(15) + "bytes=" + (r.Deleted + r.Recycled + r.Pending) + " left=" + r.Left + "  " +
                              r.Item.DisplayPath + (r.Error != null ? "  -- " + r.Error : ""));
            foreach (DriveReport d in Drives.Values.OrderBy(d => d.Drive))
                sb.AppendLine("STORAGE " + d.Drive + " uninstaller=" + d.ByUninstaller + " deleted=" + d.Deleted + " recycled=" + d.Recycled +
                              " pending=" + d.Pending + " left=" + d.Left + " free_change=" + (d.FreeChange.HasValue ? d.FreeChange.Value.ToString() : "unknown"));
            sb.AppendLine("VERIFY " + (Stopped ? "skipped" : Verified ? "completed" : "incomplete") + " safe_remaining=" + SafeRemaining + " review_remaining=" + ReviewRemaining);
            foreach (FoundItem it in Remaining)
                sb.AppendLine("REMAINING [" + it.Conf + "] " + it.DisplayPath);
            sb.AppendLine("HEADLINE " + Headline());
            sb.AppendLine("BACKUP    " + Options.BackupDir);
            return sb.ToString();
        }
    }
}
