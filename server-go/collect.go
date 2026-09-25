package main

import (
	"math"
	"runtime"
	"sort"
	"strconv"
	"strings"

	"github.com/shirou/gopsutil/v4/disk"
	gnet "github.com/shirou/gopsutil/v4/net"
	"github.com/shirou/gopsutil/v4/process"
)

// ================= 网络 =================

type netSample struct{ recv, sent uint64 }

func readNet() netSample {
	cs, err := gnet.IOCounters(false)
	if err != nil || len(cs) == 0 {
		return netSample{}
	}
	return netSample{recv: cs[0].BytesRecv, sent: cs[0].BytesSent}
}

func readNetRates(prev, cur netSample, dt float64) NetInfo {
	down := math.Max(0, float64(cur.recv-prev.recv)*8/1e6/dt)
	up := math.Max(0, float64(cur.sent-prev.sent)*8/1e6/dt)
	link := static.NetLink
	if link <= 0 {
		link = 1000
	}
	util := 0.0
	if m := math.Max(down, up); m > 0 {
		util = math.Min(100, m/link*100)
	}
	return NetInfo{
		Name: static.NetName,
		Down: round2(down),
		Up:   round2(up),
		Link: link,
		Util: round1(util),
	}
}

// ================= 磁盘 =================

func readDiskIO() map[string]disk.IOCountersStat {
	m, err := disk.IOCounters()
	if err != nil {
		return map[string]disk.IOCountersStat{}
	}
	return m
}

func readDisks(prev, cur map[string]disk.IOCountersStat, dt float64) []DiskInfo {
	out := make([]DiskInfo, 0, len(static.Disks))
	for i, d := range static.Disks {
		u, err := disk.Usage(d.Mount)
		if err != nil {
			continue
		}
		rw := 0.0
		if d.Phys != "" {
			a, okA := prev[d.Phys]
			b, okB := cur[d.Phys]
			if okA && okB {
				rw = (float64(b.ReadBytes-a.ReadBytes) + float64(b.WriteBytes-a.WriteBytes)) / 1048576.0 / dt
			}
		}
		const gb = 1024 * 1024 * 1024
		out = append(out, DiskInfo{
			Name:  "磁盘 " + strconv.Itoa(i) + " (" + d.Letter + ")",
			Used:  round1(float64(u.Used) / gb),
			Total: round1(float64(u.Total) / gb),
			Pct:   round1(u.UsedPercent),
			Util:  round1(u.UsedPercent),
			Rw:    strconv.FormatFloat(math.Max(0, rw), 'f', 0, 64) + " MB/s",
			Media: d.Media,
			Model: d.Model,
		})
	}
	return out
}

// ================= 进程 =================
// 进程 CPU 百分比依赖同一 Process 对象的两次采样差值，故按 pid 缓存对象。

var procCache = map[int32]*process.Process{}

func collectProcs() []ProcInfo {
	ps, err := process.Processes()
	if err != nil {
		return nil
	}
	ncpu := float64(runtime.NumCPU())
	if ncpu < 1 {
		ncpu = 1
	}

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

		exe, _ := p.Exe()
		out = append(out, ProcInfo{
			Pid:  p.Pid,
			Name: name,
			Exe:  exe,
			Mem:  round1(rss),
			CPU:  round1(cpuPct / ncpu),
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
	if len(out) > procLimit {
		out = out[:procLimit]
	}
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
