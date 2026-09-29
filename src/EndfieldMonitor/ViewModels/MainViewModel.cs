using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] public partial bool IsLive { get; set; }

    [ObservableProperty] public partial bool IsOverviewPage { get; set; } = true;
    [ObservableProperty] public partial bool IsPerfPage { get; set; }
    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial string EditServer { get; set; } = "";
    [ObservableProperty] public partial string EditToken { get; set; } = "";
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

    /// <summary>环上右上角标（橘弧一侧）里的 CPU 读数。</summary>
    [ObservableProperty] public partial string ChipCpu { get; set; } = "-";

    /// <summary>环上左下角标（蓝弧一侧）里的内存读数。</summary>
    [ObservableProperty] public partial string ChipMem { get; set; } = "-";

    // ---- 连接参数来自本地配置（可在设置里改）----
    private readonly AppConfig _config = AppConfig.Load();
    private readonly SnapshotClient _client = new();
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
                if (!IsLive)
                {
                    IsLive = true;
                    SourceText = "实时";
                }
            }
            else if ((DateTime.Now - _lastLiveAt).TotalSeconds > 4 && SourceText != "离线")
            {
                IsLive = false;
                SourceText = "离线";
                ClearToOffline();
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
        }

        var sec = (int)(DateTime.Now - _lastRefresh).TotalSeconds;
        LastRefreshText = sec < 3 ? "刚刚" : $"{sec} 秒前";
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
                    Name = string.IsNullOrWhiteSpace(p.Display) ? (p.Name ?? "?") : p.Display!,
                    Sub = string.IsNullOrWhiteSpace(p.Title) ? (p.Name ?? "") : p.Title!,
                    Icon = IconLibrary.Get(IconLibrary.KeyFor((p.Name ?? "") + " " + (p.Display ?? ""))),
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
                        break;
                    case "gpu":
                        d.Util = s.Gpu.Util;
                        d.Sub = s.Gpu.Ok ? $"利用率 {s.Gpu.Util:0}%" : "未采集";
                        d.Cur1Value = $"{s.Gpu.Util:0}%";
                        d.Cur2Value = GpuMemText(s.Gpu);
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
    public void GoOverview() { IsOverviewPage = true; IsPerfPage = false; }
    public void GoPerf() { IsOverviewPage = false; IsPerfPage = true; }

    public void OpenDetail(DeviceRowViewModel d)
    {
        DetailRows.Clear();
        foreach (var r in d.Detail) DetailRows.Add(r);
        DetailRows.Add(new DetailRowViewModel { Label = d.Cur1Label, Value = d.Cur1Value });
        DetailRows.Add(new DetailRowViewModel { Label = d.Cur2Label, Value = d.Cur2Value });
        IsDetailOpen = true;
    }

    public void CloseDetail() => IsDetailOpen = false;

    // ================= 设置 =================
    public void OpenSettings()
    {
        EditServer = _config.Server;
        EditToken = _config.Token;
        EditParticleIndex = ModeToIndex(_config.ParticleMode);
        EditBatchCloud = _config.BatchCloud;
        EditShowAbsolute = _config.ShowAbsolute;
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
        _config.Save();
        EditServer = _config.Server;
        ApplyConfig();
        IsSettingsOpen = false;
    }

    private void ApplyConfig()
    {
        _client.BaseUrl = "http://" + AppConfig.NormalizeServer(_config.Server);
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

        // 底栏固定给百分比汇总；具体值交给环上那两个角标
        TagCpu = $"CPU {_sysCpu:0}%";
        TagMem = $"MEM {_memPct:0}%";
        TagComp = $"综合 {Composite:F1}%";

        // 环上两个角标：各自贴近自己那条弧（橘=CPU、蓝=内存）
        ChipCpu = ShowAbsolute ? $"{_cpuFreq:0.00} GHz" : $"{_sysCpu:0}%";
        ChipMem = ShowAbsolute ? $"{_memUsed:0.#} GB" : $"{_memPct:0}%";
    }
}

public partial class AppRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Sub { get; set; } = "";
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
