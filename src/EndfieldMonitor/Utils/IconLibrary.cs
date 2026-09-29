using System.Collections.Generic;
using Avalonia.Media;

namespace EndfieldMonitor.Utils;

/// <summary>
/// 原版的矢量图标库（24×24 描边路径）。
/// 原版是内联 SVG 片段，这里等价合并成单条 path data，交给 Avalonia 的 Path 描边绘制。
/// </summary>
public static class IconLibrary
{
    private static readonly Dictionary<string, string> Raw = new()
    {
        ["cpu"] = "M6 6H18V18H6Z M10 10H14V14H10Z M9 2V6 M15 2V6 M9 18V22 M15 18V22 M2 9H6 M2 15H6 M18 9H22 M18 15H22",
        ["gpu"] = "M2 4H22V17H2Z M8 21H16 M12 17V21",
        ["mem"] = "M2 7H22V17H2Z M7 17V20 M12 17V20 M17 17V20 M7 11H7.01 M11 11H11.01 M15 11H15.01 M19 11H19.01",
        ["disk"] = "M22 12H2 M5.45 5.11A2 2 0 0 1 7.4 4H16.6A2 2 0 0 1 18.55 5.11L22 12 M2 12V18A2 2 0 0 0 4 20H20A2 2 0 0 0 22 18V12 M6 18H6.01 M18 18H18.01",
        ["net"] = "M5 12.55A11 11 0 0 1 19.08 12.55 M1.42 9A16 16 0 0 1 22.58 9 M8.53 16.11A6 6 0 0 1 15.48 16.11 M12 20H12.01",
        ["battery"] = "M2 7H17V17H2Z M19 10H21V14H19Z",
        ["app"] = "M3 3H21V21H3Z M3 9H21 M8 21V9",
        ["activity"] = "M22 12H18L15 21L9 3L6 12H2",
        ["layers"] = "M12 2L22 8.5L12 15L2 8.5Z M2 15.5L12 22L22 15.5",
        ["gauge"] = "M20.2 15.5A8.5 8.5 0 1 0 3.8 15.5 M12 13L15.5 9.5 M10.6 13A1.4 1.4 0 1 0 13.4 13A1.4 1.4 0 1 0 10.6 13Z",
        ["more"] = "M3.8 12A1.2 1.2 0 1 0 6.2 12A1.2 1.2 0 1 0 3.8 12Z M10.8 12A1.2 1.2 0 1 0 13.2 12A1.2 1.2 0 1 0 10.8 12Z M17.8 12A1.2 1.2 0 1 0 20.2 12A1.2 1.2 0 1 0 17.8 12Z",
        ["browser"] = "M12 2A10 10 0 1 0 12 22A10 10 0 1 0 12 2Z M2 12H22 M12 2A15.3 15.3 0 0 1 16 12A15.3 15.3 0 0 1 12 22A15.3 15.3 0 0 1 8 12A15.3 15.3 0 0 1 12 2Z",
        ["code"] = "M16 18L22 12L16 6 M8 6L2 12L8 18",
        ["terminal"] = "M4 17L10 11L4 5 M12 19H20",
        ["chat"] = "M21 15A2 2 0 0 1 19 17H7L3 21V5A2 2 0 0 1 5 3H19A2 2 0 0 1 21 5Z",
        ["db"] = "M3 5A9 3 0 1 0 21 5A9 3 0 1 0 3 5Z M21 12C21 13.66 17 15 12 15C7 15 3 13.66 3 12 M3 5V19C3 20.66 7 22 12 22C17 22 21 20.66 21 19V5",
        ["music"] = "M9 18V5L21 3V16 M6 18A3 3 0 1 0 12 18A3 3 0 1 0 6 18Z M18 16A3 3 0 1 0 24 16A3 3 0 1 0 18 16Z",
        ["video"] = "M23 7L16 12L23 17Z M1 5H16V19H1Z",
        ["folder"] = "M3 7A2 2 0 0 1 5 5H9L11 7H19A2 2 0 0 1 21 9V18A2 2 0 0 1 19 20H5A2 2 0 0 1 3 18Z",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var g)) return g;
        var data = Raw.TryGetValue(key, out var s) ? s : Raw["app"];
        g = Geometry.Parse(data);
        Cache[key] = g;
        return g;
    }

    /// <summary>按进程名/描述猜一个合适的图标。</summary>
    public static string KeyFor(string text)
    {
        foreach (var (pattern, key) in Rules)
            if (pattern.IsMatch(text)) return key;
        return "app";
    }

    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Key)[] Rules =
    {
        (new(@"msedge|chrome|firefox|brave|browser|360se|qqbrowser|sogou|opera|iexplore|edge|gecko|webkit", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "browser"),
        (new(@"code|devenv|idea|pycharm|webstorm|cursor|notepad|sublime|eclipse|studio64|rider|goland|clion|vim|dotnet|msbuild|roslyn", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "code"),
        (new(@"blender|maya|3dsmax|cinema|unity|unreal|obs|photoshop|illustrator|premiere|afterfx|davinci|corona|vray", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "layers"),
        (new(@"steam|epic|wegame|valorant|league|minecraft|overwolf|roblox|game|java|javaw", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "video"),
        (new(@"mysql|postgres|redis|mongod|sqlservr|oracle|sqlite|elasticsearch|etcd", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "db"),
        (new(@"wechat|weixin|\bqq\b|tim|dingtalk|telegram|discord|slack|feishu|lark|wecom|wxwork|skype|teams|napcat|maibot|python", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "chat"),
        (new(@"music|spotify|cloudmusic|kugou|kuwo|foobar", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "music"),
        (new(@"powershell|pwsh|cmd|windowsterminal|^wt$|conhost|bash|zsh|ssh|putty|wsl|ubuntu|debian|node", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "terminal"),
        (new(@"explorer|everything|totalcmd|directory|7zfm|files", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "folder"),
    };
}
