//go:build windows

package main

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os/exec"
	"strings"
)

var memTypeNames = map[int]string{
	20: "DDR", 21: "DDR2", 24: "DDR3", 26: "DDR4", 34: "DDR5",
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
	if out := runPS("@(Get-CimInstance Win32_PhysicalMemory | Select-Object Speed,SMBIOSMemoryType) | ConvertTo-Json -Compress"); out != "" {
		var mems []winMem
		if json.Unmarshal([]byte(out), &mems) == nil && len(mems) > 0 {
			best := 0
			for _, m := range mems {
				if m.Speed > best {
					best = m.Speed
				}
			}
			if best > 0 {
				s.MemSpeed = fmt.Sprintf("%d MT/s", best)
			}
			if name, ok := memTypeNames[mems[0].SMBIOSMemoryType]; ok {
				s.MemType = name
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
	if out := runPS("@(Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType) | ConvertTo-Json -Compress"); out != "" {
		_ = json.Unmarshal([]byte(out), &phys)
	}
	for i := range s.Disks {
		s.Disks[i].Media = mediaOf(s.Disks[i].Model, phys)
	}
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

type winTask struct {
	Name       string `json:"name"`
	State      string `json:"state"`
	LastRun    string `json:"last_run"`
	LastResult string `json:"last_result"`
}

func collectTasks() []TaskInfo {
	out := make([]TaskInfo, 0, len(cfg.watchTask))
	for _, name := range cfg.watchTask {
		ti := TaskInfo{Name: name, Status: "未知", LastRun: "—", LastResult: "—"}

		safe := strings.ReplaceAll(name, "'", "''")
		script := fmt.Sprintf(
			"$n='%s'; $t=Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue; "+
				"$i=Get-ScheduledTaskInfo -TaskName $n -ErrorAction SilentlyContinue; "+
				"if($t){ @([pscustomobject]@{name=$n;state=$t.State.ToString();"+
				"last_run=(''+$i.LastRunTime);last_result=(''+$i.LastTaskResult)}) | ConvertTo-Json -Compress }",
			safe)

		var arr []winTask
		if raw := runPS(script); raw != "" {
			if json.Unmarshal([]byte(raw), &arr) == nil && len(arr) > 0 {
				w := arr[0]
				if w.State != "" {
					ti.Status = w.State
				}
				if w.LastRun != "" {
					ti.LastRun = w.LastRun
				}
				if w.LastResult != "" {
					ti.LastResult = w.LastResult
				}
			}
		}
		out = append(out, ti)
	}
	return out
}
