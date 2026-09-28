//go:build windows

package main

import (
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	modUser32                    = windows.NewLazySystemDLL("user32.dll")
	procEnumWindows              = modUser32.NewProc("EnumWindows")
	procGetWindowTextW           = modUser32.NewProc("GetWindowTextW")
	procGetWindowThreadProcessID = modUser32.NewProc("GetWindowThreadProcessId")
	procIsWindowVisible          = modUser32.NewProc("IsWindowVisible")
)

// 回调必须是包级的：每次采样新建一个 syscall.NewCallback 会持续占用回调槽。
var (
	titleAccum map[int32]string
	titleCb    = syscall.NewCallback(func(hwnd uintptr, _ uintptr) uintptr {
		if v, _, _ := procIsWindowVisible.Call(hwnd); v == 0 {
			return 1
		}
		var pid uint32
		procGetWindowThreadProcessID.Call(hwnd, uintptr(unsafe.Pointer(&pid)))
		if pid == 0 {
			return 1
		}
		buf := make([]uint16, 512)
		n, _, _ := procGetWindowTextW.Call(hwnd, uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
		if n == 0 {
			return 1
		}
		title := syscall.UTF16ToString(buf[:n])
		if title == "" {
			return 1
		}
		// 同一进程通常有多个窗口，留第一个可见的（一般就是主窗口）
		if _, exists := titleAccum[int32(pid)]; !exists {
			titleAccum[int32(pid)] = title
		}
		return 1
	})
)

// windowTitles 返回 pid -> 主窗口标题；读不到窗口标题的进程不在表里。
func windowTitles() map[int32]string {
	titleAccum = make(map[int32]string, 64)
	procEnumWindows.Call(titleCb, 0)
	out := titleAccum
	titleAccum = nil
	return out
}
