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
using System.Threading;
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
        public static List<DeleteResult> Run(IList<FoundItem> items, bool recycle, string backupDir, Action<int, string> progress)
        {
            var results = new Dictionary<FoundItem, DeleteResult>();
            progress = progress ?? ((n, s) => { });
            Directory.CreateDirectory(backupDir);
            var log = new StringBuilder();
            log.AppendLine("Untrail removal log " + DateTime.Now.ToString("u"));
            log.AppendLine(recycle ? "Mode: Recycle Bin" : "Mode: permanent delete");

            // Files and folders first, registry last (uninstall entry goes only once the files are gone).
            int done = 0;
            foreach (FoundItem it in items.OrderBy(i => i.Kind >= ItemKind.Registry ? 1 : 0))
            {
                var r = new DeleteResult { Item = it };
                progress(done, "Removing " + it.DisplayPath);
                try
                {
                    r.Error = Guard.Check(it);
                    if (r.Error == null)
                    {
                        if (it.Kind == ItemKind.Folder) DeleteFolder(it, recycle, r);
                        else if (it.Kind == ItemKind.File) DeleteFile(it, recycle, r);
                        else DeleteRegistry(it, backupDir, r);
                    }
                }
                catch (Exception ex) { r.Error = ex.Message; }
                results[it] = r;
                progress(++done, null);
                log.AppendLine((r.Ok ? (r.AtReboot ? "AT-REBOOT " : "OK        ") : "FAILED    ") + it.DisplayPath + (r.Error != null ? "  -- " + r.Error : ""));
            }
            try { File.WriteAllText(Path.Combine(backupDir, "removal-log.txt"), log.ToString(), Encoding.UTF8); } catch { }
            return items.Select(i => results[i]).ToList();
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
                if (!Reg.ValueExists(keyPath, valueName)) { r.Ok = true; return; }
            }
            else if (!Reg.Exists(keyPath)) { r.Ok = true; return; }

            // No backup, no delete.
            string backup = Path.Combine(backupDir, new string(it.DisplayPath.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()) + ".reg");
            if (backup.Length > 240) backup = Path.Combine(backupDir, Guid.NewGuid().ToString("N") + ".reg");
            int code = RunHidden(Path.Combine(Environment.SystemDirectory, "reg.exe"), "export \"" + keyPath + "\" \"" + backup + "\" /y");
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
        // process ID, and the helper also re-checks every item with Guard before touching it.

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

        const byte MsgProgress = 1, MsgResult = 2, MsgDone = 3;

        /// <summary>Runs the delete in a second copy of Untrail started "as administrator". Throws Win32Exception (1223) if the UAC prompt is declined.</summary>
        /// <param name="elevate">false runs the helper at the same level (tests use this to exercise the pipe without a UAC prompt).</param>
        public static List<DeleteResult> RunElevated(List<FoundItem> items, bool recycle, string backupDir, Action<int, string> progress, bool elevate = true)
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
                    w.Write(recycle);
                    w.Write(backupDir);
                    w.Write(items.Count);
                    foreach (FoundItem it in items) { w.Write((int)it.Kind); w.Write(it.Size); w.Write(it.Path); }
                    w.Flush();

                    var rd = new BinaryReader(server, Encoding.UTF8);
                    try
                    {
                        while (true)
                        {
                            byte msg = rd.ReadByte();
                            if (msg == MsgDone) break;
                            if (msg == MsgProgress) progress(rd.ReadInt32(), rd.ReadString());
                            else if (msg == MsgResult)
                            {
                                int i = rd.ReadInt32();
                                var r = new DeleteResult { Ok = rd.ReadBoolean(), Freed = rd.ReadInt64(), AtReboot = rd.ReadBoolean() };
                                string err = rd.ReadString();
                                r.Error = err.Length > 0 ? err : null;
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
                    bool recycle = rd.ReadBoolean();
                    string backupDir = rd.ReadString();
                    int n = rd.ReadInt32();
                    if (n < 0 || n > 100000) return 5;
                    var items = new List<FoundItem>();
                    for (int i = 0; i < n; i++)
                        items.Add(new FoundItem { Kind = (ItemKind)rd.ReadInt32(), Size = rd.ReadInt64(), Path = rd.ReadString() });
                    string bd = Util.NormDir(backupDir);
                    if (bd == null || !Path.IsPathRooted(bd) || !"Untrail Backups".Equals(Path.GetFileName(Path.GetDirectoryName(bd)), StringComparison.OrdinalIgnoreCase))
                        return 6;

                    var w = new BinaryWriter(client, Encoding.UTF8);
                    object gate = new object();
                    List<DeleteResult> results = Run(items, recycle, backupDir, (done, text) =>
                    {
                        lock (gate) { w.Write(MsgProgress); w.Write(done); w.Write(text ?? ""); w.Flush(); }
                    });
                    for (int i = 0; i < results.Count; i++)
                    {
                        DeleteResult r = results[i];
                        w.Write(MsgResult); w.Write(i); w.Write(r.Ok); w.Write(r.Freed); w.Write(r.AtReboot); w.Write(r.Error ?? "");
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
