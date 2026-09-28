package main

import (
	"fmt"
	"os"
	"runtime"
	"strings"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/disk"
	"github.com/shirou/gopsutil/v4/mem"
	gnet "github.com/shirou/gopsutil/v4/net"
)

// collectStatic 采集只在启动时取一次的硬件信息（跨平台部分）。
// Windows 上额外补充内存条类型与磁盘型号，见 static_windows.go。
func collectStatic() staticInfo {
	s := staticInfo{}
	s.Host, _ = os.Hostname()
	s.Threads = runtime.NumCPU()

	if infos, err := cpu.Info(); err == nil && len(infos) > 0 {
		s.CPUName = strings.TrimSpace(infos[0].ModelName)
		if infos[0].Mhz > 0 {
			s.CPUMax = round2(infos[0].Mhz / 1000.0)
			s.CPUBase = s.CPUMax
		}
	}
	if s.CPUName == "" {
		s.CPUName = "处理器"
	}

	if vm, err := mem.VirtualMemory(); err == nil {
		s.MemTotal = fmt.Sprintf("%.1f GB", float64(vm.Total)/(1024*1024*1024))
	}

	if parts, err := disk.Partitions(false); err == nil {
		for _, p := range parts {
			if p.Fstype == "" || strings.Contains(strings.ToLower(strings.Join(p.Opts, ",")), "cdrom") {
				continue
			}
			letter := strings.TrimSuffix(p.Device, "\\")
			if letter == "" {
				continue
			}
			s.Disks = append(s.Disks, diskStatic{Letter: letter, Mount: p.Mountpoint})
		}
	}

	collectStaticExtra(&s)
	return s
}

// fallbackNetwork 遍历接口，取第一个非回环且带地址的，速率未知按 1000 估算。
// Windows 上真选网卡由 detectNetwork 走 Get-NetAdapter，这里只是最后的兜底。
func fallbackNetwork() (string, float64) {
	if ifs, err := gnet.Interfaces(); err == nil {
		for _, itf := range ifs {
			if len(itf.Addrs) == 0 || strings.HasPrefix(itf.Name, "Loopback") {
				continue
			}
			return itf.Name, 1000
		}
	}
	return "网络", 1000
}
