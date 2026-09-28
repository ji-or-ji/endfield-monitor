//go:build windows

package main

import (
	"fmt"
	"math"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

// PDH（性能计数器）封装。任务管理器读的就是这一套。
// 相较 WMI 的 PerfFormattedData，它快几个数量级（毫秒 vs 秒），
// 也是 Windows 上拿磁盘忙率、CPU 实时频率、GPU 利用率的正路。

var (
	modPdh = windows.NewLazySystemDLL("pdh.dll")

	procPdhOpenQueryW                = modPdh.NewProc("PdhOpenQueryW")
	procPdhAddEnglishCounterW        = modPdh.NewProc("PdhAddEnglishCounterW")
	procPdhCollectQueryData          = modPdh.NewProc("PdhCollectQueryData")
	procPdhGetFormattedCounterValue  = modPdh.NewProc("PdhGetFormattedCounterValue")
	procPdhGetFormattedCounterArrayW = modPdh.NewProc("PdhGetFormattedCounterArrayW")
	procPdhCloseQuery                = modPdh.NewProc("PdhCloseQuery")
)

const (
	pdhFmtDouble   = 0x00000200
	pdhFmtNoCap100 = 0x00008000
	pdhMoreData    = 0x800007D2
)

// PDH_FMT_COUNTERVALUE：4 字节状态，联合体按 8 字节对齐。
type pdhFmtCounterValue struct {
	CStatus uint32
	_       uint32
	Value   float64
}

// PDH_FMT_COUNTERVALUE_ITEM_W：LPWSTR 名字 + 值。
// 名字用 *uint16（而不是 uintptr）承载，避免 uintptr→unsafe.Pointer 的回头转换，
// 那正是 go vet 的 unsafeptr 检查会拦的写法。指针指向 PDH 自己的内存，
// 不参与 Go 堆扫描，但必须在本次 Collect 周期内用完。
type pdhFmtCounterValueItem struct {
	Name  *uint16
	Value pdhFmtCounterValue
}

type pdhCounter struct {
	hQuery   uintptr
	hCounter uintptr
}

func pdhOpen(path string) *pdhCounter {
	var hQuery uintptr
	if r, _, _ := procPdhOpenQueryW.Call(0, 0, uintptr(unsafe.Pointer(&hQuery))); uint32(r) != 0 {
		return nil
	}
	p, err := windows.UTF16PtrFromString(path)
	if err != nil {
		procPdhCloseQuery.Call(hQuery)
		return nil
	}
	var hCounter uintptr
	if r, _, _ := procPdhAddEnglishCounterW.Call(hQuery, uintptr(unsafe.Pointer(p)), 0, uintptr(unsafe.Pointer(&hCounter))); uint32(r) != 0 {
		procPdhCloseQuery.Call(hQuery)
		return nil
	}
	return &pdhCounter{hQuery: hQuery, hCounter: hCounter}
}

func (c *pdhCounter) collect() {
	if c != nil {
		procPdhCollectQueryData.Call(c.hQuery)
	}
}

func (c *pdhCounter) value() (float64, bool) {
	if c == nil {
		return 0, false
	}
	var t uint32
	var v pdhFmtCounterValue
	// CStatus 0 = VALID_DATA，1 = NEW_DATA，都算有效；再大就是错误。
	if r, _, _ := procPdhGetFormattedCounterValue.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&t)), uintptr(unsafe.Pointer(&v))); uint32(r) != 0 || v.CStatus > 1 {
		return 0, false
	}
	return v.Value, true
}

// pdhItem 是一份已经脱离 PDH 缓冲、可以安全持有的计数项。
type pdhItem struct {
	Name  string
	Value float64
}

// array 读取通配符计数器（如 GPU Engine(*)）展开后的每一项。
// 名字在函数内就地解析成 Go string：LPWSTR 指向 PDH 内部内存，
// 跨一次 CollectQueryData 就可能失效，不能把裸指针带出去。
func (c *pdhCounter) array() ([]pdhItem, bool) {
	if c == nil {
		return nil, false
	}
	var size, count uint32
	if r, _, _ := procPdhGetFormattedCounterArrayW.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&size)), uintptr(unsafe.Pointer(&count)), 0); uint32(r) != uint32(pdhMoreData) ||
		size == 0 || count == 0 {
		return nil, false
	}

	buf := make([]byte, size)
	if r, _, _ := procPdhGetFormattedCounterArrayW.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&size)), uintptr(unsafe.Pointer(&count)),
		uintptr(unsafe.Pointer(&buf[0]))); uint32(r) != 0 {
		return nil, false
	}
	// PDH 保证 size == count * sizeof(item)；用它兜个底，防越界。
	if uint64(size) < uint64(count)*uint64(unsafe.Sizeof(pdhFmtCounterValueItem{})) {
		return nil, false
	}

	raw := unsafe.Slice((*pdhFmtCounterValueItem)(unsafe.Pointer(&buf[0])), int(count))
	out := make([]pdhItem, 0, count)
	for i := range raw {
		v := raw[i]
		if v.Value.CStatus > 1 {
			continue
		}
		name := ""
		if v.Name != nil {
			name = windows.UTF16PtrToString(v.Name)
		}
		out = append(out, pdhItem{Name: name, Value: v.Value.Value})
	}
	return out, true
}

// ================= 采样集 =================

type perfSet struct {
	cpuFreq  *pdhCounter
	gpuUtil  *pdhCounter
	gpuMem   *pdhCounter
	diskBusy map[string]*pdhCounter // 盘符 -> % Disk Time
	diskBps  map[string]*pdhCounter // 盘符 -> Disk Bytes/sec
}

var perf *perfSet

// initPerf 在静态信息就绪后调用一次。rate 类计数器要两次采样才出值，故先预热两轮。
func initPerf() {
	p := &perfSet{
		diskBusy: map[string]*pdhCounter{},
		diskBps:  map[string]*pdhCounter{},
	}
	open := func(path string) *pdhCounter {
		c := pdhOpen(path)
		if c == nil {
			fmt.Printf("[collector] 性能计数器不可用，跳过: %s\n", path)
		}
		return c
	}
	p.cpuFreq = open(`\Processor Information(_Total)\Processor Frequency`)
	p.gpuUtil = open(`\GPU Engine(*)\Utilization Percentage`)
	p.gpuMem = open(`\GPU Adapter Memory(*)\Dedicated Usage`)
	for _, d := range static.Disks {
		p.diskBusy[d.Letter] = open(`\LogicalDisk(` + d.Letter + `)\% Disk Time`)
		p.diskBps[d.Letter] = open(`\LogicalDisk(` + d.Letter + `)\Disk Bytes/sec`)
	}
	perf = p
	p.tick()
	p.tick()
}

func (p *perfSet) tick() {
	if p == nil {
		return
	}
	p.cpuFreq.collect()
	p.gpuUtil.collect()
	p.gpuMem.collect()
	for _, c := range p.diskBusy {
		c.collect()
	}
	for _, c := range p.diskBps {
		c.collect()
	}
}

func perfTick() { perf.tick() }

// diskFallbackNeeded 只要有任意一块盘没拿到 PDH 计数器，就仍需 gopsutil 兜底。
func diskFallbackNeeded() bool {
	if perf == nil {
		return true
	}
	for _, d := range static.Disks {
		if perf.diskBusy[d.Letter] == nil {
			return true
		}
	}
	return false
}

func perfCPUFreqGHz() (float64, bool) {
	if perf == nil {
		return 0, false
	}
	if mhz, ok := perf.cpuFreq.value(); ok && mhz > 0 {
		return round2(mhz / 1000.0), true
	}
	return 0, false
}

// perfDiskSample 返回该盘的读写速率（MB/s）与忙率（%）。
// 忙率来自 % Disk Time，与容量占用是两回事。
func perfDiskSample(letter string) (rwMBs, busyPct float64, ok bool) {
	if perf == nil {
		return 0, 0, false
	}
	if bps, okB := perf.diskBps[letter].value(); okB {
		rwMBs = math.Max(0, bps) / 1048576.0
		ok = true
	}
	if busy, okU := perf.diskBusy[letter].value(); okU {
		busyPct = math.Min(100, math.Max(0, busy))
		ok = true
	}
	return rwMBs, busyPct, ok
}

// adapterKey 从 GPU 实例名里取适配器标识（luid_..._phys_N）。
// 引擎实例形如 pid_123_luid_0x.._0x.._phys_0_eng_0_engtype_3D，
// 显存实例形如 luid_0x.._0x.._phys_0，都归到同一个键。
func adapterKey(name string) string {
	i := strings.Index(name, "luid_")
	if i < 0 {
		return name
	}
	rest := name[i:]
	if j := strings.Index(rest, "_eng"); j >= 0 {
		return rest[:j]
	}
	return rest
}

// engineType 取引擎类型（3D / Copy / VideoDecode…），取不到就用原名。
func engineType(name string) string {
	if i := strings.Index(name, "engtype_"); i >= 0 {
		return name[i+len("engtype_"):]
	}
	return name
}

// perfGPUSample 返回 GPU 利用率（%）与专用显存占用（MB）。
// 只要有一个 GPU 计数器可用才算采到，否则让上层显示“未采集”。
func perfGPUSample() (utilPct, memUsedMB float64, ok bool) {
	if perf == nil {
		return 0, 0, false
	}
	utilItems, okU := perf.gpuUtil.array()
	memItems, okM := perf.gpuMem.array()
	if !okU && !okM {
		return 0, 0, false
	}
	utilPct, memUsedMB = gpuAggregate(utilItems, memItems)
	return utilPct, memUsedMB, true
}

// gpuAggregate 把逐引擎、逐适配器的实例折成一个数：
// 同一适配器内按引擎类型求和，取全局最忙的一格；显存只统计那块适配器，
// 多显卡时不会把几块卡的数混在一起。
func gpuAggregate(utilItems, memItems []pdhItem) (utilPct, memUsedMB float64) {
	sums := map[[2]string]float64{}
	for _, it := range utilItems {
		k := [2]string{adapterKey(it.Name), engineType(it.Name)}
		sums[k] += it.Value
	}
	bestAdapter := ""
	best := 0.0
	for k, v := range sums {
		if v > best {
			best, bestAdapter = v, k[0]
		}
	}
	utilPct = math.Min(100, best)

	var sum float64
	for _, it := range memItems {
		// 只统计选中的那块适配器；没有引擎数据时退回全部
		if bestAdapter == "" || adapterKey(it.Name) == bestAdapter {
			sum += it.Value
		}
	}
	memUsedMB = sum / 1048576.0
	return utilPct, memUsedMB
}
