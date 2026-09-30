//go:build windows

package main

import (
	"os"
	"path/filepath"
	"testing"
)

// 从 exe 提取图标这条路能不能走通。跑完会把 PNG 落到临时目录，方便肉眼确认。
func TestAppIconPNG(t *testing.T) {
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	data, err := appIconPNG(exe)
	if err != nil {
		t.Fatalf("%s 提取失败: %v", exe, err)
	}
	if len(data) < 100 || data[0] != 0x89 || data[1] != 'P' {
		t.Fatalf("结果不像 PNG，%d 字节", len(data))
	}
	out := filepath.Join(os.TempDir(), "enf-icon-test.png")
	if err := os.WriteFile(out, data, 0o644); err != nil {
		t.Fatal(err)
	}
	t.Logf("%d 字节 -> %s", len(data), out)
}
