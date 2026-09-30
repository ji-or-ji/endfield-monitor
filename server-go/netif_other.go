//go:build !windows

package main

// 非 Windows 平台读 /proc/net/dev 之类本来就便宜，不需要单接口特化，
// 交给 collect.go 里的 gopsutil 兜底路径即可。

func prepareNetInterface(string) {}

func readNetIf() (netSample, bool) { return netSample{}, false }
