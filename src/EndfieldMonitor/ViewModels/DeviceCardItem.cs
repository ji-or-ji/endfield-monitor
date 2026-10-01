namespace EndfieldMonitor.ViewModels;

/// <summary>
/// 设备页里的一条：左边是描述文字，右边是卡片条，两者同处一条，所以天然一起上下。
/// Row/RowSpan 是它在页面三行网格里的落位，由数量决定，见 MainViewModel.LayoutDeviceCards。
/// </summary>
public class DeviceCardItem
{
    /// <summary>设备名。左上角大字与卡片里的小字用的是同一个。</summary>
    public string Name { get; init; } = "-";

    /// <summary>卡片里的状态小签：当前被控 / 未指定。</summary>
    public string Tag { get; init; } = "";

    /// <summary>是不是当前被控的那一台（当前只用来区分签的颜色，先留着）。</summary>
    public bool IsCurrent { get; init; }
}
