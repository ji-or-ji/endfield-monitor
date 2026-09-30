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
