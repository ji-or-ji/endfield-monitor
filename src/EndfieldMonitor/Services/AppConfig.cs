using System;
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
    [JsonPropertyName("server")] public string Server { get; set; } = "192.168.101.14:8898";
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
