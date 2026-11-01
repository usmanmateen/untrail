using System;
using System.Drawing;
using System.Windows.Forms;

namespace Untrail
{
    /// <summary>
    /// Shown while an app's own uninstaller runs. Closes by itself only when the uninstall has really completed
    /// (process tree finished, Installed-apps entry and program gone). A cancelled, failed or partial uninstall
    /// waits for the user: Stop, or an explicit "Clean up anyway" with a warning.
    /// </summary>
    class UninstallWaitForm : Form
    {
        public enum Choice { Completed, CleanAnyway, Stop }
        public Choice Result = Choice.Stop;
        public UninstallStatus LastStatus;

        readonly UninstallRunner run;
        readonly Label body;
        readonly ProgressBar bar;
        readonly Timer timer = new Timer { Interval = 700 };

        public UninstallWaitForm(UninstallRunner run, float scale)
        {
            this.run = run;
            Func<int, int> S = v => (int)Math.Round(v * scale);
            string name = run.App.Name;
            Text = "Uninstalling " + name;
            Font = Theme.Ui(9.75f);
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(S(560), S(270));
            Icon = Theme.AppIcon();

            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(26), S(22), S(26), S(18)) };
            var title = new Label { Dock = DockStyle.Top, Height = S(34), Font = Theme.Bold(13f), Text = "Waiting for " + name + "'s uninstaller", AutoEllipsis = true, UseMnemonic = false };
            body = new Label
            {
                Dock = DockStyle.Top, Height = S(96), ForeColor = Theme.Slate, UseMnemonic = false,
                Text = "Follow the uninstaller's window. Untrail carries on by itself once the uninstall has completed.",
            };
            bar = new ProgressBar { Dock = DockStyle.Top, Height = S(6), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(42) };
            var stop = new BrandButton { Text = "Stop", Glyph = Theme.GlyphStop, Dock = DockStyle.Right, Width = S(110) };
            var clean = new BrandButton { Text = "Clean up anyway…", Glyph = Theme.GlyphTrash, Dock = DockStyle.Right, Width = S(180) };
            buttons.Controls.Add(clean);
            buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(10) });
            buttons.Controls.Add(stop);
            content.Controls.Add(buttons);
            content.Controls.Add(bar);
            content.Controls.Add(body);
            content.Controls.Add(title);
            Controls.Add(content);

            stop.Click += (s, e) => { Result = Choice.Stop; Close(); };
            clean.Click += (s, e) =>
            {
                UninstallStatus st = run.Status();
                string why = st.State == UninstallState.Running ? "The uninstaller is still running."
                           : st.State == UninstallState.Completed ? "" : st.Describe(name);
                if (MessageBox.Show(this, (why.Length > 0 ? why + "\n\n" : "") +
                        "Cleaning up now removes the selected files even though " + name + " may not have been uninstalled properly. " +
                        "Only do this if you know its uninstaller is broken or was cancelled on purpose.\n\nClean up anyway?",
                        "Untrail", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                LastStatus = st;
                Result = Choice.CleanAnyway;
                Close();
            };
            timer.Tick += (s, e) => Poll();
            Shown += (s, e) => timer.Start();
            FormClosed += (s, e) => timer.Dispose();
        }

        void Poll()
        {
            UninstallStatus st = run.Status();
            LastStatus = st;
            if (st.State == UninstallState.Completed)
            {
                timer.Stop();
                Result = Choice.Completed;
                Close();
                return;
            }
            if (st.State == UninstallState.Running) return;
            body.Text = st.Describe(run.App.Name) + (st.HandsOff
                ? "\n\nUntrail carries on as soon as it's gone. Click Stop if you cancelled it."
                : "\n\nNothing has been deleted. Click Stop to leave everything as it is.");
            if (!st.HandsOff && bar.Style == ProgressBarStyle.Marquee) { bar.Style = ProgressBarStyle.Continuous; bar.Value = 0; }
        }
    }
}
