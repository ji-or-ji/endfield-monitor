using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 外围那圈点云。就是先前定下来的那一版：
///
///   起伏用六股方向、频率、速度互不通约的行波叠加（不是两条正弦，
///   周期短的波看久了会露出条纹），再叠一层缓慢的 X 轴点头——
///   只绕 Y 轴转的话每个点的 y 永远不变，上下不对称会被永久锁死。
///   点的半径按一个**空间连续**的低频疙瘩场起伏，而不是各自随机：
///   各自随机会互相抵消，平均完还是一个规整的球。
///   点不是圆的，是沿径向被拽过的小条，长度随它此刻被推的速度。
///
/// 性能：sin(a − t·s) 拆成 sin(a)cos(t·s) − cos(a)sin(t·s)，a 是每点固定量、
/// 预先存表，于是每帧只剩几次三角函数；再按景深分 16 档合批绘制。
/// </summary>
public sealed class OuterCloud : Control
{
    private const int MaxPoints = 5500;
    private const int WaveCount = 6;
    private const int BreathBuckets = 8;
    private const double Design = 290;
    private const double BaseRadius = 126;

    /// <summary>行波：方向(x,y,z)、频率、速度、幅度。六条互不通约，最后一条是低频大尺度的慢鼓动。</summary>
    private static readonly (double Dx, double Dy, double Dz, double Freq, double Speed, double Amp)[] Waves =
    {
        (0.62, 0.55, 0.56, 5.00, 0.95, 0.038),
        (-0.55, 0.30, 0.62, 3.71, 0.68, 0.029),
        (0.20, -0.88, 0.42, 2.83, 0.52, 0.023),
        (0.80, 0.10, -0.58, 2.11, 0.37, 0.018),
        (-0.30, 0.70, -0.65, 1.57, 0.28, 0.014),
        (0.35, 0.80, -0.45, 0.72, 0.16, 0.052),
    };

    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<OuterCloud, string>(nameof(Mode), "full");

    /// <summary>full / lite / off</summary>
    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    static OuterCloud()
    {
        AffectsRender<OuterCloud>(ModeProperty);
        ModeProperty.Changed.AddClassHandler<OuterCloud>((c, _) => c.OnModeChanged());
    }

    private readonly (double X, double Y, double Z)[] _pts = new (double, double, double)[MaxPoints];
    private readonly double[] _jit = new double[MaxPoints];
    private readonly double[] _sinA = new double[MaxPoints * WaveCount];
    private readonly double[] _cosA = new double[MaxPoints * WaveCount];
    private readonly double[] _brSin = new double[MaxPoints];
    private readonly double[] _brCos = new double[MaxPoints];
    private readonly IBrush[] _brushes = new IBrush[16];
    private readonly StreamGeometry[] _batch = new StreamGeometry[16];
    private DispatcherTimer? _timer;
    private double _t;
    private bool _off;

    private int PointCount => Mode == "lite" ? 1800 : MaxPoints;

    private static TimeSpan IntervalFor(string mode) =>
        mode == "lite" ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromMilliseconds(16);

    public OuterCloud()
    {
        // 固定种子，保证每次运行的点位一致（视觉可复现）
        var rng = new Random(20260925);
        for (int i = 0; i < MaxPoints; i++)
        {
            double y = 1 - i / (double)(MaxPoints - 1) * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = i * 2.39996;
            double px = Math.Cos(th) * r;
            double pz = Math.Sin(th) * r;
            _pts[i] = (px, y, pz);

            // 空间连续的疙瘩场：让半径成块地鼓、成块地缩
            double lump = 1
                + 0.110 * Math.Sin(3.1 * px + 1.7) * Math.Sin(2.6 * y - 0.9) * Math.Sin(2.9 * pz + 2.2)
                + 0.050 * Math.Sin(5.3 * y + 0.4) * Math.Sin(4.1 * px - 1.2);
            _jit[i] = lump * (0.97 + rng.NextDouble() * 0.06);

            for (int q = 0; q < WaveCount; q++)
            {
                var wv = Waves[q];
                double a = (px * wv.Dx + y * wv.Dy + pz * wv.Dz) * wv.Freq;
                _sinA[i * WaveCount + q] = Math.Sin(a);
                _cosA[i * WaveCount + q] = Math.Cos(a);
            }

            double bp = rng.NextDouble() * Math.PI * 2;
            _brSin[i] = Math.Sin(bp);
            _brCos[i] = Math.Cos(bp);
        }

        // 按景深由淡到实。点多时每点一实会叠成块，整体压淡才有雾感。
        for (int i = 0; i < 16; i++)
        {
            double depth = i / 15.0;
            byte a = (byte)((0.06 + depth * 0.24) * 255);
            _brushes[i] = new SolidColorBrush(Color.FromArgb(a, 96, 96, 92));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1 || _off) return;

        int count = PointCount;
        if (count == 0) return;

        double s = Math.Min(w, h) / Design;
        var c = new Point(w / 2, h / 2);

        double rot = _t * 0.12;
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        double tilt = 0.34 + 0.20 * Math.Sin(_t * 0.11);
        double ct = Math.Cos(tilt), st = Math.Sin(tilt);
        double breath = 0.030 * (0.5 + 0.5 * Math.Sin(_t * 0.9) - 0.5);

        Span<double> wc = stackalloc double[WaveCount];
        Span<double> ws = stackalloc double[WaveCount];
        for (int q = 0; q < WaveCount; q++)
        {
            double ph = _t * Waves[q].Speed;
            wc[q] = Math.Cos(ph);
            ws[q] = Math.Sin(ph);
        }

        Span<double> bc = stackalloc double[BreathBuckets];
        Span<double> bs = stackalloc double[BreathBuckets];
        for (int b = 0; b < BreathBuckets; b++)
        {
            double ph = _t * (0.21 + b * 0.037);
            bc[b] = Math.Cos(ph);
            bs[b] = Math.Sin(ph);
        }

        var opens = new StreamGeometryContext[16];
        for (int i = 0; i < 16; i++)
        {
            _batch[i] = new StreamGeometry();
            opens[i] = _batch[i].Open();
        }

        // 按步长均匀取样，而不是取前 count 个点：
        // 点位数组是按 y 轴顺序生成的，取前缀等于只画半球。
        double stride = (double)MaxPoints / count;
        for (int k = 0; k < count; k++)
        {
            int pi = (int)(k * stride);
            var p = _pts[pi];
            int baseIdx = pi * WaveCount;

            double sum = 0, vel = 0;
            for (int q = 0; q < WaveCount; q++)
            {
                var wv = Waves[q];
                double sa = _sinA[baseIdx + q], ca = _cosA[baseIdx + q];
                double disp = sa * wc[q] - ca * ws[q];
                double cosd = ca * wc[q] + sa * ws[q];
                sum += wv.Amp * disp;
                vel += wv.Amp * wv.Speed * cosd;
            }

            int bk = pi % BreathBuckets;
            sum += 0.020 * (_brSin[pi] * bc[bk] + _brCos[pi] * bs[bk]);

            // 不设半径上限：一给半径封顶，尾巴就会挤成一层壳，轮廓立刻变圆。
            double radius = BaseRadius * (0.85 + breath + sum) * _jit[pi] * s;

            double x1 = p.X * cs + p.Z * sn;
            double z1 = -p.X * sn + p.Z * cs;
            double y2 = p.Y * ct - z1 * st;
            double z2 = p.Y * st + z1 * ct;

            double depth = (z2 + 1) / 2;
            int idx = (int)Math.Clamp(depth * 15, 0, 15);

            // 0.88：方点与同半径圆点面积对齐
            double dot = (0.45 + depth * 0.85) * 0.88 * s;

            // 被推得越快，顺径向拉得越长；推到头（速度归零）就还是圆的。
            double e = 1 + Math.Min(0.65, Math.Abs(vel) * 8.0);
            double halfL = dot * e;
            double halfW = dot / Math.Sqrt(e);

            double ux = x1, uy = y2;
            double ulen = Math.Sqrt(ux * ux + uy * uy);
            if (ulen < 1e-6) { ux = 1; uy = 0; } else { ux /= ulen; uy /= ulen; }
            double tx = -uy, ty = ux;

            double px = c.X + x1 * radius, py = c.Y + y2 * radius;
            double ax = ux * halfL, ay = uy * halfL;
            double bx = tx * halfW, by = ty * halfW;

            var o = opens[idx];
            o.BeginFigure(new Point(px - ax - bx, py - ay - by), true);
            o.LineTo(new Point(px + ax - bx, py + ay - by));
            o.LineTo(new Point(px + ax + bx, py + ay + by));
            o.LineTo(new Point(px - ax + bx, py - ay + by));
            o.EndFigure(true);
        }

        for (int i = 0; i < 16; i++)
        {
            opens[i].Dispose();
            ctx.DrawGeometry(_brushes[i], null, _batch[i]);
        }
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
