using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 设备行里的占用率走势图：灰底上密排细黄柱，顶部留一条灰带（柱子活动区占下方 84%）。
/// 对应原版的 drawChart()。
/// </summary>
public sealed class SparkChart : Control
{
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#A9A9A5"));

    /// <summary>柱子的 5 档透明度，避免每帧新建画刷。</summary>
    private static readonly IBrush[] Bars = BuildBars();

    private static IBrush[] BuildBars()
    {
        var arr = new IBrush[5];
        for (int k = 0; k < 5; k++)
        {
            byte a = (byte)((0.80 + k * 0.05) * 255);
            arr[k] = new SolidColorBrush(Color.FromArgb(a, 0xD5, 0xC7, 0x3A));
        }
        return arr;
    }

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparkChart, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>占用率历史序列，元素范围 0~100。</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    static SparkChart()
    {
        AffectsRender<SparkChart>(ValuesProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 1 || h <= 1) return;

        ctx.FillRectangle(Backdrop, new Rect(0, 0, w, h));

        var vals = Values;
        if (vals is null || vals.Count == 0) return;

        double bandH = Math.Round(h * 0.16);
        double zoneH = h - bandH;
        int n = vals.Count;
        double bw = w / n;

        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(vals[i], 0, 100) / 100.0;
            double bh = Math.Max(2, v * zoneH);
            double x = i * bw;
            double barW = Math.Max(1.5, bw - 0.6);
            ctx.FillRectangle(Bars[(i * 7) % 5], new Rect(x, h - bh, barW, bh));
        }
    }
}
