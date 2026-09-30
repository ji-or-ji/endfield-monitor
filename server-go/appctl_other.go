//go:build !windows && !linux

package main

import "errors"

// Windows 与 Linux 之外还有各自的实现（icon_windows.go / icon_linux.go、
// appctl_windows.go / appctl_linux.go），这里只给其余平台留桩。

var errNotSupported = errors.New("当前平台不支持这项操作")

func stopApp(int32) error    { return errNotSupported }
func restartApp(int32) error { return errNotSupported }

func appIcon(int32, string) ([]byte, string, error) { return nil, "", errNotSupported }

func probeIcon(int32) { println("[probe] 当前平台未实现图标查找") }
