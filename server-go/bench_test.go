//go:build windows

package main

import (
	"testing"
	"unsafe"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/process"
	"golang.org/x/sys/windows"
)

// 采样各子系统的单次调用成本。跑法：
//
//	go test -run NONE -bench . -benchtime=30x -count=1
//
// 单位是每次调用；采集端每秒的固定开销 ≈ 各项按实际调用频率求和。
func initBenchEnv() {
	if static.CPUName == "" {
		static = collectStatic()
	}
	initPerf()
	initDiskSampler()
	if n, _ := currentNetwork(); n == "" {
		setNetwork(detectNetwork())
	}
	_, _ = cpu.Percent(0, false)
}

func BenchmarkPerfTick(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		perfTick()
	}
}

func BenchmarkReadCPU(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readCPU()
	}
}

func BenchmarkReadGPU(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readGPU()
	}
}

func BenchmarkReadMem(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readMem()
	}
}

func BenchmarkReadDisks(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readDisks(1.0)
	}
}

func BenchmarkReadNet(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readNet()
	}
}

func BenchmarkBattery(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = readBatteryInfo()
	}
}

func BenchmarkCollectProcs(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = collectProcs()
	}
}

func BenchmarkWindowTitles(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = windowTitles()
	}
}

func BenchmarkListeningPorts(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_ = listeningPorts()
	}
}

// ---- 进程采集的成本拆解：看看到底是哪个调用贵 ----
//
// 顺带记录一个被否决的方案：想用 PDH 的 \Process(*)\ID Process / % Processor Time /
// Working Set 一次拿整张表，取代逐进程调用。实测每次 55ms，比 gopsutil 还慢
// （每 collect 一轮，Process 提供程序内部就要遍历一遍全部进程），故不采用。

var benchProcs = func() []*process.Process {
	ps, _ := process.Processes()
	return ps
}()

func BenchmarkProcEnumOnly(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		_, _ = process.Processes()
	}
}

// Toolhelp 只给 pid 与映像名，不给内存和 CPU 时间；用来衡量枚举本身能压到多低。
// 实测 312 个进程要 24ms，与 gopsutil 的 31ms 同量级（PDH 那次 55ms 更差），
// 省下的钱不够抵掉“还得逐进程补内存和 CPU”的成本，故不用。
// 结论：枚举全进程的成本是固有的，想省只能少采。
func BenchmarkProcToolhelpEnum(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
		if err != nil {
			b.Fatal(err)
		}
		var pe windows.ProcessEntry32
		pe.Size = uint32(unsafe.Sizeof(pe))
		n := 0
		for err := windows.Process32First(snap, &pe); err == nil; err = windows.Process32Next(snap, &pe) {
			n++
		}
		_ = windows.CloseHandle(snap)
		if i == 0 {
			b.Logf("进程数=%d", n)
		}
	}
}

func BenchmarkProcName(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		for _, p := range benchProcs {
			_, _ = p.Name()
		}
	}
}

func BenchmarkProcMemoryInfo(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		for _, p := range benchProcs {
			_, _ = p.MemoryInfo()
		}
	}
}

func BenchmarkProcExe(b *testing.B) {
	initBenchEnv()
	for i := 0; i < b.N; i++ {
		for _, p := range benchProcs {
			_, _ = p.Exe()
		}
	}
}

func BenchmarkProcCPUPercent(b *testing.B) {
	initBenchEnv()
	for _, p := range benchProcs {
		_, _ = p.CPUPercent()
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		for _, p := range benchProcs {
			_, _ = p.CPUPercent()
		}
	}
}
