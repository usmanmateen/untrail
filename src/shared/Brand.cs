using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace Untrail
{
    /// <summary>Untrail brand palette, fonts and artwork (see brand/brand-guide.webp).</summary>
    static class Theme
    {
        public static readonly Color Orange = Hex(0xFF4A00);
        public static readonly Color OrangeLight = Hex(0xFF6A2E);
        public static readonly Color OrangeEnd = Hex(0xFF8A4D);   // primary gradient end
        public static readonly Color OrangeSoft = Hex(0xFFF1EA);
        public static readonly Color OrangeLine = Hex(0xFFB899);
        public static readonly Color Ink = Hex(0x0B0F17);
        public static readonly Color Slate = Hex(0x1B2230);
        public static readonly Color Stone = Hex(0x6B7280);
        public static readonly Color Cloud = Hex(0xE5E7EB);
        public static readonly Color Surface = Hex(0xF8FAFC);
        public static readonly Color White = Color.White;

        public static readonly Color Safe = Hex(0x16A34A);
        public static readonly Color Review = Hex(0xD97706);
        public static readonly Color Keep = Hex(0xDC2626);

        static Color Hex(int v) { return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255); }

        static readonly HashSet<string> Installed =
            new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        // Brand type is Inter; fall back to Segoe UI when it isn't installed.
        static readonly string Regular = Installed.Contains("Inter") ? "Inter" : "Segoe UI";
        static readonly string Semibold = Installed.Contains("Inter SemiBold") ? "Inter SemiBold"
                                         : Installed.Contains("Inter") ? "Inter" : "Segoe UI Semibold";
        public static readonly string IconFont = Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public static Font Ui(float size) { return new Font(Regular, size); }
        public static Font Bold(float size) { return new Font(Semibold, size, Semibold == "Inter" ? FontStyle.Bold : FontStyle.Regular); }

        public const string GlyphTrash = "", GlyphSearch = "", GlyphFolder = "", GlyphApps = "",
                            GlyphArrow = "", GlyphStop = "", GlyphCheck = "", GlyphOpen = "";

        static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>();

        public static Image Art(string name)
        {
            Image img;
            if (!cache.TryGetValue(name, out img))
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Untrail." + name))
                    img = s == null ? null : new Bitmap(Image.FromStream(s));
                cache[name] = img;
            }
            return img;
        }

        public static Icon AppIcon()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Untrail.untrail.ico"))
                return s == null ? null : new Icon(s);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Deep Ink band with the white logo and the "Uninstall completely." tagline.</summary>
        public static void PaintBrandBar(Graphics g, Rectangle r, float scale, bool tagline)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Ink)) g.FillRectangle(b, r);
            // Dark gradient from the brand guide, kept to a soft glow on the right.
            var glow = new Rectangle(r.X + r.Width / 2, r.Y, r.Width - r.Width / 2, r.Height);
            // Brush rect is 2px wider than the fill: GDI+ wraps the end colour onto the first column otherwise.
            using (var b = new LinearGradientBrush(Rectangle.Inflate(glow, 2, 0), Color.FromArgb(0, Orange), Color.FromArgb(70, Orange), 0f))
                g.FillRectangle(b, glow);

            Image logo = Art("logo-dark.png");
            if (logo != null)
            {
                float h = r.Height * 0.5f;
                float w = logo.Width * h / logo.Height;
                g.DrawImage(logo, r.X + 22 * scale, r.Y + (r.Height - h) / 2, w, h);
            }
            if (!tagline) return;

            using (Font big = Bold(13f))
            using (Font small = Ui(9f))
            {
                string a = "Uninstall ", b2 = "completely.", sub = "Remove apps and their leftover files.";
                SizeF sa = g.MeasureString(a, big, PointF.Empty, StringFormat.GenericTypographic);
                SizeF sb = g.MeasureString(b2, big, PointF.Empty, StringFormat.GenericTypographic);
                SizeF ss = g.MeasureString(sub, small, PointF.Empty, StringFormat.GenericTypographic);
                float right = r.Right - 24 * scale;
                float top = r.Y + (r.Height - (sa.Height + ss.Height + 2 * scale)) / 2;
                float x = right - Math.Max(sa.Width + sb.Width, ss.Width);
                using (var white = new SolidBrush(White))
                using (var orange = new SolidBrush(OrangeLight))
                using (var grey = new SolidBrush(Color.FromArgb(170, 178, 192)))
                {
                    g.DrawString(a, big, white, x, top, StringFormat.GenericTypographic);
                    g.DrawString(b2, big, orange, x + sa.Width, top, StringFormat.GenericTypographic);
                    g.DrawString(sub, small, grey, x, top + sa.Height + 2 * scale, StringFormat.GenericTypographic);
                }
            }
        }
    }

    /// <summary>Rounded brand button: orange gradient (primary) or white outlined (secondary), with an icon and optional arrow.</summary>
    class BrandButton : Button
    {
        public string Glyph;
        public bool Primary;
        public bool Arrow;
        bool hover, down;

        public BrandButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        float Dpi { get { return DeviceDpi / 96f; } }

        Font IconFont() { return new Font(Theme.IconFont, Font.SizeInPoints * (Primary ? 1.1f : 1.0f)); }

        public override Size GetPreferredSize(Size proposed)
        {
            using (Graphics g = CreateGraphics())
            using (Font icon = IconFont())
            {
                float s = Dpi;
                float w = 18 * s * 2 + TextRenderer.MeasureText(g, Text, Font).Width;
                if (Glyph != null) w += TextRenderer.MeasureText(g, Glyph, icon).Width + 2 * s;
                if (Arrow) w += 30 * s;
                return new Size((int)w, Math.Max(Height, (int)(36 * s)));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Dpi;
            Color back = Parent != null ? Parent.BackColor : Theme.Surface;
            g.Clear(back);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fore;
            using (GraphicsPath p = Theme.Round(r, 9 * s))
            {
                if (Primary && Enabled)
                {
                    Color a = down ? Theme.Orange : Theme.Orange, b = down ? Theme.Orange : hover ? Theme.OrangeLight : Theme.OrangeEnd;
                    using (var br = new LinearGradientBrush(r, a, b, 0f)) g.FillPath(br, p);
                    fore = Color.White;
                }
                else if (Primary)
                {
                    using (var br = new SolidBrush(Theme.Cloud)) g.FillPath(br, p);
                    fore = Theme.Stone;
                }
                else
                {
                    using (var br = new SolidBrush(hover && Enabled ? Theme.OrangeSoft : Theme.White)) g.FillPath(br, p);
                    using (var pen = new Pen(hover && Enabled ? Theme.OrangeLine : Theme.Cloud, 1f)) g.DrawPath(pen, p);
                    fore = Enabled ? Theme.Ink : Theme.Stone;
                }
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Primary ? Theme.Ink : Theme.Orange, 1.5f) { DashStyle = DashStyle.Dot })
                    using (GraphicsPath inner = Theme.Round(RectangleF.Inflate(r, -3 * s, -3 * s), 7 * s))
                        g.DrawPath(pen, inner);
            }

            using (Font icon = IconFont())
            {
                Size ts = TextRenderer.MeasureText(g, Text, Font);
                Size gs = Glyph != null ? TextRenderer.MeasureText(g, Glyph, icon) : Size.Empty;
                const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                int x;
                if (Arrow) x = (int)(18 * s);
                else x = (Width - (ts.Width + (Glyph != null ? gs.Width + (int)(8 * s) : 0))) / 2;
                Color iconColor = Primary || !Enabled ? fore : Theme.Orange;
                if (Glyph != null)
                {
                    TextRenderer.DrawText(g, Glyph, icon, new Rectangle(x, 0, gs.Width, Height), iconColor, flags);
                    x += gs.Width + (int)(8 * s);
                }
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), fore, flags);
                if (Arrow)
                {
                    Size asz = TextRenderer.MeasureText(g, Theme.GlyphArrow, icon);
                    TextRenderer.DrawText(g, Theme.GlyphArrow, icon, new Rectangle(Width - asz.Width - (int)(18 * s), 0, asz.Width, Height), fore, flags);
                }
            }
        }
    }

    /// <summary>"12.4 GB to be freed" bar from the brand guide.</summary>
    class SizeBar : Control
    {
        float value;
        public float Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        public SizeBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.White);
            var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath p = Theme.Round(track, track.Height / 2))
            using (var b = new SolidBrush(Theme.Cloud)) g.FillPath(b, p);
            if (value <= 0) return;
            var fill = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * value), track.Height);
            using (GraphicsPath p = Theme.Round(fill, fill.Height / 2))
            using (var b = new LinearGradientBrush(new RectangleF(track.X - 1, track.Y, track.Width + 2, track.Height), Theme.Orange, Theme.OrangeEnd, 0f))
                g.FillPath(b, p);
        }
    }
}
