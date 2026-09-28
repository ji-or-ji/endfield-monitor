package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/mem"
)

const (
	fastInterval = 1 * time.Second  // CPU / 内存 / 磁盘 / 网络
	procInterval = 2 * time.Second  // 进程列表
	taskInterval = 60 * time.Second // 计划任务等慢查询
	procLimit    = 40
)

type config struct {
	host      string
	port      int
	token     string
	watchName string
	watchProc []string
	watchPort []int
	watchTask []string
}

var (
	cfg    config
	static staticInfo

	stateMu sync.RWMutex
	snap    *Snapshot
)

func splitCSV(s string) []string {
	parts := strings.Split(s, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		if v := strings.TrimSpace(p); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func main() {
	var procCSV, portCSV, taskCSV string
	flag.StringVar(&cfg.host, "host", "0.0.0.0", "监听地址")
	flag.IntVar(&cfg.port, "port", 8898, "监听端口")
	flag.StringVar(&cfg.token, "token", "", "共享口令，留空则不校验（仅本地调试用）")
	flag.StringVar(&cfg.watchName, "watch-name", "", "要盯的服务显示名，如「麦麦」")
	flag.StringVar(&procCSV, "watch-procs", "", "进程名关键字，逗号分隔，如 maibot,napcat")
	flag.StringVar(&portCSV, "watch-ports", "", "要盯的端口，逗号分隔，如 6099,7998")
	flag.StringVar(&taskCSV, "watch-tasks", "", "计划任务名，逗号分隔")
	flag.Parse()

	cfg.watchProc = splitCSV(procCSV)
	for _, s := range splitCSV(portCSV) {
		var p int
		if _, err := fmt.Sscanf(s, "%d", &p); err == nil {
			cfg.watchPort = append(cfg.watchPort, p)
		}
	}
	cfg.watchTask = splitCSV(taskCSV)

	log.SetFlags(0)
	fmt.Println("[collector] 读取静态硬件信息（仅此一次）…")
	static = collectStatic()
	fmt.Printf("[collector] 主机: %s\n", static.Host)
	fmt.Printf("[collector] CPU: %s（%d 线程）\n", static.CPUName, static.Threads)
	fmt.Printf("[collector] 内存: %s %s\n", static.MemTotal, static.MemType)
	fmt.Printf("[collector] 磁盘: %d 个分区\n", len(static.Disks))
	if len(cfg.watchProc) > 0 || len(cfg.watchPort) > 0 || len(cfg.watchTask) > 0 {
		fmt.Printf("[collector] 关注服务: %s | 进程 %v | 端口 %v | 任务 %v\n",
			orDefault(cfg.watchName, "服务"), cfg.watchProc, cfg.watchPort, cfg.watchTask)
	}

	initPerf()

	go sampleLoop()

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, 200, map[string]any{"ok": true, "service": "enf-collector"})
	})
	mux.HandleFunc("/snapshot", func(w http.ResponseWriter, r *http.Request) {
		if !authorized(r) {
			writeJSON(w, 401, map[string]any{"error": "unauthorized"})
			return
		}
		stateMu.RLock()
		s := snap
		stateMu.RUnlock()
		if s == nil {
			writeJSON(w, 503, map[string]any{"live": false, "error": "采样尚未就绪"})
			return
		}
		writeJSON(w, 200, s)
	})

	addr := fmt.Sprintf("%s:%d", cfg.host, cfg.port)
	fmt.Printf("[collector] 已监听 http://%s/snapshot (token=%s)\n",
		addr, map[bool]string{true: "已设置", false: "未设置"}[cfg.token != ""])
	log.Fatal(http.ListenAndServe(addr, mux))
}

func orDefault(s, def string) string {
	if strings.TrimSpace(s) == "" {
		return def
	}
	return s
}

func authorized(r *http.Request) bool {
	if cfg.token == "" {
		return true
	}
	if a := r.Header.Get("Authorization"); strings.HasPrefix(a, "Bearer ") {
		if strings.TrimSpace(strings.TrimPrefix(a, "Bearer ")) == cfg.token {
			return true
		}
	}
	return r.URL.Query().Get("token") == cfg.token
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func sampleLoop() {
	// CPU 百分比需要至少两次调用才能出差值，先预热
	_, _ = cpu.Percent(0, false)

	var (
		lastProcsAt = time.Time{}
		lastTasksAt = time.Time{}
		tasks       []TaskInfo
		procs       []ProcInfo
		prevNet     = readNet(static.NetName)
		prevDisk    = readDiskIO()
		prevAt      = time.Now()
	)

	for {
		perfTick()
		now := time.Now()
		dt := now.Sub(prevAt).Seconds()
		if dt < 0.2 {
			dt = 0.2
		}
		prevAt = now

		curNet := readNet(static.NetName)
		curDisk := readDiskIO()

		if now.Sub(lastProcsAt) >= procInterval {
			lastProcsAt = now
			procs = collectProcs()
		}
		if len(cfg.watchTask) > 0 && now.Sub(lastTasksAt) >= taskInterval {
			lastTasksAt = now
			tasks = collectTasks()
		}

		s := &Snapshot{
			Ts:       float64(now.UnixMilli()) / 1000.0,
			Interval: fastInterval.Seconds(),
			Live:     true,
			CPU:      readCPU(),
			GPU:      readGPU(),
			Mem:      readMem(),
			Disks:    readDisks(prevDisk, curDisk, dt),
			Net:      readNetRates(prevNet, curNet, dt),
			Procs:    procs,
			Server:   readServer(procs, tasks),
		}

		prevNet, prevDisk = curNet, curDisk

		stateMu.Lock()
		snap = s
		stateMu.Unlock()

		time.Sleep(fastInterval)
	}
}

func readServer(procs []ProcInfo, tasks []TaskInfo) *ServerInfo {
	up, _ := host.Uptime()
	srv := &ServerInfo{
		Host:        static.Host,
		UptimeHours: float64(up) / 3600.0,
	}
	if len(cfg.watchProc) > 0 || len(cfg.watchPort) > 0 || len(cfg.watchTask) > 0 {
		svc := collectService(procs)
		svc.Tasks = tasks
		srv.Service = svc
	}
	return srv
}

func readMem() MemInfo {
	vm, err := mem.VirtualMemory()
	if err != nil {
		return MemInfo{}
	}
	const gb = 1024 * 1024 * 1024
	return MemInfo{
		Used:  round1(float64(vm.Used) / gb),
		Total: round1(float64(vm.Total) / gb),
		Pct:   round1(vm.UsedPercent),
		Speed: static.MemSpeed,
		Type:  static.MemType,
	}
}

func readCPU() CPUInfo {
	util := 0.0
	if v, err := cpu.Percent(0, false); err == nil && len(v) > 0 {
		util = round1(v[0])
	}
	// 实时频率走性能计数器（Processor Frequency）。
	// gopsutil 的 cpu.Info() 在 Windows 上给的是标称上限，不是当前频率，
	// 所以不再把它当实时值用。
	freq := static.CPUBase
	if f, ok := perfCPUFreqGHz(); ok {
		freq = f
	}
	return CPUInfo{
		Name:    static.CPUName,
		Threads: static.Threads,
		Util:    util,
		Freq:    freq,
		Base:    static.CPUBase,
		Max:     static.CPUMax,
	}
}

func readGPU() GPUInfo {
	g := GPUInfo{Name: "—"}
	if static.GPUName != "" {
		g.Name = static.GPUName
	}
	if util, memMB, ok := perfGPUSample(); ok {
		g.OK = true
		g.Util = round1(util)
		used := round1(memMB)
		g.MemUsed = &used
	}
	return g
}

func round1(v float64) float64 { return float64(int(v*10+0.5)) / 10 }
func round2(v float64) float64 { return float64(int(v*100+0.5)) / 100 }
