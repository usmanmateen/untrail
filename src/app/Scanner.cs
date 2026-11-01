using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Untrail
{
    public static class Cat
    {
        public const string Program = "Program folder";
        public const string AppData = "App data & caches";
        public const string Shared = "Shared app data (ProgramData)";
        public const string Temp = "Temporary files";
        public const string Shortcuts = "Shortcuts & Start menu";
        public const string Registry = "Registry entries";
        public const string Docs = "Your documents (check before deleting)";
        public const string Deep = "Found by deep scan";
        public static readonly string[] Order = { Program, AppData, Shared, Temp, Shortcuts, Registry, Docs, Deep };
    }

    public static class Scanner
    {
        // ---------------------------------------------------------------- uninstall records

        public static List<UninstallEntry> GetUninstallEntries()
        {
            var list = new List<UninstallEntry>();
            Read(list, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            Read(list, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            Read(list, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            return list;
        }

        static void Read(List<UninstallEntry> list, string hive, string sub)
        {
            try
            {
                using (RegistryKey root = Reg.Base(hive))
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey e = k.OpenSubKey(name))
                            {
                                if (e == null) continue;
                                string dn = Reg.Str(e, "DisplayName");
                                if (string.IsNullOrWhiteSpace(dn) || e.GetValue("ParentKeyName") != null) continue;
                                list.Add(new UninstallEntry
                                {
                                    KeyPath = hive + "\\" + sub + "\\" + name,
                                    KeyName = name,
                                    DisplayName = dn.Trim(),
                                    Publisher = (Reg.Str(e, "Publisher") ?? "").Trim(),
                                    DisplayVersion = (Reg.Str(e, "DisplayVersion") ?? "").Trim(),
                                    InstallLocation = Reg.Str(e, "InstallLocation"),
                                    UninstallString = Reg.Str(e, "UninstallString"),
                                    QuietUninstallString = Reg.Str(e, "QuietUninstallString"),
                                    DisplayIcon = Reg.Str(e, "DisplayIcon"),
                                    InstallDate = Reg.Str(e, "InstallDate"),
                                    EstimatedSizeKB = Reg.Num(e, "EstimatedSize"),
                                    WindowsInstaller = Reg.Num(e, "WindowsInstaller") == 1,
                                    Hidden = Reg.Num(e, "SystemComponent") == 1,
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static string EntryDir(UninstallEntry e)
        {
            string d = Util.NormDir(e.InstallLocation);
            if (d == null || Util.IsProtected(d)) return null;
            try { return Directory.Exists(d) ? d : null; } catch { return null; }
        }

        public static string IconExe(UninstallEntry e)
        {
            if (string.IsNullOrWhiteSpace(e.DisplayIcon)) return null;
            string s = e.DisplayIcon.Trim();
            int comma = s.LastIndexOf(',');
            if (comma > 2 && !s.Substring(comma).Contains("\\")) s = s.Substring(0, comma);
            s = Util.NormDir(s);
            return s != null && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s) ? s : null;
        }

        public static string UninstallerDir(UninstallEntry e)
        {
            if (string.IsNullOrWhiteSpace(e.UninstallString)) return null;
            string file, args;
            Util.SplitCommand(e.UninstallString, out file, out args);
            if (Path.GetFileName(file).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase)) return null;
            string dir;
            try { dir = Util.NormDir(Path.GetDirectoryName(file)); } catch { return null; }
            if (dir == null || Util.IsProtected(dir) || Util.IsAtOrUnder(dir, Util.Temp)) return null;
            if (dir.IndexOf("\\Package Cache", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (dir.IndexOf("\\InstallShield Installation Information", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return Directory.Exists(dir) ? DeriveInstallDir(dir) : null;
        }

        // ---------------------------------------------------------------- identify what was dropped

        static bool IsSubfolderName(string name)
        {
            string n = name.ToLowerInvariant();
            string[] subs = { "bin", "bin64", "bin32", "x64", "x86", "amd64", "win64", "win32", "app", "application",
                              "current", "program", "lib", "runtime", "uninstall", "uninst", "_uninstaller", "launcher" };
            return subs.Contains(n) || Regex.IsMatch(n, @"^(app-)?v?\d+(\.\d+)+[\w\-\.]*$") || Regex.IsMatch(n, @"^\d+$");
        }

        /// <summary>Walks up from bin\, app-1.2.3\ etc. to the folder the app was really installed into.</summary>
        public static string DeriveInstallDir(string dir)
        {
            for (int i = 0; i < 4 && dir != null; i++)
            {
                string parent = Path.GetDirectoryName(dir);
                if (parent == null || Util.IsProtected(parent)) break;
                if (!IsSubfolderName(Path.GetFileName(dir))) break;
                dir = parent.TrimEnd('\\');
            }
            return dir;
        }

        static bool IsStoreOrSystem(string target)
        {
            return Util.IsAtOrUnder(target, Util.WinDir)
                || Util.IsAtOrUnder(target, Path.Combine(Util.ProgramFiles, "WindowsApps"))
                || Util.IsAtOrUnder(target, Path.Combine(Util.LocalAppData, @"Microsoft\WindowsApps"));
        }

        public static AppInfo Identify(string path, List<UninstallEntry> entries, out string error)
        {
            error = null;
            var app = new AppInfo { SourcePath = path };
            string target = null, nameHint = null, steamId = null, dirHint = null;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (Directory.Exists(path))
            {
                dirHint = Util.NormDir(path);
                nameHint = Path.GetFileName(dirHint);
                if (!Util.IsSafeToDelete(dirHint))
                {
                    error = "That's a Windows or shared system folder, not an app's folder.";
                    return null;
                }
            }
            else if (!File.Exists(path))
            {
                error = "That file doesn't exist any more.";
                return null;
            }
            else if (ext == ".lnk")
            {
                app.ShortcutPath = path;
                nameHint = Util.CleanName(Path.GetFileNameWithoutExtension(path));
                string lnkArgs;
                target = Util.ResolveShortcut(path, out lnkArgs);
                // Chrome/Edge "installed" web apps are shortcuts to the browser itself: never treat them as the browser.
                string tname = Path.GetFileName(target ?? "").ToLowerInvariant();
                if (lnkArgs.IndexOf("--app-id=", StringComparison.OrdinalIgnoreCase) >= 0 || tname.EndsWith("_proxy.exe"))
                {
                    error = "“" + nameHint + "” is a web app installed from your browser (Chrome or Edge), not a real program.\n\n" +
                            "Remove it from the browser: open the app, click ⋮ and choose Uninstall, or go to chrome://apps or edge://apps.";
                    return null;
                }
            }
            else if (ext == ".url")
            {
                app.ShortcutPath = path;
                nameHint = Util.CleanName(Path.GetFileNameWithoutExtension(path));
                string url = "";
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url = line.Substring(4).Trim();
                    if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) target = line.Substring(9).Trim();
                }
                Match m = Regex.Match(url, @"^steam://rungameid/(\d+)", RegexOptions.IgnoreCase);
                if (m.Success) steamId = m.Groups[1].Value;
                if (target != null && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) target = null;
                if (steamId == null && target == null)
                {
                    error = "That's a web link, not an installed app.";
                    return null;
                }
            }
            else if (ext == ".exe")
            {
                target = path;
            }
            else
            {
                error = "Drop a desktop shortcut, an app's .exe, or the app's install folder.";
                return null;
            }

            target = Util.NormDir(target);
            if (target != null && Util.SamePath(target, Util.SelfExe))
            {
                error = "To remove Untrail itself, use Settings → Apps → Installed apps → Untrail.";
                return null;
            }
            bool targetOk = target != null && File.Exists(target) && !IsStoreOrSystem(target);
            string productName = null, company = null;
            if (targetOk)
            {
                app.ExePath = target;
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(target);
                    productName = vi.ProductName;
                    company = vi.CompanyName;
                    app.Version = vi.ProductVersion ?? "";
                }
                catch { }
            }

            var nameKeys = new HashSet<string>();
            foreach (string n in new[] { nameHint, Util.CleanName(productName) })
            {
                string k = Util.Key(n);
                if (Util.UsableKey(k)) nameKeys.Add(k);
            }

            int score = 0;
            if (steamId != null)
            {
                app.Entry = entries.FirstOrDefault(e => e.KeyName.Equals("Steam App " + steamId, StringComparison.OrdinalIgnoreCase));
                score = app.Entry != null ? 1000 : 0;
            }
            if (app.Entry == null) app.Entry = MatchEntry(app.ExePath ?? dirHint, app.ExePath, nameKeys, entries, out score);

            // ---- install folder
            string entryDir = app.Entry != null ? EntryDir(app.Entry) : null;
            if (entryDir == null && app.Entry != null)
            {
                string icon = IconExe(app.Entry);
                entryDir = UninstallerDir(app.Entry);
                if (entryDir == null && icon != null && !IsStoreOrSystem(icon)) entryDir = DeriveInstallDir(Path.GetDirectoryName(icon));
            }
            if (entryDir != null) entryDir = DeriveInstallDir(entryDir);

            if (dirHint != null) app.InstallDir = dirHint;
            else if (app.ExePath != null) app.InstallDir = DeriveInstallDir(Path.GetDirectoryName(app.ExePath));
            else app.InstallDir = entryDir;

            if (entryDir != null && app.InstallDir != null && !Util.IsAtOrUnder(app.InstallDir, entryDir) && !Util.IsAtOrUnder(entryDir, app.InstallDir))
                app.ExtraDir = entryDir;      // matched by name only: show the record's folder too, but unticked
            else if (entryDir != null && app.InstallDir != null && Util.IsUnder(app.InstallDir, entryDir))
                app.InstallDir = entryDir;    // exe sits deeper inside the recorded install folder

            if (app.InstallDir != null && !Util.IsSafeToDelete(app.InstallDir))
            {
                app.Notes.Add("The app lives inside a shared system folder, so that folder itself won't be touched.");
                app.InstallDir = null;
            }

            if (app.Entry == null && app.InstallDir == null)
            {
                if (target != null && IsStoreOrSystem(target))
                    error = "This icon opens a Windows program (" + Path.GetFileName(target) + "), so Untrail can't tell which app it belongs to.\n\n" +
                            "Use “Installed apps…” to pick the app from the list instead.\n(Microsoft Store apps are removed from Settings → Apps.)";
                else
                    error = "Couldn't work out which installed app this belongs to.\nTry \"Installed apps…\" and pick it from the list.";
                return null;
            }

            FillNamesAndKeys(app, entries, nameHint, productName, company);
            return app;
        }

        public static AppInfo FromEntry(UninstallEntry e, List<UninstallEntry> entries)
        {
            var app = new AppInfo { Entry = e, SourcePath = e.DisplayName };
            string dir = EntryDir(e) ?? UninstallerDir(e);
            string icon = IconExe(e);
            if (dir == null && icon != null && !IsStoreOrSystem(icon)) dir = Path.GetDirectoryName(icon);
            if (dir != null) dir = DeriveInstallDir(dir);
            if (dir != null && Util.IsSafeToDelete(dir)) app.InstallDir = dir;
            if (icon != null && !IsStoreOrSystem(icon)) app.ExePath = icon;
            string productName = null, company = null;
            if (app.ExePath != null)
            {
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(app.ExePath);
                    productName = vi.ProductName;
                    company = vi.CompanyName;
                }
                catch { }
            }
            FillNamesAndKeys(app, entries, null, productName, company);
            return app;
        }

        static UninstallEntry MatchEntry(string probe, string exe, HashSet<string> nameKeys, List<UninstallEntry> entries, out int bestScore)
        {
            UninstallEntry best = null;
            bestScore = 0;
            foreach (UninstallEntry e in entries)
            {
                int s = 0;
                if (probe != null)
                {
                    string loc = EntryDir(e);
                    if (loc != null && Util.IsAtOrUnder(probe, loc)) s = 1000 + loc.Length;
                    string icon = IconExe(e);
                    if (exe != null && icon != null && Util.SamePath(icon, exe)) s = Math.Max(s, 900);
                    string un = UninstallerDir(e);
                    if (un != null && Util.IsAtOrUnder(probe, un)) s = Math.Max(s, 800 + un.Length);
                }
                if (s == 0 && nameKeys.Count > 0)
                {
                    string k = Util.Key(Util.CleanName(e.DisplayName));
                    if (nameKeys.Contains(k)) s = 500;
                    else if (k.Length >= 6 && nameKeys.Any(n => n.StartsWith(k))) s = 400 + k.Length; // "Google Cloud SDK Shell" -> "Google Cloud SDK"
                }
                if (s > 0 && e.Hidden) s -= 1;
                if (s > bestScore) { bestScore = s; best = e; }
            }
            return best;
        }

        static void AddKey(List<string> list, string raw)
        {
            string k = Util.Key(raw);
            if (Util.UsableKey(k) && !list.Contains(k)) list.Add(k);
        }

        static void FillNamesAndKeys(AppInfo app, List<UninstallEntry> entries, string nameHint, string productName, string company)
        {
            UninstallEntry e = app.Entry;
            app.Name = e != null ? e.DisplayName : (!string.IsNullOrWhiteSpace(productName) ? productName.Trim()
                     : nameHint ?? Path.GetFileName(app.InstallDir));
            app.Publisher = e != null && e.Publisher.Length > 0 ? e.Publisher : (company ?? "").Trim();
            if (e != null && e.DisplayVersion.Length > 0) app.Version = e.DisplayVersion;

            string pubClean = Util.CleanPublisher(app.Publisher);
            if (e != null)
            {
                string clean = Util.CleanName(e.DisplayName);
                AddKey(app.Strong, clean);
                // "Mozilla Firefox" by "Mozilla" -> also "Firefox"
                string first = pubClean.Split(' ')[0];
                if (first.Length > 2 && clean.StartsWith(first + " ", StringComparison.OrdinalIgnoreCase))
                    AddKey(app.Strong, clean.Substring(first.Length + 1));
            }
            if (app.InstallDir != null && !IsSubfolderName(Path.GetFileName(app.InstallDir)))
                AddKey(app.Strong, Path.GetFileName(app.InstallDir));
            // Only trust the icon's name when it resembles the app it opens; a renamed or odd shortcut
            // ("GitHub" opening Chrome) must not turn unrelated folders into "Sure" matches.
            string hint = Util.Key(nameHint);
            if (app.Strong.Count == 0 || app.Strong.Any(k => hint.Length >= 3 && (k.Contains(hint) || hint.Contains(k))))
                AddKey(app.Strong, nameHint);
            else if (Util.UsableKey(hint))
                app.Notes.Add("(The icon is called “" + nameHint + "” but it opens " + app.Name + ".)");
            if (e == null) AddKey(app.Strong, Util.CleanName(productName));
            else AddKey(app.Weak, Util.CleanName(productName));
            if (app.ExePath != null) AddKey(app.Weak, Path.GetFileNameWithoutExtension(app.ExePath));
            app.Weak.RemoveAll(k => app.Strong.Contains(k));

            AddKey(app.Pub, app.Publisher);
            AddKey(app.Pub, pubClean);
            AddKey(app.Pub, Util.CleanPublisher(company));
            if (app.InstallDir != null)
            {
                string parent = Path.GetDirectoryName(app.InstallDir);
                if (parent != null && !Util.IsProtected(parent)) AddKey(app.Pub, Path.GetFileName(parent));
            }

            // Other uninstall records that are really the same app (EXE wrapper + MSI, per-user + per-machine).
            if (e != null)
            {
                string myKey = Util.Key(Util.CleanName(e.DisplayName));
                foreach (UninstallEntry o in entries)
                {
                    if (o == e) continue;
                    string od = EntryDir(o);
                    bool sameDir = od != null && app.InstallDir != null && Util.SamePath(DeriveInstallDir(od), app.InstallDir);
                    bool sameName = myKey.Length >= 3 && Util.Key(Util.CleanName(o.DisplayName)) == myKey
                                    && Util.Key(Util.CleanPublisher(o.Publisher)) == Util.Key(pubClean);
                    if (sameDir || sameName) app.SameApp.Add(o);
                }
            }
        }

        // ---------------------------------------------------------------- scanning

        class Ctx
        {
            public AppInfo App;
            public List<FoundItem> Items = new List<FoundItem>();
            public List<KeyValuePair<string, string>> OtherDirs = new List<KeyValuePair<string, string>>(); // dir, app name
            public Action<string> Progress;
            public Func<bool> Cancelled;
            public DateTime? InstalledAt;
        }

        public static List<FoundItem> Scan(AppInfo app, List<UninstallEntry> entries, bool deep, Action<string> progress, Func<bool> cancelled)
        {
            var c = new Ctx { App = app, Progress = progress ?? (s => { }), Cancelled = cancelled ?? (() => false) };

            foreach (UninstallEntry o in entries)
            {
                if (o == app.Entry || app.SameApp.Contains(o)) continue;
                foreach (string d in new[] { EntryDir(o), UninstallerDir(o) })
                    if (d != null) c.OtherDirs.Add(new KeyValuePair<string, string>(DeriveInstallDir(d), o.DisplayName));
            }

            // AppData\Local\CapCut\Apps -> AppData\Local\CapCut when the wrapping folder carries the app's own name
            // (its user data usually sits next to the program files).
            while (app.InstallDir != null)
            {
                string parent = Path.GetDirectoryName(app.InstallDir);
                if (parent == null || !app.Strong.Contains(Util.Key(Path.GetFileName(parent))) || !Util.IsSafeToDelete(parent)) break;
                if (c.OtherDirs.Any(kv => Util.IsAtOrUnder(kv.Key, parent) && !Util.IsAtOrUnder(kv.Key, app.InstallDir))) break;
                app.InstallDir = parent.TrimEnd('\\');
            }

            if (app.InstallDir != null && Directory.Exists(app.InstallDir))
            {
                var inside = c.OtherDirs.Where(kv => Util.IsUnder(kv.Key, app.InstallDir)).Select(kv => kv.Value).Distinct().ToList();
                if (inside.Count == 0)
                    Add(c, app.InstallDir, ItemKind.Folder, Confidence.Sure, Cat.Program, "Where the app is installed", true);
                else
                    Add(c, app.InstallDir, ItemKind.Folder, Confidence.Likely, Cat.Program,
                        "Install folder — but it also holds: " + string.Join(", ", inside.Take(3)) + (inside.Count > 3 ? "…" : ""), true);
            }
            if (app.ExtraDir != null)
                Add(c, app.ExtraDir, ItemKind.Folder, Confidence.Likely, Cat.Program, "Install folder named in the app's uninstall record", false);
            // Remembered on the app so a re-scan after removal (install folder gone) still knows it.
            if (!app.InstalledAt.HasValue) app.InstalledAt = InstallTime(app);
            c.InstalledAt = app.InstalledAt;

            c.Progress("Looking in AppData…");
            ScanContainer(c, Util.LocalAppData, Cat.AppData, 2, Confidence.Sure);
            ScanContainer(c, Util.RoamingAppData, Cat.AppData, 2, Confidence.Sure);
            ScanContainer(c, Util.LocalLow, Cat.AppData, 2, Confidence.Sure);
            c.Progress("Looking in ProgramData and Program Files…");
            ScanContainer(c, Util.ProgramData, Cat.Shared, 2, Confidence.Sure);
            ScanContainer(c, Util.ProgramFiles, Cat.Program, 2, Confidence.Sure);
            if (!Util.SamePath(Util.ProgramFiles, Util.ProgramFilesX86)) ScanContainer(c, Util.ProgramFilesX86, Cat.Program, 2, Confidence.Sure);
            ScanContainer(c, Util.CommonFiles, Cat.Program, 1, Confidence.Sure);
            if (!Util.SamePath(Util.CommonFiles, Util.CommonFilesX86)) ScanContainer(c, Util.CommonFilesX86, Cat.Program, 1, Confidence.Sure);
            ScanContainer(c, Util.Temp, Cat.Temp, 1, Confidence.Sure);
            ScanContainer(c, Util.UserProfile, Cat.AppData, 1, Confidence.Likely);
            ScanContainer(c, Util.Documents, Cat.Docs, 1, Confidence.Maybe);
            ScanContainer(c, Util.SavedGames, Cat.Docs, 1, Confidence.Maybe);

            c.Progress("Looking for shortcuts…");
            ScanShortcuts(c);
            c.Progress("Looking in the registry…");
            ScanRegistry(c);

            if (deep && !c.Cancelled()) DeepScan(c);

            Tidy(c);

            int n = 0;
            foreach (FoundItem it in c.Items)
            {
                if (c.Cancelled()) break;
                n++;
                c.Progress("Measuring sizes (" + n + "/" + c.Items.Count + ")…");
                if (it.Kind == ItemKind.Folder) it.Size = it.IsLink ? 0 : Util.DirSize(it.Path, c.Cancelled);
                else if (it.Kind == ItemKind.File) { try { it.Size = new FileInfo(it.Path).Length; } catch { } }
                it.NeedsAdmin = (it.Kind == ItemKind.Registry || it.Kind == ItemKind.RegistryValue)
                    ? it.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
                    : !Util.IsAtOrUnder(it.Path, Util.UserProfile);
            }
            return c.Items;
        }

        static Confidence Weaker(Confidence a, Confidence b) { return (Confidence)Math.Max((int)a, (int)b); }

        /// <summary>Does a folder/key name look like it belongs to the app? null = no.</summary>
        static Confidence? Match(Ctx c, string name, out string reason)
        {
            reason = null;
            string n = Util.Key(name.TrimStart('.'));
            if (n.Length < 3) return null;
            foreach (string k in c.App.Strong)
            {
                if (n == k && Util.CommonWords.Contains(k)) { reason = "Same name as the app, but it's a common word"; return Confidence.Likely; }
                if (n == k) { reason = "Name matches the app"; return Confidence.Sure; }
                if (n == k + "updater") { reason = "The app's update cache"; return Confidence.Sure; } // Electron apps: "<name>-updater"
            }
            foreach (string k in c.App.Weak)
                if (n == k) { reason = "Name matches the app's program file"; return Confidence.Likely; }
            if (c.App.Pub.Contains(n)) return null; // publisher folders are searched inside instead
            foreach (string k in c.App.Strong)
            {
                if (k.Length >= 5 && n.StartsWith(k)) { reason = "Name starts with the app's name"; return Confidence.Likely; }
                if (n.Length >= 5 && k.StartsWith(n)) { reason = "Name is a short form of the app's name"; return Confidence.Maybe; }
                if (k.Length >= 6 && n.Contains(k)) { reason = "Name contains the app's name"; return Confidence.Maybe; }
            }
            return null;
        }

        /// <summary>
        /// A matching name alone doesn't prove a folder is the app's. To stay "Safe to remove" it also has to have been
        /// used since the app was installed; null when that holds, otherwise why it's only "Review".
        /// </summary>
        static string OwnershipDoubt(Ctx c, string dir)
        {
            if (!c.InstalledAt.HasValue) return "Same name as the app — check it's the app's";
            DateTime last;
            try
            {
                last = Directory.GetLastWriteTime(dir);
                foreach (FileSystemInfo f in new DirectoryInfo(dir).EnumerateFileSystemInfos().Take(300))
                    if (f.LastWriteTime > last) last = f.LastWriteTime;
            }
            catch { return null; }
            return last >= c.InstalledAt.Value.AddDays(-2) ? null : "Same name as the app, but untouched since before it was installed";
        }

        static DateTime? InstallTime(AppInfo app)
        {
            DateTime? t = null;
            try { if (app.InstallDir != null && Directory.Exists(app.InstallDir)) t = Directory.GetCreationTime(app.InstallDir); } catch { }
            DateTime d;
            if (app.Entry != null && DateTime.TryParseExact(app.Entry.InstallDate ?? "", "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out d))
                t = t.HasValue && t.Value < d ? t : d;
            return t;
        }

        static string Overlap(Ctx c, string dir, bool isInstallDir)
        {
            foreach (var kv in c.OtherDirs)
            {
                if (Util.IsUnder(kv.Key, dir)) return kv.Value;                     // dir would swallow another app
                if (!isInstallDir && Util.IsAtOrUnder(dir, kv.Key)) return kv.Value; // dir belongs to another app
            }
            return null;
        }

        static void Add(Ctx c, string path, ItemKind kind, Confidence conf, string cat, string reason, bool isInstallDir)
        {
            if (kind == ItemKind.Folder || kind == ItemKind.File)
            {
                path = Util.NormDir(path);
                if (path == null || !Util.IsSafeToDelete(path)) return;
                if (!isInstallDir && c.App.InstallDir != null &&
                    (Util.IsAtOrUnder(path, c.App.InstallDir) || Util.IsUnder(c.App.InstallDir, path))) return;
                if (kind == ItemKind.Folder && Overlap(c, path, isInstallDir) != null && !isInstallDir) return;
            }
            if (c.Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var item = new FoundItem { Path = path, Kind = kind };
            if (!StillExists(item)) return; // e.g. a re-scan after removal: the dropped shortcut or uninstall entry is gone
            c.Items.Add(new FoundItem
            {
                Path = path, Kind = kind, Conf = conf, Category = cat, Reason = reason,
                IsLink = kind == ItemKind.Folder && Util.IsReparse(path),
            });
        }

        static void ScanContainer(Ctx c, string root, string cat, int depth, Confidence cap)
        {
            if (root == null || !Directory.Exists(root) || c.Cancelled()) return;
            foreach (string dir in Util.SafeDirs(root))
            {
                string name = Path.GetFileName(dir);
                if (Util.IsProtected(dir))
                {
                    // AppData\Local\Programs holds per-user installs: look inside it. Other protected
                    // folders (Microsoft, Temp, Packages...) are never searched here.
                    if (depth >= 2 && Util.SamePath(dir, Path.Combine(Util.LocalAppData, "Programs"))) ScanContainer(c, dir, cat, 2, cap);
                    continue;
                }
                string reason;
                Confidence? m = Match(c, name, out reason);
                if (m.HasValue)
                {
                    Confidence conf = Weaker(m.Value, cap);
                    if (conf == Confidence.Sure)
                    {
                        string doubt = OwnershipDoubt(c, dir);
                        if (doubt != null) { conf = Confidence.Likely; reason = doubt; }
                    }
                    Add(c, dir, ItemKind.Folder, conf, cat, reason, false);
                }
                else if (depth >= 2 && c.App.Pub.Contains(Util.Key(name)))
                {
                    foreach (string sub in Util.SafeDirs(dir))
                    {
                        Confidence? sm = Match(c, Path.GetFileName(sub), out reason);
                        if (sm.HasValue) Add(c, sub, ItemKind.Folder, Weaker(sm.Value, cap), cat, reason + " (in the publisher's folder)", false);
                    }
                }
            }
        }

        static void ScanShortcuts(Ctx c)
        {
            AppInfo app = c.App;
            if (app.ShortcutPath != null)
                Add(c, app.ShortcutPath, ItemKind.File, Confidence.Sure, Cat.Shortcuts, "The icon you dropped", false);

            string[] roots = { Util.Desktop, Util.PublicDesktop, Util.StartMenu, Util.CommonStartMenu, Util.QuickLaunch };
            foreach (string root in roots)
            {
                if (root == null || !Directory.Exists(root)) continue;
                var stack = new Stack<KeyValuePair<string, int>>();
                stack.Push(new KeyValuePair<string, int>(root, 0));
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    foreach (string lnk in Util.SafeFiles(cur.Key, "*.lnk"))
                    {
                        string t = Util.NormDir(Util.ResolveShortcut(lnk));
                        string reason;
                        if (t != null && ((app.InstallDir != null && Util.IsAtOrUnder(t, app.InstallDir)) ||
                                          (app.ExePath != null && Util.SamePath(t, app.ExePath))))
                            Add(c, lnk, ItemKind.File, Confidence.Sure, Cat.Shortcuts, "Shortcut that opens the app", false);
                        else if ((t == null || Util.IsAtOrUnder(t, Path.Combine(Util.WinDir, "Installer"))) &&
                                 Match(c, Util.CleanName(Path.GetFileNameWithoutExtension(lnk)), out reason) == Confidence.Sure)
                            Add(c, lnk, ItemKind.File, Confidence.Likely, Cat.Shortcuts, "Shortcut with the app's name", false);
                    }
                    if (cur.Value >= 4) continue;
                    foreach (string d in Util.SafeDirs(cur.Key))
                    {
                        string reason;
                        Confidence? m = Util.IsProtected(d) ? null : Match(c, Path.GetFileName(d), out reason);
                        if (m == Confidence.Sure) Add(c, d, ItemKind.Folder, Confidence.Sure, Cat.Shortcuts, "Start menu folder for the app", false);
                        stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
                }
            }
        }

        public static readonly HashSet<string> RegSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "ODBC",
            "Windows", "Description", "Hardware", "Setup", "System", "AppDataLow", "Intel", "Khronos", "Partner",
        };

        static void ScanRegistry(Ctx c)
        {
            AppInfo app = c.App;
            if (app.Entry != null)
                Add(c, app.Entry.KeyPath, ItemKind.Registry, Confidence.Sure, Cat.Registry, "The app's entry in Installed apps", false);
            foreach (UninstallEntry o in app.SameApp)
                Add(c, o.KeyPath, ItemKind.Registry, Confidence.Sure, Cat.Registry, "Another Installed-apps entry for the same app", false);

            string[] roots = { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" };
            foreach (string root in roots)
            {
                try
                {
                    using (RegistryKey k = Reg.Open(root, false))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetSubKeyNames())
                        {
                            if (RegSkip.Contains(name)) continue;
                            string reason;
                            Confidence? m = Match(c, name, out reason);
                            if (m.HasValue && m.Value != Confidence.Maybe)
                            {
                                Add(c, root + "\\" + name, ItemKind.Registry, m.Value, Cat.Registry, "Settings key — " + reason.ToLowerInvariant(), false);
                                continue;
                            }
                            if (!app.Pub.Contains(Util.Key(name))) continue;
                            using (RegistryKey pk = k.OpenSubKey(name))
                            {
                                if (pk == null) continue;
                                string[] subs = pk.GetSubKeyNames();
                                int matched = 0;
                                foreach (string s in subs)
                                {
                                    Confidence? sm = Match(c, s, out reason);
                                    if (sm.HasValue && sm.Value != Confidence.Maybe)
                                    {
                                        matched++;
                                        Add(c, root + "\\" + name + "\\" + s, ItemKind.Registry, sm.Value, Cat.Registry, "Settings key in the publisher's key", false);
                                    }
                                }
                                if (matched > 0 && matched == subs.Length && pk.ValueCount == 0)
                                {
                                    c.Items.RemoveAll(i => i.Kind == ItemKind.Registry && i.Path.StartsWith(root + "\\" + name + "\\", StringComparison.OrdinalIgnoreCase));
                                    Add(c, root + "\\" + name, ItemKind.Registry, Confidence.Sure, Cat.Registry, "Publisher key that only holds this app", false);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (app.InstallDir == null && app.ExePath == null) return;

            // Start-up entries and App Paths pointing into the app.
            string[] runKeys =
            {
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
            };
            foreach (string rk in runKeys)
            {
                try
                {
                    using (RegistryKey k = Reg.Open(rk, false))
                    {
                        if (k == null) continue;
                        foreach (string v in k.GetValueNames())
                        {
                            string data = Reg.Str(k, v) ?? "";
                            if (PointsIntoApp(app, data))
                                Add(c, rk + "|" + v, ItemKind.RegistryValue, Confidence.Sure, Cat.Registry, "Starts the app when Windows starts", false);
                        }
                    }
                }
                catch { }
            }
            foreach (string ap in new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths" })
            {
                try
                {
                    using (RegistryKey k = Reg.Open(ap, false))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetSubKeyNames())
                        {
                            using (RegistryKey s = k.OpenSubKey(name))
                            {
                                if (s != null && PointsIntoApp(app, Reg.Str(s, "") ?? ""))
                                    Add(c, ap + "\\" + name, ItemKind.Registry, Confidence.Sure, Cat.Registry, "Lets Windows find the app's program file", false);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        static bool PointsIntoApp(AppInfo app, string command)
        {
            string file, args;
            Util.SplitCommand(command, out file, out args);
            file = Util.NormDir(file);
            if (file == null) return false;
            return (app.InstallDir != null && Util.IsAtOrUnder(file, app.InstallDir)) || (app.ExePath != null && Util.SamePath(file, app.ExePath));
        }

        static readonly HashSet<string> DeepSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Recovery", "Windows", "Config.Msi", "$WinREAgent",
            "$Windows.~BT", "$Windows.~WS", "MSOCache", "PerfLogs", "WinSxS", "node_modules", ".git",
        };

        static void DeepScan(Ctx c)
        {
            int seen = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (c.Cancelled()) return;
                try { if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue; } catch { continue; }
                var queue = new Queue<KeyValuePair<string, int>>();
                queue.Enqueue(new KeyValuePair<string, int>(drive.RootDirectory.FullName, 0));
                while (queue.Count > 0)
                {
                    if (c.Cancelled()) return;
                    var cur = queue.Dequeue();
                    foreach (string d in Util.SafeDirs(cur.Key))
                    {
                        string name = Path.GetFileName(d);
                        if (DeepSkip.Contains(name) || Util.IsReparse(d)) continue;
                        if (++seen % 500 == 0) c.Progress("Deep scan: " + d);
                        if (c.App.InstallDir != null && Util.IsAtOrUnder(d, c.App.InstallDir)) continue;
                        if (c.Items.Any(i => i.Kind == ItemKind.Folder && Util.IsAtOrUnder(d, i.Path))) continue;
                        string reason;
                        if (!Util.IsProtected(d) && Match(c, name, out reason) == Confidence.Sure)
                        {
                            Add(c, d, ItemKind.Folder, Confidence.Likely, Cat.Deep, "Folder with the app's name", false);
                            continue;
                        }
                        if (cur.Value + 1 < 4) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
                }
            }
        }

        /// <summary>Drop nested duplicates, then take a parent folder when it holds nothing but this app's leftovers.</summary>
        static void Tidy(Ctx c)
        {
            var paths = c.Items.Where(i => i.Kind == ItemKind.Folder || i.Kind == ItemKind.File).ToList();
            var drop = new HashSet<FoundItem>();
            foreach (FoundItem a in paths)
                foreach (FoundItem b in paths)
                {
                    if (a == b || a.Kind != ItemKind.Folder || !Util.IsUnder(b.Path, a.Path) || drop.Contains(a)) continue;
                    if (a.Conf <= b.Conf) drop.Add(b); else drop.Add(a);
                }
            c.Items.RemoveAll(drop.Contains);

            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                var byParent = c.Items.Where(i => i.Kind == ItemKind.Folder || i.Kind == ItemKind.File)
                                      .GroupBy(i => Path.GetDirectoryName(i.Path), StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var g in byParent)
                {
                    string parent = g.Key;
                    if (parent == null || !Util.IsSafeToDelete(parent) || Overlap(c, parent, false) != null) continue;
                    if (c.App.InstallDir != null && Util.IsUnder(c.App.InstallDir, parent) && !g.Any(i => Util.SamePath(i.Path, c.App.InstallDir))) continue;
                    string[] all;
                    try { all = Directory.GetFileSystemEntries(parent); } catch { continue; }
                    var mine = new HashSet<string>(g.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
                    if (all.Length == 0 || !all.All(mine.Contains)) continue;
                    FoundItem first = g.OrderBy(i => i.Conf).First();
                    c.Items.RemoveAll(i => g.Contains(i));
                    c.Items.Add(new FoundItem
                    {
                        Path = parent.TrimEnd('\\'), Kind = ItemKind.Folder, Conf = g.Max(i => i.Conf), Category = first.Category,
                        Reason = first.Reason + " (its parent folder holds nothing else)",
                    });
                    changed = true;
                }
                if (!changed) break;
            }
        }

        public static bool StillExists(FoundItem it)
        {
            switch (it.Kind)
            {
                case ItemKind.Folder: return Directory.Exists(it.Path);
                case ItemKind.File: return File.Exists(it.Path);
                case ItemKind.Registry: return Reg.Exists(it.Path);
                default:
                    int bar = it.Path.LastIndexOf('|');
                    return Reg.ValueExists(it.Path.Substring(0, bar), it.Path.Substring(bar + 1));
            }
        }

        public static List<Process> FindRunning(AppInfo app)
        {
            var list = new List<Process>();
            if (app.InstallDir == null) return list;
            foreach (Process p in Process.GetProcesses())
            {
                string f = null;
                try { f = Util.ProcessPath(p.Id); } catch { }
                if (f != null && Util.IsAtOrUnder(f, app.InstallDir)) list.Add(p);
                else p.Dispose();
            }
            return list;
        }
    }
}
