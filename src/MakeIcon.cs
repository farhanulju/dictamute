// Build-time helper: writes the exe's multi-size .ico (PNG frames) and an optional preview sheet.
//   MakeIcon.exe <out.ico> [preview.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    static void Main(string[] args)
    {
        var frames = new List<byte[]>();
        foreach (int size in Sizes)
            using (var bmp = Icons.Render(IconState.App, size))
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); frames.Add(ms.ToArray()); }

        using (var w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                byte dim = (byte)(Sizes[i] >= 256 ? 0 : Sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }

        if (args.Length > 1) Preview(args[1]);
    }

    // Each state at tray sizes on light and dark taskbars, plus the app icon large.
    static void Preview(string path)
    {
        var states = new[] { IconState.Idle, IconState.InCall, IconState.Muting, IconState.App };
        var labels = new[] { "Not in a call", "In a call", "Muting Teams", "App icon" };
        int[] traySizes = { 16, 20, 24, 32 };
        int rowH = 64, labelW = 120, cell = 48;
        int width = labelW + traySizes.Length * cell * 2 + 16 + 128 + 16;
        int height = 40 + states.Length * rowH;
        using (var sheet = new Bitmap(width, Math.Max(height, 40 + 128 + 16)))
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font("Segoe UI", 9f))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int x0 = labelW;
            using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (var light = new SolidBrush(Color.FromArgb(243, 243, 243)))
            {
                g.FillRectangle(light, x0, 0, traySizes.Length * cell, sheet.Height);
                g.FillRectangle(dark, x0 + traySizes.Length * cell, 0, traySizes.Length * cell, sheet.Height);
                g.DrawString("light taskbar  16 / 20 / 24 / 32 px", font, Brushes.Black, x0 + 4, 8);
                g.DrawString("dark taskbar", font, Brushes.White, x0 + traySizes.Length * cell + 4, 8);
            }
            for (int r = 0; r < states.Length; r++)
            {
                int y = 40 + r * rowH;
                g.DrawString(labels[r], font, Brushes.Black, 8, y + 18);
                for (int t = 0; t < 2; t++)
                    for (int i = 0; i < traySizes.Length; i++)
                        using (var icon = Icons.Render(states[r], traySizes[i]))
                            g.DrawImageUnscaled(icon, x0 + t * traySizes.Length * cell + i * cell + (cell - traySizes[i]) / 2, y + (rowH - traySizes[i]) / 2);
            }
            using (var big = Icons.Render(IconState.App, 128))
                g.DrawImageUnscaled(big, x0 + traySizes.Length * cell * 2 + 16, 40);
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
