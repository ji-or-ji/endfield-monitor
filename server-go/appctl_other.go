//go:build !windows

package main

import "errors"

// 非 Windows 平台暂不提供这几项动手能力，增强版在 Linux 上仍可用，
// 但能力列表里只会有能做的那些。

var errNotSupported = errors.New("当前平台不支持这项操作")

func stopApp(int32) error    { return errNotSupported }
func restartApp(int32) error { return errNotSupported }

func appIconPNG(string) ([]byte, error) { return nil, errNotSupported }
