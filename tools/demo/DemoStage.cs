// DemoStage.exe: the left strip of the demo recording (it stands in for the desktop and holds the demo app's
// shortcut, which you can really drag into Untrail) and the screen recorder for the whole demo area.
//
//   DemoStage.exe <x> <y> <width> <height> <stripWidth> <shortcut.lnk> <framesDir> <fps>
//
// Recording starts when <framesDir>\start exists and stops when <framesDir>\stop exists.
// Frames are PNGs of the screen area (with the mouse cursor drawn in); times.txt lists each frame's time in ms.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class DemoStage : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int w, int hh, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    readonly Rectangle area;
    readonly string lnk, frames;
    readonly int fps, strip;

    // Never activated by clicks, so pressing on the shortcut can't raise this backdrop over Untrail.
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    readonly Image icon;
    readonly string label;
    Point down;
    bool pressed;

    DemoStage(Rectangle area, int strip, string lnk, string frames, int fps)
    {
        this.area = area; this.lnk = lnk; this.frames = frames; this.fps = fps; this.strip = strip;
        label = Path.GetFileNameWithoutExtension(lnk);
        icon = ShellIcon(lnk);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;   // other windows on the desktop must not cover the recorded area
        StartPosition = FormStartPosition.Manual;
        Bounds = area;   // the whole recorded area: Untrail sits on top of the right-hand part
        BackColor = Color.FromArgb(11, 15, 23);
        DoubleBuffered = true;
        Text = "Untrail demo stage";
    }

    Rectangle IconBox { get { return new Rectangle((strip - 120) / 2, 150, 120, 120); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(11, 15, 23), Color.FromArgb(27, 34, 48), 90f)) g.FillRectangle(b, ClientRectangle);
        using (var f = new Font("Segoe UI", 9f))
        using (var grey = new SolidBrush(Color.FromArgb(150, 160, 175)))
            g.DrawString("Desktop", f, grey, 22, 22);
        Rectangle box = IconBox;
        if (pressed) using (var hl = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(hl, box);
        if (icon != null) g.DrawImage(icon, box.X + 28, box.Y + 10, 64, 64);
        using (var f = new Font("Segoe UI", 9.5f))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            g.DrawString(label, f, Brushes.White, new RectangleF(box.X - 10, box.Y + 80, box.Width + 20, 40), sf);
    }

    void Log(string s) { try { File.AppendAllText(Path.Combine(frames, "stage.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Log("down " + e.Location + " " + e.Button);
        if (IconBox.Contains(e.Location)) { pressed = true; down = e.Location; Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!pressed) return;
        Log("move " + e.Location + " " + e.Button);
        if (e.Button != MouseButtons.Left) return;
        if (Math.Abs(e.X - down.X) + Math.Abs(e.Y - down.Y) < 6) return;
        pressed = false;
        Invalidate();
        // A real OLE drag of the real shortcut file, exactly like dragging it from Explorer.
        var data = new DataObject(DataFormats.FileDrop, new[] { lnk });
        Log("drag start");
        DragDropEffects result = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Link);
        Log("drag end: " + result);
    }

    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var t = new Thread(Record) { IsBackground = true };
        t.Start();
    }

    void Record()
    {
        while (!File.Exists(Path.Combine(frames, "start"))) Thread.Sleep(30);
        var clock = Stopwatch.StartNew();
        int n = 0;
        long interval = 1000 / fps;
        // BMP: no compression, so saving keeps up with the frame rate. Times are written as we go.
        using (var times = new StreamWriter(Path.Combine(frames, "times.txt")) { AutoFlush = true })
        using (var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            while (!File.Exists(Path.Combine(frames, "stop")))
            {
                long due = n * interval;
                long wait = due - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                long at = clock.ElapsedMilliseconds;
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                DrawCursor(g);
                bmp.Save(Path.Combine(frames, (n + 1).ToString("D5") + ".bmp"), ImageFormat.Bmp);
                times.WriteLine(at);
                n++;
            }
        }
        BeginInvoke(new Action(Close));
    }

    void DrawCursor(Graphics g)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci) || ci.flags != 1) return;
        ICONINFO ii;
        if (!GetIconInfo(ci.hCursor, out ii)) return;
        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.pt.X - area.X - ii.xHotspot, ci.pt.Y - area.Y - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
        finally { g.ReleaseHdc(hdc); }
    }

    static Image ShellIcon(string path)
    {
        try { using (Icon ic = Icon.ExtractAssociatedIcon(path)) return new Bitmap(ic.ToBitmap(), 64, 64); }
        catch { return null; }
    }

    [STAThread]
    static void Main(string[] a)
    {
        Application.EnableVisualStyles();
        var area = new Rectangle(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]));
        Application.Run(new DemoStage(area, int.Parse(a[4]), a[5], a[6], int.Parse(a[7])));
    }
}
