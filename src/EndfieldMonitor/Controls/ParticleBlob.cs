using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 圆环中央那团「Siri 式」动态点云：650 个点按斐波那契球面均匀铺开，
/// 整团缓慢自转，半径被三层正弦噪声推着做呼吸起伏，点的深浅按景深实时给。
/// 原版是逐帧 canvas 重绘，这里用 DispatcherTimer 驱动 InvalidateVisual。
/// </summary>
public sealed class ParticleBlob : Control
{
    private const int N = 650;
    private const double Design = 290;

    private readonly (double X, double Y, double Z, double Ph)[] _pts = new (double, double, double, double)[N];
    private readonly IBrush[] _brushes = new IBrush[16];
    private DispatcherTimer? _timer;
    private double _t;

    public ParticleBlob()
    {
        // 固定种子，保证每次运行的点位一致（视觉可复现）
        var rng = new Random(20260925);
        for (int i = 0; i < N; i++)
        {
            double y = 1 - i / (double)(N - 1) * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = i * 2.39996;
            _pts[i] = (Math.Cos(th) * r, y, Math.Sin(th) * r, rng.NextDouble() * Math.PI * 2);
        }
        for (int i = 0; i < 16; i++)
        {
            double depth = i / 15.0;
            _brushes[i] = new SolidColorBrush(
                Color.FromArgb((byte)((0.10 + depth * 0.40) * 255), 96, 96, 92));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        double s = Math.Min(w, h) / Design;
        var c = new Point(w / 2, h / 2);

        double rot = _t * 0.12;
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        double breath = 0.5 + 0.5 * Math.Sin(_t * 0.9);

        foreach (var p in _pts)
        {
            double n = Math.Sin(3.1 * p.X + _t * 0.7 + p.Ph)
                     * Math.Sin(2.7 * p.Y - _t * 0.5)
                     * Math.Sin(2.3 * p.Z + _t * 0.6);

            double radius = 126 * (0.79 + 0.12 * breath + 0.09 * n) * s;
            double x = p.X * cs + p.Z * sn;
            double z = -p.X * sn + p.Z * cs;
            double depth = (z + 1) / 2;

            int idx = (int)Math.Clamp(depth * 15, 0, 15);
            double dot = (0.7 + depth * 1.2) * s;
            ctx.DrawEllipse(_brushes[idx], null, new Point(c.X + x * radius, c.Y + p.Y * radius), dot, dot);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _t += 0.016;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }
}
