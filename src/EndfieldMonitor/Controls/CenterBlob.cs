using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EndfieldMonitor.Controls;

/// <summary>
/// 圆环正中的那团灰块。形状与配色由 CloudBlob 统一提供，
/// 这里只负责自己的时间轴与刷新节奏。
///
/// Idle：窗口不在前台。
///   off  档 —— 本来就不画
///   lite 档 —— 画完一帧就停，画面定格
///   full 档 —— 放慢到约 7fps：开销几乎归零，但还留着一口气
/// </summary>
public sealed class CenterBlob : Control
{
    /// <summary>失焦状态下的刷新间隔。实测每秒 60 次重绘本身才是开销大头，降频即省。</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(150);

    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<CenterBlob, string>(nameof(Mode), "full");

    public static readonly StyledProperty<bool> IdleProperty =
        AvaloniaProperty.Register<CenterBlob, bool>(nameof(Idle));

    /// <summary>full / lite / off</summary>
    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>窗口不在前台时为真。</summary>
    public bool Idle
    {
        get => GetValue(IdleProperty);
        set => SetValue(IdleProperty, value);
    }

    static CenterBlob()
    {
        ModeProperty.Changed.AddClassHandler<CenterBlob>((c, _) => c.SyncTimer());
        IdleProperty.Changed.AddClassHandler<CenterBlob>((c, _) => c.SyncTimer());
    }

    private DispatcherTimer? _timer;
    private double _t;
    private bool _off;

    private int Steps => Mode == "lite" ? 120 : 260;

    private static TimeSpan ActiveInterval(string mode) =>
        mode == "lite" ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromMilliseconds(16);

    public override void Render(DrawingContext ctx)
    {
        if (_off)
        {
            return;
        }
        CloudBlob.Draw(ctx, new Rect(Bounds.Size), _t, 0.50, Steps);
    }

    private void SyncTimer()
    {
        _off = Mode == "off";

        // 失焦 + 精简：画一帧就停
        if (_off || (Idle && Mode == "lite"))
        {
            _timer?.Stop();
            // 停下前把最后一帧画出来，免得留下半张旧画面
            InvalidateVisual();
            return;
        }

        var interval = Idle ? IdleInterval : ActiveInterval(Mode);
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
