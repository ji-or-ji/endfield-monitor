using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EndfieldMonitor.Models;

/// <summary>
/// 服务端 /snapshot 返回的一份完整快照。
/// 字段命名沿用原「终末地管理器」采集端的 JSON 约定（snake_case），
/// 末尾多出一个 server 块承载麦麦业务数据。
/// </summary>
public sealed class Snapshot
{
    [JsonPropertyName("ts")]       public double Ts { get; set; }
    [JsonPropertyName("interval")] public double Interval { get; set; }
    [JsonPropertyName("live")]     public bool Live { get; set; }

    [JsonPropertyName("cpu")]   public CpuInfo Cpu { get; set; } = new();
    [JsonPropertyName("gpu")]   public GpuInfo Gpu { get; set; } = new();
    [JsonPropertyName("mem")]   public MemInfo Mem { get; set; } = new();
    [JsonPropertyName("disks")]   public List<DiskInfo> Disks { get; set; } = new();
    [JsonPropertyName("net")]     public NetInfo Net { get; set; } = new();
    [JsonPropertyName("battery")] public BatteryInfo Battery { get; set; } = new();
    [JsonPropertyName("procs")]   public List<ProcInfo> Procs { get; set; } = new();

    /// <summary>麦麦业务扩展块，本项目相对原采集端的增量。</summary>
    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }
}

public sealed class CpuInfo
{
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("threads")] public int Threads { get; set; }
    [JsonPropertyName("util")]    public double Util { get; set; }
    [JsonPropertyName("freq")]    public double Freq { get; set; }
    [JsonPropertyName("peak")]    public double Peak { get; set; }
    [JsonPropertyName("base")]    public double Base { get; set; }
    [JsonPropertyName("max")]     public double Max { get; set; }
}

public sealed class GpuInfo
{
    [JsonPropertyName("name")]      public string? Name { get; set; }
    [JsonPropertyName("util")]      public double Util { get; set; }
    [JsonPropertyName("freq")]      public double? Freq { get; set; }
    [JsonPropertyName("mem_used")]  public double? MemUsed { get; set; }
    [JsonPropertyName("mem_total")] public double MemTotal { get; set; }
    [JsonPropertyName("ok")]        public bool Ok { get; set; }
}

public sealed class MemInfo
{
    [JsonPropertyName("used")]  public double Used { get; set; }
    [JsonPropertyName("total")] public double Total { get; set; }
    [JsonPropertyName("pct")]   public double Pct { get; set; }
    [JsonPropertyName("speed")] public string? Speed { get; set; }
    [JsonPropertyName("type")]  public string? Type { get; set; }
}

public sealed class DiskInfo
{
    [JsonPropertyName("name")]  public string? Name { get; set; }
    [JsonPropertyName("used")]  public double Used { get; set; }
    [JsonPropertyName("total")] public double Total { get; set; }
    [JsonPropertyName("pct")]   public double Pct { get; set; }
    [JsonPropertyName("util")]  public double Util { get; set; }
    [JsonPropertyName("rw")]    public string? Rw { get; set; }
    [JsonPropertyName("media")] public string? Media { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
}

public sealed class NetInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("down")] public double Down { get; set; }
    [JsonPropertyName("up")]   public double Up { get; set; }
    [JsonPropertyName("link")] public double Link { get; set; }
    [JsonPropertyName("util")] public double Util { get; set; }
}

/// <summary>电池。台式机没有电池，Present 为假，其余字段无意义。</summary>
public sealed class BatteryInfo
{
    [JsonPropertyName("present")]      public bool Present { get; set; }
    [JsonPropertyName("percent")]      public double Percent { get; set; }
    [JsonPropertyName("charging")]     public bool Charging { get; set; }
    [JsonPropertyName("on_ac")]        public bool OnAC { get; set; }
    [JsonPropertyName("seconds_left")] public double SecondsLeft { get; set; }
    [JsonPropertyName("full_mwh")]     public double FullMWh { get; set; }
    [JsonPropertyName("design_mwh")]   public double DesignMWh { get; set; }
    [JsonPropertyName("health_pct")]   public double HealthPct { get; set; }
}

public sealed class ProcInfo
{
    [JsonPropertyName("pid")]     public int Pid { get; set; }
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("exe")]     public string? Exe { get; set; }
    [JsonPropertyName("mem")]     public double Mem { get; set; }
    [JsonPropertyName("cpu")]     public double Cpu { get; set; }
    [JsonPropertyName("display")] public string? Display { get; set; }
    [JsonPropertyName("title")]   public string? Title { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("host")]         public string? Host { get; set; }
    [JsonPropertyName("uptime_hours")] public double UptimeHours { get; set; }
    [JsonPropertyName("service")]      public ServiceInfo? Service { get; set; }
}

/// <summary>被关注的服务（可选，由服务端 --watch-* 参数决定；未配置则为 null）。</summary>
public sealed class ServiceInfo
{
    [JsonPropertyName("name")]    public string? Name { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("ports")]   public Dictionary<string, bool> Ports { get; set; } = new();
    [JsonPropertyName("tasks")]   public List<TaskInfo> Tasks { get; set; } = new();
}

public sealed class TaskInfo
{
    [JsonPropertyName("name")]        public string? Name { get; set; }
    [JsonPropertyName("status")]      public string? Status { get; set; }
    [JsonPropertyName("last_run")]    public string? LastRun { get; set; }
    [JsonPropertyName("last_result")] public string? LastResult { get; set; }
}
