//go:build windows

package main

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"os/exec"
	"strings"
	"sync"
	"time"

	"golang.org/x/sys/windows/registry"
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

// PowerShell 调用一律带超时：WMI 偶尔会挂住，没有超时会把启动或采样循环一起拖死。
const psTimeout = 20 * time.Second

// runPS 执行一段 PowerShell 并把结果原样取回。
// 结果先转成 UTF-8 的 Base64 再输出，绕开 PowerShell 5.1 在 stdout 被重定向时的编码问题。
func runPS(script string) string {
	wrapped := "$ErrorActionPreference='SilentlyContinue';" +
		"$o = (& { " + script + " }) | Out-String;" +
		"[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($o))"
	ctx, cancel := context.WithTimeout(context.Background(), psTimeout)
	defer cancel()
	out, err := exec.CommandContext(ctx, "powershell", "-NoProfile", "-NonInteractive", "-Command", wrapped).Output()
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

type winAdapter struct {
	Name        string `json:"Name"`
	Description string `json:"Description"`
	Speed       uint64 `json:"Speed"` // 链路速率，bps
	Type        string `json:"Type"`  // Ethernet / Wireless80211 / Loopback / Tunnel
}

// 用 .NET 的 NetworkInterface 枚举，比 Get-NetAdapter 的 CIM 查询快一个量级
// （本机实测 ~0.8s vs ~6s），而且 Type 直接给出 Ethernet / Wireless80211。
const netQueryScript = `[Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' } | Select-Object Name, Description, Speed, @{n='Type'; e={ $_.NetworkInterfaceType.ToString() }} | ConvertTo-Json -Compress`

// 盘符 -> 物理盘型号（每行形如 C:|Colorful SL300 128GB）
const diskMapScript = `$d = Get-CimInstance Win32_DiskDrive; foreach ($x in $d) { $parts = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($x.DeviceID)'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"; foreach ($p in $parts) { $logs = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"; foreach ($l in $logs) { "$($l.DeviceID)|$($x.Model)" } } }`

func collectStaticExtra(s *staticInfo) {
	// 这几项彼此独立，并行发起。串行时每次 PowerShell 冷启动几百毫秒起步，
	// 四次串下来就是启动的主要耗时。
	var (
		wg                        sync.WaitGroup
		memOut, modelOut, physOut string
		gpuOut                    string
	)
	wg.Add(4)
	go func() {
		defer wg.Done()
		memOut = runPS("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PhysicalMemory | Select-Object Speed,SMBIOSMemoryType)")
	}()
	go func() { defer wg.Done(); modelOut = runPS(diskMapScript) }()
	go func() {
		defer wg.Done()
		physOut = runPS("ConvertTo-Json -Compress -InputObject @(Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType)")
	}()
	go func() {
		defer wg.Done()
		gpuOut = runPS("Get-CimInstance Win32_VideoController | Select-Object Name | ConvertTo-Json -Compress")
	}()
	wg.Wait()

	// 内存条类型与频率
	if memOut != "" {
		if mems := unmarshalList[winMem](memOut); len(mems) > 0 {
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

	// 盘符 -> 型号
	for _, line := range strings.Split(modelOut, "\n") {
		seg := strings.Split(strings.TrimSpace(strings.TrimRight(line, "\r")), "|")
		if len(seg) < 2 {
			continue
		}
		for i := range s.Disks {
			if s.Disks[i].Letter == seg[0] {
				s.Disks[i].Model = seg[1]
			}
		}
	}

	// 介质类型
	var phys []winPhysDisk
	if physOut != "" {
		phys = unmarshalList[winPhysDisk](physOut)
	}
	for i := range s.Disks {
		s.Disks[i].Media = mediaOf(s.Disks[i].Model, phys)
	}

	// 显卡型号：排除虚拟显示适配器（Todesk / Honor / Parsec 之类），只留物理 GPU
	if gpuOut != "" {
		type vc struct {
			Name string `json:"Name"`
		}
		for _, v := range unmarshalList[vc](gpuOut) {
			if isVirtualDisplay(v.Name) {
				continue
			}
			s.GPUName = v.Name
			break
		}
	}

	// 显存总量：驱动会把它留在显示类键下。核显没有专用显存，读不到就留 0。
	if s.GPUName != "" {
		s.GPUMemMB = gpuMemTotalMB(s.GPUName)
	}
}

// gpuMemTotalMB 从显示适配器类注册表里读显存大小。
// WMI 的 AdapterRAM 是 32 位、超过 4GB 会截断，不准；这个值是驱动自己上报的。
func gpuMemTotalMB(name string) float64 {
	const classKey = `SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}`
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, classKey, registry.READ)
	if err != nil {
		return 0
	}
	defer k.Close()

	subs, err := k.ReadSubKeyNames(-1)
	if err != nil {
		return 0
	}
	for _, sub := range subs {
		if len(sub) != 4 { // 只有 0000/0001… 这种才是适配器实例
			continue
		}
		sk, err := registry.OpenKey(registry.LOCAL_MACHINE, classKey+`\`+sub, registry.READ)
		if err != nil {
			continue
		}
		desc, _, _ := sk.GetStringValue("DriverDesc")
		if desc == "" || (name != "" && !strings.Contains(name, desc) && !strings.Contains(desc, name)) {
			sk.Close()
			continue
		}

		var bytesVal uint64
		if b, _, err := sk.GetBinaryValue("HardwareInformation.qwMemorySize"); err == nil && len(b) >= 8 {
			bytesVal = binary.LittleEndian.Uint64(b[:8])
		} else if v, _, err := sk.GetIntegerValue("HardwareInformation.qwMemorySize"); err == nil && v > 0 {
			bytesVal = v
		}
		sk.Close()

		if bytesVal > 0 {
			return float64(bytesVal) / 1048576
		}
	}
	return 0
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
// 这时靠总线类型兜底。型号为空时无从匹配，直接给通用名，
// 否则 strings.Contains(x, "") 恒真，会把第一块盘的介质贴上去。
func mediaOf(model string, phys []winPhysDisk) string {
	if model == "" {
		return "本地磁盘"
	}
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

// 描述里出现这些词的多半是虚拟、隧道或远程桌面用的假网卡，
// 真流量该走的那块一定不是它们。
var virtualIfaceWords = []string{
	"virtual", "vpn", "radmin", "todesk", "anydesk", "sunlogin", "oray",
	"vmware", "hyper-v", "vethernet", "virtualbox", "loopback",
	"wi-fi direct", "wifi direct", "bluetooth", "tailscale", "zerotier",
	"tap-windows", "tap adapter", "npcap", "wsl",
}

// isWiredAdapter 判断是否有线网卡：Type 为 Ethernet 即有线，
// Wireless80211 为无线。比在描述里找 ethernet 可靠：
// 不少有线网卡（如 Realtek PCIe GbE Family Controller）描述里根本没这个词。
func isWiredAdapter(a winAdapter) bool { return a.Type == "Ethernet" }

// isVirtualIface 描述里带这些词的，多是虚拟、隧道或远程桌面用的假网卡。
func isVirtualIface(desc string) bool {
	low := strings.ToLower(desc)
	for _, w := range virtualIfaceWords {
		if strings.Contains(low, w) {
			return true
		}
	}
	return false
}

// detectNetwork 挑一块真实网卡，取名字与链路速率。
// 挑不到（全是虚拟网卡或查询不可用）就退回跨平台兜底。
func detectNetwork() (string, float64) {
	adapters := unmarshalList[winAdapter](runPS(netQueryScript))

	var picked *winAdapter
	for i := range adapters {
		a := adapters[i]
		// 只认以太网与无线；Loopback / Tunnel 之类直接跳过
		if a.Type != "Ethernet" && a.Type != "Wireless80211" {
			continue
		}
		if isVirtualIface(a.Description) {
			continue
		}
		if picked == nil {
			picked = &adapters[i]
		}
		// 同时有有线和无线时优先有线
		if isWiredAdapter(a) {
			picked = &adapters[i]
			break
		}
	}
	if picked == nil {
		return fallbackNetwork()
	}
	return picked.Name, float64(picked.Speed) / 1e6 // bps -> Mbps
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
