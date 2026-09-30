package main

// Linux 上的图标不在可执行文件里——ELF 不携带图标资源。
// 图标是"登记"在 .desktop 文件里的，再由一个名字经图标主题解析到具体文件。
//
// 这一份是这个解析过程里**与平台无关**的部分：全是对目录和字符串的处理，
// 因此可以在任何平台上跑单元测试（见 iconlookup_test.go）。
// 真正读 /proc 与读文件的动作在 icon_linux.go 里。
//
// 目录顺序与查找规则已对照 Icon Theme Specification 0.13 核实过：扩展名探测顺序
// （png > svg > xpm）、<主题>/<尺寸>/<应用>/<名字>.<后缀> 的布局、以及无主题图标的兜底，
// 都与规范的伪代码相符。有两处是按本项目的处境做的取舍，见下面 iconThemes 与
// findIconFile 的注释。仍然没有在 Linux 真机上核对过，可以用 --probe-icon 自查。

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// desktopEntry 是 .desktop 文件里与本功能相关的几个键。
type desktopEntry struct {
	Path    string // 这个条目来自哪个文件，Flatpak 的匹配要用文件名
	Name    string
	Exec    string
	TryExec string
	Icon    string
}

// parseDesktopEntry 解析 .desktop 内容。只看 [Desktop Entry] 段，
// 其余段（[Desktop Action ...] 之类）与图标无关。
func parseDesktopEntry(data string) desktopEntry {
	var e desktopEntry
	inMain := false
	for _, raw := range strings.Split(data, "\n") {
		line := strings.TrimSpace(strings.TrimRight(raw, "\r"))
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if strings.HasPrefix(line, "[") {
			inMain = line == "[Desktop Entry]"
			continue
		}
		if !inMain {
			continue
		}
		key, val, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		switch strings.TrimSpace(key) {
		case "Name":
			if e.Name == "" {
				e.Name = strings.TrimSpace(val)
			}
		case "Exec":
			e.Exec = strings.TrimSpace(val)
		case "TryExec":
			e.TryExec = strings.TrimSpace(val)
		case "Icon":
			e.Icon = strings.TrimSpace(val)
		}
	}
	return e
}

// execProgram 取 Exec= 里第一个真正的词，也就是被启动的那个程序。
// 顺带剥掉 %U %f %i 这类字段码。
func execProgram(exec string) string {
	for _, tok := range splitExec(exec) {
		if tok == "" || strings.HasPrefix(tok, "%") {
			continue
		}
		return tok
	}
	return ""
}

// splitExec 按 shell 的引号规则把 Exec 拆成词。
//
// 不能直接按空白切：命令名里的空格会用引号包起来（Exec="my app" %U），
// 切碎了就会拿 "my" 去匹配，永远对不上。
func splitExec(s string) []string {
	var (
		out   []string
		cur   strings.Builder
		quote rune
		open  bool
	)
	flush := func() {
		if open {
			out = append(out, cur.String())
			cur.Reset()
			open = false
		}
	}
	for _, r := range s {
		switch {
		case quote != 0:
			if r == quote {
				quote = 0
			} else {
				cur.WriteRune(r)
			}
		case r == '"' || r == '\'':
			quote = r
			open = true
		case r == ' ' || r == '\t':
			flush()
		default:
			cur.WriteRune(r)
			open = true
		}
	}
	flush()
	return out
}

// flatpakAppID 从 Flatpak 的 Exec 里取应用 id：
//
//	/usr/bin/flatpak run --branch=stable --arch=x86_64 --app-id=org.gnome.Foo %U
//
// 这类条目的 Exec 指向 flatpak 本身，靠可执行名永远匹配不上，
// 只能认它导出的文件名（就是 <app-id>.desktop）。
func flatpakAppID(exec string) string {
	if !strings.Contains(exec, "--app-id=") {
		return ""
	}
	for _, f := range strings.Fields(exec) {
		if v, ok := strings.CutPrefix(f, "--app-id="); ok {
			return v
		}
	}
	return ""
}

// matchDesktopEntry 按可执行文件路径反查桌面条目，返回 nil 表示没匹配上。
//
// 依据是 Exec= 里那个程序的 basename 与目标可执行文件相同；TryExec= 能对上算更硬的信号。
// 脚本型应用（python3 /path/app.py）这里匹配不上，交给上层"拿可执行名直接当图标名猜"兜底。
func matchDesktopEntry(entries []desktopEntry, exe string) *desktopEntry {
	want := strings.ToLower(filepath.Base(exe))
	var weak *desktopEntry
	for i := range entries {
		e := &entries[i]
		if e.Icon == "" {
			continue
		}
		if e.TryExec != "" && strings.ToLower(filepath.Base(e.TryExec)) == want {
			return e
		}
		if prog := execProgram(e.Exec); prog != "" && strings.ToLower(filepath.Base(prog)) == want {
			if weak == nil {
				weak = e
			}
		}
		// Flatpak：进程的可执行文件其实是 flatpak，得按应用 id 认
		if id := flatpakAppID(e.Exec); id != "" && strings.EqualFold(filepath.Base(e.Path), id+".desktop") {
			if strings.EqualFold(want, strings.ToLower(filepath.Base(id))) {
				return e
			}
		}
	}
	return weak
}

// xdgDataHome 返回 $XDG_DATA_HOME，未设置时按规范退回 ~/.local/share。
func xdgDataHome() string {
	if v := os.Getenv("XDG_DATA_HOME"); v != "" {
		return v
	}
	home, err := os.UserHomeDir()
	if err != nil || home == "" {
		return ""
	}
	return filepath.Join(home, ".local", "share")
}

// xdgDataDirs 返回 $XDG_DATA_DIRS，未设置时按规范就是这两个。
func xdgDataDirs() []string {
	if v := os.Getenv("XDG_DATA_DIRS"); v != "" {
		var out []string
		for _, p := range strings.Split(v, ":") {
			if p = strings.TrimSpace(p); p != "" {
				out = append(out, p)
			}
		}
		if len(out) > 0 {
			return out
		}
	}
	return []string{"/usr/local/share", "/usr/share"}
}

// desktopDirs 返回可能存放 .desktop 的目录，优先级从前到后。
// 同名条目以先出现的为准，所以用户目录排在系统目录前面。
func desktopDirs() []string {
	var out []string
	if h := xdgDataHome(); h != "" {
		out = append(out, filepath.Join(h, "applications"))
	}
	for _, d := range xdgDataDirs() {
		out = append(out, filepath.Join(d, "applications"))
	}
	// Flatpak 与 Snap 的导出目录不在 XDG_DATA_DIRS 里，得单独补上
	if home, err := os.UserHomeDir(); err == nil && home != "" {
		out = append(out, filepath.Join(home, ".local", "share", "flatpak", "exports", "share", "applications"))
	}
	return append(out,
		"/var/lib/flatpak/exports/share/applications",
		"/var/lib/snapd/desktop/applications")
}

// iconThemeDirs 返回可能存放图标主题的目录，按规范顺序。
// 最后的 pixmaps 是兜底：那里直接放 <名字>.<后缀>，没有主题结构。
func iconThemeDirs() []string {
	var out []string
	if home, err := os.UserHomeDir(); err == nil && home != "" {
		out = append(out, filepath.Join(home, ".icons"))
	}
	if h := xdgDataHome(); h != "" {
		out = append(out, filepath.Join(h, "icons"))
	}
	for _, d := range xdgDataDirs() {
		out = append(out, filepath.Join(d, "icons"))
	}
	return append(out, "/usr/share/pixmaps")
}

// iconExts 是探图标文件时尝试的后缀，顺序即优先级。
// 这个顺序与规范伪代码里的一致（for extension in ("png", "svg", "xpm")）。
var iconExts = []string{".png", ".svg", ".xpm"}

// iconMime 把后缀映射成 Content-Type。命中的是 SVG 时，返回给客户端的
// 就不是 PNG 了，所以这个信息必须一路带上去。
func iconMime(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png":
		return "image/png"
	case ".svg":
		return "image/svg+xml"
	case ".xpm":
		return "image/x-xpm"
	}
	return "application/octet-stream"
}

// iconSizes 是猜主题目录结构时用的尺寸子目录名，从大到小。
var iconSizes = []string{"256x256", "192x192", "128x128", "96x96", "64x64", "48x48", "32x32", "16x16", "scalable"}

// iconContexts 图标在主题里按用途分目录，应用图标在 apps 下；
// 旧一些的主题会直接放在尺寸目录下，所以空的那项也试。
var iconContexts = []string{"apps", ""}

// findIconFile 在一批图标根目录里按名字找图标文件。
// 返回命中的路径、它的 Content-Type，以及一路试过的路径（诊断用）。
//
// 这里没有解析每个主题的 index.theme，而是直接按最常见的
// <主题>/<尺寸>/<用途>/<名字>.<后缀> 布局遍历。实现简单得多，代价是多几次 stat。
func findIconFile(dirs []string, name string) (path, mime string, tried []string, ok bool) {
	if name == "" {
		return "", "", nil, false
	}
	for _, dir := range dirs {
		for _, theme := range iconThemes(dir) {
			for _, size := range iconSizes {
				for _, ctx := range iconContexts {
					for _, ext := range iconExts {
						p := filepath.Join(dir, theme, size, ctx, name+ext)
						tried = append(tried, p)
						if fileExists(p) {
							return p, iconMime(p), tried, true
						}
					}
				}
			}
		}
		// 扁平存放的 pixmaps
		for _, ext := range iconExts {
			p := filepath.Join(dir, name+ext)
			tried = append(tried, p)
			if fileExists(p) {
				return p, iconMime(p), tried, true
			}
		}
	}
	return "", "", tried, false
}

// iconThemes 列出某个图标根目录下的主题，hicolor 排在最前面。
//
// 规范里的顺序是「先当前主题，再它继承的父主题，最后才轮到 hicolor」——
// 但那个顺序建立在有桌面环境、知道“当前主题是哪个”的前提上。采集端可能跑在
// 没有任何桌面的机器上，那时没有“当前主题”可言，所以把 hicolor 放最前：
// 它是规范要求所有应用都往里装图标的通用主题，也是最中立的那个。
// 装了主题的桌面上会因此看不到主题图标，这是已知取舍。
func iconThemes(dir string) []string {
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var themes []string
	hasHicolor := false
	for _, e := range ents {
		if !e.IsDir() {
			continue
		}
		if e.Name() == "hicolor" {
			hasHicolor = true
			continue
		}
		themes = append(themes, e.Name())
	}
	sort.Strings(themes)
	if hasHicolor {
		themes = append([]string{"hicolor"}, themes...)
	}
	return themes
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}
