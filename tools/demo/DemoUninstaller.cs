// The demo app's own uninstaller: a small window that removes the demo app's program files and its
// Installed-apps entry, then closes, like a typical setup program would. Leftovers are left for Untrail.
//
//   "Uninstall Northwind Notes.exe" <installDir> <uninstallKey> <centerX> <centerY>
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

class DemoUninstaller : Form
{
    readonly string install, key;
    readonly ProgressBar bar = new ProgressBar { Left = 24, Top = 64, Width = 392, Height = 18 };
    readonly Label status = new Label { Left = 24, Top = 92, Width = 392, Height = 20, ForeColor = Color.DimGray };
    readonly Timer timer = new Timer { Interval = 60 };
    int step;

    DemoUninstaller(string install, string key, Point center)
    {
        this.install = install; this.key = key;
        Text = "Northwind Notes Uninstall";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(440, 130);
        Location = new Point(center.X - Width / 2, center.Y - Height / 2);
        TopMost = true;
        Font = new Font("Segoe UI", 9.5f);
        Controls.Add(new Label { Left = 24, Top = 22, Width = 392, Height = 30, Text = "Uninstalling Northwind Notes…", Font = new Font("Segoe UI Semibold", 12f) });
        Controls.Add(bar);
        Controls.Add(status);
        timer.Tick += (s, e) => Tick();
        Shown += (s, e) => timer.Start();
    }

    void Tick()
    {
        step++;
        bar.Value = Math.Min(100, step * 2);
        if (step == 10) { status.Text = "Removing program files…"; Remove("Northwind Notes.exe"); Remove("resources.pak"); Remove(@"resources\app.asar"); }
        if (step == 35) { status.Text = "Removing shortcuts and registry entries…"; Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        if (step == 50) { File.WriteAllText(Path.Combine(install, "uninstall.log"), "Northwind Notes was uninstalled."); status.Text = "Done."; }
        if (step == 56) { timer.Stop(); Close(); }
    }

    void Remove(string rel) { try { File.Delete(Path.Combine(install, rel)); } catch { } }

    [STAThread]
    static void Main(string[] a)
    {
        Application.EnableVisualStyles();
        Application.Run(new DemoUninstaller(a[0], a[1], new Point(int.Parse(a[2]), int.Parse(a[3]))));
    }
}
