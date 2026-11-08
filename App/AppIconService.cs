using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace ScreenBrightnessApp;

/// <summary>
/// Draws the window/taskbar icon at runtime using the user's system accent color,
/// so the icon follows the machine's theme instead of a fixed brand hue.
/// The exe's embedded icon (Explorer) stays neutral on purpose - see assets/app.ico.
/// </summary>
internal static class AppIconService
{
    private static UISettings? _uiSettings;

    public static void Apply(Window window)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            string? path = null;
            try { path = GenerateAccentIcon(); }
            catch { return; /* accent lookup can fail in odd shell states; keep last icon */ }
            window.DispatcherQueue.TryEnqueue(() =>
            {
                try { window.AppWindow.SetIcon(path); }
                catch { /* window may be closing */ }
            });
        });
    }

    /// <summary>Regenerate the icon whenever the user changes their accent color.</summary>
    public static void Watch(Window window)
    {
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
                window.DispatcherQueue.TryEnqueue(() => Apply(window));
        }
        catch { /* accent watching is best-effort */ }
    }

    private static string GenerateAccentIcon()
    {
        var settings = _uiSettings ?? new UISettings();
        var accent = ToDrawing(settings.GetColorValue(UIColorType.Accent));
        var dark1 = ToDrawing(settings.GetColorValue(UIColorType.AccentDark1));
        var dark2 = ToDrawing(settings.GetColorValue(UIColorType.AccentDark2));

        // background ramp from the accent family, always dark enough for a white glyph
        var c1 = Luminance(accent) > Luminance(dark1) ? accent : dark1;
        var c2 = Luminance(dark2) < 0.62 ? dark2 : Darken(dark1, 0.55);
        var sun = Color.White;

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenBrightness", "app-accent.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteIco(path, c1, c2, sun);
        return path;
    }

    private static Bitmap DrawIcon(int size, Color c1, Color c2, Color sun)
    {
        var bmp = new Bitmap(size, size);
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = size / 1024f;

        using (var gp = new GraphicsPath())
        {
            float rad = 200 * k, d = rad * 2;
            gp.AddArc(0, 0, d, d, 180, 90);
            gp.AddArc(size - d, 0, d, d, 270, 90);
            gp.AddArc(size - d, size - d, d, d, 0, 90);
            gp.AddArc(0, size - d, d, d, 90, 90);
            gp.CloseFigure();
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size), c1, c2, LinearGradientMode.ForwardDiagonal);
            g.FillPath(brush, gp);
        }

        float cx = size / 2f;
        // upper sun + horizon bar: the same "brightness slider" motif as assets/app.ico
        g.SetClip(new Rectangle(0, 0, size, (int)(0.60 * size)));
        float sunCy = 0.50f * size;
        float core = 130 * k;
        using (var sb = new SolidBrush(sun))
            g.FillEllipse(sb, cx - core, sunCy - core, 2 * core, 2 * core);
        for (int i = 0; i < 8; i++)
        {
            float ang = i * MathF.PI / 4;
            using var pen = new Pen(sun, 42 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float r1 = 205 * k, r2 = 285 * k;
            g.DrawLine(pen, cx + MathF.Cos(ang) * r1, sunCy + MathF.Sin(ang) * r1, cx + MathF.Cos(ang) * r2, sunCy + MathF.Sin(ang) * r2);
        }
        g.ResetClip();
        using (var pen = new Pen(sun, 52 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float half = 250 * k, barY = 0.665f * size;
            g.DrawLine(pen, cx - half, barY, cx + half, barY);
        }
        g.Dispose();
        return bmp;
    }

    private static void WriteIco(string path, Color c1, Color c2, Color sun)
    {
        using var master = DrawIcon(1024, c1, c2, sun);
        var entries = new List<(int size, byte[] data)>();
        foreach (var s in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            using var bmp = new Bitmap(s, s);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(master, new Rectangle(0, 0, s, s));
            }
            using var ms = new MemoryStream();
            if (s >= 128)
            {
                bmp.Save(ms, ImageFormat.Png);
                entries.Add((s, ms.ToArray()));
            }
            else
            {
                entries.Add((s, ToDib(bmp)));
            }
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)entries.Count);
        var offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            var sz = size >= 256 ? (byte)0 : (byte)size;
            bw.Write(sz); bw.Write(sz); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)data.Length); bw.Write((uint)offset);
            offset += data.Length;
        }
        foreach (var (_, data) in entries) bw.Write(data);
        bw.Flush();
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(bd.Stride);
        var pixels = new byte[stride * h];
        Marshal.Copy(bd.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(bd);

        int maskStride = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40u); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(0u); bw.Write((uint)(w * h * 4 + maskStride * h)); bw.Write(0); bw.Write(0); bw.Write(0u); bw.Write(0u);
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                bw.Write(pixels[o + 2]); bw.Write(pixels[o + 1]); bw.Write(pixels[o]); bw.Write(pixels[o + 3]);
            }
        }
        var zeros = new byte[maskStride];
        for (int y = 0; y < h; y++) bw.Write(zeros);
        bw.Flush();
        return ms.ToArray();
    }

    private static Color ToDrawing(Windows.UI.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Darken(Color c, double f) => Color.FromArgb(255, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
}
