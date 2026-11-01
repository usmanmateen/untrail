using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Untrail
{
    /// <summary>
    /// The identity of the app being removed, as established by the scan. Every item is checked against it again
    /// right before deletion (also inside the admin helper), so neither path can be used to delete things that
    /// have nothing to do with the app.
    /// </summary>
    public class AppScope
    {
        public string Name = "", InstallDir, ExePath, ShortcutPath;
        public List<string> Strong = new List<string>(), Weak = new List<string>(), Pub = new List<string>();
        public List<string> EntryKeys = new List<string>();
        /// <summary>Other installed apps, as "folder|name": a folder holding one of them is never deleted.</summary>
        public List<string> OtherApps = new List<string>();

        public static AppScope From(AppInfo app)
        {
            var s = new AppScope
            {
                Name = app.Name ?? "", InstallDir = app.InstallDir, ExePath = app.ExePath, ShortcutPath = app.ShortcutPath,
                Strong = app.Strong.ToList(), Weak = app.Weak.ToList(), Pub = app.Pub.ToList(),
            };
            if (app.Entry != null) s.EntryKeys.Add(app.Entry.KeyPath);
            s.EntryKeys.AddRange(app.SameApp.Select(e => e.KeyPath));
            s.OtherApps = Scanner.OtherAppDirs(app, Scanner.GetUninstallEntries()).Select(kv => kv.Key + "|" + kv.Value).Distinct().ToList();
            return s;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Name); w.Write(InstallDir ?? ""); w.Write(ExePath ?? ""); w.Write(ShortcutPath ?? "");
            foreach (List<string> l in new[] { Strong, Weak, Pub, EntryKeys, OtherApps }) { w.Write(l.Count); foreach (string x in l) w.Write(x); }
        }

        public static AppScope Read(BinaryReader r)
        {
            var s = new AppScope { Name = r.ReadString(), InstallDir = Null(r.ReadString()), ExePath = Null(r.ReadString()), ShortcutPath = Null(r.ReadString()) };
            foreach (List<string> l in new[] { s.Strong, s.Weak, s.Pub, s.EntryKeys, s.OtherApps })
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 20000) throw new InvalidDataException("bad scope");
                for (int i = 0; i < n; i++) l.Add(r.ReadString());
            }
            return s;
        }

        /// <summary>Name of another installed app this folder would take with it, or null.</summary>
        string SharedWith(string p)
        {
            foreach (string o in OtherApps)
            {
                int bar = o.LastIndexOf('|');
                string dir = o.Substring(0, bar);
                if (Util.IsAtOrUnder(dir, p)) return o.Substring(bar + 1);   // p holds (or is) another app's folder
                // Inside another app's folder only when it's this app's own install (a Steam game inside Steam).
                if (Util.IsUnder(p, dir) && !(InstallDir != null && Util.IsAtOrUnder(p, InstallDir))) return o.Substring(bar + 1);
            }
            return null;
        }

        static string Null(string s) { return s.Length == 0 ? null : s; }

        bool NameIsApps(string name)
        {
            string reason;
            return Scanner.NameMatch(Strong, Weak, Pub, name, out reason).HasValue;
        }

        bool PointsIntoApp(string pathOrCommand)
        {
            string file, args;
            Util.SplitCommand(pathOrCommand ?? "", out file, out args);
            file = Util.NormDir(file);
            return file != null && ((InstallDir != null && Util.IsAtOrUnder(file, InstallDir)) || (ExePath != null && Util.SamePath(file, ExePath)));
        }

        /// <summary>null when the item belongs to this app, otherwise why it is refused.</summary>
        public string Check(FoundItem it)
        {
            const string refused = "Refused: doesn't belong to the app being removed";
            if (it.Kind == ItemKind.Folder || it.Kind == ItemKind.File)
            {
                string p = Util.NormDir(it.Path);
                if (p == null) return refused;
                string other = SharedWith(p);
                if (other != null) return "Refused: it holds files of another installed app (" + other + ")";
                if (InstallDir != null && Util.IsSafeToDelete(InstallDir) && Util.IsAtOrUnder(p, InstallDir)) return null;
                if (ShortcutPath != null && Util.SamePath(p, ShortcutPath)) return null;
                if (it.Kind == ItemKind.File && p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && PointsIntoApp("\"" + Util.ResolveShortcut(p) + "\"")) return null;
                string leaf = Path.GetFileNameWithoutExtension(p);
                if (NameIsApps(Path.GetFileName(p)) || NameIsApps(leaf)) return null;
                // A publisher folder (or a "<name> Inc" start-menu folder) holding nothing but this app's things.
                if (it.Kind == ItemKind.Folder && (Pub.Contains(Util.Key(Path.GetFileName(p))) || Strong.Any(k => Util.Key(Path.GetFileName(p)).StartsWith(k))))
                {
                    try
                    {
                        string[] children = Directory.GetFileSystemEntries(p);
                        if (children.Length > 0 && children.All(ch => Check(new FoundItem { Kind = Directory.Exists(ch) ? ItemKind.Folder : ItemKind.File, Path = ch }) == null))
                            return null;
                    }
                    catch { }
                }
                return refused;
            }

            if (it.Kind == ItemKind.RegistryValue)
            {
                int bar = it.Path.LastIndexOf('|');
                string valueName = it.Path.Substring(bar + 1);
                try
                {
                    using (RegistryKey k = Reg.Open(it.Path.Substring(0, bar), false))
                        if (k != null && PointsIntoApp(Reg.Str(k, valueName))) return null;
                }
                catch { }
                return NameIsApps(valueName) ? null : refused;
            }

            // Registry key
            if (EntryKeys.Any(e => string.Equals(e, it.Path, StringComparison.OrdinalIgnoreCase))) return null;
            string[] parts = it.Path.Split('\\');
            if (it.Path.IndexOf(@"\App Paths\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { using (RegistryKey k = Reg.Open(it.Path, false)) if (k != null && PointsIntoApp(Reg.Str(k, ""))) return null; }
                catch { }
                return refused;
            }
            if (it.Path.IndexOf(@"\Uninstall\", StringComparison.OrdinalIgnoreCase) >= 0) return refused; // only the app's own entries (above)
            string last = parts[parts.Length - 1];
            if (NameIsApps(last)) return null;
            if (Pub.Contains(Util.Key(last)))
            {
                // Publisher key holding nothing but this app's keys.
                try
                {
                    using (RegistryKey k = Reg.Open(it.Path, false))
                        if (k != null && k.ValueCount == 0 && k.SubKeyCount > 0 && k.GetSubKeyNames().All(NameIsApps)) return null;
                }
                catch { }
            }
            return refused;
        }
    }
}
