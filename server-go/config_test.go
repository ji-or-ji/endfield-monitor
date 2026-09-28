package main

import "testing"

func TestApplyFilePrecedence(t *testing.T) {
	host := "10.0.0.5"
	port := 9000
	name := "麦麦"
	file := fileConfig{Host: &host, Port: &port, WatchName: &name, WatchProcs: []string{"a", "b"}}

	// 命令行没给 -> 文件生效
	cfg := config{}
	cfg.watchProc = []string{"from-flag"}
	applyFile(&cfg, file, map[string]bool{})
	if cfg.host != host || cfg.port != port || cfg.watchName != name {
		t.Fatalf("文件应补上命令行未给的项: %+v", cfg)
	}
	if len(cfg.watchProc) != 2 || cfg.watchProc[0] != "a" {
		t.Fatalf("watchProc 应用文件值: %v", cfg.watchProc)
	}

	// 命令行显式给了 -> 文件不覆盖
	cfg2 := config{host: "127.0.0.1", port: 1234, watchName: "本机"}
	cfg2.watchProc = []string{"from-flag"}
	applyFile(&cfg2, file, map[string]bool{
		"host": true, "port": true, "watch-name": true, "watch-procs": true,
	})
	if cfg2.host != "127.0.0.1" || cfg2.port != 1234 || cfg2.watchName != "本机" {
		t.Fatalf("命令行应优先于文件: %+v", cfg2)
	}
	if len(cfg2.watchProc) != 1 || cfg2.watchProc[0] != "from-flag" {
		t.Fatalf("watchProc 应保留命令行值: %v", cfg2.watchProc)
	}
}

func TestParsePorts(t *testing.T) {
	got := parsePorts("8898, abc ,6099,70000")
	if len(got) != 2 || got[0] != 8898 || got[1] != 6099 {
		t.Fatalf("parsePorts 应只留合法端口: %v", got)
	}
}
