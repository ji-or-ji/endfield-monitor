using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 外围那圈点云。
///
/// 两种画法：
///   Batch = true  —— 把每个点摊成两个三角形，整团**一次 DrawVertices** 交给 GPU。
///                    此前是 5500 个独立四边形路径，Skia 要逐条在 CPU 上细分，
///                    那才是真正的开销大头。
///   Batch = false —— 保留原来的逐点路径画法，作为出问题时的退路。
///
/// 另外两种省电状态（窗口不在前台时由上层置位）：
///   Cheap = true  —— 不画点云，改画和中心同款、但更大的灰块（外一圈内一圈）。
///   Freeze = true —— 停止推进时间，只留最后一帧。
/// </summary>
public sealed class OuterCloud : Control
{
    private const int MaxPoints = 5500;
    private const int LitePoints = 1800;
    private const int WaveCount = 6;
    private const int BreathBuckets = 8;
    private const int DepthBuckets = 16;
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

    public static readonly StyledProperty<bool> BatchProperty =
        AvaloniaProperty.Register<OuterCloud, bool>(nameof(Batch), true);

    public static readonly StyledProperty<bool> IdleProperty =
        AvaloniaProperty.Register<OuterCloud, bool>(nameof(Idle));

    /// <summary>full / lite / off</summary>
    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>true 用一次性 DrawVertices，false 回退到逐点路径。</summary>
    public bool Batch
    {
        get => GetValue(BatchProperty);
        set => SetValue(BatchProperty, value);
    }

    /// <summary>窗口不在前台时为真：不再画点云，改画灰块并放慢刷新。</summary>
    public bool Idle
    {
        get => GetValue(IdleProperty);
        set => SetValue(IdleProperty, value);
    }

    static OuterCloud()
    {
        AffectsRender<OuterCloud>(BatchProperty, IdleProperty);
        ModeProperty.Changed.AddClassHandler<OuterCloud>((c, _) => c.SyncTimer());
        IdleProperty.Changed.AddClassHandler<OuterCloud>((c, _) => c.SyncTimer());
    }

    private readonly (double X, double Y, double Z)[] _pts = new (double, double, double)[MaxPoints];
    private readonly double[] _jit = new double[MaxPoints];
    private readonly double[] _sinA = new double[MaxPoints * WaveCount];
    private readonly double[] _cosA = new double[MaxPoints * WaveCount];
    private readonly double[] _brSin = new double[MaxPoints];
    private readonly double[] _brCos = new double[MaxPoints];
    private readonly IBrush[] _brushes = new IBrush[DepthBuckets];
    private readonly StreamGeometry[] _batch = new StreamGeometry[DepthBuckets];

    private DispatcherTimer? _timer;
    private double _t;
    private bool _off;

    private int PointCount => Mode == "lite" ? LitePoints : MaxPoints;

    private int BlobSteps => Mode == "lite" ? 120 : 260;

    /// <summary>失焦状态下的刷新间隔。实测每秒 60 次重绘本身才是开销大头，降频即省。</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(150);

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
        for (int i = 0; i < DepthBuckets; i++)
        {
            double depth = i / (double)(DepthBuckets - 1);
            byte a = (byte)((0.06 + depth * 0.24) * 255);
            _brushes[i] = new SolidColorBrush(Color.FromArgb(a, 96, 96, 92));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1 || _off)
        {
            return;
        }

        // 失焦降级：不画点云，换成和中心同款、更大的灰块（外一圈内一圈）
        if (Idle)
        {
            CloudBlob.Draw(ctx, new Rect(Bounds.Size), _t, 0.85, BlobSteps);
            return;
        }

        int count = PointCount;
        if (count == 0)
        {
            return;
        }

        var rect = new Rect(Bounds.Size);
        if (Batch)
        {
            var frame = BuildVertices(rect, _t, count);
            ctx.Custom(new CloudDrawOp(rect, frame));
            return;
        }

        RenderPaths(ctx, rect, _t, count);
    }

    // ================= 批量顶点 =================

    /// <summary>
    /// 一帧的顶点数据。刻意每帧新建，交给绘制指令后不再改动：
    /// 合成器可能稍后才真正绘制（甚至重放），共享的可变缓冲会被后一帧改写。
    /// </summary>
    private sealed class VertexFrame
    {
        public required SKPoint[] Positions;
        public required SKColor[] Colors;
    }

    /// <summary>每个点摊成两个三角形、共 6 个顶点（不用索引，把共享的两个角重复一遍）。</summary>
    private const int VertsPerPoint = 6;

    /// <summary>把这一帧所有点的六个角算出来。坐标以控件左上角为原点。</summary>
    private VertexFrame BuildVertices(Rect bounds, double t, int count)
    {
        double s = Math.Min(bounds.Width, bounds.Height) / Design;
        double cx = bounds.X + bounds.Width / 2;
        double cy = bounds.Y + bounds.Height / 2;

        double rot = t * 0.12;
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        double tilt = 0.34 + 0.20 * Math.Sin(t * 0.11);
        double ct = Math.Cos(tilt), st = Math.Sin(tilt);
        double breath = 0.030 * Math.Sin(t * 0.9);

        Span<double> wc = stackalloc double[WaveCount];
        Span<double> ws = stackalloc double[WaveCount];
        for (int q = 0; q < WaveCount; q++)
        {
            double ph = t * Waves[q].Speed;
            wc[q] = Math.Cos(ph);
            ws[q] = Math.Sin(ph);
        }

        Span<double> bc = stackalloc double[BreathBuckets];
        Span<double> bs = stackalloc double[BreathBuckets];
        for (int b = 0; b < BreathBuckets; b++)
        {
            double ph = t * (0.21 + b * 0.037);
            bc[b] = Math.Cos(ph);
            bs[b] = Math.Sin(ph);
        }

        int verts = count * VertsPerPoint;
        var pos = new SKPoint[verts];
        var col = new SKColor[verts];

        // 按步长均匀取样，而不是取前 count 个点：点位按 y 轴顺序生成，取前缀只画半球
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

            // 不设半径上限：一给半径封顶，尾巴就会挤成一层壳，轮廓立刻变圆
            double radius = BaseRadius * (0.85 + breath + sum) * _jit[pi] * s;

            double x1 = p.X * cs + p.Z * sn;
            double z1 = -p.X * sn + p.Z * cs;
            double y2 = p.Y * ct - z1 * st;
            double z2 = p.Y * st + z1 * ct;

            double depth = (z2 + 1) / 2;
            int idx = (int)Math.Clamp(depth * (DepthBuckets - 1), 0, DepthBuckets - 1);

            // 0.88：方点与同半径圆点面积对齐
            double dot = (0.45 + depth * 0.85) * 0.88 * s;

            // 被推得越快，顺径向拉得越长；推到头（速度归零）就还是圆的
            double e = 1 + Math.Min(0.65, Math.Abs(vel) * 8.0);
            double halfL = dot * e;
            double halfW = dot / Math.Sqrt(e);

            double ux = x1, uy = y2;
            double ulen = Math.Sqrt(ux * ux + uy * uy);
            if (ulen < 1e-6)
            {
                ux = 1; uy = 0;
            }
            else
            {
                ux /= ulen; uy /= ulen;
            }
            double tx = -uy, ty = ux;

            double px = cx + x1 * radius, py = cy + y2 * radius;
            double ax = ux * halfL, ay = uy * halfL;
            double bx = tx * halfW, by = ty * halfW;

            // 顶点颜色会与画笔颜色相乘：画笔必须保持白色，否则点云会被染成黑 / 红 / 白。
            // 另外 DrawVertices 是逐三角形混合，重叠处会叠上去；
            // 老画法是“每个景深桶整条路径填一次”，天然不叠加。
            byte alpha = (byte)((0.06 + depth * 0.24) * 255 * 0.25);
            var color = new SKColor(96, 96, 92, alpha);

            // 两个三角形：v0-v1-v2 与 v0-v2-v3
            float ax0 = (float)(px - ax - bx), ay0 = (float)(py - ay - by);
            float ax1 = (float)(px + ax - bx), ay1 = (float)(py + ay - by);
            float ax2 = (float)(px + ax + bx), ay2 = (float)(py + ay + by);
            float ax3 = (float)(px - ax + bx), ay3 = (float)(py - ay + by);

            int v = k * VertsPerPoint;
            pos[v] = new SKPoint(ax0, ay0);
            pos[v + 1] = new SKPoint(ax1, ay1);
            pos[v + 2] = new SKPoint(ax2, ay2);
            pos[v + 3] = new SKPoint(ax0, ay0);
            pos[v + 4] = new SKPoint(ax2, ay2);
            pos[v + 5] = new SKPoint(ax3, ay3);
            for (int j = 0; j < VertsPerPoint; j++)
            {
                col[v + j] = color;
            }
        }

        return new VertexFrame
        {
            Positions = pos,
            Colors = col,
        };
    }

    private sealed class CloudDrawOp : ICustomDrawOperation
    {
        private readonly VertexFrame _frame;

        public CloudDrawOp(Rect bounds, VertexFrame frame)
        {
            Bounds = bounds;
            _frame = frame;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        // 每帧都是新内容，不做去重
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            {
                return;
            }

            using var lease = feature.Lease();
            // 抗锯齿必须开：关掉时每个小四边形都是硬边像素块，看着像“实心、没透明度”。
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            lease.SkCanvas.DrawVertices(
                SKVertexMode.Triangles, _frame.Positions, _frame.Colors, paint);
        }
    }

    // ================= 旧的逐点路径（Batch=false 时用） =================

    private void RenderPaths(DrawingContext ctx, Rect bounds, double t, int count)
    {
        double s = Math.Min(bounds.Width, bounds.Height) / Design;
        double cx = bounds.X + bounds.Width / 2;
        double cy = bounds.Y + bounds.Height / 2;

        double rot = t * 0.12;
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        double tilt = 0.34 + 0.20 * Math.Sin(t * 0.11);
        double ct = Math.Cos(tilt), st = Math.Sin(tilt);
        double breath = 0.030 * Math.Sin(t * 0.9);

        Span<double> wc = stackalloc double[WaveCount];
        Span<double> ws = stackalloc double[WaveCount];
        for (int q = 0; q < WaveCount; q++)
        {
            double ph = t * Waves[q].Speed;
            wc[q] = Math.Cos(ph);
            ws[q] = Math.Sin(ph);
        }

        Span<double> bc = stackalloc double[BreathBuckets];
        Span<double> bs = stackalloc double[BreathBuckets];
        for (int b = 0; b < BreathBuckets; b++)
        {
            double ph = t * (0.21 + b * 0.037);
            bc[b] = Math.Cos(ph);
            bs[b] = Math.Sin(ph);
        }

        var opens = new StreamGeometryContext[DepthBuckets];
        for (int i = 0; i < DepthBuckets; i++)
        {
            _batch[i] = new StreamGeometry();
            opens[i] = _batch[i].Open();
        }

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

            double radius = BaseRadius * (0.85 + breath + sum) * _jit[pi] * s;

            double x1 = p.X * cs + p.Z * sn;
            double z1 = -p.X * sn + p.Z * cs;
            double y2 = p.Y * ct - z1 * st;
            double z2 = p.Y * st + z1 * ct;

            double depth = (z2 + 1) / 2;
            int idx = (int)Math.Clamp(depth * (DepthBuckets - 1), 0, DepthBuckets - 1);

            double dot = (0.45 + depth * 0.85) * 0.88 * s;

            double e = 1 + Math.Min(0.65, Math.Abs(vel) * 8.0);
            double halfL = dot * e;
            double halfW = dot / Math.Sqrt(e);

            double ux = x1, uy = y2;
            double ulen = Math.Sqrt(ux * ux + uy * uy);
            if (ulen < 1e-6)
            {
                ux = 1; uy = 0;
            }
            else
            {
                ux /= ulen; uy /= ulen;
            }
            double tx = -uy, ty = ux;

            double px = cx + x1 * radius, py = cy + y2 * radius;
            double ax = ux * halfL, ay = uy * halfL;
            double bx = tx * halfW, by = ty * halfW;

            var o = opens[idx];
            o.BeginFigure(new Point(px - ax - bx, py - ay - by), true);
            o.LineTo(new Point(px + ax - bx, py + ay - by));
            o.LineTo(new Point(px + ax + bx, py + ay + by));
            o.LineTo(new Point(px - ax + bx, py - ay + by));
            o.EndFigure(true);
        }

        for (int i = 0; i < DepthBuckets; i++)
        {
            opens[i].Dispose();
            ctx.DrawGeometry(_brushes[i], null, _batch[i]);
        }
    }

    // ================= 刷新节奏 =================

    private void SyncTimer()
    {
        _off = Mode == "off";

        // 失焦 + 精简：画一帧就停
        if (_off || (Idle && Mode == "lite"))
        {
            _timer?.Stop();
            // 停下前把最后一帧画出来
            InvalidateVisual();
            return;
        }

        var interval = Idle ? IdleInterval : IntervalFor(Mode);
        _timer ??= new DispatcherTimer(interval, DispatcherPriority.Render, (_, _) =>
        {
            _t += 0.016;
            InvalidateVisual();
        });
        _timer.Interval = interval;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
    }
}
