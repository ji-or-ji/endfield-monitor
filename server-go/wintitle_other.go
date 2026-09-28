//go:build !windows

package main

// windowTitles 非 Windows 平台返回空表，进程不显示窗口标题。
func windowTitles() map[int32]string { return nil }
