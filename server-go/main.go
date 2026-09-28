package main

import (
	"crypto/subtle"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/mem"
)

const (
	fastInterval = 1 * time.Second  // CPU / 内存 / 磁盘 / 网络
	procInterval = 2 * time.Second  // 进程列表与关注服务
	taskInterval = 60 * time.Second // 计划任务等慢查询
	netInterval  = 60 * time.Second // 主网卡链路速率刷新
	procLimit    = 24               // 快照里带的进程条数，与客户端展示一致
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

	// 慢采集（进程、计划任务）单独跑，结果缓存在这里，避免拖慢 1 秒的快采样节拍。
	collectMu sync.RWMutex
	procsAll  []ProcInfo
	tasksAll  []TaskInfo
	service   *ServiceInfo
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
	var procCSV, portCSV, taskCSV, configPath string
	var setup bool
	flag.StringVar(&cfg.host, "host", "0.0.0.0", "监听地址")
	flag.IntVar(&cfg.port, "port", 8898, "监听端口")
	flag.StringVar(&cfg.token, "token", "", "共享口令，留空则不校验（仅本地调试用）")
	flag.StringVar(&cfg.watchName, "watch-name", "", "要盯的服务显示名，如「麦麦」")
	flag.StringVar(&procCSV, "watch-procs", "", "进程名关键字，逗号分隔，如 maibot,napcat")
	flag.StringVar(&portCSV, "watch-ports", "", "要盯的端口，逗号分隔，如 6099,7998")
	flag.StringVar(&taskCSV, "watch-tasks", "", "计划任务名，逗号分隔")
	flag.StringVar(&configPath, "config", "", "配置文件路径，默认取 exe 同目录的 enf-collector.json")
	flag.BoolVar(&setup, "setup", false, "交互式生成配置文件，写完即退出")
	flag.Parse()

	// 记住哪些开关是命令行显式给的，它们要压过配置文件
	set := map[string]bool{}
	flag.Visit(func(f *flag.Flag) { set[f.Name] = true })

	cfgPath := configPath
	if cfgPath == "" {
		cfgPath = defaultConfigPath()
	}

	// 向导只负责写配置，不常驻
	if setup {
		if err := runSetup(cfgPath); err != nil {
			fmt.Printf("[collector] 生成配置失败: %v\n", err)
			os.Exit(1)
		}
		return
	}

	// 先按命令行解析，再让配置文件补齐没被显式指定的项
	cfg.watchProc = splitCSV(procCSV)
	cfg.watchPort = parsePorts(portCSV)
	cfg.watchTask = splitCSV(taskCSV)

	file, found, err := loadConfigFile(cfgPath)
	if err != nil {
		fmt.Printf("[collector] 读取配置失败: %v\n", err)
		os.Exit(1)
	}
	if found {
		applyFile(&cfg, file, set)
		fmt.Printf("[collector] 已加载配置: %s\n", cfgPath)
	} else if configPath != "" {
		// 明确指定了路径却找不到，属于配置错误，直接报错退出
		fmt.Printf("[collector] 指定的配置文件不存在: %s\n", configPath)
		os.Exit(1)
	}

	log.SetFlags(0)
	fmt.Println("[collector] 读取静态硬件信息（仅此一次）…")
	// 静态采集与网卡探测彼此独立，并行跑，免得串起来等两轮慢查询
	var startupWg sync.WaitGroup
	startupWg.Add(2)
	go func() { defer startupWg.Done(); static = collectStatic() }()
	go func() { defer startupWg.Done(); setNetwork(detectNetwork()) }()
	startupWg.Wait()

	fmt.Printf("[collector] 主机: %s\n", static.Host)
	fmt.Printf("[collector] CPU: %s（%d 线程）\n", static.CPUName, static.Threads)
	fmt.Printf("[collector] 内存: %s %s\n", static.MemTotal, static.MemType)
	fmt.Printf("[collector] 磁盘: %d 个分区\n", len(static.Disks))
	if len(cfg.watchProc) > 0 || len(cfg.watchPort) > 0 || len(cfg.watchTask) > 0 {
		fmt.Printf("[collector] 关注服务: %s | 进程 %v | 端口 %v | 任务 %v\n",
			orDefault(cfg.watchName, "服务"), cfg.watchProc, cfg.watchPort, cfg.watchTask)
	}

	name, link := currentNetwork()
	fmt.Printf("[collector] 主网卡: %s (%.0f Mbps)\n", name, link)

	initPerf()
	initDiskSampler()

	go procLoop()
	if len(cfg.watchTask) > 0 {
		go taskLoop()
	}
	go netLoop()
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
	if cfg.token == "" && cfg.host != "127.0.0.1" && cfg.host != "localhost" {
		fmt.Println("[collector] 警告: 未设置 token 且监听在非本机地址，局域网内任何设备都能读到快照")
	}
	fmt.Printf("[collector] 已监听 http://%s/snapshot (token=%s)\n",
		addr, map[bool]string{true: "已设置", false: "未设置"}[cfg.token != ""])

	// 对外服务一律带超时，读、写、空闲都不无限等
	srv := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       10 * time.Second,
		WriteTimeout:      10 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	log.Fatal(srv.ListenAndServe())
}

// ================= 后台采集 =================

// procLoop 每 2 秒采一次进程与关注服务，放进缓存。
func procLoop() {
	for {
		ps := collectProcs()

		var svc *ServiceInfo
		if len(cfg.watchProc) > 0 || len(cfg.watchPort) > 0 || len(cfg.watchTask) > 0 {
			svc = collectService(ps)
		}

		collectMu.Lock()
		procsAll = ps
		service = svc
		collectMu.Unlock()

		time.Sleep(procInterval)
	}
}

// taskLoop 每 60 秒刷一次计划任务状态。
func taskLoop() {
	for {
		ts := collectTasks()
		collectMu.Lock()
		tasksAll = ts
		collectMu.Unlock()
		time.Sleep(taskInterval)
	}
}

// netLoop 定期重探主网卡：Wi-Fi 会中途重新协商速率。
func netLoop() {
	for {
		time.Sleep(netInterval)
		setNetwork(detectNetwork())
	}
}

func sampleLoop() {
	// CPU 百分比需要至少两次调用才能出差值，先预热
	_, _ = cpu.Percent(0, false)

	prevNet := readNet()
	prevAt := time.Now()

	ticker := time.NewTicker(fastInterval)
	defer ticker.Stop()

	for range ticker.C {
		perfTick()
		now := time.Now()
		dt := now.Sub(prevAt).Seconds()
		if dt < 0.2 {
			dt = 0.2
		}
		prevAt = now

		curNet := readNet()

		collectMu.RLock()
		procs := procsAll
		tasks := tasksAll
		svc := service
		collectMu.RUnlock()

		// 快照里只带前 procLimit 个进程；服务匹配用的是全量列表
		snapProcs := procs
		if len(snapProcs) > procLimit {
			snapProcs = snapProcs[:procLimit]
		}

		s := &Snapshot{
			Ts:       float64(now.UnixMilli()) / 1000.0,
			Interval: fastInterval.Seconds(),
			Live:     true,
			CPU:      readCPU(),
			GPU:      readGPU(),
			Mem:      readMem(),
			Disks:    readDisks(dt),
			Net:      readNetRates(prevNet, curNet, dt),
			Procs:    snapProcs,
			Server:   readServer(svc, tasks),
		}

		prevNet = curNet

		stateMu.Lock()
		snap = s
		stateMu.Unlock()
	}
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
	a := r.Header.Get("Authorization")
	if !strings.HasPrefix(a, "Bearer ") {
		return false
	}
	got := strings.TrimSpace(strings.TrimPrefix(a, "Bearer "))
	return subtle.ConstantTimeCompare([]byte(got), []byte(cfg.token)) == 1
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func readServer(svc *ServiceInfo, tasks []TaskInfo) *ServerInfo {
	up, _ := host.Uptime()
	srv := &ServerInfo{
		Host:        static.Host,
		UptimeHours: float64(up) / 3600.0,
	}
	if svc != nil {
		// 复制一份再挂任务，避免改动 procLoop 正在维护的缓存对象
		c := *svc
		if tasks == nil {
			c.Tasks = []TaskInfo{}
		} else {
			c.Tasks = tasks
		}
		srv.Service = &c
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
	// gopsutil 的 cpu.Info() 在 Windows 上给的是标称上限，不是当前频率。
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
