using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
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

    // ---- 连接参数来自本地配置（可在设置里改）----
    private readonly AppConfig _config = AppConfig.Load();
    private readonly SnapshotClient _client = new();
    private readonly Random _rng = new();
    private DateTime _lastLiveAt = DateTime.MinValue;
    private DateTime _lastRefresh = DateTime.Now;
    private string _deviceSig = "";
    private string _appSig = "";
    private double _sysCpu = 32, _memPct = 60;
    private readonly DateTime _demoStart = DateTime.Now;

    private static readonly (string Name, string Sub, string Icon, double Cpu, double Mem)[] DemoApps =
    {
        ("Microsoft Edge",     "18 个标签页",       "browser",  16.4, 2840),
        ("Visual Studio Code", "工作区: zmd-usage", "code",     11.1, 1720),
        ("Steam",              "后台待机",          "video",    14.8, 1180),
        ("Discord",            "3 个服务器",        "chat",      7.6,  620),
        ("MySQL",              "本地数据库服务",    "db",        5.9,  940),
        ("Spotify",            "正在播放",          "music",     2.1,  310),
        ("Windows Terminal",   "pwsh × 2",          "terminal",  0.9,   86),
        ("File Explorer",      "此电脑",            "folder",    1.2,  120),
    };

    public MainViewModel()
    {
        SeedDemoApps();
        SeedDemoDevices();
        foreach (var d in Devices) d.SeedHistory(_rng);
        UpdateBars();
        UpdateOverview();

        ApplyConfig();
        if (!_config.Existed) OpenSettings();

        // 演示数据只在离线时继续抖动
        var demo = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => { if (!IsLive) TickDemo(); });
        demo.Start();

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
            else if (IsLive && (DateTime.Now - _lastLiveAt).TotalSeconds > 4)
            {
                IsLive = false;
                SourceText = "离线";
            }

            await Task.Delay(1000);
        }
    }

    private void ApplySnapshot(Snapshot s)
    {
        _sysCpu = s.Cpu.Util;
        _memPct = s.Mem.Pct;
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
        var keys = new List<string> { "cpu", "mem" };
        for (int i = 0; i < s.Disks.Count; i++) keys.Add("disk-" + i);
        keys.Add("net");
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
                Spec = $"最高 {s.Cpu.Max:0.00} GHz",
                Detail =
                {
                    new DetailRowViewModel { Label = "型号", Value = s.Cpu.Name ?? "—" },
                    new DetailRowViewModel { Label = "逻辑处理器", Value = s.Cpu.Threads + " 线程" },
                    new DetailRowViewModel { Label = "基准频率", Value = s.Cpu.Base + " GHz" },
                    new DetailRowViewModel { Label = "最高频率", Value = s.Cpu.Max + " GHz" },
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
                    Cur1Label = "占用", Cur1Value = $"{d.Util:0}%",
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

    private static string FormatUptime(double hours)
    {
        var ts = TimeSpan.FromHours(hours);
        int totalHours = (int)ts.TotalHours;
        return $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ================= 演示回落 =================
    private void TickDemo()
    {
        foreach (var a in Apps)
        {
            a.Cpu = Math.Clamp(a.Cpu + (_rng.NextDouble() - 0.5) * 8, 0, 42);
            a.Mem = Math.Clamp(a.Mem + (_rng.NextDouble() - 0.5) * 300, 60, 4200);
        }
        UpdateBars();

        _sysCpu = Math.Clamp(Apps.Sum(x => x.Cpu) * 0.42 + (_rng.NextDouble() - 0.5) * 4, 3, 98);
        _memPct = Math.Clamp(_memPct + (_rng.NextDouble() - 0.5) * 3, 20, 92);
        UpdateOverview();

        foreach (var d in Devices)
        {
            double amp = d.Type == "disk" ? 4 : 9;
            d.Util = Math.Clamp(d.Util + (_rng.NextDouble() - 0.5) * amp, 1, 97);
            d.PushHistory(d.Util);
        }

        var up = DateTime.Now - _demoStart;
        UptimeText = $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
        UptimeCaption = "已运行";

        var sec = (int)(DateTime.Now - _lastRefresh).TotalSeconds;
        LastRefreshText = sec < 3 ? "刚刚" : $"{sec} 秒前";
    }

    // ================= 演示数据种子 =================
    private void SeedDemoApps()
    {
        foreach (var (name, sub, icon, cpu, mem) in DemoApps)
        {
            Apps.Add(new AppRowViewModel
            {
                Name = name, Sub = sub, Icon = IconLibrary.Get(icon),
                Cpu = cpu, Mem = mem,
            });
        }
    }

    private void SeedDemoDevices()
    {
        Devices.Add(new DeviceRowViewModel
        {
            Key = "cpu", Type = "cpu", Name = "处理器", Sub = "16 线程 · x64 架构",
            Icon = IconLibrary.Get("cpu"), Util = 34,
            Cur1Label = "占用", Cur1Value = "34%", Cur2Label = "速度", Cur2Value = "3.62 GHz",
            Spec = "最高 4.20 GHz",
        });
        Devices.Add(new DeviceRowViewModel
        {
            Key = "mem", Type = "mem", Name = "内存", Sub = "已用 9.8 / 16.0 GB",
            Icon = IconLibrary.Get("mem"), Util = 61,
            Cur1Label = "占用", Cur1Value = "61%", Cur2Label = "速度", Cur2Value = "3200 MT/s",
            Spec = "16 GB DDR4",
        });
        Devices.Add(new DeviceRowViewModel
        {
            Key = "disk-0", Type = "disk", Name = "磁盘 0 (C:)", Sub = "已用 486 / 1024 GB",
            Icon = IconLibrary.Get("disk"), Util = 12,
            Cur1Label = "占用", Cur1Value = "12%", Cur2Label = "读写", Cur2Value = "126 MB/s",
            Spec = "1024 GB NVMe",
        });
        Devices.Add(new DeviceRowViewModel
        {
            Key = "net", Type = "net", Name = "网络", Sub = "以太网 · 1000 Mbps",
            Icon = IconLibrary.Get("net"), Util = 18,
            Cur1Label = "下行", Cur1Value = "12.4 Mbps", Cur2Label = "上行", Cur2Value = "3.1 Mbps",
            Spec = "1000 Mbps",
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
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SaveSettings()
    {
        _config.Server = EditServer.Trim();
        _config.Token = EditToken.Trim();
        _config.Save();
        ApplyConfig();
        IsSettingsOpen = false;
    }

    private void ApplyConfig()
    {
        _client.BaseUrl = "http://" + _config.Server;
        _client.Token = _config.Token;
        _deviceSig = "";
        _appSig = "";
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
        TagCpu = $"CPU {_sysCpu:0}%";
        TagMem = $"MEM {_memPct:0}%";
        TagComp = $"综合 {Composite:F1}%";
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
