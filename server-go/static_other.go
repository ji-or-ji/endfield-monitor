//go:build !windows

package main

// 非 Windows 平台：不做额外的静态采集（型号信息缺失时客户端显示占位符），
// 也没有 Windows 计划任务这个概念。

func collectStaticExtra(_ *staticInfo) {}

func collectTasks() []TaskInfo { return nil }

// selectPrimaryNetwork 非 Windows 平台不做事，保留 static.go 里的兜底值。
func selectPrimaryNetwork(_ *staticInfo) {}
