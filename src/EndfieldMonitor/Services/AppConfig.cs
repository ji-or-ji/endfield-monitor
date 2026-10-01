using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndfieldMonitor.Services;

/// <summary>
/// 客户端本地配置，落在 %AppData%\EnfieldMonitor\config.json。
/// 分发出去后，每台机器各自填自己的服务器地址与共享口令。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 服务器地址，形如 host:port。留空 = 监视本机：同目录带着采集端时客户端会自己拉起来。
    /// 这里刻意不预填具体地址：套件分发出去后预填一个作者的局域网 IP，使用者多半
    /// 照单收下，结果既连不上、又不会触发本机自看。
    /// </summary>
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>中心点云开销档：full（默认，仿终末地原版）/ lite / off。</summary>
    [JsonPropertyName("particleMode")] public string ParticleMode { get; set; } = "full";

    /// <summary>点云是否用一次性 DrawVertices 批量提交（走 GPU 顶点）。关掉则回退逐点路径。</summary>
    [JsonPropertyName("batchCloud")] public bool BatchCloud { get; set; } = true;

    /// <summary>启动时检查更新，默认关：检查要连 Gitee，不是每个使用环境都有外网。</summary>
    [JsonPropertyName("autoUpdate")] public bool AutoUpdate { get; set; }

    /// <summary>CPU / 内存是否显示具体数值（频率、GB），而不是占用百分比。</summary>
    [JsonPropertyName("showAbsolute")] public bool ShowAbsolute { get; set; }

    /// <summary>本次启动前是否已存在配置文件（用于决定要不要首次引导）。</summary>
    [JsonIgnore] public bool Existed { get; private set; }

    /// <summary>
    /// 已记录的被控设备。空表示还没配过，此时按老规矩用 Server / Token 顶一台。
    /// 进「跨设备监控总览」时对这里每一台做一次连通探测。
    /// </summary>
    [JsonPropertyName("devices")] public List<DeviceRecord> Devices { get; set; } = new();

    /// <summary>
    /// 配置结构版本。老文件里没有这个字段，反序列化后是 0，据此判断要不要迁移。
    /// 加字段或改语义时把它 +1，并在 Migrate 里补一条。
    /// </summary>
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; }

    /// <summary>当前结构版本。</summary>
    public const int CurrentConfigVersion = 1;

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EnfieldMonitor", "config.json");

    /// <summary>
    /// 规整用户填的服务器地址：剥掉多余的 http(s):// 前缀（含连续误加）、
    /// 结尾的斜杠与路径、以及首尾空白。客户端只会再拼一次 "http://"，
    /// 所以这里必须先收干净，否则会拼成 http://http://… 连不上。
    /// </summary>
    public static string NormalizeServer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        while (true)
        {
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
            else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
            else break;
            s = s.TrimStart();
        }
        int slash = s.IndexOf('/'); // 只保留 host:port，丢掉 /snapshot 这类尾巴
        if (slash >= 0) s = s[..slash];
        return s.Trim();
    }

    /// <summary>把 host:port 拆成两截；没写端口就回默认 8898。</summary>
    public static (string Host, int Port) SplitServer(string? endpoint)
    {
        var s = (endpoint ?? "").Trim();
        var i = s.LastIndexOf(':');
        if (i > 0 && int.TryParse(s[(i + 1)..], out var p) && p > 0 && p <= 65535)
            return (s[..i], p);
        return (s, 8898);
    }

    /// <summary>
    /// 把老版本配置就地升级到当前结构，返回是否值得写回。
    /// v0 -> v1：被控设备从单个 server/token 改成 devices 列表。先把老那一台搬进去，
    /// 不让人升级上来还得重填一遍。地址留空（监视本机）也落成一条，新页面才有东西可点。
    /// 老文件本来就有 devices 的（已经在用新版）不重复添，保证跑两次不会长出两条。
    /// </summary>
    private bool Migrate()
    {
        if (ConfigVersion >= CurrentConfigVersion) return false;

        if (Devices.Count == 0)
        {
            var (host, port) = SplitServer(Server);
            if (host.Length == 0) host = "127.0.0.1";
            Devices.Add(new DeviceRecord
            {
                Name = host == "127.0.0.1" ? "本机" : host,
                Addr = host,
                Port = port,
                Token = Token,
            });
        }

        ConfigVersion = CurrentConfigVersion;
        return true;
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg is not null)
                {
                    cfg.Server = NormalizeServer(cfg.Server);
                    if (cfg.Migrate()) cfg.Save();   // 老结构就地升级并立刻写回，下次启动不再重跑
                    cfg.Existed = true;
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏就退回默认值，不让它拦住启动
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            Existed = true;
        }
        catch
        {
            // 写不进去就只在内存里生效，不弹错打断使用
        }
    }
}

/// <summary>
/// 一台被控设备的记录。地址与端口分开存（老写法是一个 host:port 串，
/// 拆开以后端口好单独改）。描述暂时没有地方用，先存着。
/// </summary>
public sealed class DeviceRecord
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
    [JsonPropertyName("addr")] public string Addr { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 8898;
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>拼成客户端要用的 host:port。端口没填就只回地址。</summary>
    [JsonIgnore] public string Endpoint => Port > 0 ? $"{Addr}:{Port}" : Addr;

    /// <summary>名称没写就退回地址，列表里总得有个能认的。</summary>
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) ? (Addr.Length > 0 ? Addr : "未命名") : Name;
}
