using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EndfieldMonitor.ViewModels;

namespace EndfieldMonitor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 无边框窗口：拖动由顶栏承担
        if (this.FindControl<Border>("TopBar") is { } topBar)
        {
            topBar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
        }

        if (this.FindControl<Button>("CloseButton") is { } close)
            close.Click += (_, _) => Close();

        if (this.FindControl<Button>("RefreshButton") is { } refresh)
            refresh.Click += (_, _) => (DataContext as MainViewModel)?.RefreshNow();

        // ---- 设置 ----
        if (this.FindControl<Button>("SettingsButton") is { } settingsOpen)
            settingsOpen.Click += (_, _) => (DataContext as MainViewModel)?.OpenSettings();

        if (this.FindControl<Button>("SettingsSaveButton") is { } settingsSave)
            settingsSave.Click += (_, _) => (DataContext as MainViewModel)?.SaveSettings();

        if (this.FindControl<Button>("SettingsCancelButton") is { } settingsCancel)
            settingsCancel.Click += (_, _) => (DataContext as MainViewModel)?.CloseSettings();

        if (this.FindControl<Button>("SettingsCloseButton") is { } settingsClose)
            settingsClose.Click += (_, _) => (DataContext as MainViewModel)?.CloseSettings();

        if (this.FindControl<Border>("SettingsOverlay") is { } settingsOverlay)
        {
            settingsOverlay.PointerPressed += (s, e) =>
            {
                if (ReferenceEquals(e.Source, settingsOverlay))
                    (DataContext as MainViewModel)?.CloseSettings();
            };
        }

        // ---- 新建被控（设备页右上角图标按钮进来） ----
        if (this.FindControl<Button>("DeviceConfigButton") is { } deviceConfig)
            deviceConfig.Click += (_, _) => (DataContext as MainViewModel)?.GoNewDevicePage();

        if (this.FindControl<Button>("NewDeviceSaveButton") is { } newDeviceSave)
            newDeviceSave.Click += (_, _) => (DataContext as MainViewModel)?.SaveNewDevice();

        if (this.FindControl<Button>("NewDeviceCancelButton") is { } newDeviceCancel)
            newDeviceCancel.Click += (_, _) => (DataContext as MainViewModel)?.GoDevicePage();

        if (this.FindControl<Button>("NewDeviceBackButton") is { } newDeviceBack)
            newDeviceBack.Click += (_, _) => (DataContext as MainViewModel)?.GoDevicePage();

        // ---- 跨设备监控总览（整页：底栏「切换被控」进，面板右上角的✕退回总览） ----
        if (this.FindControl<Button>("SwitchDeviceButton") is { } switchDevice)
            switchDevice.Click += (_, _) => (DataContext as MainViewModel)?.GoDevicePage();

        if (this.FindControl<Button>("DeviceOverviewCloseButton") is { } deviceOverviewBack)
            deviceOverviewBack.Click += (_, _) => (DataContext as MainViewModel)?.GoOverview();

        // ---- 型号详情 ----
        if (this.FindControl<Button>("DetailCloseButton") is { } detailClose)
            detailClose.Click += (_, _) => (DataContext as MainViewModel)?.CloseDetail();

        if (this.FindControl<Border>("DetailOverlay") is { } overlay)
        {
            overlay.PointerPressed += (s, e) =>
            {
                if (ReferenceEquals(e.Source, overlay))
                    (DataContext as MainViewModel)?.CloseDetail();
            };
        }

        // 窗口不在前台时让点云降级：后台不该继续烧 CPU 画满屏动画
        Activated += (_, _) => (DataContext as MainViewModel)?.WindowFocused = true;
        Deactivated += (_, _) => (DataContext as MainViewModel)?.WindowFocused = false;

        // 开发用：ENF_FOCUS=0/1 把焦点状态钉死，方便分别验证正常与降级两条绘制路径
        string? force = Environment.GetEnvironmentVariable("ENF_FOCUS");
        if (force is "0" or "1")
        {
            bool v = force == "1";
            void Apply(object? _, EventArgs __) => (DataContext as MainViewModel)?.WindowFocused = v;
            Activated += Apply;
            Deactivated += Apply;
        }
    }

    /// <summary>点设备页左侧那一条：把被控切到它并回主页。按钮在数据模板里，只能走事件，不能 FindControl。</summary>
    private void OnDeviceCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceCardItem item } && DataContext is MainViewModel vm)
            vm.SwitchToDevice(item);
    }

    private void OnNavOverview(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.GoOverview();

    private void OnNavPerf(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.GoPerf();

    private void OnDeviceIconClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceRowViewModel d } && DataContext is MainViewModel vm)
            vm.OpenDetail(d);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 开发用：可指定初始页面对照截图
        if (DataContext is MainViewModel vm)
        {
            if (Environment.GetEnvironmentVariable("ENF_PAGE") == "perf") vm.GoPerf();
            if (Environment.GetEnvironmentVariable("ENF_SETTINGS") == "1") vm.OpenSettings();
            if (Environment.GetEnvironmentVariable("ENF_DEVOVERVIEW") == "1") vm.GoDevicePage();

            // 开发用：ENF_DETAIL=<设备 Key> 打开某个设备的详情面板（等首帧数据到了再开）
            string? detailKey = Environment.GetEnvironmentVariable("ENF_DETAIL");
            if (!string.IsNullOrWhiteSpace(detailKey))
            {
                DispatcherTimer.RunOnce(() =>
                {
                    foreach (var dev in vm.Devices)
                    {
                        if (dev.Key == detailKey)
                        {
                            vm.OpenDetail(dev);
                            break;
                        }
                    }
                }, TimeSpan.FromMilliseconds(2600));
            }
        }

        // 开发用离屏截图：设了 ENF_SHOT 环境变量就渲染一张 PNG 后退出（方便无人工介入时验证渲染）
        string? shot = Environment.GetEnvironmentVariable("ENF_SHOT");
        if (string.IsNullOrWhiteSpace(shot)) return;

        // 开发用：截图延时可通过 ENF_SHOT_DELAY（毫秒）指定，
        // 方便在不同动画相位各拍一张做对比。
        int delayMs = 3000;
        if (int.TryParse(Environment.GetEnvironmentVariable("ENF_SHOT_DELAY"), out int d) && d > 0)
            delayMs = d;

        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                int w = Math.Max(1, (int)ClientSize.Width);
                int h = Math.Max(1, (int)ClientSize.Height);
                using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                rtb.Render(this);
                rtb.Save(shot, PngBitmapEncoderOptions.Default);
            }
            catch
            {
                // 截图失败不影响正常启动路径
            }
            Close();
        }, TimeSpan.FromMilliseconds(delayMs));
    }
}
