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

	entries := loadDesktopEntries(desktopDirs())

	// Flatpak 应用先认：它们的可执行文件是 bwrap，按名字永远匹配不上，
	// 但环境里留着应用 id，导出的 .desktop 文件名就是它。
	if id := flatpakID(pid); id != "" {
		if e := findEntryByBase(entries, id+".desktop"); e != nil {
			if data, mime, ok := readIcon(e.Icon); ok {
				return data, mime, nil
			}
		}
	}

	// 名字候选：脚本型应用（cinnamon-settings 之类，exe 其实是 python）的“真身”
	// 在 argv 里而不在 exe 上，得按 argv[0] 去认，否则全被认成解释器。
	names := programNames(pid, exe)

	for _, n := range names {
		if e := matchDesktopEntry(entries, n); e != nil {
			if data, mime, ok := readIcon(e.Icon); ok {
				return data, mime, nil
			}
		}
	}

	// 兜底：很多 .desktop 的 Icon= 名字就等于程序名，直接拿候选名当图标名猜一次
	for _, n := range names {
		if data, mime, ok := readIcon(n); ok {
			return data, mime, nil
		}
	}
	// 老规矩的最后一猜：按去掉后缀的可执行名当图标名。
	// 解释器跳过——它只会把 Python 一类的图标塞给所有脚本应用。
	if !isInterpreter(filepath.Base(exe)) {
		if guess := strings.TrimSuffix(filepath.Base(exe), filepath.Ext(exe)); guess != "" {
			if data, mime, ok := readIcon(guess); ok {
				return data, mime, nil
			}
		}
	}
	return nil, "", errors.New("没找到图标")
}

// procArgv 读 /proc/<pid>/cmdline（NUL 分隔的 argv）。读不到就返回空。
func procArgv(pid int32) []string {
	data, err := os.ReadFile(filepath.Join("/proc", strconv.Itoa(int(pid)), "cmdline"))
	if err != nil || len(data) == 0 {
		return nil
	}
	var out []string
	for _, part := range strings.Split(string(data), "\x00") {
		if part != "" {
			out = append(out, part)
		}
	}
	return out
}

// programNames 列出认图标时的名字候选，越靠前越可信。
// 脚本型应用（cinnamon-settings 的 exe 是 python3.12）的“真身”在 argv 里，
// 只认 exe 会把它们一股脑认成解释器（于是全顶着 Python 的图标）。
func programNames(pid int32, exe string) []string {
	var out []string
	seen := map[string]bool{}
	add := func(p string) {
		if p == "" {
			return
		}
		b := strings.TrimSuffix(filepath.Base(p), " (deleted)")
		if b == "" || b == "." || b == "/" || seen[b] {
			return
		}
		// 解释器不是应用本身，别拿它去认：否则所有脚本应用都会顶解释器的图标。
		// （脚本走 shebang 时 argv[0] 就是解释器，正好也在这里被挡掉。）
		if isInterpreter(b) {
			return
		}
		seen[b] = true
		out = append(out, b)
	}

	// argv 里每个不是开关的 token 都当候选：正常应用 argv[0] 就是它自己；
	// 脚本应用（cinnamon-settings、blueman-applet 之类）的真身则藏在 argv 里。
	for _, a := range procArgv(pid) {
		if strings.HasPrefix(a, "-") {
			continue // 开关不是程序名
		}
		add(a)
	}
	add(exe) // 兜底：按可执行文件本身
	return out
}

// iconKey 是图标缓存的键。不能只看 exe：脚本型应用共用解释器那个 exe，
// 各自的图标却不一样，得把 argv 里的主名也算进去，否则它们会共用一份图标。
func iconKey(pid int32, exe string) string {
	if names := programNames(pid, exe); len(names) > 0 {
		return exe + "\x00" + names[0]
	}
	return exe
}

// isInterpreter 判断这名字是不是“跑别人代码”的解释器/运行时。
func isInterpreter(base string) bool {
	switch base {
	case "node", "nodejs", "deno", "bun", "java", "javaw", "ruby", "perl", "php",
		"sh", "bash", "dash", "zsh", "fish", "env", "wine", "mono":
		return true
	}
	return strings.HasPrefix(base, "python") || strings.HasPrefix(base, "pypy")
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

// flatpakID 从进程的环境里取 Flatpak 应用 id。
// 这类进程的可执行文件是 bwrap，只有环境变量还记得它到底是谁。
func flatpakID(pid int32) string {
	data, err := os.ReadFile(filepath.Join("/proc", strconv.Itoa(int(pid)), "environ"))
	if err != nil {
		return ""
	}
	return environValue(data, "FLATPAK_ID")
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

	// Flatpak 那条路靠这个变量，名字对不对一看便知
	if id := flatpakID(pid); id != "" {
		fmt.Printf("[probe] FLATPAK_ID = %q\n", id)
	} else {
		fmt.Println("[probe] 环境里没有 FLATPAK_ID（不是 Flatpak 应用，或者变量名不对）")
	}

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
