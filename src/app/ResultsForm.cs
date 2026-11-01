using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Untrail
{
    /// <summary>What a removal did: the verified outcome, storage per drive, and anything that needs attention.</summary>
    class ResultsForm : Form
    {
        public ResultsForm(RemovalSession s, float scale)
        {
            Func<int, int> S = v => (int)Math.Round(v * scale);
            Text = "Untrail — " + s.App.Name;
            Font = Theme.Ui(9.5f);
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(S(760), S(560));
            Icon = Theme.AppIcon();

            bool clean = s.Clean;
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(26), S(20), S(26), S(16)) };
            var title = new Label
            {
                Dock = DockStyle.Top, Height = S(64), Font = Theme.Bold(14f), Text = (clean ? "✓  " : "⚠  ") + s.Headline(),
                ForeColor = clean ? Theme.Safe : Theme.Ink, UseMnemonic = false,
            };
            var uninst = new Label { Dock = DockStyle.Top, Height = S(42), ForeColor = Theme.Slate, Text = s.UninstallLine(), UseMnemonic = false };

            var drives = new ListView
            {
                Dock = DockStyle.Top, Height = S(30 + 24 * Math.Max(1, s.Drives.Count)), View = View.Details, FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, Scrollable = false, BorderStyle = BorderStyle.FixedSingle,
            };
            drives.Columns.Add("Drive", S(50));
            drives.Columns.Add("Uninstaller removed", S(132), HorizontalAlignment.Right);
            drives.Columns.Add("Untrail deleted", S(110), HorizontalAlignment.Right);
            drives.Columns.Add("Recycle Bin", S(92), HorizontalAlignment.Right);
            drives.Columns.Add("At restart", S(84), HorizontalAlignment.Right);
            drives.Columns.Add("Not removed", S(96), HorizontalAlignment.Right);
            drives.Columns.Add("Free space change", S(138), HorizontalAlignment.Right);
            foreach (DriveReport d in s.Drives.Values.OrderBy(x => x.Drive))
            {
                var row = new ListViewItem(d.Drive);
                foreach (long v in new[] { d.ByUninstaller, d.Deleted, d.Recycled, d.Pending, d.Left }) row.SubItems.Add(v > 0 ? Util.FormatSize(v) : "—");
                long? fc = d.FreeChange;
                row.SubItems.Add(fc.HasValue ? (fc.Value >= 0 ? "+" : "−") + Util.FormatSize(Math.Abs(fc.Value)) : "unknown");
                drives.Items.Add(row);
            }
            var note = new Label
            {
                Dock = DockStyle.Top, Height = S(54), ForeColor = Theme.Stone, Font = Theme.Ui(8.75f),
                Text = "Sizes are file sizes. “Free space change” is measured on the drive, so other programs can affect it. " +
                       "Files in the Recycle Bin still use space until you empty it.",
            };

            var details = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.White,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.Ui(9f),
            };
            var lines = new System.Collections.Generic.List<string>();
            foreach (DeleteResult r in s.Results.Where(x => x.Outcome == Outcome.PendingRestart))
                lines.Add("At next restart:  " + r.Item.DisplayPath + "  (" + r.Error + ")");
            foreach (DeleteResult r in s.Results.Where(x => x.Outcome == Outcome.Failed))
                lines.Add("Couldn't remove:  " + r.Item.DisplayPath + "  (" + r.Error + ")");
            foreach (FoundItem it in s.Remaining.Where(i => i.Conf == Confidence.Sure))
                if (!s.Results.Any(r => r.Outcome == Outcome.Failed && Util.SamePath(r.Item.Path, it.Path))) lines.Add("Still there:  " + it.DisplayPath);
            foreach (FoundItem it in s.Remaining.Where(i => i.Conf != Confidence.Sure))
                lines.Add("Needs your review:  " + it.DisplayPath + "  (" + it.Reason + ")");
            lines.Add("");
            lines.Add("Removed: " + (s.Count(Outcome.Deleted) + s.Count(Outcome.Recycled)) + " item(s)" +
                      (s.Count(Outcome.AlreadyGone) > 0 ? ", " + s.Count(Outcome.AlreadyGone) + " were already gone after the uninstaller" : "") + ".");
            lines.Add("Registry backups and a log: " + Util.Show(s.Options.BackupDir));
            details.Text = string.Join(Environment.NewLine, lines);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(52), Padding = new Padding(0, S(12), 0, 0) };
            bool anyLeft = s.Remaining.Count > 0;
            var close = new BrandButton { Primary = true, Dock = DockStyle.Right, Width = S(190), Font = Theme.Bold(10.5f),
                                          Text = anyLeft ? "Review what's left" : "Done", Glyph = anyLeft ? Theme.GlyphSearch : Theme.GlyphCheck };
            var open = new BrandButton { Dock = DockStyle.Right, Width = S(170), Text = "Open backup folder", Glyph = Theme.GlyphFolder };
            buttons.Controls.Add(close);
            buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(10) });
            buttons.Controls.Add(open);
            close.Click += (o, e) => Close();
            open.Click += (o, e) => { try { if (Directory.Exists(s.Options.BackupDir)) Process.Start("explorer.exe", "\"" + s.Options.BackupDir + "\""); } catch { } };
            AcceptButton = close;
            // Focus the button once visible; otherwise the details box takes focus and shows all its text selected.
            Shown += (o, e) => { close.Focus(); details.SelectionStart = 0; details.SelectionLength = 0; };

            content.Controls.Add(details);
            content.Controls.Add(buttons);
            content.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(8) });
            content.Controls.Add(note);
            content.Controls.Add(drives);
            content.Controls.Add(uninst);
            content.Controls.Add(title);
            Controls.Add(content);
        }
    }
}
