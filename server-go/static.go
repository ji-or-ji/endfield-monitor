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

	if ifs, err := gnet.Interfaces(); err == nil {
		for _, itf := range ifs {
			if len(itf.Addrs) == 0 || strings.HasPrefix(itf.Name, "Loopback") {
				continue
			}
			s.NetName = itf.Name
			break
		}
	}
	if s.NetName == "" {
		s.NetName = "网络"
	}
	s.NetLink = 1000

	// Windows 上改用物理网卡的名字与真实链路速率，
	// 避免抓到虚拟网卡、速率又被写死成 1000。
	selectPrimaryNetwork(&s)

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

// collectStaticExtra 由平台文件实现：Windows 用一次 WMI 查询补内存条与磁盘型号，
// 其他平台不做事（型号缺失时客户端显示占位符）。
