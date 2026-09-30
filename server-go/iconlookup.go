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
	"strconv"
	"strings"
	"sync"
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

// flatpakAppID 取 Flatpak 的 Exec 里那个 --app-id= 的值。
//
// 真正的 Flatpak 反查不走这条：那类进程的可执行文件是 bwrap，在 Exec 里找 app-id
// 对不上。反查走“读环境变量 + 认导出的 .desktop 文件名”（见 flatpakID 与 findEntryByBase）。
// 这里只留着解析导出的 .desktop 内容时可能用得上。
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
// 脚本型应用（python3 /path/app.py）这里匹配不上，交给上层“拿可执行名直接当图标名猜”兜底。
// Flatpak 应用也不走这里——它们的可执行文件是 bwrap，得靠环境变量认，见 flatpakID。
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
	}
	return weak
}

// findEntryByBase 按文件名找一个条目。
// Flatpak 导出的 .desktop 文件名就是它的应用 id，认名字比认 Exec 靠得住。
func findEntryByBase(entries []desktopEntry, filename string) *desktopEntry {
	for i := range entries {
		if entries[i].Icon != "" && strings.EqualFold(filepath.Base(entries[i].Path), filename) {
			return &entries[i]
		}
	}
	return nil
}

// environValue 从 /proc/<pid>/environ 的内容里取某个变量的值。
// 那份内容是以 NUL 分隔的 KEY=VALUE 串。
//
// 注：调用方用的键名（FLATPAK_ID）是按已知的 Flatpak 行为写的，没能找到官方文档核实。
// --probe-icon 会把进程环境里的这一项打出来，真机跑一次就知道对不对。
func environValue(data []byte, key string) string {
	prefix := key + "="
	for _, kv := range strings.Split(string(data), "\x00") {
		if v, ok := strings.CutPrefix(kv, prefix); ok {
			return v
		}
	}
	return ""
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

// iconSizes 是猜主题目录结构时用的尺寸子目录名，顺序即优先级。
// 我们要的是 48 这一档（客户端按 38px 显示），所以 48x48 排最前，
// 其余按与它接近的程度往后排。index.theme 走不通时才会用到这个猜测。
var iconSizes = []string{"48x48", "64x64", "32x32", "96x96", "128x128", "192x192", "256x256", "16x16", "scalable"}

// iconContexts 图标在主题里按用途分目录，应用图标在 apps 下；
// 旧一些的主题会直接放在尺寸目录下，所以空的那项也试。
var iconContexts = []string{"apps", ""}

// findIconFile 在一批图标根目录里按名字找图标文件。
// 返回命中的路径、它的 Content-Type，以及一路试过的路径（诊断用）。
//
// 优先按主题自己的 index.theme 找（规范做法），没有或没命中再退回遍历常见布局。
// 后者是近似手段，会漏掉 48x48@2/apps 这类非常规目录名。
func findIconFile(dirs []string, name string) (path, mime string, tried []string, ok bool) {
	if name == "" {
		return "", "", nil, false
	}
	// 客户端按 38px 显示，45 这一档最合适（规范要求应用至少装 48x48）
	const wantSize, wantScale = 48, 1
	for _, root := range dirs {
		for _, theme := range iconThemes(root) {
			base := filepath.Join(root, theme)
			if sub, hasIndex := themeSubdirs(base); hasIndex {
				if p, found := lookupByIndex(base, sub, name, wantSize, wantScale, &tried); found {
					return p, iconMime(p), tried, true
				}
			}
			for _, size := range iconSizes {
				for _, ctx := range iconContexts {
					if p, found := probeIconIn(filepath.Join(base, size, ctx), name, &tried); found {
						return p, iconMime(p), tried, true
					}
				}
			}
		}
		// 扁平存放的 pixmaps
		if p, found := probeIconIn(root, name, &tried); found {
			return p, iconMime(p), tried, true
		}
	}
	return "", "", tried, false
}

// probeIconIn 在一个目录里按后缀优先级找一个图标，试过的路径记在 tried 里。
func probeIconIn(dir, name string, tried *[]string) (string, bool) {
	for _, ext := range iconExts {
		p := filepath.Join(dir, name+ext)
		*tried = append(*tried, p)
		if fileExists(p) {
			return p, true
		}
	}
	return "", false
}

// ================= index.theme =================
//
// 规范要求主题目录里有一份 index.theme，用 Directories= 列出该主题实际使用的子目录，
// 每个子目录再用一个同名节描述它的尺寸属性。按这份描述查比遍历布局更准，
// 也才照顾得到 48x48@2/apps 这类非常规名字。下面这段是照规范实现的。

// iconDir 是 index.theme 里的一个尺寸目录。
type iconDir struct {
	Path      string // 相对主题根，如 48x48/apps
	Size      int    // 名义尺寸
	Scale     int    // 目标缩放，缺省 1
	Type      string // Fixed / Scalable / Threshold，缺省 Threshold
	MinSize   int
	MaxSize   int
	Threshold int
}

// matchingSizes 按规范判断这个目录算不算“尺寸正好合适”。
func (d iconDir) matchingSizes(size, scale int) bool {
	if d.Scale != scale {
		return false
	}
	switch strings.ToLower(d.Type) {
	case "fixed":
		return d.Size == size
	case "scalable":
		return d.MinSize <= size && size <= d.MaxSize
	default: // threshold
		return d.Size-d.Threshold <= size && size <= d.Size+d.Threshold
	}
}

// sizeDistance 是这个目录离想要的尺寸有多远，越小越优先；规则照规范。
func (d iconDir) sizeDistance(size, scale int) int {
	switch strings.ToLower(d.Type) {
	case "fixed":
		return absInt(d.Size*d.Scale - size*scale)
	case "scalable":
		return outside(d.MinSize, d.MaxSize, d.Scale, size, scale)
	default: // threshold
		return outside(d.Size-d.Threshold, d.Size+d.Threshold, d.Scale, size, scale)
	}
}

// outside 算某个尺寸落在 [lo,hi]（已乘 Scale）之外差多少，落在里面就是 0。
func outside(lo, hi, scale, size, sizeScale int) int {
	target := size * sizeScale
	if target < lo*scale {
		return lo*scale - target
	}
	if target > hi*scale {
		return target - hi*scale
	}
	return 0
}

func absInt(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

// themeSubdirs 读主题里的 index.theme 并解析出尺寸目录。
func themeSubdirs(base string) ([]iconDir, bool) {
	data, err := os.ReadFile(filepath.Join(base, "index.theme"))
	if err != nil {
		return nil, false
	}
	return parseIndexTheme(string(data))
}

// parseIndexTheme 解析 index.theme，返回它列出的尺寸目录。
// 没有 [Icon Theme] 段、或 Directories 里没有一个能用的节，就返回 ok=false。
func parseIndexTheme(data string) (dirs []iconDir, ok bool) {
	sections := map[string]map[string]string{}
	section := ""
	for _, raw := range strings.Split(data, "\n") {
		line := strings.TrimSpace(strings.TrimRight(raw, "\r"))
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if strings.HasPrefix(line, "[") && strings.HasSuffix(line, "]") {
			section = strings.TrimSpace(line[1 : len(line)-1])
			if _, exists := sections[section]; !exists {
				sections[section] = map[string]string{}
			}
			continue
		}
		if section == "" {
			continue
		}
		if k, v, found := strings.Cut(line, "="); found {
			sections[section][strings.TrimSpace(k)] = strings.TrimSpace(v)
		}
	}

	main, exists := sections["Icon Theme"]
	if !exists {
		return nil, false
	}
	for _, sub := range splitList(main["Directories"]) {
		kv, exists := sections[sub]
		if !exists {
			continue
		}
		d := iconDir{Path: sub, Scale: 1, Type: "Threshold", Threshold: 2}
		d.Size = atoiDefault(kv["Size"], 0)
		if d.Size <= 0 {
			continue // Size 是必需键，没有就跳过这个目录
		}
		if v := atoiDefault(kv["Scale"], 1); v > 0 {
			d.Scale = v
		}
		if t := kv["Type"]; t != "" {
			d.Type = t
		}
		d.MinSize = atoiDefault(kv["MinSize"], d.Size)
		d.MaxSize = atoiDefault(kv["MaxSize"], d.Size)
		d.Threshold = atoiDefault(kv["Threshold"], 2)
		dirs = append(dirs, d)
	}
	return dirs, len(dirs) > 0
}

// lookupByIndex 按 index.theme 的描述在主题里找一个图标。
// 两遍：先找尺寸正好合适的目录，再在所有目录里挑尺寸最接近的那个（规范的做法）。
func lookupByIndex(base string, dirs []iconDir, name string, size, scale int, tried *[]string) (string, bool) {
	for _, d := range dirs {
		if !d.matchingSizes(size, scale) {
			continue
		}
		if p, found := probeIconIn(filepath.Join(base, d.Path), name, tried); found {
			return p, true
		}
	}

	bestPath := ""
	bestDist := 0
	for _, d := range dirs {
		for _, ext := range iconExts {
			p := filepath.Join(base, d.Path, name+ext)
			*tried = append(*tried, p)
			if !fileExists(p) {
				continue
			}
			if dist := d.sizeDistance(size, scale); bestPath == "" || dist < bestDist {
				bestPath, bestDist = p, dist
			}
			break
		}
	}
	return bestPath, bestPath != ""
}

// ================= 图标缓存 =================
//
// 图标是死的，同一个可执行文件反复请求不必反复提取。按路径缓存；
// 条目数天然有限（一台机器上的可执行文件就那么多），不做淘汰。
// 提取失败不进缓存：那可能是暂时的（进程刚起、权限未就绪），下次还得再试。

var (
	iconCacheMu sync.Mutex
	iconCache   = map[string]cachedIcon{}
)

type cachedIcon struct {
	data []byte
	mime string
}

func cacheGet(exe string) ([]byte, string, bool) {
	iconCacheMu.Lock()
	defer iconCacheMu.Unlock()
	c, ok := iconCache[exe]
	return c.data, c.mime, ok
}

func cachePut(exe string, data []byte, mime string) {
	iconCacheMu.Lock()
	defer iconCacheMu.Unlock()
	iconCache[exe] = cachedIcon{data: data, mime: mime}
}

// splitList 拆开逗号分隔的列表，顺手去掉空项。
func splitList(s string) []string {
	var out []string
	for _, p := range strings.Split(s, ",") {
		if v := strings.TrimSpace(p); v != "" {
			out = append(out, v)
		}
	}
	return out
}

// atoiDefault 解析整数，失败或空就用默认值。
func atoiDefault(s string, def int) int {
	v, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return def
	}
	return v
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
