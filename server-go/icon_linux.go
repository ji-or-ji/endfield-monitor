//go:build linux

package main

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// appIcon 在 Linux 上取图标的完整链条：
//
//	pid -> 可执行文件 -> 匹配 .desktop -> Icon 字段 -> 图标主题里的具体文件
//
// 决策逻辑都在 iconlookup.go（平台无关、可测），这里只负责读 /proc 与读文件。
func appIcon(pid int32, exe string) (data []byte, mime string, err error) {
	if exe == "" {
		exe = procExe(pid)
	}
	if exe == "" {
		return nil, "", errors.New("拿不到可执行文件路径")
	}
	exe = strings.TrimSuffix(exe, " (deleted)")

	if e := matchDesktopEntry(loadDesktopEntries(desktopDirs()), exe); e != nil {
		if data, mime, ok := readIcon(e.Icon); ok {
			return data, mime, nil
		}
	}

	// 兜底：很多 .desktop 的 Icon= 名字就等于可执行名，直接拿它当图标名猜一次
	guess := strings.TrimSuffix(filepath.Base(exe), filepath.Ext(exe))
	if data, mime, ok := readIcon(guess); ok {
		return data, mime, nil
	}
	return nil, "", errors.New("没找到图标")
}

// readIcon 把 Icon= 的值变成图标字节：绝对路径就直接读，
// 否则当成图标名去图标主题里找。
func readIcon(icon string) ([]byte, string, bool) {
	if icon == "" {
		return nil, "", false
	}
	if filepath.IsAbs(icon) {
		data, err := os.ReadFile(icon)
		if err != nil {
			return nil, "", false
		}
		return data, iconMime(icon), true
	}
	path, mime, _, ok := findIconFile(iconThemeDirs(), icon)
	if !ok {
		return nil, "", false
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, "", false
	}
	return data, mime, true
}

// procExe 读 /proc/<pid>/exe 的符号链接。
// 读别的用户的进程会拿到 EACCES，这时还有 /snapshot 里采到的路径可用。
func procExe(pid int32) string {
	p, err := os.Readlink(filepath.Join("/proc", strconv.Itoa(int(pid)), "exe"))
	if err != nil {
		return ""
	}
	return p
}

// loadDesktopEntries 扫一批目录里的 .desktop 条目。
// 同名文件以先出现的目录为准（用户目录排在系统目录前面）。
func loadDesktopEntries(dirs []string) []desktopEntry {
	seen := map[string]bool{}
	var out []desktopEntry
	for _, dir := range dirs {
		ents, err := os.ReadDir(dir)
		if err != nil {
			continue
		}
		for _, de := range ents {
			name := de.Name()
			if de.IsDir() || !strings.HasSuffix(name, ".desktop") || seen[name] {
				continue
			}
			data, err := os.ReadFile(filepath.Join(dir, name))
			if err != nil {
				continue
			}
			seen[name] = true
			e := parseDesktopEntry(string(data))
			e.Path = filepath.Join(dir, name)
			out = append(out, e)
		}
	}
	return out
}

// probeIcon 是 --probe-icon 的实现：把查找链条上每一步的结果原样打出来。
//
// 这套目录与匹配规则是照 freedesktop 的约定写的，但没在真机上核过。
// 在目标机器上跑一次，就能看出是哪一步没对上——是没匹配到条目、
// 还是图标名解析不到文件、还是路径压根不在预期位置。
func probeIcon(pid int32) {
	fmt.Printf("[probe] pid = %d\n", pid)
	exe := procExe(pid)
	fmt.Printf("[probe] /proc/%d/exe -> %q\n", pid, exe)
	if exe == "" {
		fmt.Println("[probe] 读不到可执行文件路径（不是本用户的进程时属正常）")
		return
	}
	exe = strings.TrimSuffix(exe, " (deleted)")
	fmt.Printf("[probe] 归一后 exe = %q\n", exe)

	dirs := desktopDirs()
	fmt.Println("[probe] .desktop 目录：")
	total := 0
	for _, d := range dirs {
		n := 0
		if ents, err := os.ReadDir(d); err == nil {
			for _, e := range ents {
				if !e.IsDir() && strings.HasSuffix(e.Name(), ".desktop") {
					n++
				}
			}
		}
		total += n
		fmt.Printf("[probe]   %-72s %d 个\n", d, n)
	}

	entries := loadDesktopEntries(dirs)
	fmt.Printf("[probe] 共载入 %d 个条目（去重后）\n", len(entries))

	name := ""
	if e := matchDesktopEntry(entries, exe); e != nil {
		fmt.Printf("[probe] 匹配到条目: %s\n", e.Path)
		fmt.Printf("[probe]   Name=%q Exec=%q TryExec=%q Icon=%q\n", e.Name, e.Exec, e.TryExec, e.Icon)
		name = e.Icon
	} else {
		fmt.Println("[probe] 没有条目匹配得上（脚本型应用常见）")
	}
	if name == "" {
		name = strings.TrimSuffix(filepath.Base(exe), filepath.Ext(exe))
		fmt.Printf("[probe] 退回用可执行名当图标名: %q\n", name)
	}

	fmt.Println("[probe] 图标主题根目录：")
	for _, d := range iconThemeDirs() {
		fmt.Printf("[probe]   %-72s 主题=%v\n", d, iconThemes(d))
	}

	path, mime, tried, ok := findIconFile(iconThemeDirs(), name)
	fmt.Printf("[probe] 图标名=%q，试了 %d 个路径\n", name, len(tried))
	if ok {
		fmt.Printf("[probe] 命中: %s  (%s)\n", path, mime)
	} else {
		fmt.Println("[probe] 没命中。前 20 个试过的路径：")
		for i, tp := range tried {
			if i >= 20 {
				break
			}
			fmt.Printf("[probe]   %s\n", tp)
		}
	}
}
