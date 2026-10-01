using CommunityToolkit.Mvvm.ComponentModel;

namespace EndfieldMonitor.ViewModels;

/// <summary>
/// 设备页里的一条：左边是描述文字，右边是卡片条，两者同处一条，所以天然一起上下。
/// 点左侧这块即把被控切到这一台并回主页。
/// </summary>
public partial class DeviceCardItem : ViewModelBase
{
    /// <summary>设备名。左上角大字与卡片里的小字用的是同一个。</summary>
    [ObservableProperty] public partial string Name { get; set; } = "-";

    /// <summary>卡片里的状态文字：当前被控 / 未指定。</summary>
    [ObservableProperty] public partial string Tag { get; set; } = "";

    /// <summary>是不是当前被控的那一台。</summary>
    [ObservableProperty] public partial bool IsCurrent { get; set; }

    /// <summary>这一台的地址与口令。多设备配置做完后由它驱动真正的切换，现在还是空的。</summary>
    public string Server { get; init; } = "";

    public string Token { get; init; } = "";
}
