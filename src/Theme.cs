using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AlapCamera
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(15, 15, 19);
        public static readonly Color Panel = Color.FromArgb(22, 22, 28);
        public static readonly Color Card = Color.FromArgb(27, 27, 34);
        public static readonly Color CardHover = Color.FromArgb(33, 33, 42);
        public static readonly Color Field = Color.FromArgb(34, 34, 43);
        public static readonly Color Border = Color.FromArgb(46, 46, 58);
        public static readonly Color Text = Color.FromArgb(242, 242, 247);
        public static readonly Color Muted = Color.FromArgb(146, 146, 164);
        public static readonly Color Accent = Color.FromArgb(124, 92, 255);
        public static readonly Color AccentHover = Color.FromArgb(143, 116, 255);
        public static readonly Color AccentSoft = Color.FromArgb(190, 172, 255);
        public static readonly Color Green = Color.FromArgb(34, 197, 94);
        public static readonly Color GreenBg = Color.FromArgb(16, 62, 42);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color DangerBg = Color.FromArgb(63, 29, 36);
        public static readonly Color Warn = Color.FromArgb(252, 211, 77);
        public static readonly Color WarnBg = Color.FromArgb(72, 46, 16);

        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        public static Font UI(float size) { return Get("Segoe UI", size, FontStyle.Regular); }
        public static Font UI(float size, FontStyle st) { return Get("Segoe UI", size, st); }
        public static Font Semi(float size) { return Get("Segoe UI Semibold", size, FontStyle.Regular); }

        static Font Get(string family, float size, FontStyle st)
        {
            string k = family + "|" + size + "|" + (int)st;
            Font f;
            if (!cache.TryGetValue(k, out f)) { f = new Font(family, size, st); cache[k] = f; }
            return f;
        }

        public static void HQ(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float width)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl)
        {
            TextRenderer.DrawText(g, s, f, r, c, fl | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public static void ApplyDarkTitleBar(Form f)
        {
            try
            {
                int val = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref val, 4);
            }
            catch { }
        }
    }

    class StudioCard : Panel
    {
        public string Title { get; set; }
        public StudioCard(string title)
        {
            Title = title;
            DoubleBuffered = true;
            BackColor = Theme.Card;
            Padding = new Padding(Theme.S(12), Theme.S(28), Theme.S(12), Theme.S(10));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.HQ(e.Graphics);
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Theme.Fill(e.Graphics, r, Theme.S(10), Theme.Card);
            Theme.Stroke(e.Graphics, r, Theme.S(10), Theme.Border, 1f);
            if (!string.IsNullOrEmpty(Title))
            {
                Theme.DrawText(e.Graphics, Title.ToUpperInvariant(), Theme.Semi(8f), Theme.AccentSoft,
                    new Rectangle(Theme.S(12), Theme.S(8), Width - Theme.S(24), Theme.S(16)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    class ToggleSwitch : Control
    {
        bool _checked;
        bool _hover;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public ToggleSwitch(string text, bool initial)
        {
            Text = text;
            _checked = initial;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Height = Theme.S(28);
            Font = Theme.Semi(9f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.HQ(e.Graphics);
            int tw = Theme.S(38), th = Theme.S(20);
            int tx = Width - tw - 2;
            int ty = (Height - th) / 2;

            Theme.DrawText(e.Graphics, Text, Font, _checked ? Theme.Text : Theme.Muted,
                new Rectangle(0, 0, tx - Theme.S(6), Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            Color track = _checked ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Theme.CardHover : Theme.Field);
            Theme.Fill(e.Graphics, new RectangleF(tx, ty, tw, th), th / 2f, track);
            if (!_checked) Theme.Stroke(e.Graphics, new RectangleF(tx, ty, tw, th), th / 2f, Theme.Border, 1f);

            int ks = th - Theme.S(6);
            int kx = _checked ? (tx + tw - ks - Theme.S(3)) : (tx + Theme.S(3));
            Theme.Fill(e.Graphics, new RectangleF(kx, ty + Theme.S(3), ks, ks), ks / 2f, Color.White);
        }
    }

    class FlatBtn : Control
    {
        bool _hover;
        bool _active;
        public bool Active { get { return _active; } set { _active = value; Invalidate(); } }

        public FlatBtn(string text)
        {
            Text = text;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Height = Theme.S(30);
            Font = Theme.Semi(8.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.HQ(e.Graphics);
            Color bg = _active ? (_hover ? Theme.AccentHover : Theme.Accent) : (_hover ? Theme.CardHover : Theme.Field);
            Color fg = _active ? Color.White : Theme.Muted;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Theme.Fill(e.Graphics, r, Theme.S(7), bg);
            if (!_active) Theme.Stroke(e.Graphics, r, Theme.S(7), Theme.Border, 1f);
            Theme.DrawText(e.Graphics, Text, Font, fg, new Rectangle(0, 0, Width, Height),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
