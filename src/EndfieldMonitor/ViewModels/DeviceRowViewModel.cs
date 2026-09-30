using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EndfieldMonitor.ViewModels;

/// <summary>页二「设备性能」里的一行：设备信息 / 走势 / 当前数据 / 规格。</summary>
public partial class DeviceRowViewModel : ObservableObject
{
    public const int HistoryLen = 180;

    /// <summary>行的稳定标识，用于跨帧保留走势历史。</summary>
    public string Key { get; init; } = "";

    /// <summary>cpu / gpu / mem / disk / net。</summary>
    public string Type { get; init; } = "";

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Sub { get; set; } = "";
    [ObservableProperty] public partial Geometry Icon { get; set; } = Geometry.Parse("");
    [ObservableProperty] public partial string Cur1Label { get; set; } = "";
    [ObservableProperty] public partial string Cur1Value { get; set; } = "";
    [ObservableProperty] public partial string Cur2Label { get; set; } = "";
    [ObservableProperty] public partial string Cur2Value { get; set; } = "";
    [ObservableProperty] public partial string Spec { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double> History { get; set; } = Array.Empty<double>();

    /// <summary>点开图标后弹出的型号参数。</summary>
    public List<DetailRowViewModel> Detail { get; init; } = new();

    /// <summary>当前占用率（0~100），走势图与演示抖动用。</summary>
    public double Util { get; set; }

    private readonly double[] _buf = new double[HistoryLen];

    public DeviceRowViewModel()
    {
        History = _buf;
    }

    public void PushHistory(double util)
    {
        Array.Copy(_buf, 1, _buf, 0, HistoryLen - 1);
        _buf[HistoryLen - 1] = util;
        History = (double[])_buf.Clone();
    }

    /// <summary>详情面板展示的是同一批实例，改它们的值就能实时刷新。</summary>
    public void SetDetail(string label, string value)
    {
        foreach (var r in Detail)
        {
            if (r.Label == label)
            {
                r.Value = value;
                return;
            }
        }
    }

    /// <summary>重建行时把上一份走势接过来，避免图表归零。</summary>
    public void AdoptHistory(IReadOnlyList<double>? old)
    {
        if (old is null || old.Count == 0) return;
        int n = Math.Min(old.Count, HistoryLen);
        for (int i = 0; i < n; i++)
            _buf[HistoryLen - n + i] = old[old.Count - n + i];
        History = (double[])_buf.Clone();
    }
}

public partial class DetailRowViewModel : ObservableObject
{
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
}
