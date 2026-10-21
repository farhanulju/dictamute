// DictaMute artwork, drawn in code so every size is pixel-sharp (no scaled bitmaps).
// A white microphone on a coloured disc; the colour and an optional slash show the state.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

enum IconState { Idle, InCall, Muting, App }

static class Icons
{
    static readonly Color Slate = Color.FromArgb(0x64, 0x74, 0x8B);   // not in a call
    static readonly Color Indigo = Color.FromArgb(0x4F, 0x46, 0xE5);  // watching your call
    static readonly Color Red = Color.FromArgb(0xDC, 0x26, 0x26);     // muting Teams now

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>A GDI icon for the tray. Keep it for the app's lifetime (handles are cached).</summary>
    public static Icon Tray(IconState state, int size)
    {
        using (var bmp = Render(state, size))
        {
            IntPtr h = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }

    public static Bitmap Render(IconState state, int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            Color bg = state == IconState.Idle ? Slate : state == IconState.Muting ? Red : Indigo;
            bool slash = state == IconState.Muting || state == IconState.App;

            // Disc (the app icon gets a soft top-to-bottom gradient for depth).
            var disc = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
            if (state == IconState.App)
                using (var b = new LinearGradientBrush(disc, Lighten(bg, 0.18f), bg, LinearGradientMode.Vertical))
                    g.FillEllipse(b, disc);
            else
                using (var b = new SolidBrush(bg)) g.FillEllipse(b, disc);

            float stroke = Math.Max(1.5f, s * 0.075f);
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // Capsule
                float cw = s * 0.24f, ch = s * 0.36f, cx = s * 0.5f - cw / 2, cy = s * 0.17f;
                using (var capsule = RoundRect(new RectangleF(cx, cy, cw, ch), cw / 2)) g.FillPath(white, capsule);
                // Holder: the lower half of an ellipse around the capsule
                var arc = new RectangleF(s * 0.28f, s * 0.29f, s * 0.44f, s * 0.38f);
                g.DrawArc(pen, arc, 0, 180);
                // Stem and base
                g.DrawLine(pen, s * 0.5f, arc.Bottom, s * 0.5f, s * 0.80f);
                g.DrawLine(pen, s * 0.39f, s * 0.80f, s * 0.61f, s * 0.80f);

                if (slash)
                {
                    // Cut a gap in the disc colour first so the slash reads at 16 px.
                    var a = new PointF(s * 0.25f, s * 0.22f);
                    var z = new PointF(s * 0.75f, s * 0.80f);
                    Color gap = state == IconState.App ? Lighten(bg, 0.08f) : bg;
                    using (var cut = new Pen(gap, stroke * 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(cut, a, z);
                    g.DrawLine(pen, a, z);
                }
            }
        }
        return bmp;
    }

    static Color Lighten(Color c, float t)
    {
        return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t));
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
