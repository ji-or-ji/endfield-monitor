using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EndfieldMonitor.Utils;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 复刻原版的「电量式」系统占用环。
/// 结构（从外到内）：装饰弧内外描边 → 左橙右蓝两条装饰弧 → 楔形细条纹 →
/// 内盘底色 → 灰衬环 → 淡黄轨道 → 亮黄进度弧（12 点起顺时针，圆头带光晕）。
/// 全部按 470×470 的设计尺寸生成，绘制时等比缩放到控件实际大小。
/// 坐标约定与原版一致：0° 在正上方，顺时针为正。
/// </summary>
public sealed class RingGauge : Control
{
    private const double CX = 235, CY = 235, Design = 470;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value));

    /// <summary>综合占用百分比，0~100。</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static RingGauge()
    {
        AffectsRender<RingGauge>(ValueProperty);
    }

    private static readonly Color RailColor = Color.Parse("#CFCFC9");
    private static readonly Color InnerDiscColor = Color.Parse("#EDEDEA");
    private static readonly Color MainBgColor = Color.Parse("#D3D3CE");
    private static readonly Color MainRailColor = Color.Parse("#F2EDC4");
    private static readonly Color ArcColor = Color.Parse("#FFE23D");
    private static readonly Color OrangeBase = Color.Parse("#ECB063");
    private static readonly Color BlueBase = Color.Parse("#7FB2CC");

    private StreamGeometry? _baseOrange, _baseBlue;
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

        // 三重变换：设计坐标 → 原点 → 等比缩放 → 控件中心
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

            // 2) 装饰弧基底（半径 202，粗 28）
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(OrangeBase), 28), _baseOrange!);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(BlueBase), 28), _baseBlue!);

            // 3) 楔形细条纹（半径 189 → 215）
            foreach (var (geo, col) in _stripesOrange)
                ctx.DrawGeometry(new SolidColorBrush(col), null, geo);
            foreach (var (geo, col) in _stripesBlue)
                ctx.DrawGeometry(new SolidColorBrush(col), null, geo);

            // 4) 内盘底色
            ctx.DrawEllipse(new SolidColorBrush(InnerDiscColor), null, new Point(CX, CY), 126, 126);

            // 5) 灰衬环 + 淡黄轨道
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(MainBgColor), 24), new Point(CX, CY), 151, 151);
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(MainRailColor), 18), new Point(CX, CY), 150, 150);

            // 6) 亮黄进度弧：0.5°~359.5° 之间，圆头 + 两层光晕
            double sweep = Math.Clamp(Value / 100.0 * 360.0, 0.5, 359.5);
            var progress = ArcGeometry(150, 0, sweep);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ArcColor, 0.16), 27) { LineCap = PenLineCap.Round }, progress);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ArcColor, 0.28), 22) { LineCap = PenLineCap.Round }, progress);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(ArcColor), 18) { LineCap = PenLineCap.Round }, progress);
        }
    }

    private void BuildArtwork()
    {
        if (_built) return;
        _built = true;

        _baseOrange = ArcGeometry(202, 270, 360);
        _baseBlue   = ArcGeometry(202, 90, 180);
        _railOutL   = ArcGeometry(221, 270, 360);
        _railInL    = ArcGeometry(183, 270, 360);
        _railOutR   = ArcGeometry(221, 90, 180);
        _railInR    = ArcGeometry(183, 90, 180);

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
            double stripeW = 0.2 + rng.Next() * 0.7;   // 切向宽度 0.2°~0.9°
            double gap = 0.25 + rng.Next() * 1.4;      // 切向间距
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
