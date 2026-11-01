using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Untrail
{
    static class ShellIcon
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint attr;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll", EntryPoint = "#727")]
        static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
        [DllImport("comctl32.dll")]
        static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        /// <summary>48px icon with proper transparency (Icon.ExtractAssociatedIcon only gives 32px).</summary>
        public static Image Get(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x4000); // SHGFI_SYSICONINDEX
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                IntPtr list;
                if (SHGetImageList(2, ref iid, out list) != 0) return null;   // SHIL_EXTRALARGE
                IntPtr h = ImageList_GetIcon(list, info.iIcon, 1);
                if (h == IntPtr.Zero) return null;
                using (Icon ic = Icon.FromHandle(h))
                {
                    Image img = ic.ToBitmap();
                    DestroyIcon(h);
                    return img;
                }
            }
            catch { return null; }
        }
    }

    class DropZone : Control
    {
        public Image AppImage;
        public string Title = "Drop an app icon here";
        public string Sub = "Drag a shortcut from your desktop, an .exe, or the app's folder";
        public bool Hot;
        Font titleFont;

        public DropZone()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            float s = DeviceDpi / 96f;
            var r = new RectangleF(1.5f * s, 1.5f * s, Width - 3.5f * s, Height - 3.5f * s);
            using (GraphicsPath p = Theme.Round(r, 14 * s))
            {
                using (var b = new SolidBrush(Hot ? Theme.OrangeSoft : Theme.White)) g.FillPath(b, p);
                using (var pen = new Pen(Hot ? Theme.Orange : Theme.OrangeLine, 1.6f * s))
                {
                    if (!Hot) pen.DashPattern = new[] { 4f, 3f };
                    g.DrawPath(pen, p);
                }
            }
            int icon = (int)(52 * s);
            var ir = new Rectangle((Width - icon) / 2, (int)(r.Top + 20 * s), icon, icon);
            if (AppImage != null) g.DrawImage(AppImage, ir);
            else
            {
                Image mark = Theme.Art("mark.png");
                if (mark != null)
                {
                    int h = icon, w = mark.Width * h / mark.Height;
                    g.DrawImage(mark, ir.X + (icon - w) / 2, ir.Y, w, h);
                }
            }

            var tr = new Rectangle((int)(r.X + 12 * s), ir.Bottom + (int)(10 * s), (int)(r.Width - 24 * s), (int)(24 * s));
            if (titleFont == null) titleFont = Theme.Bold(11f);
            TextRenderer.DrawText(g, Title, titleFont, tr, Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            var sr = new Rectangle(tr.X, tr.Bottom + (int)(2 * s), tr.Width, (int)(r.Bottom - tr.Bottom - 8 * s));
            TextRenderer.DrawText(g, Sub, Font, sr, Theme.Stone, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
    }

    public class MainForm : Form
    {
        readonly float scale;
        DropZone drop;
        Label lblName, lblPublisher, lblInstall, lblUninst, lblSize, lblSizeSub, lblHint;
        BrandButton btnBrowse, btnPick, btnRescan, btnDelete, btnCancel;
        CheckBox chkUninstaller, chkDeep, chkPermanent;
        ListView list;
        SizeBar bar;
        ToolStripStatusLabel status;
        ToolStripProgressBar progress;
        LinkLabel lnkAll, lnkSure, lnkNone;

        AppInfo app;
        List<FoundItem> items = new List<FoundItem>();
        List<UninstallEntry> entries = new List<UninstallEntry>();
        volatile bool cancelRequested;
        bool busy, populating;

        int S(int v) { return (int)Math.Round(v * scale); }

        static T Place<T>(Control parent, T c, DockStyle d) where T : Control
        {
            c.Dock = d;
            parent.Controls.Add(c);
            c.BringToFront();
            return c;
        }

        public static string StateLabel(Confidence c)
        {
            return c == Confidence.Sure ? "✓  Safe to remove" : c == Confidence.Likely ? "⚠  Review" : "✕  Keep";
        }

        public static Color StateColor(Confidence c)
        {
            return c == Confidence.Sure ? Theme.Safe : c == Confidence.Likely ? Theme.Review : Theme.Keep;
        }

        public MainForm(string startPath)
        {
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = "Untrail — uninstall apps completely";
            Font = Theme.Ui(9.5f);
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(1120), S(800));
            MinimumSize = new Size(S(920), S(660));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.AppIcon();

            var brandBar = Place(this, new Panel { Height = S(64) }, DockStyle.Top);
            brandBar.Paint += (s, e) => Theme.PaintBrandBar(e.Graphics, brandBar.ClientRectangle, scale, true);
            brandBar.Resize += (s, e) => brandBar.Invalidate();
            BuildHeader();
            BuildOptions();
            var strip = new StatusStrip { SizingGrip = true, BackColor = Theme.White };
            status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Stone };
            progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Width = S(160) };
            strip.Items.Add(status);
            strip.Items.Add(progress);
            Place(this, strip, DockStyle.Bottom);
            BuildFooter();
            BuildList();

            EnableDrop(this);
            ShowEmpty();
            if (Util.IsAdmin)
                drop.Sub = "Running as administrator: Windows blocks drag & drop into admin windows. Use Browse… or Installed apps…";
            if (!string.IsNullOrEmpty(startPath)) Shown += (s, e) => LoadPath(startPath);
        }

        // ---------------------------------------------------------------- layout

        BrandButton MakeButton(string text, string glyph, bool primary = false, bool arrow = false)
        {
            var b = new BrandButton
            {
                Text = text, Glyph = glyph, Primary = primary, Arrow = arrow, Height = S(38),
                AutoSize = !primary, Margin = new Padding(0, 0, S(10), 0), Font = primary ? Theme.Bold(11f) : Theme.Ui(9.5f),
            };
            return b;
        }

        void BuildHeader()
        {
            var header = Place(this, new Panel { Height = S(200), Padding = new Padding(S(20), S(20), S(20), S(8)) }, DockStyle.Top);
            drop = Place(header, new DropZone { Width = S(270), Font = Theme.Ui(9f) }, DockStyle.Left);
            drop.Click += (s, e) => Browse();
            Place(header, new Panel { Width = S(24) }, DockStyle.Left);
            var info = Place(header, new Panel { Padding = new Padding(0, S(6), 0, 0) }, DockStyle.Fill);

            lblName = Place(info, new Label { Height = S(42), Font = Theme.Bold(18f), AutoEllipsis = true, ForeColor = Theme.Ink }, DockStyle.Top);
            lblPublisher = Place(info, new Label { Height = S(24), ForeColor = Theme.Stone, AutoEllipsis = true, UseMnemonic = false }, DockStyle.Top);
            lblInstall = Place(info, new Label { Height = S(24), AutoEllipsis = true, UseMnemonic = false }, DockStyle.Top);
            lblUninst = Place(info, new Label { Height = S(26), AutoEllipsis = true, UseMnemonic = false }, DockStyle.Top);
            Place(info, new Panel { Height = S(8) }, DockStyle.Top);
            var buttons = Place(info, new FlowLayoutPanel { Height = S(44), WrapContents = false }, DockStyle.Top);
            btnBrowse = MakeButton("Browse…", Theme.GlyphFolder);
            btnPick = MakeButton("Installed apps…", Theme.GlyphApps);
            btnRescan = MakeButton("Scan for leftovers", Theme.GlyphSearch);
            btnBrowse.Click += (s, e) => Browse();
            btnPick.Click += (s, e) => PickInstalled();
            btnRescan.Click += (s, e) => { if (app != null) RunScan(app, false); };
            buttons.Controls.AddRange(new Control[] { btnBrowse, btnPick, btnRescan });
        }

        void BuildOptions()
        {
            var bar = Place(this, new FlowLayoutPanel { Height = S(40), Padding = new Padding(S(18), S(6), S(18), 0), WrapContents = false }, DockStyle.Top);
            chkUninstaller = new CheckBox { Text = "Run the app's own uninstaller first", AutoSize = true, Checked = true, Margin = new Padding(0, 0, S(24), 0) };
            chkDeep = new CheckBox { Text = "Deep scan all drives (slower)", AutoSize = true, Margin = new Padding(0, 0, S(24), 0) };
            chkPermanent = new CheckBox { Text = "Delete permanently (skip the Recycle Bin, frees space right away)", AutoSize = true };
            chkDeep.CheckedChanged += (s, e) => { if (app != null && chkDeep.Checked && !busy) RunScan(app, false); };
            bar.Controls.AddRange(new Control[] { chkUninstaller, chkDeep, chkPermanent });
        }

        void BuildFooter()
        {
            var footer = Place(this, new Panel { Height = S(112), BackColor = Theme.White, Padding = new Padding(S(22), S(14), S(22), S(12)) }, DockStyle.Bottom);
            footer.Paint += (s, e) => { using (var pen = new Pen(Theme.Cloud)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

            var right = Place(footer, new Panel { Width = S(290), Padding = new Padding(S(20), S(12), 0, S(14)) }, DockStyle.Right);
            btnDelete = Place(right, MakeButton("Uninstall & Clean", Theme.GlyphTrash, true, true), DockStyle.Fill);
            btnDelete.Text = "Uninstall && Clean";
            btnDelete.Click += (s, e) => StartRemoval();
            btnCancel = MakeButton("Stop scan", Theme.GlyphStop);
            btnCancel.AutoSize = false;
            btnCancel.Visible = false;
            btnCancel.Click += (s, e) => { cancelRequested = true; status.Text = "Stopping…"; };
            Place(right, btnCancel, DockStyle.Fill);

            var left = Place(footer, new Panel(), DockStyle.Fill);
            var sizeRow = Place(left, new FlowLayoutPanel { Height = S(42), WrapContents = false }, DockStyle.Top);
            lblSize = new Label { AutoSize = true, Font = Theme.Bold(20f), ForeColor = Theme.Ink, Margin = new Padding(0, 0, S(8), 0) };
            lblSizeSub = new Label { AutoSize = true, ForeColor = Theme.Stone, Margin = new Padding(0, S(13), 0, 0) };
            sizeRow.Controls.AddRange(new Control[] { lblSize, lblSizeSub });
            var barRow = Place(left, new Panel { Height = S(18), Padding = new Padding(0, S(4), S(40), S(4)) }, DockStyle.Top);
            bar = Place(barRow, new SizeBar(), DockStyle.Fill);
            var links = Place(left, new FlowLayoutPanel { Height = S(26), WrapContents = false }, DockStyle.Top);
            lblHint = new Label { AutoSize = true, ForeColor = Theme.Stone, Margin = new Padding(0, S(4), S(14), 0) };
            lnkAll = MakeLink("Select all");
            lnkSure = MakeLink("Only safe");
            lnkNone = MakeLink("Select none");
            lnkAll.Click += (s, e) => SetChecks(it => true);
            lnkSure.Click += (s, e) => SetChecks(it => it.Conf == Confidence.Sure);
            lnkNone.Click += (s, e) => SetChecks(it => false);
            links.Controls.AddRange(new Control[] { lblHint, lnkAll, lnkSure, lnkNone });
        }

        void BuildList()
        {
            var wrap = Place(this, new Panel { Padding = new Padding(S(20), S(4), S(20), S(12)) }, DockStyle.Fill);
            list = Place(wrap, new ListView
            {
                View = View.Details, CheckBoxes = true, FullRowSelect = true, ShowGroups = true, HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.Ui(9.5f), ShowItemToolTips = true, ForeColor = Theme.Ink,
            }, DockStyle.Fill);
            list.Columns.Add("What will be removed", S(500));
            list.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            list.Columns.Add("Status", S(140));
            list.Columns.Add("Why it was found", S(300));
            list.ItemChecked += (s, e) => { if (!populating) UpdateTotal(); };
            list.Resize += (s, e) => FitColumns();
            list.DoubleClick += (s, e) => OpenSelected();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show in Explorer / Registry Editor", null, (s, e) => OpenSelected());
            menu.Items.Add("Tick selected rows", null, (s, e) => { foreach (ListViewItem i in list.SelectedItems) i.Checked = true; });
            menu.Items.Add("Untick selected rows", null, (s, e) => { foreach (ListViewItem i in list.SelectedItems) i.Checked = false; });
            menu.Items.Add("Copy path", null, (s, e) =>
            {
                if (list.SelectedItems.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, list.SelectedItems.Cast<ListViewItem>().Select(i => ((FoundItem)i.Tag).DisplayPath)));
            });
            list.ContextMenuStrip = menu;
        }

        void FitColumns()
        {
            int other = list.Columns[1].Width + list.Columns[2].Width + list.Columns[3].Width;
            int w = list.ClientSize.Width - other - SystemInformation.VerticalScrollBarWidth;
            if (w > S(260)) list.Columns[0].Width = w;
        }

        LinkLabel MakeLink(string text)
        {
            return new LinkLabel
            {
                Text = text, AutoSize = true, LinkColor = Theme.Orange, ActiveLinkColor = Theme.OrangeLight, DisabledLinkColor = Theme.Cloud,
                LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, S(4), S(12), 0),
            };
        }

        void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragOver += OnDragEnter;
            c.DragLeave += (s, e) => { drop.Hot = false; drop.Invalidate(); };
            c.DragDrop += OnDragDrop;
            foreach (Control child in c.Controls) EnableDrop(child);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            bool ok = !busy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = ok ? DragDropEffects.Link : DragDropEffects.None; // Link: we only read the path, Explorer never moves anything
            if (drop.Hot != ok) { drop.Hot = ok; drop.Invalidate(); }
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            drop.Hot = false;
            drop.Invalidate();
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) BeginInvoke(new Action(() => LoadPath(files[0])));
        }

        // ---------------------------------------------------------------- state

        void ShowEmpty()
        {
            app = null;
            items = new List<FoundItem>();
            drop.AppImage = null;
            drop.Title = "Drop an app icon here";
            drop.Invalidate();
            lblName.Text = "No app selected yet";
            lblPublisher.Text = "Drag an app's icon from your desktop into the box on the left.";
            lblInstall.Text = "Untrail finds its program folder, AppData, caches, shortcuts and registry entries.";
            lblUninst.Text = "";
            chkUninstaller.Enabled = true;
            PopulateList();
        }

        void SetBusy(bool on, string text)
        {
            busy = on;
            progress.Visible = on;
            if (on) progress.Style = ProgressBarStyle.Marquee; // the delete step switches it to real progress
            if (text != null) status.Text = text;
            foreach (Control c in new Control[] { btnBrowse, btnPick, btnRescan, chkDeep, chkPermanent, chkUninstaller, list })
                c.Enabled = !on;
            if (!on && app != null) chkUninstaller.Enabled = app.HasUninstaller;
            btnRescan.Enabled = !on && app != null;
            UpdateTotal();
        }

        void Progress(string s)
        {
            try { BeginInvoke(new Action(() => status.Text = s)); } catch { }
        }

        void RunBg(Action work, Action<Exception> done)
        {
            var t = new Thread(() =>
            {
                Exception err = null;
                try { work(); } catch (Exception ex) { err = ex; }
                try { BeginInvoke(new Action(() => done(err))); } catch { }
            });
            t.SetApartmentState(ApartmentState.STA); // WScript.Shell for reading shortcuts
            t.IsBackground = true;
            t.Start();
        }

        // ---------------------------------------------------------------- loading an app

        void Browse()
        {
            if (busy) return;
            using (var d = new OpenFileDialog
            {
                Title = "Pick an app shortcut or program file",
                Filter = "Apps and shortcuts (*.lnk;*.exe;*.url)|*.lnk;*.exe;*.url|All files (*.*)|*.*",
                DereferenceLinks = false,
                InitialDirectory = Util.Desktop,
            })
            {
                if (d.ShowDialog(this) == DialogResult.OK) LoadPath(d.FileName);
            }
        }

        void PickInstalled()
        {
            if (busy) return;
            SetBusy(true, "Reading the list of installed apps…");
            RunBg(() => entries = Scanner.GetUninstallEntries(), err =>
            {
                SetBusy(false, "Ready");
                using (var picker = new AppPicker(entries, scale))
                {
                    if (picker.ShowDialog(this) == DialogResult.OK && picker.Selected != null)
                        RunScan(null, true, picker.Selected);
                }
            });
        }

        void LoadPath(string path)
        {
            if (busy) return;
            SetBusy(true, "Working out which app this is…");
            cancelRequested = false;
            AppInfo found = null;
            string error = null;
            RunBg(() =>
            {
                entries = Scanner.GetUninstallEntries();
                found = Scanner.Identify(path, entries, out error);
            }, err =>
            {
                SetBusy(false, "Ready");
                if (err != null) error = err.Message;
                if (error != null)
                {
                    MessageBox.Show(this, error, "Untrail", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                RunScan(found, false);
            });
        }

        void RunScan(AppInfo target, bool refreshEntries, UninstallEntry fromEntry = null)
        {
            if (busy) return;
            cancelRequested = false;
            bool deep = chkDeep.Checked;
            SetBusy(true, "Searching for everything that belongs to the app…");
            btnCancel.Visible = true;
            btnDelete.Visible = false;
            List<FoundItem> found = null;
            RunBg(() =>
            {
                if (refreshEntries || entries.Count == 0) entries = Scanner.GetUninstallEntries();
                if (fromEntry != null) target = Scanner.FromEntry(fromEntry, entries);
                found = Scanner.Scan(target, entries, deep, Progress, () => cancelRequested);
            }, err =>
            {
                btnCancel.Visible = false;
                btnDelete.Visible = true;
                if (err != null)
                {
                    SetBusy(false, "Scan failed");
                    MessageBox.Show(this, "Scan failed: " + err.Message, "Untrail", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                ShowApp(target, found);
                SetBusy(false, (cancelRequested ? "Scan stopped early. " : "Scan finished. ") + found.Count + " item(s) found.");
            });
        }

        void ShowApp(AppInfo a, List<FoundItem> found)
        {
            app = a;
            items = found;
            drop.AppImage = ShellIcon.Get(a.ExePath ?? a.ShortcutPath ?? a.InstallDir ?? Util.SelfExe);
            drop.Title = a.Name;
            if (!Util.IsAdmin) drop.Sub = "Drop another app here to switch";
            drop.Invalidate();
            lblName.Text = a.Name;
            string pub = a.Publisher.Length > 0 ? a.Publisher : "Unknown publisher";
            lblPublisher.Text = pub + (a.Version.Length > 0 ? "  ·  version " + a.Version : "");
            lblInstall.Text = "Installed in:  " + (a.InstallDir != null ? Util.Show(a.InstallDir) : "(no program folder found)");
            if (a.HasUninstaller)
            {
                lblUninst.Text = "✓  Official uninstaller found — it runs first, then Untrail removes what it leaves behind.";
                lblUninst.ForeColor = Theme.Safe;
            }
            else
            {
                lblUninst.Text = "⚠  No official uninstaller — Untrail will remove the files directly.";
                lblUninst.ForeColor = Theme.Review;
            }
            if (a.Notes.Count > 0) lblUninst.Text += "   " + string.Join(" ", a.Notes);
            chkUninstaller.Checked = a.HasUninstaller;
            PopulateList();
        }

        void PopulateList()
        {
            populating = true;
            list.BeginUpdate();
            list.Items.Clear();
            list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (string c in Cat.Order)
            {
                var g = new ListViewGroup(c, c);
                list.Groups.Add(g);
                groups[c] = g;
            }
            foreach (FoundItem it in items.OrderBy(i => Array.IndexOf(Cat.Order, i.Category)).ThenBy(i => i.Conf).ThenByDescending(i => i.Size))
            {
                var lvi = new ListViewItem(it.DisplayPath) { Tag = it, Checked = it.Conf == Confidence.Sure, UseItemStyleForSubItems = false };
                lvi.Group = groups.ContainsKey(it.Category) ? groups[it.Category] : null;
                lvi.SubItems.Add(it.Kind >= ItemKind.Registry ? "—" : Util.FormatSize(it.Size));
                lvi.SubItems.Add(StateLabel(it.Conf));
                lvi.SubItems.Add(it.Reason + (it.NeedsAdmin ? "  · needs admin" : "") + (it.IsLink ? "  · link only" : ""));
                lvi.SubItems[2].ForeColor = StateColor(it.Conf);
                lvi.SubItems[3].ForeColor = Theme.Stone;
                if (it.Conf == Confidence.Maybe) lvi.ForeColor = Theme.Stone;
                lvi.ToolTipText = it.DisplayPath;
                list.Items.Add(lvi);
            }
            foreach (var kv in groups)
            {
                var inGroup = items.Where(i => i.Category == kv.Key).ToList();
                if (inGroup.Count > 0)
                    kv.Value.Header = kv.Key + "   —   " + inGroup.Count + " item" + (inGroup.Count == 1 ? "" : "s") +
                                      (inGroup.Any(i => i.Size > 0) ? ", " + Util.FormatSize(inGroup.Sum(i => i.Size)) : "");
            }
            list.EndUpdate();
            populating = false;
            FitColumns();
            UpdateTotal();
        }

        List<FoundItem> CheckedItems()
        {
            return list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (FoundItem)i.Tag).ToList();
        }

        void SetChecks(Func<FoundItem, bool> rule)
        {
            populating = true;
            list.BeginUpdate();
            foreach (ListViewItem i in list.Items) i.Checked = rule((FoundItem)i.Tag);
            list.EndUpdate();
            populating = false;
            UpdateTotal();
        }

        void UpdateTotal()
        {
            if (lblSize == null || list == null) return;
            List<FoundItem> sel = CheckedItems();
            long size = sel.Sum(i => i.Size), all = items.Sum(i => i.Size);
            if (app == null)
            {
                lblSize.Text = "0 GB";
                lblSizeSub.Text = "to be freed  ·  drop an app above to start";
                lblHint.Text = "";
            }
            else if (items.Count == 0)
            {
                lblSize.Text = "All clean";
                lblSizeSub.Text = "no leftovers found in the places Untrail looks";
                lblHint.Text = "";
            }
            else
            {
                lblSize.Text = Util.FormatSize(size);
                lblSizeSub.Text = "selected to free  ·  " + sel.Count + " of " + items.Count + " items  ·  " + Util.FormatSize(all) + " found";
                lblHint.Text = "“Safe to remove” items are ticked. Tick “Review” or “Keep” items only if you recognise them.";
            }
            bar.Value = all > 0 ? (float)size / all : 0;
            btnDelete.Enabled = !busy && sel.Count > 0;
            bool any = app != null && items.Count > 0 && !busy;
            lnkAll.Enabled = lnkSure.Enabled = lnkNone.Enabled = any;
        }

        void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var it = (FoundItem)list.SelectedItems[0].Tag;
            try
            {
                if (it.Kind == ItemKind.Folder || it.Kind == ItemKind.File)
                {
                    if (File.Exists(it.Path) || Directory.Exists(it.Path)) Process.Start("explorer.exe", "/select,\"" + it.Path + "\"");
                    return;
                }
                string key = it.Kind == ItemKind.RegistryValue ? it.Path.Substring(0, it.Path.LastIndexOf('|')) : it.Path;
                key = (key.StartsWith("HKLM") ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + key.Substring(4);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    k.SetValue("LastKey", "Computer\\" + key);
                Process.Start("regedit.exe", "/m");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Untrail"); }
        }

        // ---------------------------------------------------------------- removal

        void StartRemoval()
        {
            if (busy || app == null) return;
            List<FoundItem> selected = CheckedItems();
            if (selected.Count == 0) return;
            bool recycle = !chkPermanent.Checked;
            bool runUninstaller = chkUninstaller.Checked && chkUninstaller.Enabled && app.HasUninstaller;
            long size = selected.Sum(i => i.Size);

            var msg = new StringBuilder();
            msg.AppendLine("Remove " + app.Name + "?");
            msg.AppendLine();
            if (runUninstaller) msg.AppendLine("1.  The app's own uninstaller runs first. If it's cancelled or fails, Untrail stops.");
            msg.AppendLine((runUninstaller ? "2.  " : "") + selected.Count + " item(s), " + Util.FormatSize(size) + ", will be " +
                           (recycle ? "moved to the Recycle Bin (space is freed when you empty it)." : "PERMANENTLY deleted."));
            int risky = selected.Count(i => i.Conf != Confidence.Sure);
            if (risky > 0) { msg.AppendLine(); msg.AppendLine("⚠  " + risky + " of them are marked Review/Keep — make sure you recognise them."); }
            msg.AppendLine();
            msg.AppendLine("Registry entries are backed up first (Documents\\Untrail Backups).");
            if (MessageBox.Show(this, msg.ToString(), "Confirm removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            List<Process> running = Scanner.FindRunning(app);
            if (running.Count > 0)
            {
                string names = string.Join(", ", running.Select(p => { try { return p.ProcessName; } catch { return "?"; } }).Distinct());
                DialogResult ans = MessageBox.Show(this, "These are still running:\n\n" + names +
                    "\n\nClose them now? Anything unsaved in them will be lost.\n\nYes = close them   No = carry on anyway   Cancel = stop",
                    "App is running", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ans == DialogResult.Cancel) return;
                if (ans == DialogResult.Yes)
                    foreach (Process p in running) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            }

            var session = new RemovalSession(app, items.ToList(), selected,
                new DeleteOptions { Recycle = recycle, BackupDir = Deleter.NewBackupDir(app.Name) });
            SetBusy(true, "Measuring before anything changes…");
            RunBg(() => session.Measure(), err =>
            {
                if (runUninstaller) RunUninstaller(session);
                else DoDelete(session, null);
            });
        }

        void RunUninstaller(RemovalSession session)
        {
            SetBusy(true, "Running " + app.Name + "'s uninstaller — follow its window…");
            UninstallRunner run;
            try { run = UninstallRunner.Start(app); }
            catch (Exception err)
            {
                string why = Deleter.IsUacCancelled(err) ? "the admin prompt was declined" : err.Message;
                if (MessageBox.Show(this, "The app's uninstaller couldn't run: " + why + ".\n\n" +
                        "Remove the selected files anyway? Only do this if you know the uninstaller is broken.",
                        "Untrail", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    SetBusy(false, "Stopped. Untrail didn't delete anything.");
                    return;
                }
                session.ManualCleanup = true;
                DoDelete(session, null);
                return;
            }
            using (var wait = new UninstallWaitForm(run, scale))
            {
                wait.ShowDialog(this);
                session.Uninstall = wait.LastStatus ?? run.Status();
                if (wait.Result == UninstallWaitForm.Choice.Stop)
                {
                    SetBusy(false, "Stopped. Untrail didn't delete anything.");
                    return;
                }
                session.ManualCleanup = wait.Result == UninstallWaitForm.Choice.CleanAnyway;
            }
            DoDelete(session, null);
        }

        void DoDelete(RemovalSession session, List<FoundItem> only)
        {
            SetBusy(true, "Removing leftovers…");
            List<FoundItem> toDelete = null;
            RunBg(() =>
            {
                // What the uninstaller removed is measured first, so nothing is counted twice.
                Progress("Checking what the uninstaller left…");
                toDelete = only ?? session.AfterUninstaller();
                int total = toDelete.Count;
                Action<int, string> step = (done, text) =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            progress.Style = ProgressBarStyle.Continuous;
                            progress.Maximum = Math.Max(1, total);
                            progress.Value = Math.Min(done, progress.Maximum);
                            if (text != null) status.Text = "(" + Math.Min(done + 1, total) + "/" + total + ")  " + text;
                        }));
                    }
                    catch { }
                };
                List<DeleteResult> results;
                if (toDelete.Count == 0) results = new List<DeleteResult>();
                else if (toDelete.Any(i => i.NeedsAdmin) && !Util.IsAdmin)
                {
                    Progress("Waiting for admin permission…");
                    results = Deleter.RunElevated(toDelete, session.Options, session.Scope, step);
                }
                else results = Deleter.Run(toDelete, session.Options, session.Scope, step);
                session.Record(results);
                Progress("Scanning again to check what's left…");
                session.Verify(Progress);
            }, err =>
            {
                if (err != null && Deleter.IsUacCancelled(err) && toDelete != null)
                {
                    var userOnly = toDelete.Where(i => !i.NeedsAdmin).ToList();
                    if (userOnly.Count > 0 && MessageBox.Show(this,
                            "Admin permission is needed for items in Program Files, ProgramData and the machine-wide registry.\n\nRemove just the " +
                            userOnly.Count + " item(s) that don't need it?", "Untrail", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        DoDelete(session, userOnly);
                        return;
                    }
                    SetBusy(false, "Nothing was deleted.");
                    return;
                }
                if (err != null)
                {
                    SetBusy(false, "Removal failed.");
                    MessageBox.Show(this, "Something went wrong: " + err.Message, "Untrail", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                ShowResults(session);
            });
        }

        void ShowResults(RemovalSession session)
        {
            // The list now shows what the fresh scan found, not what we expected to be gone.
            items = session.Remaining;
            PopulateList();
            bool entryLeft = app.Entry != null && Reg.Exists(app.Entry.KeyPath);
            chkUninstaller.Checked = entryLeft;
            SetBusy(false, session.Headline());
            chkUninstaller.Enabled = entryLeft;
            using (var f = new ResultsForm(session, scale)) f.ShowDialog(this);

            if (items.Count == 0)
            {
                string name = app.Name;
                ShowEmpty();
                lblName.Text = "✓  " + name + " removed";
                lblPublisher.Text = session.Headline();
                lblInstall.Text = "Drop another app to clean it up too.";
            }
            else
            {
                lblUninst.Text = session.Headline() + " Review the list, then run Uninstall & Clean again if you want.";
                lblUninst.ForeColor = session.Clean ? Theme.Safe : Theme.Review;
            }
        }
    }

    class AppPicker : Form
    {
        public UninstallEntry Selected;
        readonly List<UninstallEntry> all;
        readonly TextBox search;
        readonly ListView lv;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public AppPicker(List<UninstallEntry> entries, float scale)
        {
            Func<int, int> S = v => (int)Math.Round(v * scale);
            all = entries.Where(e => !e.Hidden)
                         .GroupBy(e => (e.DisplayName + "|" + e.Publisher).ToLowerInvariant()).Select(g => g.First())
                         .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            Text = "Pick an installed app";
            Font = Theme.Ui(9.5f);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(760), S(560));
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = Theme.Surface;

            var top = new Panel { Dock = DockStyle.Top, Height = S(48), Padding = new Padding(S(14), S(12), S(14), S(4)) };
            search = new TextBox { Dock = DockStyle.Fill };
            top.Controls.Add(search);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(60), Padding = new Padding(S(14), S(10), S(14), S(12)) };
            var ok = new BrandButton { Text = "Scan this app", Glyph = Theme.GlyphSearch, Primary = true, Dock = DockStyle.Right, Width = S(170), Font = Theme.Bold(10f) };
            var cancel = new BrandButton { Text = "Cancel", Dock = DockStyle.Right, Width = S(100), DialogResult = DialogResult.Cancel };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(10) });
            bottom.Controls.Add(cancel);
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            lv.Columns.Add("App", S(330));
            lv.Columns.Add("Publisher", S(200));
            lv.Columns.Add("Size", S(80), HorizontalAlignment.Right);
            lv.Columns.Add("Version", S(110));
            var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(14), S(4), S(14), 0) };
            mid.Controls.Add(lv);
            Controls.Add(mid);
            Controls.Add(bottom);
            Controls.Add(top);
            CancelButton = cancel;
            AcceptButton = ok;

            search.TextChanged += (s, e) => Fill();
            ok.Click += (s, e) => Choose();
            lv.DoubleClick += (s, e) => Choose();
            Shown += (s, e) => { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Type to filter…"); search.Focus(); };
            Fill();
        }

        void Fill()
        {
            string q = search.Text.Trim();
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (UninstallEntry e in all)
            {
                if (q.Length > 0 && e.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    e.Publisher.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var i = new ListViewItem(e.DisplayName) { Tag = e };
                i.SubItems.Add(e.Publisher);
                i.SubItems.Add(e.EstimatedSizeKB > 0 ? Util.FormatSize(e.EstimatedSizeKB * 1024) : "");
                i.SubItems.Add(e.DisplayVersion);
                lv.Items.Add(i);
            }
            lv.EndUpdate();
            if (lv.Items.Count > 0) lv.Items[0].Selected = true;
        }

        void Choose()
        {
            if (lv.SelectedItems.Count == 0) return;
            Selected = (UninstallEntry)lv.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
        }
    }
}
