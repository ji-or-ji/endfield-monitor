using System;
using Avalonia.Media;

namespace EndfieldMonitor.Utils;

/// <summary>HSL/RGB 互转。复刻原版条纹取色时要在 HSL 空间里做小抖动。</summary>
public static class ColorUtil
{
    /// <summary>返回 h(0-360)、s(0-1)、l(0-1)。</summary>
    public static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b));
        double mn = Math.Min(r, Math.Min(g, b));
        double d = mx - mn;
        double l = (mx + mn) / 2.0;
        double h = 0, s = 0;
        if (d > 1e-9)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (Math.Abs(mx - r) < 1e-9) h = ((g - b) / d) % 6;
            else if (Math.Abs(mx - g) < 1e-9) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        return (h, s, l);
    }

    public static Color HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b + m, 0, 1) * 255));
    }
}

/// <summary>与原版 JS 同源的线性同余随机数，保证条纹每次生成完全一致。</summary>
public sealed class Lcg
{
    private double _seed;
    public Lcg(double seed) => _seed = seed;
    public double Next()
    {
        _seed = (_seed * 9301 + 49297) % 233280;
        return _seed / 233280.0;
    }
}
