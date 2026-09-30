package main

// 快照结构。字段名与 C# 客户端的 JSON 约定保持一致（snake_case）。

type Snapshot struct {
	Ts       float64     `json:"ts"`
	Interval float64     `json:"interval"`
	Live     bool        `json:"live"`
	CPU      CPUInfo     `json:"cpu"`
	GPU      GPUInfo     `json:"gpu"`
	Mem      MemInfo     `json:"mem"`
	Disks    []DiskInfo  `json:"disks"`
	Net      NetInfo     `json:"net"`
	Battery  BatteryInfo `json:"battery"`
	Procs    []ProcInfo  `json:"procs"`
	Server   *ServerInfo `json:"server"`

	// 这一份服务端能做什么。普通版为空（用 omitempty，键都不出现）；
	// 增强版（plus）会列出可动的指令，客户端据此切增强模式。
	Capabilities []string `json:"capabilities,omitempty"`
}

type CPUInfo struct {
	Name    string  `json:"name"`
	Threads int     `json:"threads"`
	Util    float64 `json:"util"`
	Freq    float64 `json:"freq"`
	Peak    float64 `json:"peak"` // 观测到的最高实时频率；一直没超过标称就为零
	Base    float64 `json:"base"`
}

type GPUInfo struct {
	Name     string   `json:"name"`
	Util     float64  `json:"util"`
	Freq     *float64 `json:"freq"`
	MemUsed  *float64 `json:"mem_used"`
	MemTotal float64  `json:"mem_total"`
	OK       bool     `json:"ok"`
}

type MemInfo struct {
	Used  float64 `json:"used"`
	Total float64 `json:"total"`
	Pct   float64 `json:"pct"`
	Speed string  `json:"speed"`
	Type  string  `json:"type"`
}

type DiskInfo struct {
	Name  string  `json:"name"`
	Used  float64 `json:"used"`
	Total float64 `json:"total"`
	Pct   float64 `json:"pct"`
	Util  float64 `json:"util"`
	Rw    string  `json:"rw"`
	Media string  `json:"media"`
	Model string  `json:"model"`
}

type NetInfo struct {
	Name string  `json:"name"`
	Down float64 `json:"down"`
	Up   float64 `json:"up"`
	Link float64 `json:"link"`
	Util float64 `json:"util"`
}

// BatteryInfo 是电池状态。电池为笔记本/手持设备才有的东西，
// 台式机（或桌面 Linux）没有电池，Present 为 false，其它字段无意义。
// 容量类字段来自启动时的一次性采集，数值单位是毫瓦时（mWh）。
type BatteryInfo struct {
	Present     bool    `json:"present"`
	Percent     float64 `json:"percent"`      // 0~100；-1 表示未知
	Charging    bool    `json:"charging"`     // 正在充电
	OnAC        bool    `json:"on_ac"`        // 接着电源
	SecondsLeft float64 `json:"seconds_left"` // 预计剩余秒数；0 表示未知
	FullMWh     float64 `json:"full_mwh"`     // 满充容量；0 表示未知
	DesignMWh   float64 `json:"design_mwh"`   // 设计容量；0 表示未知
	HealthPct   float64 `json:"health_pct"`   // 满充 / 设计；0 表示未知
}

type ProcInfo struct {
	Pid   int32   `json:"pid"`
	Name  string  `json:"name"`
	Exe   string  `json:"exe"`
	Mem   float64 `json:"mem"`
	CPU   float64 `json:"cpu"`
	Title string  `json:"title"`

	// 启动参数。只在增强版（plus）里采：它服务于「重启」这个能力，
	// 而且命令行里可能带着口令之类的敏感参数，普通版没必要往局域网里广播。
	Cmd []string `json:"cmd,omitempty"`
}

// AuditEntry 是增强版动过手之后留下的一条记录。
type AuditEntry struct {
	At     string `json:"at"`     // 本地时间，秒级
	Action string `json:"action"` // stop / restart
	Pid    int32  `json:"pid"`
	Name   string `json:"name,omitempty"` // 操作时进程的名字，便于事后认人
	From   string `json:"from"`           // 来源地址
	OK     bool   `json:"ok"`
	Err    string `json:"err,omitempty"`
}

type ServerInfo struct {
	Host        string       `json:"host"`
	UptimeHours float64      `json:"uptime_hours"`
	Service     *ServiceInfo `json:"service"`

	// 增强版动过手的记录，最近几十条。只读模式与没动过手时都不出现。
	Audit []AuditEntry `json:"audit,omitempty"`
}

// ServiceInfo 只在配置了 --watch-* 时出现，否则为 null（通用形态）。
type ServiceInfo struct {
	Name    string          `json:"name"`
	Running bool            `json:"running"`
	Matches int             `json:"matches"`
	Ports   map[string]bool `json:"ports"`
	Tasks   []TaskInfo      `json:"tasks"`
}

type TaskInfo struct {
	Name       string `json:"name"`
	Status     string `json:"status"`
	LastRun    string `json:"last_run"`
	LastResult string `json:"last_result"`
}

// staticInfo 是启动时只采集一次的硬件信息。
type staticInfo struct {
	Host        string
	CPUName     string
	Threads     int
	CPUBase     float64
	MemTotal    string
	MemSpeed    string
	MemType     string
	GPUName     string
	GPUMemMB    float64 // 显卡专用显存总量；核显读不到时为 0
	Disks       []diskStatic
	BatteryFull int // 满充容量（mWh）
	BatteryDes  int // 设计容量（mWh）
}

type diskStatic struct {
	Letter string
	Mount  string
	Model  string
	Media  string
}
