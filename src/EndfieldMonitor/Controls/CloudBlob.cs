using System;
using Avalonia;
using Avalonia.Media;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 那团灰色的东西：边缘起伏、中心实、边缘淡到没有的实心盘。
///
/// 中心的 CenterBlob 用它；外圈点云在窗口失焦时也降级成它，
/// 只是半径系数更大，于是看上去就是「外一圈、内一圈」两个灰块。
/// </summary>
internal static class CloudBlob
{
    /// <summary>设计尺寸，绘制时按控件实际尺寸等比缩放。</summary>
    public const int Design = 290;

    /// <summary>基准半径，乘以半径系数得到实际半径。</summary>
    public const double BaseRadius = 126;

    /// <summary>边缘谐波：阶数、角速度、初相、幅度。整数阶保证首尾闭合。</summary>
    private static readonly (int K, double Speed, double Phase, double Amp)[] Rim =
    {
        (2, 0.230, 0.0, 0.055),
        (3, -0.310, 1.7, 0.042),
        (5, 0.170, 3.1, 0.030),
        (7, -0.120, 0.6, 0.022),
        (11, 0.090, 2.4, 0.015),
    };

    /// <summary>共用同一份画刷，避免每次绘制都新建。</summary>
    private static readonly IBrush Fill = BuildFill();

    private static IBrush BuildFill() => new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb((byte)(0.46 * 255), 116, 116, 113), 0.00),
            new GradientStop(Color.FromArgb((byte)(0.32 * 255), 120, 120, 117), 0.55),
            new GradientStop(Color.FromArgb(0, 120, 120, 117), 1.00),
        },
    };

    /// <summary>
    /// 在 bounds 中央画一团灰块。radiusFactor 决定大小（中心 0.50、外圈 0.85），
    /// t 是时间（秒），steps 是闭合路径的采样段数。
    /// </summary>
    public static void Draw(DrawingContext ctx, Rect bounds, double t, double radiusFactor, int steps)
    {
        double w = bounds.Width, h = bounds.Height;
        if (w <= 1 || h <= 1 || steps < 3)
        {
            return;
        }

        double s = Math.Min(w, h) / Design;
        var c = new Point(bounds.X + w / 2, bounds.Y + h / 2);
        double breath = 0.030 * Math.Sin(t * 0.9);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < steps; i++)
            {
                double a = i * 2 * Math.PI / steps;

                double ripple = 0;
                for (int q = 0; q < Rim.Length; q++)
                {
                    var (k, sp, ph, amp) = Rim[q];
                    ripple += amp * Math.Sin(k * a + ph + sp * t);
                }

                double radius = BaseRadius * (radiusFactor + breath + ripple) * s;
                var pt = new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
                if (i == 0)
                {
                    g.BeginFigure(pt, true);
                }
                else
                {
                    g.LineTo(pt);
                }
            }
            g.EndFigure(true);
        }

        ctx.DrawGeometry(Fill, null, geo);
    }
}
