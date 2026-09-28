//go:build windows

package main

import (
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

// PDH_FMT_COUNTERVALUE：4 字节状态 + 8 字节对齐的联合体。
type pdhFmtCounterValue struct {
	CStatus uint32
	_       uint32
	Value   float64
}

// PDH_FMT_COUNTERVALUE_ITEM_W：宽字符串名 + 值。
type pdhFmtCounterValueItem struct {
	Name  uintptr
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
	if r, _, _ := procPdhGetFormattedCounterValue.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&t)), uintptr(unsafe.Pointer(&v))); uint32(r) != 0 || v.CStatus > 1 {
		return 0, false
	}
	return v.Value, true
}

// array 读取通配符计数器（如 GPU Engine(*)）展开后的每一项。
func (c *pdhCounter) array() []pdhFmtCounterValueItem {
	if c == nil {
		return nil
	}
	var size, count uint32
	r, _, _ := procPdhGetFormattedCounterArrayW.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&size)), uintptr(unsafe.Pointer(&count)), 0)
	if uint32(r) != uint32(pdhMoreData) || size == 0 || count == 0 {
		return nil
	}
	buf := make([]byte, size)
	r, _, _ = procPdhGetFormattedCounterArrayW.Call(
		c.hCounter, pdhFmtDouble|pdhFmtNoCap100,
		uintptr(unsafe.Pointer(&size)), uintptr(unsafe.Pointer(&count)),
		uintptr(unsafe.Pointer(&buf[0])))
	if uint32(r) != 0 {
		return nil
	}
	items := unsafe.Slice((*pdhFmtCounterValueItem)(unsafe.Pointer(&buf[0])), int(count))
	out := make([]pdhFmtCounterValueItem, len(items))
	copy(out, items)
	return out
}

func pdhItemName(item pdhFmtCounterValueItem) string {
	if item.Name == 0 {
		return ""
	}
	return windows.UTF16PtrToString((*uint16)(unsafe.Pointer(item.Name)))
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
	p.cpuFreq = pdhOpen(`\Processor Information(_Total)\Processor Frequency`)
	p.gpuUtil = pdhOpen(`\GPU Engine(*)\Utilization Percentage`)
	p.gpuMem = pdhOpen(`\GPU Adapter Memory(*)\Dedicated Usage`)
	for _, d := range static.Disks {
		p.diskBusy[d.Letter] = pdhOpen(`\LogicalDisk(` + d.Letter + `)\% Disk Time`)
		p.diskBps[d.Letter] = pdhOpen(`\LogicalDisk(` + d.Letter + `)\Disk Bytes/sec`)
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

// perfGPUSample 返回 GPU 利用率（%）与专用显存占用（MB）。
func perfGPUSample() (utilPct, memUsedMB float64, ok bool) {
	if perf == nil {
		return 0, 0, false
	}
	utilPct = gpuEngineUtil(perf.gpuUtil.array())
	if items := perf.gpuMem.array(); len(items) > 0 {
		var sum float64
		for _, it := range items {
			if it.Value.CStatus <= 1 {
				sum += it.Value.Value
			}
		}
		memUsedMB = sum / 1048576.0
	}
	return utilPct, memUsedMB, true
}

// gpuEngineUtil 把按引擎铺开的利用率折成一个数：
// 同一引擎类型（3D / Copy / VideoDecode…）内相加，取最忙的那一类。
func gpuEngineUtil(items []pdhFmtCounterValueItem) float64 {
	byType := map[string]float64{}
	for _, it := range items {
		if it.Value.CStatus > 1 {
			continue
		}
		name := pdhItemName(it)
		key := name
		if i := strings.Index(name, "engtype_"); i >= 0 {
			key = name[i+len("engtype_"):]
		}
		byType[key] += it.Value.Value
	}
	best := 0.0
	for _, v := range byType {
		if v > best {
			best = v
		}
	}
	return math.Min(100, best)
}
