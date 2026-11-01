using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Untrail
{
    public enum Outcome { Deleted, Recycled, PendingRestart, Failed, AlreadyGone }

    public class DeleteResult
    {
        public FoundItem Item;
        public Outcome Outcome = Outcome.Failed;
        public long Deleted;   // bytes permanently deleted by Untrail
        public long Recycled;  // bytes moved to the Recycle Bin (still using disk space)
        public long Pending;   // bytes scheduled for deletion at the next restart
        public long Left;      // bytes still there after a failure
        public string Error;
        public bool Ok { get { return Outcome != Outcome.Failed; } }
    }

    public class DeleteOptions
    {
        public bool Recycle = true;
        public string BackupDir;
        /// <summary>Test switch: pretend every registry export failed. It can only make Untrail delete less.</summary>
        public bool SimulateBackupFailure;
    }

    /// <summary>Last line of defence: what Untrail is ever allowed to delete, checked again right before each delete.</summary>
    public static class Guard
    {
        const string Cv = @"\Microsoft\Windows\CurrentVersion\";
        static readonly string[] SoftwareRoots = { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" };

        /// <summary>null when allowed, otherwise the reason it isn't.</summary>
        public static string Check(FoundItem it)
        {
            if (string.IsNullOrEmpty(it.Path) || it.Path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) return "Refused: invalid path";
            if (it.Kind == ItemKind.Folder || it.Kind == ItemKind.File)
            {
                string p = Util.NormDir(it.Path);
                if (p == null || !Path.IsPathRooted(p) || p.StartsWith(@"\\")) return "Refused: not a local path";
                if (!Util.IsSafeToDelete(p)) return "Refused: protected system location";
                return null;
            }
            if (it.Kind == ItemKind.RegistryValue)
            {
                int bar = it.Path.LastIndexOf('|');
                if (bar <= 0) return "Refused: invalid registry value";
                string key = it.Path.Substring(0, bar);
                foreach (string root in SoftwareRoots)
                    if (Same(key, root + Cv + "Run") || Same(key, root + Cv + "RunOnce")) return null;
                return "Refused: only start-up entries can be removed as single values";
            }
            return CheckKey(it.Path);
        }

        static string CheckKey(string key)
        {
            if (key.Contains("\\\\") || key.EndsWith("\\")) return "Refused: invalid registry path";
            foreach (string root in SoftwareRoots)
            {
                // ...\Uninstall\<one key> and ...\App Paths\<one key>
                foreach (string list in new[] { Cv + "Uninstall", Cv + "App Paths" })
                {
                    string prefix = root + list + "\\";
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !key.Substring(prefix.Length).Contains("\\")) return null;
                }
                // Software\<Vendor or App>[\<App>], never Microsoft, Classes, Policies...
                if (!key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = key.Substring(root.Length + 1).Split('\\');
                if (root == @"HKLM\SOFTWARE" && parts[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;
                if (parts.Length > 2 || Scanner.RegSkip.Contains(parts[0])) return "Refused: protected registry location";
                return null;
            }
            return "Refused: protected registry location";
        }

        /// <summary>Folders and files must still lead where they did at scan time: no link swapped in since.</summary>
        public static string CheckUnchanged(FoundItem it)
        {
            if (it.Kind != ItemKind.Folder && it.Kind != ItemKind.File) return null;
            bool exists = it.Kind == ItemKind.Folder ? Directory.Exists(it.Path) : File.Exists(it.Path);
            if (!exists) return null;
            if (!it.IsLink && Util.IsReparse(it.Path)) return "Refused: it became a link since the scan";
            string now = Util.FinalPath(it.Path);
            if (now == null) return "Refused: couldn't confirm where it leads";
            if (!it.IsLink && !Util.IsSafeToDelete(now)) return "Refused: it now leads to a protected location";
            if (it.RealPath != null && !Util.SamePath(now, it.RealPath)) return "Refused: it changed since the scan (now leads to " + now + ")";
            return null;
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
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

        /// <summary>Deletes items in order, reporting (items finished, text) as it goes. Results come back in input order.</summary>
        public static List<DeleteResult> Run(IList<FoundItem> items, DeleteOptions opts, AppScope scope, Action<int, string> progress)
        {
            var results = new Dictionary<FoundItem, DeleteResult>();
            progress = progress ?? ((n, s) => { });
            Directory.CreateDirectory(opts.BackupDir);
            var log = new StringBuilder();
            log.AppendLine("Untrail removal log " + DateTime.Now.ToString("u") + "  app: " + scope.Name);
            log.AppendLine(opts.Recycle ? "Mode: Recycle Bin" : "Mode: permanent delete");

            // Files and folders first, registry last (uninstall entry goes only once the files are gone).
            int done = 0;
            foreach (FoundItem it in items.OrderBy(i => i.Kind >= ItemKind.Registry ? 1 : 0))
            {
                var r = new DeleteResult { Item = it };
                progress(done, "Removing " + it.DisplayPath);
                try
                {
                    r.Error = Guard.Check(it) ?? scope.Check(it) ?? Guard.CheckUnchanged(it);
                    if (r.Error == null)
                    {
                        if (it.Kind == ItemKind.Folder) DeleteFolder(it, opts.Recycle, r);
                        else if (it.Kind == ItemKind.File) DeleteFile(it, opts.Recycle, r);
                        else DeleteRegistry(it, opts, r);
                    }
                }
                catch (Exception ex) { r.Outcome = Outcome.Failed; r.Error = ex.Message; }
                if (r.Error != null && r.Outcome != Outcome.PendingRestart) r.Outcome = Outcome.Failed;
                results[it] = r;
                progress(++done, null);
                log.AppendLine(r.Outcome.ToString().ToUpperInvariant().PadRight(15) + it.DisplayPath + (r.Error != null ? "  -- " + r.Error : ""));
            }
            try { File.WriteAllText(Path.Combine(opts.BackupDir, "removal-log.txt"), log.ToString(), Encoding.UTF8); } catch { }
            return items.Select(i => results[i]).ToList();
        }

        static void DeleteFile(FoundItem it, bool recycle, DeleteResult r)
        {
            if (!File.Exists(it.Path)) { r.Outcome = Outcome.AlreadyGone; return; }
            long len = new FileInfo(it.Path).Length;
            File.SetAttributes(it.Path, FileAttributes.Normal);
            if (recycle) FileSystem.DeleteFile(it.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else File.Delete(it.Path);
            if (File.Exists(it.Path)) { r.Error = "Couldn't be removed"; r.Left = len; return; }
            r.Outcome = recycle ? Outcome.Recycled : Outcome.Deleted;
            if (recycle) r.Recycled = len; else r.Deleted = len;
        }

        static void DeleteFolder(FoundItem it, bool recycle, DeleteResult r)
        {
            string p = it.Path;
            if (!Directory.Exists(p)) { r.Outcome = Outcome.AlreadyGone; return; }
            if (Util.IsReparse(p))
            {
                Directory.Delete(p, false); // removes the link only, never what it points at
                r.Outcome = Outcome.Deleted;
                return;
            }
            long before = Util.DirSize(p, null);
            if (recycle)
            {
                try
                {
                    FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                }
                catch (OperationCanceledException) { r.Error = "Cancelled"; }
                catch (Exception ex) { r.Error = ex.Message; }
                long after = Directory.Exists(p) ? Util.DirSize(p, null) : 0;
                r.Recycled = Math.Max(0, before - after);
                if (!Directory.Exists(p)) { r.Outcome = Outcome.Recycled; r.Error = null; }
                else { r.Left = after; if (r.Error == null) r.Error = "Some files are in use"; }
                return;
            }

            var failed = new List<string>();
            bool sawLinks;
            r.Deleted = DeleteTree(p, failed, out sawLinks);
            if (Directory.Exists(p) && !sawLinks)
            {
                // Long paths (>260 chars) defeat the normal APIs; rd with the \\?\ prefix doesn't care.
                // Only used on a tree with no links left in it.
                RunHidden(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c rd /s /q \"\\\\?\\" + p + "\"");
            }
            if (!Directory.Exists(p)) { r.Outcome = Outcome.Deleted; r.Deleted = Math.Max(r.Deleted, before); return; }

            long left = Util.DirSize(p, null);
            r.Deleted = Math.Max(0, before - left);
            if (Util.IsAdmin)
            {
                // Files locked by a running program: Windows removes them at the next restart. Always reported.
                var files = new List<string>();
                var dirs = new List<string>();
                try
                {
                    files.AddRange(Directory.GetFiles(p, "*", System.IO.SearchOption.AllDirectories));
                    dirs.AddRange(Directory.GetDirectories(p, "*", System.IO.SearchOption.AllDirectories));
                }
                catch { }
                bool allScheduled = true;
                foreach (string f in files) allScheduled &= MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                foreach (string d in dirs.OrderByDescending(d => d.Length)) MoveFileEx(d, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                MoveFileEx(p, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                if (allScheduled)
                {
                    r.Outcome = Outcome.PendingRestart;
                    r.Pending = left;
                    r.Error = files.Count + " file(s) are in use \u2014 Windows will remove them when you restart";
                    return;
                }
            }
            r.Left = left;
            r.Error = "Some files couldn't be removed (in use or access denied), " + Util.FormatSize(left) + " left";
        }

        static long DeleteTree(string dir, List<string> failed, out bool sawLinks)
        {
            sawLinks = false;
            long freed = 0;
            foreach (string f in Util.SafeFiles(dir, "*"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    bool link = (fi.Attributes & FileAttributes.ReparsePoint) != 0;
                    long len = link ? 0 : fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();    // a file symlink: removes the link, not its target
                    freed += len;
                }
                catch { failed.Add(f); }
            }
            foreach (string d in Util.SafeDirs(dir))
            {
                if (Util.IsReparse(d))
                {
                    // Junction or directory symlink: remove the link only, never walk into it.
                    try { Directory.Delete(d, false); } catch { failed.Add(d); sawLinks = true; }
                    continue;
                }
                bool inner;
                freed += DeleteTree(d, failed, out inner);
                sawLinks |= inner;
            }
            try
            {
                File.SetAttributes(dir, FileAttributes.Normal);
                Directory.Delete(dir, false);
            }
            catch { }
            return freed;
        }

        static void DeleteRegistry(FoundItem it, DeleteOptions opts, DeleteResult r)
        {
            string keyPath = it.Path, valueName = null;
            if (it.Kind == ItemKind.RegistryValue)
            {
                int bar = it.Path.LastIndexOf('|');
                keyPath = it.Path.Substring(0, bar);
                valueName = it.Path.Substring(bar + 1);
                if (!Reg.ValueExists(keyPath, valueName)) { r.Outcome = Outcome.AlreadyGone; return; }
            }
            else if (!Reg.Exists(keyPath)) { r.Outcome = Outcome.AlreadyGone; return; }

            // No backup, no delete.
            string backup = Path.Combine(opts.BackupDir, new string(it.DisplayPath.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()) + ".reg");
            if (backup.Length > 240) backup = Path.Combine(opts.BackupDir, Guid.NewGuid().ToString("N") + ".reg");
            int code = opts.SimulateBackupFailure ? -1
                : RunHidden(Path.Combine(Environment.SystemDirectory, "reg.exe"), "export \"" + keyPath + "\" \"" + backup + "\" /y");
            if (code != 0 || !File.Exists(backup) || new FileInfo(backup).Length == 0)
            {
                r.Error = "Couldn't back it up first, so it was left alone";
                return;
            }

            string hive, sub;
            Reg.Split(keyPath, out hive, out sub);
            using (RegistryKey root = Reg.Base(hive))
            {
                if (valueName != null)
                {
                    using (RegistryKey k = root.OpenSubKey(sub, true)) if (k != null) k.DeleteValue(valueName, false);
                    if (!Reg.ValueExists(keyPath, valueName)) r.Outcome = Outcome.Deleted;
                }
                else
                {
                    int slash = sub.LastIndexOf('\\');
                    using (RegistryKey parent = root.OpenSubKey(sub.Substring(0, slash), true))
                        if (parent != null) parent.DeleteSubKeyTree(sub.Substring(slash + 1), false);
                    if (!Reg.Exists(keyPath)) r.Outcome = Outcome.Deleted;
                }
            }
            if (r.Outcome != Outcome.Deleted) r.Error = "Access denied";
        }

        static int RunHidden(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000)) return -1;
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- elevated helper
        //
        // The admin copy of Untrail gets its job over a named pipe that only this process can serve:
        // nothing passes through a file another program could swap. Each side checks the other's
        // process, and the helper re-checks every item against Guard, the app's scope and its
        // scan-time real path before touching it.

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        const byte MsgProgress = 1, MsgResult = 2, MsgDone = 3;

        /// <summary>Runs the delete in a second copy of Untrail started "as administrator". Throws Win32Exception (1223) if the UAC prompt is declined.</summary>
        /// <param name="elevate">false runs the helper at the same level (tests use this to exercise the pipe without a UAC prompt).</param>
        public static List<DeleteResult> RunElevated(List<FoundItem> items, DeleteOptions opts, AppScope scope, Action<int, string> progress, bool elevate = true)
        {
            progress = progress ?? ((n, s) => { });
            string pipeName = "Untrail-" + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

            var results = items.Select(i => new DeleteResult { Item = i, Error = "The admin helper didn't report back" }).ToList();
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
            {
                var psi = new ProcessStartInfo(Util.SelfExe, "--elevated-helper " + pipeName + " " + Process.GetCurrentProcess().Id)
                {
                    UseShellExecute = true, Verb = elevate ? "runas" : "",
                };
                using (Process helper = Process.Start(psi))
                {
                    IAsyncResult wait = server.BeginWaitForConnection(null, null);
                    while (!wait.AsyncWaitHandle.WaitOne(200))
                        if (helper.HasExited) throw new InvalidOperationException("The admin helper closed before it started.");
                    server.EndWaitForConnection(wait);

                    uint clientPid;
                    if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out clientPid) || clientPid != helper.Id)
                        throw new InvalidOperationException("Something other than Untrail's admin helper connected. Nothing was deleted.");

                    var w = new BinaryWriter(server, Encoding.UTF8);
                    w.Write(opts.Recycle);
                    w.Write(opts.BackupDir);
                    w.Write(opts.SimulateBackupFailure);
                    scope.Write(w);
                    w.Write(items.Count);
                    foreach (FoundItem it in items)
                    {
                        w.Write((int)it.Kind); w.Write(it.Size); w.Write(it.Path); w.Write(it.RealPath ?? ""); w.Write(it.IsLink);
                    }
                    w.Flush();

                    var rd = new BinaryReader(server, Encoding.UTF8);
                    try
                    {
                        while (true)
                        {
                            byte msg = rd.ReadByte();
                            if (msg == MsgDone) break;
                            if (msg == MsgProgress) progress(rd.ReadInt32(), Null(rd.ReadString()));
                            else if (msg == MsgResult)
                            {
                                int i = rd.ReadInt32();
                                var r = new DeleteResult
                                {
                                    Outcome = (Outcome)rd.ReadInt32(), Deleted = rd.ReadInt64(), Recycled = rd.ReadInt64(),
                                    Pending = rd.ReadInt64(), Left = rd.ReadInt64(), Error = Null(rd.ReadString()),
                                };
                                if (i >= 0 && i < items.Count) { r.Item = items[i]; results[i] = r; }
                            }
                        }
                    }
                    catch (EndOfStreamException) { }
                    helper.WaitForExit(10000);
                }
            }
            return results;
        }

        static string Null(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        /// <summary>Entry point of the admin copy: Untrail.exe --elevated-helper &lt;pipe&gt; &lt;parent pid&gt;.</summary>
        public static int RunHelper(string pipeName, string parentPid)
        {
            try
            {
                if (!Regex.IsMatch(pipeName, "^Untrail-[0-9a-f]{32}$")) return 2;
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    client.Connect(10000);
                    uint serverPid;
                    if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out serverPid) || serverPid.ToString() != parentPid)
                        return 3;
                    // The other end must be this same Untrail.exe, not some other program.
                    if (!Util.SamePath(Util.ProcessPath((int)serverPid), Util.SelfExe)) return 4;

                    var rd = new BinaryReader(client, Encoding.UTF8);
                    var opts = new DeleteOptions { Recycle = rd.ReadBoolean(), BackupDir = rd.ReadString(), SimulateBackupFailure = rd.ReadBoolean() };
                    AppScope scope = AppScope.Read(rd);
                    int n = rd.ReadInt32();
                    if (n < 0 || n > 100000) return 5;
                    var items = new List<FoundItem>();
                    for (int i = 0; i < n; i++)
                        items.Add(new FoundItem
                        {
                            Kind = (ItemKind)rd.ReadInt32(), Size = rd.ReadInt64(), Path = rd.ReadString(), RealPath = Null(rd.ReadString()), IsLink = rd.ReadBoolean(),
                        });
                    string bd = Util.NormDir(opts.BackupDir);
                    if (bd == null || !Path.IsPathRooted(bd) || !"Untrail Backups".Equals(Path.GetFileName(Path.GetDirectoryName(bd)), StringComparison.OrdinalIgnoreCase))
                        return 6;

                    var w = new BinaryWriter(client, Encoding.UTF8);
                    List<DeleteResult> results = Run(items, opts, scope, (done, text) =>
                    {
                        w.Write(MsgProgress); w.Write(done); w.Write(text ?? ""); w.Flush();
                    });
                    for (int i = 0; i < results.Count; i++)
                    {
                        DeleteResult r = results[i];
                        w.Write(MsgResult); w.Write(i); w.Write((int)r.Outcome);
                        w.Write(r.Deleted); w.Write(r.Recycled); w.Write(r.Pending); w.Write(r.Left); w.Write(r.Error ?? "");
                    }
                    w.Write(MsgDone);
                    w.Flush();
                    client.WaitForPipeDrain();
                    return 0;
                }
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
