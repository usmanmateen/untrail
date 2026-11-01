using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Untrail
{
    public class DeleteResult
    {
        public FoundItem Item;
        public bool Ok;
        public long Freed;
        public bool AtReboot;
        public string Error;
    }

    public static class Deleter
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string existing, string newName, int flags);
        const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        public static string NewBackupDir(string appName)
        {
            string safe = new string((appName ?? "app").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            return Path.Combine(Util.Documents, "Untrail Backups", safe + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"));
        }

        public static List<DeleteResult> Run(IList<FoundItem> items, bool recycle, string backupDir, Action<string> progress)
        {
            var results = new List<DeleteResult>();
            progress = progress ?? (s => { });
            Directory.CreateDirectory(backupDir);
            var log = new StringBuilder();
            log.AppendLine("Untrail removal log " + DateTime.Now.ToString("u"));
            log.AppendLine(recycle ? "Mode: Recycle Bin" : "Mode: permanent delete");

            // Files and folders first, registry last (uninstall entry goes only once the files are gone).
            foreach (FoundItem it in items.OrderBy(i => i.Kind >= ItemKind.Registry ? 1 : 0))
            {
                var r = new DeleteResult { Item = it };
                progress("Removing " + it.DisplayPath);
                try
                {
                    // Re-check every path here: the elevated helper must not trust its job file blindly.
                    if ((it.Kind == ItemKind.Folder || it.Kind == ItemKind.File) && !Util.IsSafeToDelete(Util.NormDir(it.Path)))
                        r.Error = "Refused: protected system location";
                    else if (it.Kind == ItemKind.Folder) DeleteFolder(it, recycle, r);
                    else if (it.Kind == ItemKind.File) DeleteFile(it, recycle, r);
                    else DeleteRegistry(it, backupDir, r);
                }
                catch (Exception ex) { r.Error = ex.Message; }
                results.Add(r);
                log.AppendLine((r.Ok ? (r.AtReboot ? "AT-REBOOT " : "OK        ") : "FAILED    ") + it.DisplayPath + (r.Error != null ? "  -- " + r.Error : ""));
            }
            try { File.WriteAllText(Path.Combine(backupDir, "removal-log.txt"), log.ToString(), Encoding.UTF8); } catch { }
            // Callers (and the elevated helper's result file) rely on input order.
            return items.Select(i => results.First(r => r.Item == i)).ToList();
        }

        static void DeleteFile(FoundItem it, bool recycle, DeleteResult r)
        {
            if (!File.Exists(it.Path)) { r.Ok = true; return; }
            long len = new FileInfo(it.Path).Length;
            File.SetAttributes(it.Path, FileAttributes.Normal);
            if (recycle) FileSystem.DeleteFile(it.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else File.Delete(it.Path);
            r.Ok = !File.Exists(it.Path);
            r.Freed = r.Ok ? len : 0;
        }

        static void DeleteFolder(FoundItem it, bool recycle, DeleteResult r)
        {
            string p = it.Path;
            if (!Directory.Exists(p)) { r.Ok = true; return; }
            if (Util.IsReparse(p))
            {
                Directory.Delete(p, false); // removes the link only, never what it points at
                r.Ok = true;
                return;
            }
            if (recycle)
            {
                try
                {
                    FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                }
                catch (OperationCanceledException) { r.Error = "Cancelled"; return; }
                catch (Exception ex) { r.Error = ex.Message; }
                r.Ok = !Directory.Exists(p);
                if (r.Ok) { r.Freed = it.Size; r.Error = null; }
                else if (r.Error == null) r.Error = "Some files are in use";
                return;
            }

            var failed = new List<string>();
            r.Freed = DeleteTree(p, failed);
            if (Directory.Exists(p))
            {
                // Long paths (>260 chars) defeat the normal APIs; rd with the \\?\ prefix doesn't care.
                RunHidden(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c rd /s /q \"\\\\?\\" + p + "\"");
            }
            if (!Directory.Exists(p)) { r.Ok = true; r.Freed = Math.Max(r.Freed, it.Size); return; }

            long left = Util.DirSize(p, null);
            if (Util.IsAdmin)
            {
                // Files locked by a running program: let Windows remove them on the next restart.
                var files = new List<string>();
                var dirs = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(p, "*", System.IO.SearchOption.AllDirectories));
                    dirs.AddRange(Directory.GetDirectories(p, "*", System.IO.SearchOption.AllDirectories));
                }
                catch { }
                foreach (string f in files) MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                foreach (string d in dirs.OrderByDescending(d => d.Length)) MoveFileEx(d, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                MoveFileEx(p, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                r.Ok = true;
                r.AtReboot = true;
                r.Freed += left;
                r.Error = "Some files are in use \u2014 they'll be removed when you restart";
            }
            else
            {
                r.Error = "Some files couldn't be removed (in use or access denied), " + Util.FormatSize(left) + " left";
            }
        }

        static long DeleteTree(string dir, List<string> failed)
        {
            long freed = 0;
            foreach (string f in Util.SafeFiles(dir, "*"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    freed += len;
                }
                catch { failed.Add(f); }
            }
            foreach (string d in Util.SafeDirs(dir))
            {
                if (Util.IsReparse(d)) { try { Directory.Delete(d, false); } catch { failed.Add(d); } }
                else freed += DeleteTree(d, failed);
            }
            try
            {
                File.SetAttributes(dir, FileAttributes.Normal);
                Directory.Delete(dir, false);
            }
            catch { }
            return freed;
        }

        static void DeleteRegistry(FoundItem it, string backupDir, DeleteResult r)
        {
            string keyPath = it.Path, valueName = null;
            if (it.Kind == ItemKind.RegistryValue)
            {
                int bar = it.Path.LastIndexOf('|');
                keyPath = it.Path.Substring(0, bar);
                valueName = it.Path.Substring(bar + 1);
            }
            if (!Reg.Exists(keyPath)) { r.Ok = true; return; }

            string backup = Path.Combine(backupDir, new string(it.DisplayPath.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()) + ".reg");
            if (backup.Length > 240) backup = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".reg");
            RunHidden(Path.Combine(Environment.SystemDirectory, "reg.exe"), "export \"" + keyPath + "\" \"" + backup + "\" /y");

            string hive, sub;
            Reg.Split(keyPath, out hive, out sub);
            using (RegistryKey root = Reg.Base(hive))
            {
                if (valueName != null)
                {
                    using (RegistryKey k = root.OpenSubKey(sub, true)) if (k != null) k.DeleteValue(valueName, false);
                    r.Ok = !Reg.ValueExists(keyPath, valueName);
                }
                else
                {
                    int slash = sub.LastIndexOf('\\');
                    using (RegistryKey parent = root.OpenSubKey(sub.Substring(0, slash), true))
                        if (parent != null) parent.DeleteSubKeyTree(sub.Substring(slash + 1), false);
                    r.Ok = !Reg.Exists(keyPath);
                }
            }
            if (!r.Ok) r.Error = "Access denied";
        }

        static void RunHidden(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                using (Process p = Process.Start(psi)) p.WaitForExit(120000);
            }
            catch { }
        }

        // ---------------------------------------------------------------- elevated helper

        /// <summary>Runs the delete in a second copy of Untrail started "as administrator". Throws Win32Exception (1223) if the UAC prompt is declined.</summary>
        public static List<DeleteResult> RunElevated(List<FoundItem> items, bool recycle, string backupDir)
        {
            string job = Path.Combine(Path.GetTempPath(), "Untrail-job-" + Guid.NewGuid().ToString("N") + ".txt");
            WriteJob(job, items, recycle, backupDir);
            try
            {
                var psi = new ProcessStartInfo(Util.SelfExe, "--job \"" + job + "\"") { UseShellExecute = true, Verb = "runas" };
                using (Process p = Process.Start(psi)) p.WaitForExit();
                var results = new List<DeleteResult>();
                string[] lines = File.Exists(job + ".result") ? File.ReadAllLines(job + ".result", Encoding.UTF8) : new string[0];
                for (int i = 0; i < items.Count; i++)
                {
                    var r = new DeleteResult { Item = items[i], Error = "The admin helper didn't report back" };
                    if (i < lines.Length)
                    {
                        string[] f = lines[i].Split('\t');
                        r.Ok = f[0] == "1";
                        r.Freed = long.Parse(f[1]);
                        r.AtReboot = f[2] == "1";
                        r.Error = f.Length > 3 && f[3].Length > 0 ? f[3] : null;
                    }
                    results.Add(r);
                }
                return results;
            }
            finally
            {
                try { File.Delete(job); } catch { }
                try { File.Delete(job + ".result"); } catch { }
            }
        }

        public static void WriteJob(string job, IEnumerable<FoundItem> items, bool recycle, string backupDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine(recycle ? "recycle" : "permanent");
            sb.AppendLine(backupDir);
            foreach (FoundItem it in items) sb.AppendLine((int)it.Kind + "\t" + it.Size + "\t" + it.Path);
            File.WriteAllText(job, sb.ToString(), Encoding.UTF8);
        }

        public static int RunJobFile(string job)
        {
            try
            {
                string[] lines = File.ReadAllLines(job, Encoding.UTF8);
                bool recycle = lines[0].Trim() == "recycle";
                string backupDir = lines[1].Trim();
                var items = new List<FoundItem>();
                foreach (string line in lines.Skip(2))
                {
                    if (line.Trim().Length == 0) continue;
                    string[] f = line.Split(new[] { '\t' }, 3);
                    items.Add(new FoundItem { Kind = (ItemKind)int.Parse(f[0]), Size = long.Parse(f[1]), Path = f[2] });
                }
                List<DeleteResult> results = Run(items, recycle, backupDir, null);
                var sb = new StringBuilder();
                foreach (DeleteResult r in results)
                    sb.AppendLine((r.Ok ? "1" : "0") + "\t" + r.Freed + "\t" + (r.AtReboot ? "1" : "0") + "\t" + (r.Error ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
                File.WriteAllText(job + ".result", sb.ToString(), Encoding.UTF8);
                return 0;
            }
            catch { return 1; }
        }

        public static bool IsUacCancelled(Exception ex)
        {
            var w = ex as Win32Exception;
            return w != null && w.NativeErrorCode == 1223;
        }
    }
}
