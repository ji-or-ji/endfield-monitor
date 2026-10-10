package main

import (
	"fmt"
	"os"
	"runtime"
	"strconv"
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
		// gopsutil 报的 Mhz 就是基准频率（Windows 上实测如此），
		// 睿频上限没有可靠接口，快照里只给基准与实测峰值。
		if infos[0].Mhz > 0 {
			s.CPUBase = round2(infos[0].Mhz / 1000.0)
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

	// 电池容量只在启动时取一次（WMI / sysfs，运行期不重复查）
	s.BatteryDes, s.BatteryFull = batteryCapacity()
	return s
}

// fallbackNetwork 遍历接口，取第一个非回环且带地址的，速率未知按 1000 估算。
// Windows 上真选网卡由 detectNetwork 走 Get-NetAdapter，这里只是最后的兜底。
func fallbackNetwork() (string, float64) {
	if ifs, err := gnet.Interfaces(); err == nil {
		for _, itf := range ifs {
			if len(itf.Addrs) == 0 || isLoopbackIface(itf) {
				continue
			}
			return itf.Name, linkSpeed(itf.Name)
		}
	}
	return "网络", 1000
}

// linkSpeed 取网卡链路速率（Mbps）。Linux 读 /sys/class/net/<iface>/speed；
// 读不到或是别的系统就按 1000 估算。Wi-Fi 重协商后由 netLoop 重新探测。
func linkSpeed(name string) float64 {
	if runtime.GOOS == "linux" {
		if b, err := os.ReadFile("/sys/class/net/" + name + "/speed"); err == nil {
			if v, err := strconv.Atoi(strings.TrimSpace(string(b))); err == nil && v > 0 {
				return float64(v)
			}
		}
	}
	return 1000
}

// isLoopbackIface 判一个接口是不是回环。不能只看名字：Windows 叫 "Loopback…"，
// Linux 叫 "lo"，BSD 叫 "lo0"，光认 "Loopback" 前缀在 Linux 上会漏掉 lo，
// 回环就被当成主网卡。优先认 Flags 里的 loopback 标志，名字只作兜底。
func isLoopbackIface(itf gnet.InterfaceStat) bool {
	if itf.Name == "lo" || itf.Name == "lo0" || strings.HasPrefix(itf.Name, "Loopback") {
		return true
	}
	for _, f := range itf.Flags {
		if strings.EqualFold(f, "loopback") {
			return true
		}
	}
	return false
}
