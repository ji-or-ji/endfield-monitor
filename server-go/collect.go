package main

import (
	"fmt"
	"math"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"

	"github.com/shirou/gopsutil/v4/disk"
	gnet "github.com/shirou/gopsutil/v4/net"
	"github.com/shirou/gopsutil/v4/process"
)

// ================= 网络 =================

type netSample struct{ recv, sent uint64 }

// 名字对不上时退回聚合值，只提醒一次，不每次采样都刷屏。
var netFallbackOnce sync.Once

// readNet 只统计主网卡。
//
// 优先走平台特化的单接口读取：gopsutil 的 IOCounters 会先枚举全部网卡
// （这台机器上几十块虚拟网卡），再逐块查一遍，实测每次 10ms；按索引直接查
// 一块只要几微秒。取不到时退回 gopsutil，最后退回全部网卡的聚合值。
func readNet() netSample {
	if s, ok := readNetIf(); ok {
		return s
	}

	name, _ := currentNetwork()
	if name != "" {
		if cs, err := gnet.IOCounters(true); err == nil {
			for _, c := range cs {
				if c.Name == name {
					return netSample{recv: c.BytesRecv, sent: c.BytesSent}
				}
			}
		}
		netFallbackOnce.Do(func() {
			fmt.Printf("[collector] 主网卡 %q 未在计数器里找到，网络数据退回全部网卡聚合\n", name)
		})
	}
	if cs, err := gnet.IOCounters(false); err == nil && len(cs) > 0 {
		return netSample{recv: cs[0].BytesRecv, sent: cs[0].BytesSent}
	}
	return netSample{}
}

// rateMbps 把两次采样的字节差换算成 Mbps。
// 计数器回绕或网卡重置时 cur < prev，直接当 0，不然差值会翻成一个天文数字。
func rateMbps(cur, prev uint64, dt float64) float64 {
	if cur < prev || dt <= 0 {
		return 0
	}
	return float64(cur-prev) * 8 / 1e6 / dt
}

func readNetRates(prev, cur netSample, dt float64) NetInfo {
	name, link := currentNetwork()
	if link <= 0 {
		link = 1000
	}
	down := rateMbps(cur.recv, prev.recv, dt)
	up := rateMbps(cur.sent, prev.sent, dt)
	// 单块网卡的吞吐不可能超过链路速率，超过说明这一帧不可信，丢掉
	if math.Max(down, up) > link*1.5 {
		down, up = 0, 0
	}
	util := 0.0
	if m := math.Max(down, up); m > 0 {
		util = math.Min(100, m/link*100)
	}
	return NetInfo{
		Name: name,
		Down: round2(down),
		Up:   round2(up),
		Link: link,
		Util: round1(util),
	}
}

// ================= 磁盘 =================

var diskPrev map[string]disk.IOCountersStat

func readDiskIO() map[string]disk.IOCountersStat {
	m, err := disk.IOCounters()
	if err != nil {
		return map[string]disk.IOCountersStat{}
	}
	return m
}

// initDiskSampler 只在 PDH 兜底路径需要时预热一帧。
func initDiskSampler() {
	if diskFallbackNeeded() {
		diskPrev = readDiskIO()
	}
}

func readDisks(dt float64) []DiskInfo {
	cur := diskPrev
	if diskFallbackNeeded() {
		cur = readDiskIO()
	}

	out := make([]DiskInfo, 0, len(static.Disks))
	for i, d := range static.Disks {
		u, err := disk.Usage(d.Mount)
		if err != nil {
			continue
		}
		rw := 0.0
		busy := 0.0
		// 优先走 PDH（与任务管理器同源的计数器）。
		// gopsutil 的 IOCounters 靠 IOCTL_DISK_PERFORMANCE，需注册表开关且要管理员，
		// 实测增量几乎不更新，只能当兜底。
		if r, bu, ok := perfDiskSample(d.Letter); ok {
			rw, busy = r, bu
		} else if a, okA := diskPrev[d.Letter]; okA {
			if b, okB := cur[d.Letter]; okB {
				if b.ReadBytes >= a.ReadBytes && b.WriteBytes >= a.WriteBytes && dt > 0 {
					dBytes := (b.ReadBytes - a.ReadBytes) + (b.WriteBytes - a.WriteBytes)
					rw = float64(dBytes) / 1048576.0 / dt
				}
				// ReadTime/WriteTime 单位为毫秒，增量占窗口的比例即忙率
				dMs := int64(b.ReadTime) - int64(a.ReadTime) + int64(b.WriteTime) - int64(a.WriteTime)
				if dMs > 0 {
					busy = math.Min(100, float64(dMs)/(dt*1000.0)*100)
				}
			}
		}
		const gb = 1024 * 1024 * 1024
		out = append(out, DiskInfo{
			Name:  "磁盘 " + strconv.Itoa(i) + " (" + d.Letter + ")",
			Used:  round1(float64(u.Used) / gb),
			Total: round1(float64(u.Total) / gb),
			Pct:   round1(u.UsedPercent), // 容量占用
			Util:  round1(busy),          // IO 忙率，与容量占用区分开
			Rw:    strconv.FormatFloat(math.Max(0, rw), 'f', 0, 64) + " MB/s",
			Media: d.Media,
			Model: d.Model,
		})
	}

	diskPrev = cur
	return out
}

// ================= 进程 =================
// 进程 CPU 百分比依赖同一 Process 对象的两次采样差值，故按 pid 缓存对象。

var procCache = map[int32]*process.Process{}

// collectProcs 返回全部进程（按 CPU 降序）。截断到展示条数由上层负责，
// 因为关注服务的匹配需要看全量，不能只看榜上前几名。
func collectProcs() []ProcInfo {
	ps, err := process.Processes()
	if err != nil {
		return nil
	}
	ncpu := float64(runtime.NumCPU())
	if ncpu < 1 {
		ncpu = 1
	}

	// 一次枚举全部窗口，取各进程的可见主窗口标题
	titles := windowTitles()

	out := make([]ProcInfo, 0, len(ps))
	seen := make(map[int32]bool, len(ps))

	for _, p := range ps {
		name, err := p.Name()
		if err != nil || name == "" {
			continue
		}
		mi, err := p.MemoryInfo()
		if err != nil || mi == nil {
			continue
		}
		rss := float64(mi.RSS) / 1048576.0
		if rss < 20 {
			continue
		}

		obj, ok := procCache[p.Pid]
		if !ok {
			obj = p
			procCache[p.Pid] = obj
			_, _ = obj.CPUPercent() // 预热，下一次才有差值
		}
		cpuPct, err := obj.CPUPercent()
		if err != nil {
			cpuPct = 0
		}

		// 可执行文件路径：客户端目前没消费，但快照里保留着，供按路径区分同名
		// 进程、白名单过滤之类用途。全表读一次 PEB 约 7ms，算主要模式的固定开销；
		// 轻量模式下采样本身就稀疏，这点开销随之摊薄。
		exe, _ := p.Exe()
		out = append(out, ProcInfo{
			Pid:   p.Pid,
			Name:  name,
			Exe:   exe,
			Mem:   round1(rss),
			CPU:   round1(cpuPct / ncpu),
			Title: titles[p.Pid],
		})
		seen[p.Pid] = true
	}

	for pid := range procCache {
		if !seen[pid] {
			delete(procCache, pid)
		}
	}

	sort.Slice(out, func(i, j int) bool {
		if out[i].CPU != out[j].CPU {
			return out[i].CPU > out[j].CPU
		}
		return out[i].Mem > out[j].Mem
	})
	return out
}

// ================= 关注的服务 =================

func collectService(procs []ProcInfo) *ServiceInfo {
	svc := &ServiceInfo{
		Name:  orDefault(cfg.watchName, "服务"),
		Ports: map[string]bool{},
	}

	if len(cfg.watchProc) > 0 {
		keys := make([]string, len(cfg.watchProc))
		for i, k := range cfg.watchProc {
			keys[i] = strings.ToLower(k)
		}
		n := 0
		for _, p := range procs {
			low := strings.ToLower(p.Name)
			for _, k := range keys {
				if strings.Contains(low, k) {
					n++
					break
				}
			}
		}
		svc.Matches = n
		svc.Running = n > 0
	}

	if len(cfg.watchPort) > 0 {
		listening := listeningPorts()
		for _, p := range cfg.watchPort {
			svc.Ports[strconv.Itoa(p)] = listening[p]
		}
	}
	return svc
}

func listeningPorts() map[int]bool {
	out := map[int]bool{}
	cs, err := gnet.Connections("inet")
	if err != nil {
		return out
	}
	for _, c := range cs {
		if c.Status == "LISTEN" {
			out[int(c.Laddr.Port)] = true
		}
	}
	return out
}

// ================= 计划任务（平台相关实现在 static_*.go） =================
