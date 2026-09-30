//go:build windows

package main

import (
	"fmt"
	"os/exec"
	"path/filepath"
	"syscall"

	"github.com/shirou/gopsutil/v4/process"
)

// 增强版的动手能力。只在加 -tags plus 编出来的那一份里注册成端点，
// 普通版里这些函数存在但不会被调用。
//
// 取图标的实现见 icon_windows.go。

// stopApp 结束进程，连同它派生的子进程一起。
// 不少应用（浏览器、Electron 系）真正的活儿在子进程里，只杀父进程会留下孤儿。
func stopApp(pid int32) error {
	p, err := process.NewProcess(pid)
	if err != nil {
		return fmt.Errorf("找不到进程 %d: %w", pid, err)
	}
	if kids, err := p.Children(); err == nil {
		for _, k := range kids {
			_ = k.Kill()
		}
	}
	return p.Kill()
}

// restartApp 用原来的路径与参数把进程重启一遍。
// 参数从进程自身取，所以客户端只需要说“重启哪个 pid”。
func restartApp(pid int32) error {
	p, err := process.NewProcess(pid)
	if err != nil {
		return fmt.Errorf("找不到进程 %d: %w", pid, err)
	}
	exe, err := p.Exe()
	if err != nil || exe == "" {
		return fmt.Errorf("拿不到进程 %d 的可执行文件路径", pid)
	}
	argv, err := p.CmdlineSlice()
	if err != nil || len(argv) == 0 {
		return fmt.Errorf("拿不到进程 %d 的启动参数", pid)
	}

	if err := stopApp(pid); err != nil {
		return err
	}

	cmd := exec.Command(exe, argv[1:]...)
	cmd.Dir = filepath.Dir(exe)
	// 让它脱离服务端：服务端退出或被结束，不该把它带下去
	cmd.SysProcAttr = &syscall.SysProcAttr{
		CreationFlags: 0x00000008 | 0x00000200, // DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
	}
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
