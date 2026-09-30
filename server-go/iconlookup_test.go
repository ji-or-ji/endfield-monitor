package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// 这些用例是这套 Linux 图标查找唯一的验证手段：逻辑部分与平台无关，
// 因此在 Windows 上也能跑。真机行为靠 --probe-icon 在目标机器上核。

func TestParseDesktopEntry(t *testing.T) {
	data := `[Desktop Entry]
Type=Application
Name=Firefox
Exec=/usr/bin/firefox %u
Icon=firefox
NoDisplay=true

[Desktop Action new-window]
Name=不该被读到
Icon=ignored
`
	e := parseDesktopEntry(data)
	if e.Name != "Firefox" || e.Exec != "/usr/bin/firefox %u" || e.Icon != "firefox" {
		t.Fatalf("解析结果不对: %+v", e)
	}
}

func TestExecProgram(t *testing.T) {
	cases := map[string]string{
		`/usr/bin/firefox %u`:               "/usr/bin/firefox",
		`"my app" %U`:                       "my app",
		`/opt/Foo/foo --flag %i`:            "/opt/Foo/foo",
		`env GDK_BACKEND=x11 /usr/bin/x %F`: "env", // 包装类命令只能匹配到 env，属已知局限
	}
	for in, want := range cases {
		if got := execProgram(in); got != want {
			t.Errorf("execProgram(%q) = %q，期望 %q", in, got, want)
		}
	}
}

func TestMatchDesktopEntry(t *testing.T) {
	entries := []desktopEntry{
		{Path: "/usr/share/applications/firefox.desktop", Name: "Firefox", Exec: "/usr/bin/firefox %u", Icon: "firefox"},
		{Path: "/usr/share/applications/noicon.desktop", Name: "无图标", Exec: "/usr/bin/noicon"},
		{Path: "/usr/share/applications/try.desktop", Name: "Try", Exec: "/usr/bin/other", TryExec: "/usr/bin/tryme", Icon: "tryme"},
		{Path: "/var/lib/flatpak/exports/share/applications/org.gnome.Foo.desktop", Name: "Foo",
			Exec: "/usr/bin/flatpak run --branch=stable --app-id=org.gnome.Foo", Icon: "org.gnome.Foo"},
	}

	// 按 basename 匹配：进程路径和 Exec 的目录可以不同
	if e := matchDesktopEntry(entries, "/usr/lib/firefox/firefox"); e == nil || e.Name != "Firefox" {
		t.Fatalf("应匹配到 Firefox，得到 %+v", e)
	}
	// Icon 为空的条目不该被选中
	if e := matchDesktopEntry(entries, "/usr/bin/noicon"); e != nil {
		t.Fatalf("Icon 为空的条目不该匹配: %+v", e)
	}
	// TryExec 是更硬的信号
	if e := matchDesktopEntry(entries, "/usr/bin/tryme"); e == nil || e.Name != "Try" {
		t.Fatalf("TryExec 应匹配到 Try，得到 %+v", e)
	}
	if e := matchDesktopEntry(entries, "/usr/bin/nothing-here"); e != nil {
		t.Fatalf("不该匹配到任何条目: %+v", e)
	}
}

func mustWrite(t *testing.T, path, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestFindIconFile(t *testing.T) {
	root := t.TempDir()
	// 典型的 hicolor 结构
	mustWrite(t, filepath.Join(root, "icons", "hicolor", "48x48", "apps", "firefox.png"), "PNG")
	// 只有 SVG 的图标也要认，但类型要如实上报
	mustWrite(t, filepath.Join(root, "icons", "Papirus", "64x64", "apps", "foo.svg"), "<svg/>")
	// 两个主题都有同一个名字时，hicolor 应该赢
	mustWrite(t, filepath.Join(root, "icons", "hicolor", "32x32", "apps", "both.png"), "PNG")
	mustWrite(t, filepath.Join(root, "icons", "Papirus", "32x32", "apps", "both.png"), "PNG")
	// pixmaps 那种扁平存放
	mustWrite(t, filepath.Join(root, "pixmaps", "bar.png"), "PNG")

	dirs := []string{filepath.Join(root, "icons"), filepath.Join(root, "pixmaps")}

	if p, mime, _, ok := findIconFile(dirs, "firefox"); !ok || mime != "image/png" || !strings.HasSuffix(p, "firefox.png") {
		t.Fatalf("应找到 firefox.png，得到 %q %q %v", p, mime, ok)
	}
	if _, mime, _, ok := findIconFile(dirs, "foo"); !ok || mime != "image/svg+xml" {
		t.Fatalf("应找到 foo.svg 并如实报 svg 类型，得到 %q %v", mime, ok)
	}
	if p, _, _, ok := findIconFile(dirs, "both"); !ok || !strings.Contains(p, "hicolor") {
		t.Fatalf("hicolor 应优先，得到 %q", p)
	}
	if _, mime, _, ok := findIconFile(dirs, "bar"); !ok || mime != "image/png" {
		t.Fatalf("应找到 pixmaps/bar.png，得到 %q %v", mime, ok)
	}
	if _, _, _, ok := findIconFile(dirs, "nope"); ok {
		t.Fatal("不存在的图标名不该命中")
	}
}

// 用规范文档里那份 Birch 主题的例子，逐条对尺寸判定规则。
func TestParseIndexTheme(t *testing.T) {
	data := `[Icon Theme]
Name=Birch
Inherits=wood,default
Directories=48x48/apps,48x48@2/apps,48x48/mimetypes,32x32/apps,32x32@2/apps,scalable/apps,scalable/mimetypes

[scalable/apps]
Size=48
Type=Scalable
MinSize=1
MaxSize=256
Context=Applications

[32x32/apps]
Size=32
Type=Fixed
Context=Applications

[32x32@2/apps]
Size=32
Scale=2
Type=Fixed
Context=Applications

[48x48/apps]
Size=48
Type=Fixed
Context=Applications

[48x48@2/apps]
Size=48
Scale=2
Type=Fixed
Context=Applications

[48x48/mimetypes]
Size=48
Type=Fixed
Context=MimeTypes

[scalable/mimetypes]
Size=48
Type=Scalable
MinSize=1
MaxSize=256
Context=MimeTypes
`
	dirs, ok := parseIndexTheme(data)
	if !ok || len(dirs) != 7 {
		t.Fatalf("应解析出 7 个尺寸目录，得到 %d（ok=%v）", len(dirs), ok)
	}

	byPath := map[string]iconDir{}
	for _, d := range dirs {
		byPath[d.Path] = d
	}

	fixed32 := byPath["32x32/apps"]
	if !fixed32.matchingSizes(32, 1) {
		t.Error("Fixed 32 应匹配请求 32")
	}
	if fixed32.matchingSizes(48, 1) {
		t.Error("Fixed 32 不该匹配请求 48")
	}
	if got := fixed32.sizeDistance(48, 1); got != 16 {
		t.Errorf("Fixed 32 到 48 的距离应为 16，得到 %d", got)
	}

	// 带 Scale=2 的目录：尺寸相同但缩放不同，不该算匹配
	scaled := byPath["32x32@2/apps"]
	if scaled.Scale != 2 {
		t.Errorf("Scale 应解析为 2，得到 %d", scaled.Scale)
	}
	if scaled.matchingSizes(32, 1) {
		t.Error("Scale=2 的目录不该匹配 scale=1 的请求")
	}

	scalable := byPath["scalable/apps"]
	for _, s := range []int{1, 48, 256} {
		if !scalable.matchingSizes(s, 1) {
			t.Errorf("Scalable 1~256 应匹配 %d", s)
		}
	}
	if scalable.matchingSizes(512, 1) {
		t.Error("Scalable 1~256 不该匹配 512")
	}
}

// 布局非常规的主题（目录名带 @2），靠遍历常见布局是找不到的，
// 只有读了 index.theme 才能命中——这个用例就是为这件事写的。
func TestFindIconViaIndexTheme(t *testing.T) {
	root := t.TempDir()
	base := filepath.Join(root, "icons", "Weird")
	mustWrite(t, filepath.Join(base, "index.theme"), `[Icon Theme]
Name=Weird
Directories=48x48@2/apps

[48x48@2/apps]
Size=48
Scale=2
Type=Fixed
`)
	mustWrite(t, filepath.Join(base, "48x48@2", "apps", "thing.png"), "PNG")

	p, mime, _, ok := findIconFile([]string{filepath.Join(root, "icons")}, "thing")
	if !ok || mime != "image/png" {
		t.Fatalf("应透过 index.theme 找到 thing.png，得到 %q %q %v", p, mime, ok)
	}
	if !strings.Contains(p, "48x48@2") {
		t.Fatalf("命中的路径不对：%s", p)
	}
}
