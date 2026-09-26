using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EndfieldMonitor.Utils;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 复刻原版的「电量式」圆环。
///
/// 谁动、谁不动（按终末地的实际表现）：
///   内层亮黄环  —— **固定不动**，它是刻度，不是进度条
///   左上橘色弧  —— 会动，绕 CPU 的历史
///   右下蓝色弧  —— 会动，绕内存的历史
///
/// 两条弧不是"当前值有多长"，而是**把时间轴弯过来**：
/// 角度 = 时间（一路 60 个采样），半径 = 那一刻的占用率。
/// 于是弧上是一条会起伏的带子，最末一格就是当前值。
/// 底轨（整段 90°、淡色）表示量程，值再小也看得出这段弧有多长。
///
/// 坐标约定与原版一致：0° 在正上方，顺时针为正；按 470×470 设计尺寸生成，绘制时等比缩放。
/// </summary>
public sealed class RingGauge : Control
{
    private const double CX = 235, CY = 235, Design = 470;
    private const double BandInner = 188;   // 弧带内侧半径（= 0%）
    private const double BandOuter = 216;   // 弧带外侧半径（= 100%）

    // 属性名按「弧的位置」而不是「哪个指标」命名：指标换边时不用再改名。

    /// <summary>左上橘色弧的历史序列（0~100）。当前接 CPU 占用率。</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> UpperHistoryProperty =
        AvaloniaProperty.Register<RingGauge, IReadOnlyList<double>?>(nameof(UpperHistory));

    /// <summary>右下蓝色弧的历史序列（0~100）。当前接内存占用率。</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> LowerHistoryProperty =
        AvaloniaProperty.Register<RingGauge, IReadOnlyList<double>?>(nameof(LowerHistory));

    public IReadOnlyList<double>? UpperHistory
    {
        get => GetValue(UpperHistoryProperty);
        set => SetValue(UpperHistoryProperty, value);
    }

    public IReadOnlyList<double>? LowerHistory
    {
        get => GetValue(LowerHistoryProperty);
        set => SetValue(LowerHistoryProperty, value);
    }

    static RingGauge()
    {
        AffectsRender<RingGauge>(UpperHistoryProperty, LowerHistoryProperty);
    }

    private static readonly Color RailColor = Color.Parse("#CFCFC9");
    private static readonly Color MainBgColor = Color.Parse("#D3D3CE");
    private static readonly Color MainRailColor = Color.Parse("#F2EDC4");
    private static readonly Color ArcColor = Color.Parse("#FFE23D");
    private static readonly Color OrangeBase = Color.Parse("#ECB063");
    private static readonly Color BlueBase = Color.Parse("#7FB2CC");

    private StreamGeometry? _railOutL, _railInL, _railOutR, _railInR;
    private readonly List<(StreamGeometry Geo, Color Col)> _stripesOrange = new();
    private readonly List<(StreamGeometry Geo, Color Col)> _stripesBlue = new();
    private bool _built;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        BuildArtwork();

        double s = Math.Min(w, h) / Design;
        var center = new Point(w / 2, h / 2);

        using (ctx.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (ctx.PushTransform(Matrix.CreateScale(s, s)))
        using (ctx.PushTransform(Matrix.CreateTranslation(-CX, -CY)))
        {
            var railPen = new Pen(new SolidColorBrush(RailColor), 3);

            // 1) 装饰弧的内外描边（半径 221 / 183）
            ctx.DrawGeometry(null, railPen, _railOutL!);
            ctx.DrawGeometry(null, railPen, _railInL!);
            ctx.DrawGeometry(null, railPen, _railOutR!);
            ctx.DrawGeometry(null, railPen, _railInR!);

            // 2) 两条会起伏的弧：淡底轨 + 历史带子（左上 CPU、右下内存）
            DrawHistoryArc(ctx, 270, 360, UpperHistory, OrangeBase, _stripesOrange);
            DrawHistoryArc(ctx, 90, 180, LowerHistory, BlueBase, _stripesBlue);

            // 3) 灰衬环 + 淡黄轨道
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(MainBgColor), 24), new Point(CX, CY), 151, 151);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(MainRailColor), 18), new Point(CX, CY), 150, 150);

            // 4) 内层亮黄环：固定的一整圈，不是进度条
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(ArcColor), 18), new Point(CX, CY), 150, 150);
        }
    }

    /// <summary>
    /// 一条把时间弯成角度的弧：每一格的半径 = 那一刻的占用率。
    /// 内缘固定在 BandInner（0%），外缘随值向外推，于是整条是一根会起伏的带子。
    /// </summary>
    private static void DrawHistoryArc(DrawingContext ctx, double from, double to,
                                       IReadOnlyList<double>? hist, Color color,
                                       List<(StreamGeometry Geo, Color Col)> stripes)
    {
        // 淡底轨：表示量程
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color, 0.18), 28), ArcGeometry(202, from, to));

        if (hist is null || hist.Count < 2) return;

        int n = hist.Count;
        double slot = (to - from) / n;
        double span = BandOuter - BandInner;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double a = from + i * slot;
                var p = Polar(BandInner, a);
                if (i == 0) g.BeginFigure(p, true);
                else g.LineTo(p);
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double a = from + i * slot;
                double frac = Math.Clamp(hist[i] / 100.0, 0, 1);
                // 留 6% 的底厚：值为 0 时也留一条细线，看得出这段弧占在哪
                g.LineTo(Polar(BandInner + span * (0.06 + 0.94 * frac), a));
            }
            g.EndFigure(true);
        }

        ctx.DrawGeometry(new SolidColorBrush(color), null, geo);

        // 条纹只铺在这条带子上，底轨保持干净，两截才分得开
        using (ctx.PushGeometryClip(geo))
        {
            foreach (var (sg, col) in stripes)
                ctx.DrawGeometry(new SolidColorBrush(col), null, sg);
        }
    }

    private void BuildArtwork()
    {
        if (_built) return;
        _built = true;

        _railOutL = ArcGeometry(221, 270, 360);
        _railInL = ArcGeometry(183, 270, 360);
        _railOutR = ArcGeometry(221, 90, 180);
        _railInR = ArcGeometry(183, 90, 180);

        BuildStripes(_stripesOrange, 270, 360, OrangeBase, 31);
        BuildStripes(_stripesBlue, 90, 180, BlueBase, 97);
    }

    private static void BuildStripes(List<(StreamGeometry, Color)> target, double from, double to, Color baseColor, double seed)
    {
        var rng = new Lcg(seed);
        const double ri = 189, ro = 215;
        var (bh, bs, bl) = ColorUtil.RgbToHsl(baseColor);

        double a = from + 1.0;
        while (a < to - 1.0)
        {
            double stripeW = 0.2 + rng.Next() * 0.7;
            double gap = 0.25 + rng.Next() * 1.4;
            double a2 = a + stripeW;
            if (a2 > to - 0.5) break;

            double l = Math.Clamp(bl + (rng.Next() - 0.5) * 0.14, 0.30, 0.88);
            double sat = Math.Clamp(bs + (rng.Next() - 0.5) * 0.10, 0.24, 0.96);
            var col = ColorUtil.HslToRgb(bh, sat, l);

            var p1 = Polar(ri, a);
            var p2 = Polar(ro, a);
            var p3 = Polar(ro, a2);
            var p4 = Polar(ri, a2);

            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(p1, true);
                c.LineTo(p2);
                c.LineTo(p3);
                c.LineTo(p4);
                c.EndFigure(true);
            }
            target.Add((geo, col));

            a = a2 + gap;
        }
    }

    private static Point Polar(double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180.0;
        return new Point(CX + r * Math.Cos(a), CY + r * Math.Sin(a));
    }

    private static StreamGeometry ArcGeometry(double r, double startDeg, double endDeg)
    {
        double e = endDeg < startDeg ? endDeg + 360 : endDeg;
        bool large = e - startDeg > 180;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(Polar(r, startDeg), false);
            c.ArcTo(Polar(r, e), new Size(r, r), 0, large, SweepDirection.Clockwise);
            c.EndFigure(false);
        }
        return geo;
    }
}
