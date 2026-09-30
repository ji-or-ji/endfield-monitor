package main

import (
	"crypto/subtle"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
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

	// 轻量模式（--lite）下：超过这段时间没人拉快照，就认为没人在看，两条循环都放慢。
	// 主要模式用不到这几个值，始终按 fastInterval / procInterval 固定节拍采。
	procIdleAfter    = 30 * time.Second
	procIdleInterval = 30 * time.Second
	fastIdleInterval = 5 * time.Second
)

type config struct {
	host      string
	port      int
	token     string
	watchName string
	watchProc []string
	watchPort []int
	watchTask []string
	lite      bool
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

	// 有人拉快照就说明有人在看；轻量模式下据此放慢采样。
	lastPull atomic.Int64 // Unix 秒
	procKick = make(chan struct{}, 1)
	fastKick = make(chan struct{}, 1)
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
	var probeIconPid int
	flag.StringVar(&cfg.host, "host", "0.0.0.0", "监听地址")
	flag.IntVar(&cfg.port, "port", 8898, "监听端口")
	flag.StringVar(&cfg.token, "token", "", "共享口令，留空则不校验（仅本地调试用）")
	flag.StringVar(&cfg.watchName, "watch-name", "", "要盯的服务显示名，如「麦麦」")
	flag.StringVar(&procCSV, "watch-procs", "", "进程名关键字，逗号分隔，如 maibot,napcat")
	flag.StringVar(&portCSV, "watch-ports", "", "要盯的端口，逗号分隔，如 6099,7998")
	flag.StringVar(&taskCSV, "watch-tasks", "", "计划任务名，逗号分隔")
	flag.StringVar(&configPath, "config", "", "配置文件路径，默认取 exe 同目录的 enf-collector.json")
	flag.BoolVar(&setup, "setup", false, "交互式生成配置文件，写完即退出")
	flag.IntVar(&probeIconPid, "probe-icon", 0, "诊断用：打印该 pid 的图标查找全过程后退出")
	flag.BoolVar(&cfg.lite, "lite", false, "轻量模式：没人拉快照时自动放慢采样（默认关闭，始终按固定节拍）")
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

	// 诊断模式：只把图标查找过程打出来，不常驻
	if probeIconPid > 0 {
		probeIcon(int32(probeIconPid))
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
	if cfg.lite {
		fmt.Println("[collector] 采样模式: 轻量（没人拉快照时自动放慢）")
	} else {
		fmt.Println("[collector] 采样模式: 主要（始终按固定节拍）")
	}

	if plusBuild {
		fmt.Println("[collector] 版本: 增强版（plus），提供启停应用与取图标")
		if cfg.token == "" {
			fmt.Println("[collector] 警告: 增强版未设置共享口令，启停操作会被拒绝——先配上再说")
		}
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
		markPull()
		stateMu.RLock()
		s := snap
		stateMu.RUnlock()
		if s == nil {
			writeJSON(w, 503, map[string]any{"live": false, "error": "采样尚未就绪"})
			return
		}
		writeJSON(w, 200, s)
	})

	// 增强版才有：启停应用、取图标。普通版连路由都不注册。
	if plusBuild {
		mux.HandleFunc("/app/stop", handleAppStop)
		mux.HandleFunc("/app/restart", handleAppRestart)
		mux.HandleFunc("/app/icon", handleAppIcon)
	}

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

// procLoop 采进程与关注服务，放进缓存。
//
// 全量枚举进程一次要十几毫秒（NtQuerySystemInformation 会把线程一起快照），
// 是采集端最贵的一项。没人在看的时候没必要 2 秒采一次，放慢到 30 秒；
// 客户端一拉快照就把它叫醒，补一次新的，界面上看不出差别。
func procLoop() {
	// 状态切换时说一声，方便看出它什么时候在偷懒、什么时候真在干活
	wasIdle := false
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

		// 只有轻量模式才看“有没有人在看”；主要模式 idle 恒为 false，
		// 于是日志不会打、间隔也不会变。
		idle := cfg.lite && watchIdle()
		if idle != wasIdle {
			if idle {
				fmt.Println("[collector] 没人拉快照，进程采样放慢到 30 秒")
			} else {
				fmt.Println("[collector] 有客户端在拉，进程采样恢复 2 秒")
			}
			wasIdle = idle
		}

		d := procInterval
		if idle {
			d = procIdleInterval
		}
		select {
		case <-time.After(d):
		case <-procKick:
		}
	}
}

// watchIdle 判断是不是已经有一阵子没人拉过快照。
func watchIdle() bool {
	t := lastPull.Load()
	return t == 0 || time.Since(time.Unix(t, 0)) > procIdleAfter
}

// markPull 记录一次快照拉取。轻量模式下，只在“从没人看变成有人看”的那一刻
// 叫醒采样循环，平时连拉不打扰它们，免得把节奏压成每拉一次就采一次。
func markPull() {
	if !cfg.lite {
		// 主要模式始终按固定节拍采，没什么要唤醒的
		return
	}
	now := time.Now()
	prev := lastPull.Swap(now.Unix())
	if prev == 0 || now.Sub(time.Unix(prev, 0)) > procIdleAfter {
		for _, ch := range []chan struct{}{procKick, fastKick} {
			select {
			case ch <- struct{}{}:
			default:
			}
		}
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

// 增强版动过手的记录，留着便于事后查。只保留最近 auditLimit 条。
const auditLimit = 50

var (
	auditMu  sync.Mutex
	auditLog []AuditEntry
)

// recordAudit 记一条操作。
func recordAudit(e AuditEntry) {
	auditMu.Lock()
	defer auditMu.Unlock()
	auditLog = append(auditLog, e)
	if len(auditLog) > auditLimit {
		auditLog = auditLog[len(auditLog)-auditLimit:]
	}
}

// recentAudit 返回现有记录的副本；一次都没动过手时返回 nil（快照里就不出现这个键）。
func recentAudit() []AuditEntry {
	auditMu.Lock()
	defer auditMu.Unlock()
	if len(auditLog) == 0 {
		return nil
	}
	out := make([]AuditEntry, len(auditLog))
	copy(out, auditLog)
	return out
}

// auditOf 拼一条审计记录，进程名取当前快照里的，便于事后认人。
func auditOf(at, action string, pid int32, r *http.Request, ok bool, errMsg string) AuditEntry {
	e := AuditEntry{At: at, Action: action, Pid: pid, From: r.RemoteAddr, OK: ok, Err: errMsg}
	if p := lookupProc(pid); p != nil {
		e.Name = p.Name
	}
	return e
}

// capabilities 列出这一份构建提供的能力。普通版为空；
// 增强版（-tags plus）才有，客户端据此切增强模式。
func capabilities() []string {
	if !plusBuild {
		return nil
	}
	return []string{"app.stop", "app.restart", "app.icon"}
}

// allowAppControl 判断这次请求能不能动别人的机器。
// 增强版的两道硬门槛：先过口令校验，而且服务端必须真的配了口令。
// 否则任何知道地址的人都能停掉你的进程。
func allowAppControl(w http.ResponseWriter, r *http.Request) bool {
	if !authorized(r) {
		writeJSON(w, 401, map[string]any{"error": "unauthorized"})
		return false
	}
	if cfg.token == "" {
		writeJSON(w, 403, map[string]any{"error": "增强操作要求服务端先配置共享口令（--token 或配置文件）"})
		return false
	}
	return true
}

// appPidArg 取出并校验 ?pid=。
func appPidArg(w http.ResponseWriter, r *http.Request) (int32, bool) {
	v, err := strconv.Atoi(r.URL.Query().Get("pid"))
	if err != nil || v <= 0 {
		writeJSON(w, 400, map[string]any{"error": "缺少合法的 pid"})
		return 0, false
	}
	return int32(v), true
}

// lookupProc 在最近一次采到的全量进程里找这个 pid。
// 动手指令只认这里面的 pid，外部传不进任意路径。
func lookupProc(pid int32) *ProcInfo {
	collectMu.RLock()
	defer collectMu.RUnlock()
	for i := range procsAll {
		if procsAll[i].Pid == pid {
			return &procsAll[i]
		}
	}
	return nil
}

// handleAppStop 结束一个进程（含其子进程）。
func handleAppStop(w http.ResponseWriter, r *http.Request) {
	if !allowAppControl(w, r) {
		return
	}
	pid, ok := appPidArg(w, r)
	if !ok {
		return
	}
	at := time.Now().Format("2006-01-02 15:04:05")
	if err := stopApp(pid); err != nil {
		fmt.Printf("[collector] %s 结束进程 %d 失败：%v\n", at, pid, err)
		recordAudit(auditOf(at, "stop", pid, r, false, err.Error()))
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}
	fmt.Printf("[collector] %s 已结束进程 %d（来自 %s）\n", at, pid, r.RemoteAddr)
	recordAudit(auditOf(at, "stop", pid, r, true, ""))
	writeJSON(w, 200, map[string]any{"ok": true})
}

// handleAppRestart 用原路径与参数重启一个进程。
func handleAppRestart(w http.ResponseWriter, r *http.Request) {
	if !allowAppControl(w, r) {
		return
	}
	pid, ok := appPidArg(w, r)
	if !ok {
		return
	}
	at := time.Now().Format("2006-01-02 15:04:05")
	if err := restartApp(pid); err != nil {
		fmt.Printf("[collector] %s 重启进程 %d 失败：%v\n", at, pid, err)
		recordAudit(auditOf(at, "restart", pid, r, false, err.Error()))
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}
	fmt.Printf("[collector] %s 已重启进程 %d（来自 %s）\n", at, pid, r.RemoteAddr)
	recordAudit(auditOf(at, "restart", pid, r, true, ""))
	writeJSON(w, 200, map[string]any{"ok": true})
}

// handleAppIcon 返回该进程可执行文件的图标（PNG）。
func handleAppIcon(w http.ResponseWriter, r *http.Request) {
	if !authorized(r) {
		writeJSON(w, 401, map[string]any{"error": "unauthorized"})
		return
	}
	pid, ok := appPidArg(w, r)
	if !ok {
		return
	}
	// exe 只是走近路：Linux 那边拿不到时会自己从 /proc 读。
	// 图标不会变，按 exe 路径缓存；拿不到路径时就不缓存。
	var exe string
	if p := lookupProc(pid); p != nil {
		exe = p.Exe
	}
	if data, mime, ok := cacheGet(exe); ok {
		writeIcon(w, data, mime)
		return
	}
	data, mime, err := appIcon(pid, exe)
	if err != nil {
		writeJSON(w, 404, map[string]any{"error": err.Error()})
		return
	}
	if exe != "" {
		cachePut(exe, data, mime)
	}
	writeIcon(w, data, mime)
}

// writeIcon 把图标发出去。图标不会变，让客户端放心缓存。
func writeIcon(w http.ResponseWriter, data []byte, mime string) {
	w.Header().Set("Content-Type", mime)
	w.Header().Set("Cache-Control", "max-age=86400")
	w.WriteHeader(200)
	_, _ = w.Write(data)
}

func sampleLoop() {
	// CPU 百分比需要至少两次调用才能出差值，先预热
	_, _ = cpu.Percent(0, false)

	prevNet := readNet()
	prevAt := time.Now()

	for {
		// 轻量模式下没人看时不必每秒采一次，有客户端来拉再立刻恢复
		d := fastInterval
		if cfg.lite && watchIdle() {
			d = fastIdleInterval
		}
		select {
		case <-time.After(d):
		case <-fastKick:
		}

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
			Interval: dt, // 实际间隔；空闲期会变长，速率按真实 dt 算
			Live:     true,
			CPU:      readCPU(),
			GPU:      readGPU(),
			Mem:      readMem(),
			Disks:    readDisks(dt),
			Net:      readNetRates(prevNet, curNet, dt),
			Battery:  readBatteryInfo(),
			Procs:    snapProcs,
			Server:   readServer(svc, tasks),

			Capabilities: capabilities(),
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
		Audit:       recentAudit(),
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

// 观测到过的最高实时频率。睿频上限在 Windows 上没有可靠的标准接口，
// 与其编一个，不如把实际见到的峰值报出来。
var cpuPeakGHz float64

func readCPU() CPUInfo {
	util := 0.0
	if v, err := cpu.Percent(0, false); err == nil && len(v) > 0 {
		util = round1(v[0])
	}
	// 实时频率 = 基准频率 × % Processor Performance。
	// 直接读“Processor Frequency”计数器拿到的是各核加权平均，看不出睿频。
	freq := static.CPUBase
	if pct, ok := perfCPUPerfPct(); ok {
		freq = round2(static.CPUBase * pct / 100.0)
		if freq > cpuPeakGHz {
			cpuPeakGHz = freq
		}
	}
	return CPUInfo{
		Name:    static.CPUName,
		Threads: static.Threads,
		Util:    util,
		Freq:    freq,
		Peak:    cpuPeakGHz,
		Base:    static.CPUBase,
	}
}

func readGPU() GPUInfo {
	g := GPUInfo{Name: "—"}
	if static.GPUName != "" {
		g.Name = static.GPUName
	}
	g.MemTotal = round1(static.GPUMemMB)
	if util, memMB, ok := perfGPUSample(); ok {
		g.OK = true
		g.Util = round1(util)
		used := round1(memMB)
		g.MemUsed = &used
	}
	return g
}

// readBatteryInfo 把平台相关的实时状态与启动时取到的容量信息拼在一起。
func readBatteryInfo() BatteryInfo {
	b := readBattery()
	if !b.Present {
		return b
	}
	b.FullMWh = float64(static.BatteryFull)
	b.DesignMWh = float64(static.BatteryDes)
	if b.FullMWh > 0 && b.DesignMWh > 0 {
		b.HealthPct = round1(b.FullMWh / b.DesignMWh * 100)
	}
	return b
}

func round1(v float64) float64 { return float64(int(v*10+0.5)) / 10 }
func round2(v float64) float64 { return float64(int(v*100+0.5)) / 100 }
