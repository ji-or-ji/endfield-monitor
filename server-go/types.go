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
	Procs    []ProcInfo  `json:"procs"`
	Server   *ServerInfo `json:"server"`
}

type CPUInfo struct {
	Name    string  `json:"name"`
	Threads int     `json:"threads"`
	Util    float64 `json:"util"`
	Freq    float64 `json:"freq"`
	Base    float64 `json:"base"`
	Max     float64 `json:"max"`
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

type ProcInfo struct {
	Pid     int32   `json:"pid"`
	Name    string  `json:"name"`
	Exe     string  `json:"exe"`
	Mem     float64 `json:"mem"`
	CPU     float64 `json:"cpu"`
	Display string  `json:"display"`
	Title   string  `json:"title"`
}

type ServerInfo struct {
	Host        string       `json:"host"`
	UptimeHours float64      `json:"uptime_hours"`
	Service     *ServiceInfo `json:"service"`
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
	Host     string
	CPUName  string
	Threads  int
	CPUMax   float64
	CPUBase  float64
	MemTotal string
	MemSpeed string
	MemType  string
	GPUName  string
	Disks    []diskStatic
}

type diskStatic struct {
	Letter string
	Mount  string
	Model  string
	Media  string
}
