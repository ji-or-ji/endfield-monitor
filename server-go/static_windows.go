//go:build windows

package main

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os/exec"
	"strings"
)

// SMBIOS 内存类型编号（Win32_PhysicalMemory.SMBIOSMemoryType）。
// LPDDR 系列是独立编号，板载颗粒常见 27~30，别只认 DDR3/DDR4/DDR5。
var memTypeNames = map[int]string{
	20: "DDR", 21: "DDR2", 22: "DDR2 FB-DIMM", 24: "DDR3", 26: "DDR4",
	27: "LPDDR", 28: "LPDDR2", 29: "LPDDR3", 30: "LPDDR4",
	34: "DDR5", 35: "LPDDR5",
}

// Get-ScheduledTask 的状态是英文枚举，转成界面统一的中文。
var taskStateNames = map[string]string{
	"Ready": "就绪", "Running": "正在运行", "Disabled": "已禁用",
	"Queued": "已排队", "Unknown": "未知",
}

// runPS 执行一段 PowerShell 并把结果原样取回。
// 结果先转成 UTF-8 的 Base64 再输出，绕开 PowerShell 5.1 在 stdout 被重定向时的编码问题。
// 只在启动时调用少数几次，之后采样完全不碰 WMI。
func runPS(script string) string {
	wrapped := "$ErrorActionPreference='SilentlyContinue';" +
		"$o = (& { " + script + " }) | Out-String;" +
		"[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($o))"
	out, err := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", wrapped).Output()
	if err != nil {
		return ""
	}
	raw, err := base64.StdEncoding.DecodeString(strings.TrimSpace(string(out)))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(raw))
}

// unmarshalList 容忍两种返回：真正的 JSON 数组，或单元素被拆包后的单个对象。
// PowerShell 里「@(...) | ConvertTo-Json」会被管道拆包，单元素就退化成对象。
func unmarshalList[T any](raw string) []T {
	var list []T
	if json.Unmarshal([]byte(raw), &list) == nil && len(list) > 0 {
		return list
	}
	var one T
	if json.Unmarshal([]byte(raw), &one) == nil {
		return []T{one}
	}
	return nil
}

type winMem struct {
	Speed            int `json:"Speed"`
	SMBIOSMemoryType int `json:"SMBIOSMemoryType"`
}

type winPhysDisk struct {
	FriendlyName string `json:"FriendlyName"`
	MediaType    string `json:"MediaType"`
	BusType      string `json:"BusType"`
}

func collectStaticExtra(s *staticInfo) {
	// 内存条类型与频率
	if out := runPS("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PhysicalMemory | Select-Object Speed,SMBIOSMemoryType)"); out != "" {
		if mems := unmarshalList[winMem](out); len(mems) > 0 {
			best := 0
			for _, m := range mems {
				if m.Speed > best {
					best = m.Speed
				}
			}
			if best > 0 {
				s.MemSpeed = fmt.Sprintf("%d MT/s", best)
			}
			// 取第一根能映射出名字的内存条：部分机器的首条类型码是 0 或表外值
			for _, m := range mems {
				if name, ok := memTypeNames[m.SMBIOSMemoryType]; ok {
					s.MemType = name
					break
				}
			}
		}
	}

	// 逻辑盘 -> 物理盘映射（每行形如 C:|PhysicalDrive0|Colorful SL300 128GB）
	mapping := runPS(`$d = Get-CimInstance Win32_DiskDrive; foreach ($x in $d) { $parts = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($x.DeviceID)'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"; foreach ($p in $parts) { $logs = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"; foreach ($l in $logs) { "$($l.DeviceID)|PhysicalDrive$($x.Index)|$($x.Model)" } } }`)
	for _, line := range strings.Split(mapping, "\n") {
		seg := strings.Split(strings.TrimSpace(strings.TrimRight(line, "\r")), "|")
		if len(seg) < 2 {
			continue
		}
		for i := range s.Disks {
			if s.Disks[i].Letter == seg[0] {
				s.Disks[i].Phys = seg[1]
				if len(seg) > 2 {
					s.Disks[i].Model = seg[2]
				}
			}
		}
	}

	// 介质类型
	var phys []winPhysDisk
	if out := runPS("ConvertTo-Json -Compress -InputObject @(Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType)"); out != "" {
		phys = unmarshalList[winPhysDisk](out)
	}
	for i := range s.Disks {
		s.Disks[i].Media = mediaOf(s.Disks[i].Model, phys)
	}

	// 显卡型号：排除虚拟显示适配器（Todesk / Honor / Parsec 之类），
	// 只留真正的物理 GPU。
	if out := runPS("Get-CimInstance Win32_VideoController | Select-Object Name | ConvertTo-Json -Compress"); out != "" {
		type vc struct {
			Name string `json:"Name"`
		}
		for _, v := range unmarshalList[vc](out) {
			if isVirtualDisplay(v.Name) {
				continue
			}
			s.GPUName = v.Name
			break
		}
	}
}

// 描述里出现这些词的多半是虚拟/远程显示适配器，不是真显卡。
var virtualDisplayWords = []string{
	"virtual", "todesk", "honor virtual", "parsec", "spacedesk", "displaylink", "usb display",
}

func isVirtualDisplay(name string) bool {
	low := strings.ToLower(name)
	for _, w := range virtualDisplayWords {
		if strings.Contains(low, w) {
			return true
		}
	}
	return false
}

// mediaOf 判断介质类型。部分盘（尤其机械盘、U 盘）不会自报 MediaType，
// 这时靠总线类型兜底。
func mediaOf(model string, phys []winPhysDisk) string {
	for _, p := range phys {
		if p.FriendlyName == "" {
			continue
		}
		if !strings.Contains(model, p.FriendlyName) && !strings.Contains(p.FriendlyName, model) {
			continue
		}
		switch {
		case p.MediaType == "SSD" && p.BusType == "NVMe":
			return "NVMe SSD"
		case p.MediaType == "SSD":
			return "SATA SSD"
		case p.BusType == "USB":
			return "USB 存储"
		case p.MediaType != "" && p.MediaType != "Unspecified":
			return p.MediaType
		case p.BusType == "ATA":
			return "SATA 硬盘"
		}
	}
	return "本地磁盘"
}

type winAdapter struct {
	Name      string `json:"Name"`
	Interface string `json:"InterfaceDescription"`
	Speed     uint64 `json:"Speed"` // 链路速率，bps
	Status    string `json:"Status"`
}

// 描述里出现这些词的多半是虚拟、隧道或远程桌面用的假网卡，
// 真流量该走的那块一定不是它们。
var virtualIfaceWords = []string{
	"virtual", "vpn", "radmin", "todesk", "anydesk", "sunlogin", "oray",
	"vmware", "hyper-v", "vethernet", "virtualbox", "loopback",
	"wi-fi direct", "wifi direct", "bluetooth", "tailscale", "zerotier",
	"tap-windows", "tap adapter", "npcap", "wsl",
}

func isWiredAdapter(a winAdapter) bool {
	low := strings.ToLower(a.Interface)
	return strings.Contains(low, "ethernet") &&
		!strings.Contains(low, "wireless") &&
		!strings.Contains(low, "wi-fi") &&
		!strings.Contains(low, "wlan")
}

// selectPrimaryNetwork 挑一块真实网卡，取名字与链路速率。
// 挑不到（全是虚拟网卡或 Get-NetAdapter 不可用）就保留 static.go 的兜底值。
func selectPrimaryNetwork(s *staticInfo) {
	raw := runPS("Get-NetAdapter | Where-Object Status -eq 'Up' | " +
		"Select-Object Name,InterfaceDescription,Speed,Status | ConvertTo-Json -Compress")
	adapters := unmarshalList[winAdapter](raw)
	if len(adapters) == 0 {
		return
	}

	var picked *winAdapter
	for i := range adapters {
		low := strings.ToLower(adapters[i].Interface)
		virtual := false
		for _, w := range virtualIfaceWords {
			if strings.Contains(low, w) {
				virtual = true
				break
			}
		}
		if virtual {
			continue
		}
		if picked == nil {
			picked = &adapters[i]
		}
		// 同时有有线和无线时优先有线
		if isWiredAdapter(adapters[i]) {
			picked = &adapters[i]
			break
		}
	}
	if picked == nil {
		return
	}

	s.NetName = picked.Name
	if picked.Speed > 0 {
		s.NetLink = float64(picked.Speed) / 1e6 // bps -> Mbps
	}
}

type winTask struct {
	Name       string `json:"name"`
	State      string `json:"state"`
	LastRun    string `json:"last_run"`
	LastResult string `json:"last_result"`
}

func collectTasks() []TaskInfo {
	out := make([]TaskInfo, 0, len(cfg.watchTask))
	if len(cfg.watchTask) == 0 {
		return out
	}

	// 一次进程查完所有任务。逐个查会让 powershell.exe 冷启动次数等于任务数，
	// 每个进程几百毫秒，纯属白费。
	quoted := make([]string, len(cfg.watchTask))
	for i, n := range cfg.watchTask {
		quoted[i] = "'" + strings.ReplaceAll(n, "'", "''") + "'"
	}
	script := fmt.Sprintf(
		"$names=@(%s); "+
			"Get-ScheduledTask -TaskName $names -ErrorAction SilentlyContinue | ForEach-Object { "+
			"$i=Get-ScheduledTaskInfo -TaskName $_.TaskName -ErrorAction SilentlyContinue; "+
			"[pscustomobject]@{name=$_.TaskName; state=$_.State.ToString(); "+
			"last_run=$(if($i.LastRunTime){$i.LastRunTime.ToString('yyyy-MM-dd HH:mm')}else{''}); "+
			"last_result=(''+$i.LastTaskResult)} } | ConvertTo-Json -Compress",
		strings.Join(quoted, ","))

	byName := map[string]winTask{}
	for _, w := range unmarshalList[winTask](runPS(script)) {
		byName[strings.ToLower(w.Name)] = w
	}

	for _, name := range cfg.watchTask {
		ti := TaskInfo{Name: name, Status: "未知", LastRun: "—", LastResult: "—"}
		if w, ok := byName[strings.ToLower(name)]; ok {
			if w.State != "" {
				if zh, ok := taskStateNames[w.State]; ok {
					ti.Status = zh
				} else {
					ti.Status = w.State
				}
			}
			if w.LastRun != "" {
				ti.LastRun = w.LastRun
			}
			if w.LastResult != "" {
				ti.LastResult = w.LastResult
			}
		}
		out = append(out, ti)
	}
	return out
}
