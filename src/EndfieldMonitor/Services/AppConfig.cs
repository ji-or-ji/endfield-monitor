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

    /// <summary>本次启动前是否已存在配置文件（用于决定要不要首次引导）。</summary>
    [JsonIgnore] public bool Existed { get; private set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EnfieldMonitor", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath));
                if (cfg is not null)
                {
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
