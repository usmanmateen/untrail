using System;
using System.Drawing;
using System.Windows.Forms;

namespace Untrail
{
    /// <summary>Shown while an app's own uninstaller runs. Closes by itself once the uninstaller is done and the app has gone.</summary>
    class UninstallWaitForm : Form
    {
        public enum Outcome { Finished, CleanAnyway, Stop }
        public Outcome Result = Outcome.Stop;

        readonly UninstallRunner run;
        readonly Label body;
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
            ClientSize = new Size(S(540), S(260));
            Icon = Theme.AppIcon();

            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(26), S(22), S(26), S(18)) };
            var title = new Label { Dock = DockStyle.Top, Height = S(34), Font = Theme.Bold(13f), Text = "Waiting for " + name + "'s uninstaller", AutoEllipsis = true };
            body = new Label
            {
                Dock = DockStyle.Top, Height = S(88), ForeColor = Theme.Slate,
                Text = "Follow the uninstaller's window. Untrail carries on by itself once it has finished and the app is gone.",
            };
            var bar = new ProgressBar { Dock = DockStyle.Top, Height = S(6), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(42) };
            var stop = new BrandButton { Text = "Stop", Glyph = Theme.GlyphStop, Dock = DockStyle.Right, Width = S(110) };
            var clean = new BrandButton { Text = "Clean up now", Glyph = Theme.GlyphTrash, Dock = DockStyle.Right, Width = S(160) };
            buttons.Controls.Add(clean);
            buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(10) });
            buttons.Controls.Add(stop);
            content.Controls.Add(buttons);
            content.Controls.Add(bar);
            content.Controls.Add(body);
            content.Controls.Add(title);
            Controls.Add(content);

            stop.Click += (s, e) => { Result = Outcome.Stop; Close(); };
            clean.Click += (s, e) =>
            {
                if (MessageBox.Show(this, name + " may still be installed. Remove the selected files anyway?", "Untrail",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                Result = Outcome.CleanAnyway;
                Close();
            };
            timer.Tick += (s, e) => Poll();
            Shown += (s, e) => timer.Start();
            FormClosed += (s, e) => timer.Dispose();
        }

        void Poll()
        {
            bool running = run.Running;
            if (!running && UninstallRunner.AppGone(run.App))
            {
                timer.Stop();
                Result = Outcome.Finished;
                Close();
                return;
            }
            if (!running)
            {
                string code = run.ExitCode.HasValue && run.ExitCode.Value != 0 ? " (it ended with error code " + run.ExitCode.Value + ")" : "";
                body.Text = "The uninstaller has closed" + code + ", but " + run.App.Name + " still looks installed.\n\n" +
                            "If you cancelled it, click Stop. If it's still working in another window (Steam, for example), " +
                            "Untrail will carry on as soon as the app is gone.";
            }
        }
    }
}
