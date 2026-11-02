using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Untrail
{
    /// <summary>
    /// UntrailSetup.exe: installs the embedded Untrail.exe for the current user (no admin needed).
    ///   UntrailSetup.exe            normal install window
    ///   UntrailSetup.exe /S         silent install (add /desktop for a desktop shortcut, /launch to open Untrail after;
    ///                               Untrail's own "Update now" uses both)
    /// </summary>
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a == "--silent");
            if (silent)
            {
                try { Install(args.Any(a => a.Equals("/desktop", StringComparison.OrdinalIgnoreCase))); }
                catch { return 1; }
                if (args.Any(a => a.Equals("/launch", StringComparison.OrdinalIgnoreCase)))
                    try { Process.Start(new ProcessStartInfo(InstallInfo.Exe) { UseShellExecute = true, WorkingDirectory = InstallInfo.Dir }); } catch { }
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        public static void Install(bool desktopShortcut)
        {
            InstallInfo.CloseRunning();
            Directory.CreateDirectory(InstallInfo.Dir);
            string tmp = InstallInfo.Exe + ".new";
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Untrail.payload.exe"))
            using (FileStream f = File.Create(tmp))
                s.CopyTo(f);
            if (File.Exists(InstallInfo.Exe)) File.Delete(InstallInfo.Exe);
            File.Move(tmp, InstallInfo.Exe);

            InstallInfo.CreateShortcut(InstallInfo.StartMenuLink, InstallInfo.Exe);
            if (desktopShortcut) InstallInfo.CreateShortcut(InstallInfo.DesktopLink, InstallInfo.Exe);
            InstallInfo.Register(BuildInfo.Version);
        }
    }

    class SetupForm : Form
    {
        readonly float scale;
        readonly Label title, body, path;
        readonly CheckBox chkDesktop;
        readonly BrandButton primary, secondary;
        bool done;

        int S(int v) { return (int)Math.Round(v * scale); }

        public SetupForm()
        {
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            bool upgrade = InstallInfo.IsInstalled;
            Text = "Untrail Setup";
            Font = Theme.Ui(9.75f);
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(S(560), S(436));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.AppIcon();

            var bar = new Panel { Dock = DockStyle.Top, Height = S(96) };
            bar.Paint += (s, e) => Theme.PaintBrandBar(e.Graphics, bar.ClientRectangle, scale, false);

            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(30), S(24), S(30), S(20)) };
            title = new Label { Dock = DockStyle.Top, Height = S(40), Font = Theme.Bold(16f), Text = (upgrade ? "Update Untrail to " : "Install Untrail ") + BuildInfo.Version };
            body = new Label
            {
                Dock = DockStyle.Top, Height = S(66), ForeColor = Theme.Slate,
                Text = "Untrail removes apps completely — their program folder plus the leftover files, caches and registry entries normal uninstallers leave behind.",
            };
            path = new Label { Dock = DockStyle.Top, Height = S(42), ForeColor = Theme.Stone, Text = "Installs for your account only (no admin needed) to:\n" + InstallInfo.Dir };
            chkDesktop = new CheckBox { Dock = DockStyle.Top, Height = S(32), Text = "Add a desktop shortcut", Checked = !upgrade || File.Exists(InstallInfo.DesktopLink) };
            var privacy = new LinkLabel
            {
                Dock = DockStyle.Top, Height = S(44), ForeColor = Theme.Stone, LinkColor = Theme.Orange, ActiveLinkColor = Theme.OrangeLight,
                Text = "Untrail checks GitHub for new versions once a day and sends nothing about you. You can turn this off in Settings. Privacy policy",
            };
            privacy.LinkArea = new LinkArea(privacy.Text.Length - "Privacy policy".Length, "Privacy policy".Length);
            privacy.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(InstallInfo.PrivacyUrl) { UseShellExecute = true }); } catch { }
            };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(46) };
            primary = new BrandButton { Primary = true, Arrow = true, Glyph = Theme.GlyphCheck, Dock = DockStyle.Right, Width = S(200), Font = Theme.Bold(11f), Text = upgrade ? "Update" : "Install" };
            secondary = new BrandButton { Dock = DockStyle.Right, Width = S(110), Text = "Cancel" };
            buttons.Controls.Add(secondary);
            buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(10) });
            buttons.Controls.Add(primary);

            content.Controls.Add(buttons);
            content.Controls.Add(chkDesktop);
            content.Controls.Add(privacy);
            content.Controls.Add(path);
            content.Controls.Add(body);
            content.Controls.Add(title);
            Controls.Add(content);
            Controls.Add(bar);
            AcceptButton = primary;

            primary.Click += (s, e) => { if (done) Launch(); else DoInstall(); };
            secondary.Click += (s, e) => Close();
        }

        void DoInstall()
        {
            if (InstallInfo.IsRunning() &&
                MessageBox.Show(this, "Untrail is open. Close it and continue?", "Untrail Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            Cursor = Cursors.WaitCursor;
            try
            {
                SetupProgram.Install(chkDesktop.Checked);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Setup couldn't finish: " + ex.Message, "Untrail Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            done = true;
            title.Text = "Untrail is ready";
            body.Text = "Find it in the Start menu" + (chkDesktop.Checked ? " or on your desktop" : "") +
                        ". To remove it later, use Settings → Apps → Installed apps → Untrail.";
            path.Text = "Installed to:\n" + InstallInfo.Dir;
            chkDesktop.Visible = false;
            primary.Text = "Open Untrail";
            primary.Glyph = Theme.GlyphOpen;
            secondary.Text = "Close";
            primary.Invalidate();
            secondary.Invalidate();
        }

        void Launch()
        {
            try { Process.Start(new ProcessStartInfo(InstallInfo.Exe) { UseShellExecute = true, WorkingDirectory = InstallInfo.Dir }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Untrail Setup"); }
            Close();
        }
    }
}
