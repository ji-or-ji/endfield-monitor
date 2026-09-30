//go:build linux

package main

import (
	"fmt"
	"os/exec"
	"path/filepath"
	"syscall"

	"github.com/shirou/gopsutil/v4/process"
)

// Linux 上的启停。找子进程与结束进程走 gopsutil（跨平台、与 Windows 那份一致），
// 重启那一步用 Setsid 让新进程脱离服务端的会话，免得服务端一停就把应用带走。

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
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	if err := cmd.Start(); err != nil {
		return err
	}
	return cmd.Process.Release()
}
