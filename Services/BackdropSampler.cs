using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace CpuTempWidget.Services;

/// <summary>
/// Samples a few pixels around the overlay (not under it) so text can stay readable
/// on wallpaper, apps, and borderless games. Eight GetPixel calls ~1/s is negligible CPU.
/// </summary>
internal static class BackdropSampler
{
    public readonly record struct Sample(double Luminance, Color Average);

    public static Sample? TrySampleAround(Window window, int insetPx = 22)
    {
        if (window is null || !window.IsVisible) return null;

        Point origin;
        Size size;
        try
        {
            origin = window.PointToScreen(new Point(0, 0));
            size = new Size(window.ActualWidth, window.ActualHeight);
            var src = PresentationSource.FromVisual(window);
            if (src?.CompositionTarget is { } ct)
            {
                var m = ct.TransformToDevice;
                size = new Size(size.Width * m.M11, size.Height * m.M22);
            }
        }
        catch
        {
            return null;
        }

        var l = (int)Math.Round(origin.X);
        var t = (int)Math.Round(origin.Y);
        var r = l + Math.Max(8, (int)Math.Round(size.Width));
        var b = t + Math.Max(8, (int)Math.Round(size.Height));
        var mx = (l + r) / 2;
        var my = (t + b) / 2;
        var o = Math.Clamp(insetPx, 12, 48);

        var points = new (int X, int Y)[]
        {
            (l - o, my), (r + o, my),
            (mx, t - o), (mx, b + o),
            (l - o, t - o), (r + o, t - o),
            (l - o, b + o), (r + o, b + o)
        };

        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;

        try
        {
            long rs = 0, gs = 0, bs = 0;
            var n = 0;
            foreach (var (x, y) in points)
            {
                var px = GetPixel(hdc, x, y);
                if (px == unchecked((uint)-1)) continue;
                rs += px & 0xFF;
                gs += (px >> 8) & 0xFF;
                bs += (px >> 16) & 0xFF;
                n++;
            }

            if (n == 0) return null;
            var r8 = (byte)(rs / n);
            var g8 = (byte)(gs / n);
            var b8 = (byte)(bs / n);
            var lum = (0.2126 * r8 + 0.7152 * g8 + 0.0722 * b8) / 255.0;
            return new Sample(lum, Color.FromRgb(r8, g8, b8));
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * t),
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);
}
