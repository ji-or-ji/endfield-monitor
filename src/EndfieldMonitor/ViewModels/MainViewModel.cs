using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EndfieldMonitor.Models;
using EndfieldMonitor.Services;
using EndfieldMonitor.Utils;

namespace EndfieldMonitor.ViewModels;

/// <summary>
/// 视图模型。综合占用 = CPU×0.4 + 内存×0.6（与原版一致，内存为主）。
/// 数据优先取服务端快照，掉线时回落演示数据。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const double W_CPU = 0.4, W_MEM = 0.6;

    public ObservableCollection<AppRowViewModel> Apps { get; } = new();
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();
    public ObservableCollection<DetailRowViewModel> DetailRows { get; } = new();

    /// <summary>对面是不是增强版（plus）。界面还没接，先把状态立起来。</summary>
    [ObservableProperty] public partial bool IsPlus { get; set; }

    [ObservableProperty] public partial double Composite { get; set; }
    [ObservableProperty] public partial double MemPercent { get; set; }

    // 手写而不是用 [ObservableProperty]：源生成那条属性在运行时绑不到值（同为源生成的
    // MemPercent 却正常），先绕开。
    private double _cpuPercent;
    public double CpuPercent
    {
        get => _cpuPercent;
        set => SetProperty(ref _cpuPercent, value);
    }

    /// <summary>圆环上那两条弧画的是「过去一分钟」的起伏，一秒一格。</summary>
    private const int HistoryLen = 60;
    private readonly double[] _memHist = new double[HistoryLen];
    private readonly double[] _cpuHist = new double[HistoryLen];
    private bool _histSeeded;

    [ObservableProperty] public partial IReadOnlyList<double> MemHistory { get; set; } = Array.Empty<double>();
    [ObservableProperty] public partial IReadOnlyList<double> CpuHistory { get; set; } = Array.Empty<double>();
    [ObservableProperty] public partial string BigNumber { get; set; } = "0";
    [ObservableProperty] public partial string MaxNumber { get; set; } = "100";
    [ObservableProperty] public partial string UptimeText { get; set; } = "00:00:00";
    [ObservableProperty] public partial string UptimeCaption { get; set; } = "已运行";
    [ObservableProperty] public partial string LastRefreshText { get; set; } = "刚刚";
    [ObservableProperty] public partial string TagCpu { get; set; } = "CPU --";
    [ObservableProperty] public partial string TagMem { get; set; } = "MEM --";
    [ObservableProperty] public partial string TagComp { get; set; } = "综合 --";
    [ObservableProperty] public partial string SourceText { get; set; } = "连接中…";
    [ObservableProperty] public partial string TargetText { get; set; } = "当前监控目标";

    /// <summary>主页面左下角显示的设备名：优先用设备记录里的名称，没记录就退回主机名。</summary>
    [ObservableProperty] public partial string CurrentDeviceName { get; set; } = "-";

    /// <summary>同一块的第二行：设备描述。没记录或没写就是空。</summary>
    [ObservableProperty] public partial string CurrentDeviceDesc { get; set; } = "-";

    /// <summary>被控设备名（设备页左半边那行大字）。</summary>
    [ObservableProperty] public partial string DeviceNameText { get; set; } = "-";
    [ObservableProperty] public partial bool IsLive { get; set; }

    [ObservableProperty] public partial bool IsOverviewPage { get; set; } = true;
    [ObservableProperty] public partial bool IsPerfPage { get; set; }

    /// <summary>页三：跨设备监控总览（底栏「切换被控」进来）。</summary>
    [ObservableProperty] public partial bool IsDevicePage { get; set; }

    /// <summary>页四：新建被控（设备页右上角那个图标按钮进来）。</summary>
    [ObservableProperty] public partial bool IsNewDevicePage { get; set; }

    /// <summary>点卡片上的「编辑」弹出的那张卡。</summary>
    [ObservableProperty] public partial bool IsEditDeviceOpen { get; set; }

    [ObservableProperty] public partial string EditDeviceName { get; set; } = "";

    [ObservableProperty] public partial string EditDeviceDesc { get; set; } = "";

    [ObservableProperty] public partial string EditDeviceAddr { get; set; } = "";

    [ObservableProperty] public partial string EditDevicePort { get; set; } = "8898";

    [ObservableProperty] public partial string EditDeviceToken { get; set; } = "";

    /// <summary>新建/编辑卡里的校验提示。空串表示没问题。</summary>
    [ObservableProperty] public partial string DeviceFormError { get; set; } = "";

    /// <summary>正在编辑的那条记录的原始地址，保存时用它定位要覆盖的那条。</summary>
    private string _editDeviceKey = "";

    /// <summary>新建被控页要填的五栏：名称、描述、地址、端口、口令。</summary>
    [ObservableProperty] public partial string NewDeviceName { get; set; } = "";

    [ObservableProperty] public partial string NewDeviceDesc { get; set; } = "";

    [ObservableProperty] public partial string NewDeviceAddr { get; set; } = "";

    [ObservableProperty] public partial string NewDevicePort { get; set; } = "8898";

    [ObservableProperty] public partial string NewDeviceToken { get; set; } = "";

    /// <summary>底栏只在综合占用与设备性能两页显示，设备页收起。</summary>
    [ObservableProperty] public partial bool IsBottomBarVisible { get; set; } = true;

    /// <summary>设备卡片三台及以上时改成滚动列表。</summary>
    [ObservableProperty] public partial bool IsDeviceListScroll { get; set; }

    /// <summary>只有一台：卡片跨满三行，中心落在 1/2。</summary>
    [ObservableProperty] public partial bool IsSingleDevice { get; set; } = true;

    /// <summary>正好两台：两条各占两行，中心落在 1/3 与 2/3。</summary>
    [ObservableProperty] public partial bool HasTwoDevices { get; set; }

    /// <summary>第一、二条卡片。一台/两台时按固定行位摆，不走列表。</summary>
    [ObservableProperty] public partial DeviceCardItem? CardA { get; set; }

    [ObservableProperty] public partial DeviceCardItem? CardB { get; set; }

    /// <summary>三台及以上时的滚动列表数据。</summary>
    public ObservableCollection<DeviceCardItem> DeviceCards { get; } = new();
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial string EditServer { get; set; } = "";
    [ObservableProperty] public partial string EditToken { get; set; } = "";

    /// <summary>设置窗里「服务器地址」那一栏的说明。内容随当前处境变，见 BuildServerHint。</summary>
    [ObservableProperty] public partial string ServerHint { get; set; } = "";
    [ObservableProperty] public partial string ParticleMode { get; set; } = "full";
    [ObservableProperty] public partial int EditParticleIndex { get; set; }

    /// <summary>设置里「中心点云」下拉的选项，顺序与 full / lite / off 一一对应。</summary>
    public string[] ParticleModeOptions { get; } = { "完整（仿终末地）", "精简（省电）", "关闭" };

    private static int ModeToIndex(string mode) => mode switch
    {
        "lite" => 1,
        "off" => 2,
        _ => 0,
    };

    private static string IndexToMode(int index) => index switch
    {
        1 => "lite",
        2 => "off",
        _ => "full",
    };

    /// <summary>窗口是否在前台。由窗口的 Activated / Deactivated 更新，失焦时点云降级。</summary>
    private bool _windowFocused = true;
    public bool WindowFocused
    {
        get => _windowFocused;
        set
        {
            if (SetProperty(ref _windowFocused, value))
            {
                OnPropertyChanged(nameof(CloudIdle));
            }
        }
    }

    /// <summary>窗口不在前台：点云降级成灰块并放慢刷新（off 档本来就停着，不受影响）。</summary>
    public bool CloudIdle => !_windowFocused;

    /// <summary>生效中的批量绘制开关，绑到外圈点云。</summary>
    [ObservableProperty] public partial bool BatchCloud { get; set; } = true;

    /// <summary>设置面板里那个勾选框的编辑值。</summary>
    [ObservableProperty] public partial bool EditBatchCloud { get; set; } = true;

    /// <summary>生效中的「具体数值」开关：开则 CPU 显频率、内存显已用/总量。</summary>
    [ObservableProperty] public partial bool ShowAbsolute { get; set; }

    /// <summary>设置面板里的编辑值。</summary>
    [ObservableProperty] public partial bool EditShowAbsolute { get; set; }

    /// <summary>设置面板里的编辑值：启动时是否检查更新。</summary>
    [ObservableProperty] public partial bool EditAutoUpdate { get; set; }

    /// <summary>检查更新的结果文字。空表示还没查过，那一块就不显示。</summary>
    [ObservableProperty] public partial string UpdateText { get; set; } = "";

    /// <summary>有检查结果或正在下载时，才显示更新那一块。</summary>
    [ObservableProperty] public partial bool ShowUpdate { get; set; }

    /// <summary>确实比本机新，才给发行版页面按钮。</summary>
    [ObservableProperty] public partial bool HasNewer { get; set; }

    /// <summary>新版已下载好，可以替换了。</summary>
    [ObservableProperty] public partial bool UpdateReady { get; set; }

    private string _releasePage = "";
    private string? _downloaded;
    private string _collectorVersion = "";

    /// <summary>拉本机采集端失败的原因（没失败就是空）。连不上时一并说给用户听。</summary>
    private string _localLaunchError = "";

    /// <summary>最近一次拉起本机采集端的时刻。用来区分「正在等它起来」与「真的离线」。</summary>
    private DateTime _localLaunchAt = DateTime.MinValue;

    /// <summary>环上右上角标（橘弧一侧）里的 CPU 读数。</summary>
    [ObservableProperty] public partial string ChipCpu { get; set; } = "-";

    /// <summary>环上左下角标（蓝弧一侧）里的内存读数。</summary>
    [ObservableProperty] public partial string ChipMem { get; set; } = "-";

    // ---- 连接参数来自本地配置（可在设置里改）----
    private readonly AppConfig _config = AppConfig.Load();
    private readonly SnapshotClient _client = new();

    /// <summary>探不到的那些地址。重建卡片时按它决定置不置灰（重建不该把探测结果抹掉）。</summary>
    private readonly HashSet<string> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上次建卡时的设备清单指纹。没变就不重建，免得每秒换一批对象。</summary>
    private string _deviceSignature = "";
    private DateTime _lastLiveAt = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.Now;
    private string _deviceSig = "";
    private string _appSig = "";
    private double _sysCpu, _memPct;
    private double _cpuFreq, _memUsed, _memTotal;

    /// <summary>是否拿到过有效快照。为假时一切数据位显占位符。</summary>
    private bool _hasData;

    public MainViewModel()
    {
        // 起始即离线形态：没连上之前一律显示占位符，不摆演示数据
        ClearToOffline();

        ApplyConfig();
        // 开了开关就在启动时查一次；套件形态会自动把新版下下来
        if (_config.AutoUpdate) _ = CheckUpdateAsync();
        if (!_config.Existed) OpenSettings();

        _ = PollLoop();
    }

    // ================= 轮询服务端 =================
    private async Task PollLoop()
    {
        while (true)
        {
            Snapshot? snap = null;
            try { snap = await _client.FetchAsync(); } catch { }

            if (snap is { Live: true })
            {
                ApplySnapshot(snap);
                _lastLiveAt = DateTime.Now;
                _lastRefresh = DateTime.Now;
                // 识别对面是不是增强版（plus）：它会在快照里声明自己的动手指令。
                // 增强模式的界面还没接，等对着真界面定下加在哪再动。
                IsPlus = snap.Capabilities.Count > 0;
                // 采集端自报的版本，用来判断分布部署时该更新哪一端
                _collectorVersion = snap.Server?.CollectorVersion ?? "";
                if (!IsLive)
                {
                    IsLive = true;
                    SourceText = "实时";
                }
            }
            else if ((DateTime.Now - _lastLiveAt).TotalSeconds > 4)
            {
                // 刚拉起本机采集端时，它要几秒才起来（那个 exe 第一次跑尤其慢），
                // 这段时间别急着报「离线」——否则看起来就像是没拉起来，
                // 使用者会去手动开一个，反而多出一个控制台窗口。
                var waiting = (DateTime.Now - _localLaunchAt).TotalSeconds < 60;
                var text = waiting ? "正在拉起采集端…" : "离线";
                if (SourceText != text)
                {
                    IsLive = false;
                    SourceText = text;
                    ClearToOffline();
                }
            }

            await Task.Delay(1000);
        }
    }

    private void ApplySnapshot(Snapshot s)
    {
        _hasData = true;
        _sysCpu = s.Cpu.Util;
        _memPct = s.Mem.Pct;
        _cpuFreq = s.Cpu.Freq;
        _memUsed = s.Mem.Used;
        _memTotal = s.Mem.Total;
        UpdateOverview();
        ApplyDisksAndNet(s);
        ApplyApps(s.Procs);

        if (s.Server is not null)
        {
            UptimeText = FormatUptime(s.Server.UptimeHours);
            UptimeCaption = "服务器已运行";

            string target = s.Server.Host ?? "服务器";
            if (s.Server.Service is { } svc)
            {
                string name = string.IsNullOrWhiteSpace(svc.Name) ? "服务" : svc.Name!;
                var openPorts = svc.Ports.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                target += svc.Running
                    ? " · " + name + " 在线" + (openPorts.Count > 0 ? " " + string.Join("/", openPorts) : "")
                    : " · " + name + " 离线";
            }
            TargetText = target;
            DeviceNameText = s.Server.Host ?? "服务器";

            var endpoint = AppConfig.NormalizeServer(_config.Server);
            var rec = _config.Devices.Find(d =>
                string.Equals(d.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            CurrentDeviceName = rec is not null ? rec.Label : (s.Server.Host ?? "服务器");
            CurrentDeviceDesc = rec is not null ? rec.Desc : "";

            RebuildDeviceCards();
        }

        var sec = (int)(DateTime.Now - _lastRefresh).TotalSeconds;
        LastRefreshText = sec < 3 ? "刚刚" : $"{sec} 秒前";
    }

    /// <summary>
    /// 路径太长时只留尾巴，前面的目录折成“…”：用来区分程序的是目录名加文件名，
    /// 头部多半是 C:\Program Files 这类公共前缀。完整路径挂在悬停提示里。
    /// </summary>
    private static string ShortenPath(string? exe, int keep = 56)
    {
        if (string.IsNullOrEmpty(exe) || exe.Length <= keep)
        {
            return exe ?? "";
        }
        int cut = exe.Length - keep;
        // 从分隔符之后开始，别切出半个目录名
        int slash = exe.IndexOf('\\', cut);
        if (slash >= 0 && slash < exe.Length - 8)
        {
            cut = slash + 1;
        }
        return "…\\" + exe[cut..];
    }

    private void ApplyApps(List<ProcInfo> procs)
    {
        var top = procs.Take(24).ToList();
        string sig = string.Join(",", top.Select(p => p.Pid));

        if (sig != _appSig)
        {
            _appSig = sig;
            Apps.Clear();
            foreach (var p in top)
            {
                Apps.Add(new AppRowViewModel
                {
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "?" : p.Name!,
                    // 副标题只留给窗口标题；没有就不占位，别把进程名重复一遍
                    Sub = p.Title ?? "",
                    HasTitle = !string.IsNullOrWhiteSpace(p.Title),
                    Icon = IconLibrary.Get(IconLibrary.KeyFor(p.Name ?? "")),
                    // 可执行文件路径：同名进程靠它区分（好几个 chrome、几个 python 之类）
                    Exe = ShortenPath(p.Exe),
                    HasExe = !string.IsNullOrWhiteSpace(p.Exe),
                    Tip = string.IsNullOrWhiteSpace(p.Exe) ? $"PID {p.Pid}" : $"PID {p.Pid} · {p.Exe}",
                    Cpu = p.Cpu,
                    Mem = p.Mem,
                });
            }
        }
        else
        {
            for (int i = 0; i < Apps.Count && i < top.Count; i++)
            {
                Apps[i].Cpu = top[i].Cpu;
                Apps[i].Mem = top[i].Mem;
            }
        }

        double maxCpu = 10, maxMem = 500;
        foreach (var a in Apps)
        {
            maxCpu = Math.Max(maxCpu, a.Cpu);
            maxMem = Math.Max(maxMem, a.Mem);
        }
        foreach (var a in Apps)
        {
            a.CpuBar = Math.Clamp(a.Cpu / maxCpu * 100, 2, 100);
            a.MemBar = Math.Clamp(a.Mem / maxMem * 100, 2, 100);
        }
    }

    private void ApplyDisksAndNet(Snapshot s)
    {
        var keys = new List<string> { "cpu", "gpu", "mem" };
        for (int i = 0; i < s.Disks.Count; i++) keys.Add("disk-" + i);
        keys.Add("net");
        if (s.Battery.Present) keys.Add("battery");
        string sig = string.Join("|", keys);

        if (sig != _deviceSig)
        {
            _deviceSig = sig;
            var old = Devices.ToDictionary(d => d.Key, d => d.History);
            Devices.Clear();

            Devices.Add(new DeviceRowViewModel
            {
                Key = "cpu", Type = "cpu", Name = "处理器",
                Sub = $"{s.Cpu.Threads} 线程 · {s.Cpu.Util:0}% 负载",
                Icon = IconLibrary.Get("cpu"), Util = s.Cpu.Util,
                Cur1Label = "占用", Cur1Value = $"{s.Cpu.Util:0}%",
                Cur2Label = "速度", Cur2Value = $"{s.Cpu.Freq:0.00} GHz",
                Spec = $"基准 {s.Cpu.Base:0.00} GHz",
                Detail =
                {
                    new DetailRowViewModel { Label = "型号", Value = s.Cpu.Name ?? "—" },
                    new DetailRowViewModel { Label = "逻辑处理器", Value = s.Cpu.Threads + " 线程" },
                    new DetailRowViewModel { Label = "基准频率", Value = $"{s.Cpu.Base:0.00} GHz" },
                    new DetailRowViewModel { Label = "实测峰值", Value = CpuPeakText(s.Cpu) },
                },
            });

            Devices.Add(new DeviceRowViewModel
            {
                Key = "gpu", Type = "gpu", Name = s.Gpu.Name ?? "显卡",
                Sub = s.Gpu.Ok ? $"利用率 {s.Gpu.Util:0}%" : "未采集",
                Icon = IconLibrary.Get("gpu"), Util = s.Gpu.Util,
                Cur1Label = "占用", Cur1Value = $"{s.Gpu.Util:0}%",
                Cur2Label = "显存", Cur2Value = GpuMemText(s.Gpu),
                Spec = s.Gpu.Name ?? "—",
                Detail =
                {
                    new DetailRowViewModel { Label = "型号", Value = s.Gpu.Name ?? "—" },
                    new DetailRowViewModel { Label = "利用率", Value = $"{s.Gpu.Util:0}%" },
                    new DetailRowViewModel { Label = "显存占用", Value = s.Gpu.MemUsed is { } gm2 ? $"{gm2:0} MB" : "—" },
                    new DetailRowViewModel { Label = "显存总量", Value = s.Gpu.MemTotal > 0 ? $"{s.Gpu.MemTotal:0} MB" : "核显或驱动未上报" },
                },
            });

            Devices.Add(new DeviceRowViewModel
            {
                Key = "mem", Type = "mem", Name = "内存",
                Sub = $"已用 {s.Mem.Used} / {s.Mem.Total} GB",
                Icon = IconLibrary.Get("mem"), Util = s.Mem.Pct,
                Cur1Label = "占用", Cur1Value = $"{s.Mem.Pct:0}%",
                Cur2Label = "速度", Cur2Value = s.Mem.Speed ?? "—",
                Spec = $"{s.Mem.Total:0.#} GB {s.Mem.Type}".Trim(),
                Detail =
                {
                    new DetailRowViewModel { Label = "容量", Value = s.Mem.Total + " GB" },
                    new DetailRowViewModel { Label = "类型", Value = s.Mem.Type ?? "—" },
                    new DetailRowViewModel { Label = "频率", Value = s.Mem.Speed ?? "—" },
                },
            });

            for (int i = 0; i < s.Disks.Count; i++)
            {
                var d = s.Disks[i];
                Devices.Add(new DeviceRowViewModel
                {
                    Key = "disk-" + i, Type = "disk", Name = d.Name ?? ("磁盘 " + i),
                    Sub = $"已用 {d.Used} / {d.Total} GB",
                    Icon = IconLibrary.Get("disk"), Util = d.Util,
                    Cur1Label = "活动", Cur1Value = $"{d.Util:0}%",
                    Cur2Label = "读写", Cur2Value = d.Rw ?? "0 MB/s",
                    Spec = $"{d.Total:0.#} GB {d.Media}".Trim(),
                    Detail =
                    {
                        new DetailRowViewModel { Label = "型号", Value = d.Model ?? "—" },
                        new DetailRowViewModel { Label = "介质", Value = d.Media ?? "—" },
                        new DetailRowViewModel { Label = "容量", Value = d.Total + " GB" },
                        new DetailRowViewModel { Label = "已用", Value = d.Used + " GB" },
                    },
                });
            }

            Devices.Add(new DeviceRowViewModel
            {
                Key = "net", Type = "net", Name = s.Net.Name ?? "网络",
                Sub = $"链路 {s.Net.Link:0} Mbps",
                Icon = IconLibrary.Get("net"), Util = s.Net.Util,
                Cur1Label = "下行", Cur1Value = $"{s.Net.Down:F1} Mbps",
                Cur2Label = "上行", Cur2Value = $"{s.Net.Up:F1} Mbps",
                Spec = $"{s.Net.Link:0} Mbps",
                Detail =
                {
                    new DetailRowViewModel { Label = "适配器", Value = s.Net.Name ?? "—" },
                    new DetailRowViewModel { Label = "链路速度", Value = s.Net.Link + " Mbps" },
                },
            });

            // 只有笔记本 / 手持设备才有电池，台式机不摆这一行
            if (s.Battery.Present)
            {
                var bat = s.Battery;
                Devices.Add(new DeviceRowViewModel
                {
                    Key = "battery", Type = "battery", Name = "电池",
                    Sub = BatteryStateText(bat),
                    Icon = IconLibrary.Get("battery"),
                    Util = bat.Percent < 0 ? 0 : bat.Percent,
                    Cur1Label = "电量", Cur1Value = bat.Percent < 0 ? "—" : $"{bat.Percent:0}%",
                    Cur2Label = "剩余", Cur2Value = FormatLeft(bat.SecondsLeft),
                    Spec = BatterySpecText(bat),
                    Detail =
                    {
                        new DetailRowViewModel { Label = "电量", Value = bat.Percent < 0 ? "—" : $"{bat.Percent:0}%" },
                        new DetailRowViewModel { Label = "状态", Value = BatteryStateText(bat) },
                        new DetailRowViewModel { Label = "剩余时间", Value = FormatLeft(bat.SecondsLeft) },
                        new DetailRowViewModel { Label = "满充容量", Value = WhText(bat.FullMWh) },
                        new DetailRowViewModel { Label = "设计容量", Value = WhText(bat.DesignMWh) },
                        new DetailRowViewModel { Label = "健康度", Value = bat.HealthPct > 0 ? $"{bat.HealthPct:0.0}%" : "—" },
                    },
                });
            }

            // 重建时把旧走势接回去，避免图表归零
            foreach (var d in Devices)
                if (old.TryGetValue(d.Key, out var hist)) d.AdoptHistory(hist);
        }
        else
        {
            foreach (var d in Devices)
            {
                switch (d.Key)
                {
                    case "cpu":
                        d.Util = s.Cpu.Util;
                        d.Sub = $"{s.Cpu.Threads} 线程 · {s.Cpu.Util:0}% 负载";
                        d.Cur1Value = $"{s.Cpu.Util:0}%";
                        d.Cur2Value = $"{s.Cpu.Freq:0.00} GHz";
                        d.SetDetail("实测峰值", CpuPeakText(s.Cpu));
                        break;
                    case "gpu":
                        d.Util = s.Gpu.Util;
                        d.Sub = s.Gpu.Ok ? $"利用率 {s.Gpu.Util:0}%" : "未采集";
                        d.Cur1Value = $"{s.Gpu.Util:0}%";
                        d.Cur2Value = GpuMemText(s.Gpu);
                        d.SetDetail("利用率", $"{s.Gpu.Util:0}%");
                        d.SetDetail("显存占用", s.Gpu.MemUsed is { } gu2 ? $"{gu2:0} MB" : "—");
                        d.SetDetail("显存总量", s.Gpu.MemTotal > 0 ? $"{s.Gpu.MemTotal:0} MB" : "核显或驱动未上报");
                        break;
                    case "mem":
                        d.Util = s.Mem.Pct;
                        d.Sub = $"已用 {s.Mem.Used} / {s.Mem.Total} GB";
                        d.Cur1Value = $"{s.Mem.Pct:0}%";
                        break;
                    case "net":
                        d.Util = s.Net.Util;
                        d.Cur1Value = $"{s.Net.Down:F1} Mbps";
                        d.Cur2Value = $"{s.Net.Up:F1} Mbps";
                        break;
                    case "battery":
                        d.Util = s.Battery.Percent < 0 ? 0 : s.Battery.Percent;
                        d.Sub = BatteryStateText(s.Battery);
                        d.Cur1Value = s.Battery.Percent < 0 ? "—" : $"{s.Battery.Percent:0}%";
                        d.Cur2Value = FormatLeft(s.Battery.SecondsLeft);
                        d.SetDetail("电量", s.Battery.Percent < 0 ? "—" : $"{s.Battery.Percent:0}%");
                        d.SetDetail("状态", BatteryStateText(s.Battery));
                        d.SetDetail("剩余时间", FormatLeft(s.Battery.SecondsLeft));
                        break;
                    default:
                        if (d.Key.StartsWith("disk-") &&
                            int.TryParse(d.Key.AsSpan(5), out int idx) && idx < s.Disks.Count)
                        {
                            var dd = s.Disks[idx];
                            d.Util = dd.Util;
                            d.Sub = $"已用 {dd.Used} / {dd.Total} GB";
                            d.Cur1Value = $"{dd.Util:0}%";
                            d.Cur2Value = dd.Rw ?? "0 MB/s";
                            d.SetDetail("已用", dd.Used + " GB");
                        }
                        break;
                }
            }
        }

        foreach (var d in Devices) d.PushHistory(d.Util);
    }

    /// <summary>睿频上限在系统里拿不到可靠值，只能报实际观测到的峰值。</summary>
    private static string CpuPeakText(CpuInfo c) =>
        c.Peak > c.Base + 0.01 ? $"{c.Peak:0.00} GHz（运行中观测）" : "尚未观测到超过基准";

    /// <summary>显存：总量读得到时给出“占用 / 总量”。</summary>
    private static string GpuMemText(GpuInfo g)
    {
        bool hasUsed = g.MemUsed is { };
        if (g.MemTotal > 0)
        {
            return hasUsed ? $"{g.MemUsed:0} / {g.MemTotal:0} MB" : $"— / {g.MemTotal:0} MB";
        }
        return hasUsed ? $"{g.MemUsed:0} MB" : "—";
    }

    private static string WhText(double mwh) => mwh > 0 ? $"{mwh / 1000.0:0.0} Wh" : "—";

    /// <summary>电池的“规格”一栏：满充容量。</summary>
    private static string BatterySpecText(BatteryInfo b) => b.FullMWh > 0 ? $"{b.FullMWh / 1000.0:0.0} Wh" : "";

    /// <summary>电池状态的短描述。</summary>
    private static string BatteryStateText(BatteryInfo b)
    {
        if (b.Charging)
        {
            return "充电中";
        }
        return b.OnAC ? "已接电源" : "电池供电";
    }

    /// <summary>剩余时间：秒 -> “1 小时 31 分”。未知时给占位符。</summary>
    private static string FormatLeft(double seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分" : $"{ts.Minutes} 分";
    }

    private static string FormatUptime(double hours)
    {
        var ts = TimeSpan.FromHours(hours);
        int totalHours = (int)ts.TotalHours;
        return $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ================= 离线形态 =================

    /// <summary>
    /// 掉线时不回落演示数据：清掉所有数据，各显示位一律给占位符，
    /// 免得看上去像真的。
    /// </summary>
    private void ClearToOffline()
    {
        _hasData = false;
        Apps.Clear();
        SeedOfflineDevices();

        _sysCpu = 0;
        _memPct = 0;
        _cpuFreq = 0;
        _memUsed = 0;
        _memTotal = 0;

        Composite = 0;
        CpuPercent = 0;
        MemPercent = 0;
        BigNumber = "-";
        UptimeText = "-";
        UptimeCaption = "已运行";
        LastRefreshText = "-";
        TargetText = "-";
        DeviceNameText = "-";
        CurrentDeviceName = "-";
        CurrentDeviceDesc = "-";
        RebuildDeviceCards();

        Array.Clear(_cpuHist, 0, _cpuHist.Length);
        Array.Clear(_memHist, 0, _memHist.Length);
        _histSeeded = false;
        MemHistory = (double[])_memHist.Clone();
        CpuHistory = (double[])_cpuHist.Clone();

        UpdateTags();
    }

    /// <summary>离线时的设备骨架：固定几类，数值全部是占位符。</summary>
    private void SeedOfflineDevices()
    {
        Devices.Clear();

        Devices.Add(new DeviceRowViewModel
        {
            Key = "cpu", Type = "cpu", Name = "处理器", Sub = "-",
            Icon = IconLibrary.Get("cpu"),
            Cur1Label = "占用", Cur1Value = "-", Cur2Label = "速度", Cur2Value = "-",
            Spec = "-",
            Detail =
            {
                new DetailRowViewModel { Label = "型号", Value = "-" },
                new DetailRowViewModel { Label = "逻辑处理器", Value = "-" },
            },
        });

        Devices.Add(new DeviceRowViewModel
        {
            Key = "gpu", Type = "gpu", Name = "显卡", Sub = "-",
            Icon = IconLibrary.Get("gpu"),
            Cur1Label = "占用", Cur1Value = "-", Cur2Label = "显存", Cur2Value = "-",
            Spec = "-",
            Detail =
            {
                new DetailRowViewModel { Label = "型号", Value = "-" },
                new DetailRowViewModel { Label = "显存占用", Value = "-" },
            },
        });

        Devices.Add(new DeviceRowViewModel
        {
            Key = "mem", Type = "mem", Name = "内存", Sub = "-",
            Icon = IconLibrary.Get("mem"),
            Cur1Label = "占用", Cur1Value = "-", Cur2Label = "速度", Cur2Value = "-",
            Spec = "-",
            Detail =
            {
                new DetailRowViewModel { Label = "容量", Value = "-" },
                new DetailRowViewModel { Label = "类型", Value = "-" },
                new DetailRowViewModel { Label = "频率", Value = "-" },
            },
        });

        Devices.Add(new DeviceRowViewModel
        {
            Key = "net", Type = "net", Name = "网络", Sub = "-",
            Icon = IconLibrary.Get("net"),
            Cur1Label = "下行", Cur1Value = "-", Cur2Label = "上行", Cur2Value = "-",
            Spec = "-",
            Detail =
            {
                new DetailRowViewModel { Label = "适配器", Value = "-" },
                new DetailRowViewModel { Label = "链路速度", Value = "-" },
            },
        });
    }

    // ================= 交互 =================
    public void GoOverview() { IsOverviewPage = true; IsPerfPage = false; IsDevicePage = false; IsNewDevicePage = false; IsBottomBarVisible = true; }
    public void GoPerf() { IsOverviewPage = false; IsPerfPage = true; IsDevicePage = false; IsNewDevicePage = false; IsBottomBarVisible = true; }
    public void GoDevicePage() { IsOverviewPage = false; IsPerfPage = false; IsDevicePage = true; IsNewDevicePage = false; IsBottomBarVisible = false; _ = ProbeDevicesAsync(); }
    public void GoNewDevicePage() { IsOverviewPage = false; IsPerfPage = false; IsDevicePage = false; IsNewDevicePage = true; IsBottomBarVisible = false; }

    /// <summary>新建被控：五栏先过一遍检查，过了才写成记录（不再顺手切连接）。</summary>
    public void SaveNewDevice()
    {
        var err = CleanHostField(NewDeviceAddr, out var host, out var portFromHost);
        var errPort = CleanPortField(portFromHost ?? NewDevicePort, out var port);
        if (err.Length == 0) err = errPort;
        if (err.Length > 0) { DeviceFormError = err; return; }

        var rec = new DeviceRecord
        {
            Name = NewDeviceName.Trim(),
            Desc = NewDeviceDesc.Trim(),
            Addr = host.Length == 0 ? "127.0.0.1" : host,   // 地址留空 = 监视本机
            Port = port,
            Token = NewDeviceToken.Trim(),
        };

        DeviceFormError = "";
        UpsertDevice(rec);
        GoDevicePage();
    }

    /// <summary>把 host:port 拆开；没写端口就回默认。地址则可能是空的。</summary>
    private static (string Host, string Port) SplitEndpoint(string? endpoint)
    {
        var s = (endpoint ?? "").Trim();
        var i = s.LastIndexOf(':');
        if (i > 0 && int.TryParse(s[(i + 1)..], out var p)) return (s[..i], p.ToString());
        return (s, "8898");
    }

    /// <summary>
    /// 地址栏的整理与检查：剥掉 http(s):// 前缀与路径尾巴，再把误粘进来的 :端口 摘出去
    /// （端口自成一栏，不摘就会拼成 host:8898:8898）。
    /// 全是数字与点号的当 IP 看，必须四段、每段 0-255——"22"、"191.981" 这类都是拄错了。
    /// 返回提示文字，空串表示没问题。
    /// </summary>
    private static string CleanHostField(string? raw, out string host, out string? portInHost)
    {
        host = AppConfig.NormalizeServer(raw);
        portInHost = null;

        var i = host.LastIndexOf(':');
        if (i > 0 && int.TryParse(host[(i + 1)..], out var p) && p > 0 && p <= 65535)
        {
            portInHost = p.ToString();
            host = host[..i];
        }

        if (host.Length == 0) return "";   // 留空 = 监视本机

        var allDigitsAndDots = true;
        foreach (var c in host)
            if (!char.IsDigit(c) && c != '.') { allDigitsAndDots = false; break; }

        if (allDigitsAndDots)
        {
            var parts = host.Split('.');
            var bad = parts.Length != 4;
            if (!bad)
                foreach (var x in parts)
                    if (x.Length == 0 || x.Length > 3 || !int.TryParse(x, out var v) || v > 255) { bad = true; break; }
            if (bad) return "地址不像 IP：要四段、每段 0-255（例如 192.168.1.10）。只看本机就留空。";
        }
        else
        {
            foreach (var c in host)
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '-') return "地址里有不认得的字符：主机名只能含字母、数字、点、连字符。";
        }

        return "";
    }

    /// <summary>端口栏的检查。留空则用默认 8898。</summary>
    private static string CleanPortField(string? raw, out int port)
    {
        port = 8898;
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (!int.TryParse(s, out var p) || p < 1 || p > 65535) return "端口要在 1-65535 之间。";
        port = p;
        return "";
    }

    /// <summary>按地址端口去重写进配置（同一个地址就覆盖那一条）。</summary>
    private void UpsertDevice(DeviceRecord rec)
    {
        var i = _config.Devices.FindIndex(d =>
            string.Equals(d.Endpoint, rec.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _config.Devices[i] = rec;
        else _config.Devices.Add(rec);
        _config.Save();
    }

    /// <summary>把某台设为当前连接，并落盘。</summary>
    private void ApplyDevice(DeviceRecord rec)
    {
        _config.Server = rec.Endpoint;
        _config.Token = rec.Token;
        EditServer = rec.Endpoint;
        EditToken = rec.Token;
        _config.Save();
    }

    private static int ParsePort(string? raw) =>
        int.TryParse(raw?.Trim(), out var p) && p > 0 && p <= 65535 ? p : 8898;

    /// <summary>
    /// <summary>点卡片上的「编辑」：把这台的五个字段填进弹卡。</summary>
    public void OpenEditDevice(DeviceCardItem item)
    {
        _editDeviceKey = item.Server;
        EditDeviceName = item.Name;
        EditDeviceDesc = item.Desc;
        var (host, port) = SplitEndpoint(item.Server);
        EditDeviceAddr = host;
        EditDevicePort = port;
        EditDeviceToken = item.Token;
        IsEditDeviceOpen = true;
    }

    /// <summary>编辑卡里保存：先过检查，按原地址定位那一条覆盖，随后重探。</summary>
    public void SaveEditDevice()
    {
        var err = CleanHostField(EditDeviceAddr, out var host, out var portFromHost);
        var errPort = CleanPortField(portFromHost ?? EditDevicePort, out var port);
        if (err.Length == 0) err = errPort;
        if (err.Length > 0) { DeviceFormError = err; return; }

        var rec = new DeviceRecord
        {
            Name = EditDeviceName.Trim(),
            Desc = EditDeviceDesc.Trim(),
            Addr = host.Length == 0 ? "127.0.0.1" : host,
            Port = port,
            Token = EditDeviceToken.Trim(),
        };

        DeviceFormError = "";
        var i = _config.Devices.FindIndex(d =>
            string.Equals(d.Endpoint, _editDeviceKey, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _config.Devices[i] = rec;
        else _config.Devices.Add(rec);
        _config.Save();

        IsEditDeviceOpen = false;

        // 改的正是当前连着的那台：把新地址接上去，否则改完还是连旧的（或者连不上的）
        if (string.Equals(_editDeviceKey, AppConfig.NormalizeServer(_config.Server),
                          StringComparison.OrdinalIgnoreCase))
            ApplyDevice(rec);

        RebuildDeviceCards();
        _ = ProbeDevicesAsync();
    }

    public void CloseEditDevice() => IsEditDeviceOpen = false;

    /// <summary>点卡片：把它设为当前被控并回主页。带了地址就真切过去并落盘。</summary>
    public void SwitchToDevice(DeviceCardItem item)
    {
        foreach (var c in DeviceCards)
        {
            var active = ReferenceEquals(c, item);
            if (active) c.Tag = "当前被控";
            else if (c.Tag == "当前被控") c.Tag = "未指定";
            c.IsCurrent = active;
        }

        if (!string.IsNullOrWhiteSpace(item.Server))
        {
            _config.Server = item.Server;
            _config.Token = item.Token;
            EditServer = item.Server;
            EditToken = item.Token;
            _config.Save();
        }

        DeviceNameText = item.Name;
        GoOverview();
    }

    /// 重建设备卡片：第一张是当前被控目标，其余台数由开发钩子 ENF_DEVICES 决定。
    /// 一台跨满三行（中心 1/2），两台各占两行（中心 1/3 与 2/3），三台及以上走滚动列表。
    /// </summary>
    private void RebuildDeviceCards()
    {
        var current = AppConfig.NormalizeServer(_config.Server);

        var sig = current + "||" + DeviceNameText + "||";
        // 指纹要盖住卡片上会显示的每个字段：只算地址的话，改名字会被当成"清单没变"而跳过重建
        foreach (var d in _config.Devices) sig += d.Endpoint + "|" + d.Name + "|" + d.Desc + "|" + d.Token + "||";

        if (sig == _deviceSignature && DeviceCards.Count > 0)
        {
            // 清单没变：只刷新「当前被控」标记，对象不换，置灰过渡也不重启
            foreach (var c in DeviceCards)
            {
                var isCurrent = string.Equals(c.Server, current, StringComparison.OrdinalIgnoreCase);
                c.IsCurrent = isCurrent;
                if (isCurrent) c.Tag = "当前被控";
                else if (c.Tag == "当前被控") c.Tag = "未指定";
            }
            return;
        }
        _deviceSignature = sig;

        DeviceCards.Clear();

        foreach (var d in _config.Devices)
        {
            var isCurrent = string.Equals(d.Endpoint, current, StringComparison.OrdinalIgnoreCase);
            DeviceCards.Add(new DeviceCardItem
            {
                Name = d.Label,
                Tag = isCurrent ? "当前被控" : "未指定",
                IsCurrent = isCurrent,
                Server = d.Endpoint,
                Token = d.Token,
                Desc = d.Desc,
                Reachable = !_unreachable.Contains(d.Endpoint),
            });
        }

        if (DeviceCards.Count == 0)
            DeviceCards.Add(new DeviceCardItem { Name = DeviceNameText, Tag = "当前被控", IsCurrent = true });

        var raw = Environment.GetEnvironmentVariable("ENF_DEVICES");
        var extra = int.TryParse(raw, out var v) && v > 0 ? v : 0;
        for (var i = 0; i < extra; i++)
            DeviceCards.Add(new DeviceCardItem { Name = "设备 " + (i + 1), Tag = "未指定", Reachable = false });

        var n = DeviceCards.Count;
        CardA = DeviceCards[0];
        CardB = n >= 2 ? DeviceCards[1] : null;
        IsSingleDevice = n == 1;
        HasTwoDevices = n == 2;
        IsDeviceListScroll = n >= 3;
    }

    /// <summary>
    /// 对每一台记录发一次短超时探测（800ms）。通不了就把 Reachable 置假，
    /// 卡片会按 Border.card 的过渡缓动置灰。
    /// </summary>
    private async Task ProbeDevicesAsync()
    {
        foreach (var card in DeviceCards)
        {
            // 没地址的那台（当前被控的占位）不去探，保持原样
            if (string.IsNullOrWhiteSpace(card.Server)) continue;

            var ok = false;
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                using var probe = new SnapshotClient { BaseUrl = "http://" + card.Server, Token = card.Token };
                var snap = await probe.FetchAsync(cts.Token);
                ok = snap is not null;
            }
            catch
            {
                ok = false;
            }

            if (ok) _unreachable.Remove(card.Server);
            else _unreachable.Add(card.Server);
            card.Reachable = ok;
        }
    }

    public void OpenDetail(DeviceRowViewModel d)
    {
        // 直接复用这一行自己的详情实例：它们会随快照持续更新，
        // 面板开着的时候也是活的（以前是拷一份，拷完就冻住了）。
        DetailRows.Clear();
        foreach (var r in d.Detail)
        {
            DetailRows.Add(r);
        }
        IsDetailOpen = true;
    }

    public void CloseDetail() => IsDetailOpen = false;

    // ================= 设置 =================
    public void OpenSettings()
    {
        EditServer = _config.Server;
        EditToken = _config.Token;
        ServerHint = BuildServerHint();
        EditParticleIndex = ModeToIndex(_config.ParticleMode);
        EditBatchCloud = _config.BatchCloud;
        EditShowAbsolute = _config.ShowAbsolute;
        EditAutoUpdate = _config.AutoUpdate;
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SaveSettings()
    {
        // 地址先归一：剥掉手填的 http(s):// 与尾巴斜杠，存档和显示都用干净值
        _config.Server = AppConfig.NormalizeServer(EditServer);
        _config.Token = EditToken.Trim();
        _config.ParticleMode = IndexToMode(EditParticleIndex);
        _config.BatchCloud = EditBatchCloud;
        _config.ShowAbsolute = EditShowAbsolute;
        _config.AutoUpdate = EditAutoUpdate;
        _config.Save();
        // 刚打开开关就顺手查一次，不用等下次启动
        if (_config.AutoUpdate) _ = CheckUpdateAsync();
        EditServer = _config.Server;
        ApplyConfig();
        IsSettingsOpen = false;
    }

    /// <summary>
    /// 地址是不是指向本机（留空、localhost、127.0.0.1、::1、0.0.0.0），顺带取出口。
    /// 留空按本机算：那是「我自己这台」的简写。
    /// </summary>
    private static bool IsLoopbackTarget(string server, out int port)
    {
        port = 8898;
        if (string.IsNullOrWhiteSpace(server))
        {
            return true;
        }
        var s = server.Trim();
        var host = s;
        if (s.StartsWith("[", StringComparison.Ordinal))
        {
            // [::1]:8898
            var end = s.IndexOf(']');
            if (end > 0)
            {
                host = s[1..end];
                var rest = s[(end + 1)..];
                if (rest.StartsWith(":", StringComparison.Ordinal)
                    && int.TryParse(rest[1..], out var p6) && p6 > 0)
                {
                    port = p6;
                }
            }
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p) && p > 0)
            {
                host = s[..colon];
                port = p;
            }
        }
        host = host.Trim();
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1"
            || host == "0.0.0.0";
    }

    /// <summary>
    /// 同机自看的自动档：客户端与采集端放在同一个文件夹时（发行版里的 bundle），
    /// 由客户端负责把采集端拉起来，省得让人自己开第二个窗口。
    ///
    /// 本机已经在监听就不重复拉起——可能用户自己开了一个，留着当被监视端给别的
    /// 机器看。客户端退出也不去动它拉起来的那个进程：它可能正被别人看着。
    /// </summary>
    private void EnsureLocalCollector(int port)
    {
        _localLaunchError = "";
        if (IsLocalPortOpen(port))
        {
            return;
        }
        var exe = FindBundledCollector();
        if (exe is null)
        {
            return; // 只拿了客户端的版本，没有采集端可拉
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // zip 不保留执行位，补上
                File.SetUnixFileMode(exe,
                    File.GetUnixFileMode(exe) | UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = $"--port {port}",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            _localLaunchAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            // 拉不起来不中断启动，但把原因留下来：连不上时设置窗会把它显示出来，
            // 否则用户只看见「离线」，无从下手。
            _localLaunchError = ex.Message;
        }
    }

    /// <summary>
    /// 设置窗里地址那栏的说明文字。除了一般的「留空即本机」，连不上时补一句
    /// 当前该怎么做——套件用户最常见的错就是地址里留着别人的 IP。
    /// </summary>
    private string BuildServerHint()
    {
        const string basic = "留空就监视本机（同目录里带着采集端的话会自动拉起来）；要看别的机器，填 地址:端口。";
        if (_localLaunchError.Length > 0)
        {
            return $"本机采集端没能启动：{_localLaunchError}\n{basic}";
        }
        var target = AppConfig.NormalizeServer(_config.Server);
        var bundled = FindBundledCollector() is not null;
        var offlineAwhile = (DateTime.Now - _lastLiveAt).TotalSeconds > 6;
        if (!bundled || offlineAwhile is false || target.Length == 0 || IsLoopbackTarget(target, out _))
        {
            return basic;
        }
        return $"当前连不上 {target}。这个文件夹里带着采集端，把地址清空就能看本机。\n{basic}";
    }

    private static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>找同文件夹里的采集端。优先增强版，但普通版也认。</summary>
    private static string? FindBundledCollector()
    {
        var dir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }
        foreach (var pattern in new[] { "enf-collector-plus-*", "enf-collector-*" })
        {
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                if (IsRunnableHere(f))
                {
                    return f;
                }
            }
        }
        return null;
    }

    /// <summary>Windows 上只认 .exe，其它平台反过来。</summary>
    private static bool IsRunnableHere(string path)
    {
        var isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return OperatingSystem.IsWindows() ? isExe : !isExe;
    }

    /// <summary>
    /// 检查更新。套件形态（同目录带着采集端）会自动下载，分布部署只提示：
    /// 分布部署里采集端在别人机器上，替它做决定不合适。
    /// </summary>
    public async Task CheckUpdateAsync()
    {
        ShowUpdate = true;
        HasNewer = false;
        UpdateReady = false;
        _downloaded = null;
        UpdateText = "正在检查更新…";

        var info = await UpdateChecker.FetchLatestAsync();
        var mine = UpdateChecker.CurrentVersion;
        if (info is null)
        {
            UpdateText = "检查更新失败：拿不到发行版信息（可能没有外网）。";
            return;
        }
        _releasePage = info.PageUrl;

        var clientOld = UpdateChecker.IsNewer(info.Version, mine);
        var collectorOld = _collectorVersion.Length > 0 && UpdateChecker.IsNewer(info.Version, _collectorVersion);
        if (!clientOld && !collectorOld)
        {
            UpdateText = _collectorVersion.Length > 0
                ? $"已是最新（客户端 {mine}，采集端 {_collectorVersion}）。"
                : $"已是最新（{mine}）。";
            return;
        }
        HasNewer = true;

        var who = string.Join("、", new[] { clientOld ? "客户端" : null, collectorOld ? "采集端" : null }
            .Where(x => x is not null));

        if (FindBundledCollector() is null)
        {
            UpdateText = $"发现新版本 {info.Tag}：{who}可以更新。分布部署只作提示，请到发行版页面自行更新。";
            return;
        }

        UpdateText = $"发现新版本 {info.Tag}（{who}）：准备下载…";
        var dir = await SelfUpdate.DownloadAsync(info, new Progress<string>(s => UpdateText = s));
        if (dir is null)
        {
            return; // 失败原因已经写在 UpdateText 里
        }
        _downloaded = dir;
        UpdateReady = true;
        UpdateText = $"新版本 {info.Tag} 已下载好。点「立即更新」会关闭程序、替换文件并自动重启。";

        // ENF_AUTO_APPLY=1 时不等点击，下完直接换。
        // 给「全自动更新」留的口子，也是自动化测试替换那一段的入口。
        if (Environment.GetEnvironmentVariable("ENF_AUTO_APPLY") == "1")
        {
            ApplyUpdate();
        }
    }

    /// <summary>把下好的新版换上去：写一段替换脚本、启动它，然后本程序退出。</summary>
    [RelayCommand]
    private void ApplyUpdate()
    {
        if (_downloaded is null)
        {
            return;
        }
        var dir = AppContext.BaseDirectory;
        var olds = Directory.GetFiles(dir, "EndfieldMonitor-*")
            .Concat(Directory.GetFiles(dir, "enf-collector*"))
            .Where(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (olds.Count == 0)
        {
            UpdateText = "找不到当前版本的文件，无法替换。";
            return;
        }
        if (!SelfUpdate.Apply(_downloaded, olds))
        {
            UpdateText = "启动替换脚本失败，可以手动把 .update 里的文件覆盖过来。";
            return;
        }
        // 脚本正等着这个进程退出
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>打开发行版页面。</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_releasePage.Length == 0)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_releasePage) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }

    private void ApplyConfig()
    {
        // 地址留空就默认连本机：同机自看是最常见的用法，
        // 不该逼人先去想明白“我自己这台机器的地址是什么”。
        // 填了地址就按填的走（连别的机器）。
        var server = AppConfig.NormalizeServer(_config.Server);
        if (server.Length == 0)
        {
            server = "127.0.0.1:8898";
        }
        // 目标是本机就顺手把同目录里的采集端拉起来，不让人自己开第二个窗口。
        // 以前这里只在“地址留空”时才拉：使用者在向导里保留了预填的地址，
        // 或者老老实实填了 127.0.0.1，都会漏掉拉起这一步。
        if (IsLoopbackTarget(server, out var localPort))
        {
            EnsureLocalCollector(localPort);
        }
        _client.BaseUrl = "http://" + server;
        _client.Token = _config.Token;
        ParticleMode = _config.ParticleMode;
        BatchCloud = _config.BatchCloud;
        ShowAbsolute = _config.ShowAbsolute;
        UpdateTags();
        _deviceSig = "";
        _appSig = "";
        // 必须重置在线标记：否则下一轮拉取成功时会被「已经在线」挡住，
        // 状态文字就永远停在“连接中…”。同时把计时归零，
        // 让连不上时能在 4 秒后才落到“离线”。
        _lastLiveAt = DateTime.Now;
        IsLive = false;
        SourceText = "连接中…";
    }

    public void RefreshNow() => _lastRefresh = DateTime.Now;

    private void UpdateBars()
    {
        double maxCpu = 10, maxMem = 500;
        foreach (var a in Apps)
        {
            maxCpu = Math.Max(maxCpu, a.Cpu);
            maxMem = Math.Max(maxMem, a.Mem);
        }
        foreach (var a in Apps)
        {
            a.CpuBar = Math.Clamp(a.Cpu / maxCpu * 100, 2, 100);
            a.MemBar = Math.Clamp(a.Mem / maxMem * 100, 2, 100);
        }
    }

    private void UpdateOverview()
    {
        Composite = Math.Clamp(_sysCpu * W_CPU + _memPct * W_MEM, 0, 100);
        BigNumber = Math.Round(Composite).ToString("0");
        CpuPercent = _sysCpu;
        MemPercent = _memPct;

        // 弧上的历史：右移一格，把当前值推到末位（最末一格就是当前读数）
        if (!_histSeeded)
        {
            // 刚启动时缓冲全是 0，画出来前半小时是空的；先用当前值铺平
            _histSeeded = true;
            for (int i = 0; i < HistoryLen; i++)
            {
                _memHist[i] = _memPct;
                _cpuHist[i] = _sysCpu;
            }
        }
        Array.Copy(_memHist, 1, _memHist, 0, HistoryLen - 1);
        _memHist[HistoryLen - 1] = _memPct;
        Array.Copy(_cpuHist, 1, _cpuHist, 0, HistoryLen - 1);
        _cpuHist[HistoryLen - 1] = _sysCpu;
        MemHistory = (double[])_memHist.Clone();
        CpuHistory = (double[])_cpuHist.Clone();
        UpdateTags();
    }

    /// <summary>
    /// 底部的 CPU / 内存 / 综合标签。切到具体数值时，综合仍保留占比，
    /// 免得把“整体如何”这一眼丢掉了。
    /// </summary>
    private void UpdateTags()
    {
        if (!_hasData)
        {
            TagCpu = "CPU -";
            TagMem = "MEM -";
            TagComp = "综合 -";
            ChipCpu = "-";
            ChipMem = "-";
            return;
        }

        // 环上与底栏永远一正一反：环上摆具体值，底栏就摆百分比，反之亦然
        ChipCpu = ShowAbsolute ? $"{_cpuFreq:0.00} GHz" : $"{_sysCpu:0}%";
        ChipMem = ShowAbsolute ? $"{_memUsed:0.#} GB" : $"{_memPct:0}%";

        TagCpu = ShowAbsolute ? $"CPU {_sysCpu:0}%" : $"CPU {_cpuFreq:0.00} GHz";
        TagMem = ShowAbsolute ? $"MEM {_memPct:0}%" : $"MEM {_memUsed:0.#} / {_memTotal:0.#} GB";
        TagComp = $"综合 {Composite:F1}%";
    }
}

public partial class AppRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Sub { get; set; } = "";
    [ObservableProperty] public partial bool HasTitle { get; set; }
    [ObservableProperty] public partial string Exe { get; set; } = "";
    [ObservableProperty] public partial bool HasExe { get; set; }
    [ObservableProperty] public partial string Tip { get; set; } = "";
    [ObservableProperty] public partial Geometry Icon { get; set; } = Geometry.Parse("");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    public partial double Cpu { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemText))]
    public partial double Mem { get; set; }

    [ObservableProperty] public partial double CpuBar { get; set; }
    [ObservableProperty] public partial double MemBar { get; set; }

    public string CpuText => Cpu.ToString("F1");
    public string MemText => Mem.ToString("0");
}
