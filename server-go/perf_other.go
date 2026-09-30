//go:build !windows

package main

// 非 Windows 平台没有 PDH，磁盘 IO 与 CPU 频率回落到 collect.go 里的 gopsutil 路径。

func initPerf() {}

func perfTick() {}

func diskFallbackNeeded() bool { return true }

func perfCPUPerfPct() (float64, bool) { return 0, false }

func perfDiskSample(_ string) (float64, float64, bool) { return 0, 0, false }

func perfGPUSample() (float64, float64, bool) { return 0, 0, false }
