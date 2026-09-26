using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 圆环中央那团东西：一个**边缘会动的实心团**。
///
/// 早先是几千个点铺成的点云，但点云做不出那种"水面"的手感，
/// 反而越调越重。改成一条闭合路径：沿圆周采样算半径，连成边界，
/// 用径向渐变填充（中心实、边缘淡到没有），于是边界既在动、又不硬。
///
/// 边缘起伏用五阶**互不通约**的谐波，有正转有反转——
/// 整数阶保证闭合，不通约保证看不出周期，正反混合保证看不出流向。
///
/// 代价：没有颗粒感。要颗粒得另外叠。
/// </summary>
public sealed class CenterBlob : Control
{
    private const int Design = 290;
    private const double BaseRadius = 126;

    /// <summary>边缘谐波：阶数、角速度、初相、幅度。阶数整数保证首尾闭合。</summary>
    private static readonly (int K, double Speed, double Phase, double Amp)[] Rim =
    {
        (2, 0.230, 0.0, 0.055),
        (3, -0.310, 1.7, 0.042),
        (5, 0.170, 3.1, 0.030),
        (7, -0.120, 0.6, 0.022),
        (11, 0.090, 2.4, 0.015),
    };

    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<CenterBlob, string>(nameof(Mode), "full");

    /// <summary>full / lite / off</summary>
    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    static CenterBlob()
    {
        AffectsRender<CenterBlob>(ModeProperty);
        ModeProperty.Changed.AddClassHandler<CenterBlob>((c, _) => c.OnModeChanged());
    }

    private readonly IBrush _fill;
    private DispatcherTimer? _timer;
    private double _t;
    private bool _off;

    private int Steps => Mode == "lite" ? 120 : 260;

    private static TimeSpan IntervalFor(string mode) =>
        mode == "lite" ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromMilliseconds(16);

    public CenterBlob()
    {
        _fill = new RadialGradientBrush
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
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1 || _off) return;

        double s = Math.Min(w, h) / Design;
        var c = new Point(w / 2, h / 2);
        double breath = 0.030 * Math.Sin(_t * 0.9);

        int steps = Steps;
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
                    ripple += amp * Math.Sin(k * a + ph + sp * _t);
                }

                double radius = BaseRadius * (0.50 + breath + ripple) * s;
                var pt = new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
                if (i == 0) g.BeginFigure(pt, true);
                else g.LineTo(pt);
            }
            g.EndFigure(true);
        }

        ctx.DrawGeometry(_fill, null, geo);
    }

    private void OnModeChanged()
    {
        _off = Mode == "off";
        if (_timer is not null)
        {
            _timer.Interval = IntervalFor(Mode);
            if (_off) _timer.Stop();
            else if (!_timer.IsEnabled) _timer.Start();
        }
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _off = Mode == "off";
        if (_off) return;

        _timer ??= new DispatcherTimer(IntervalFor(Mode), DispatcherPriority.Render, (_, _) =>
        {
            _t += 0.016;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
    }
}
