package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
)

// fileConfig 是配置文件（enf-collector.json）的结构。
// 用指针区分"没写这一项"和"写了个空值"：只有写了才去覆盖命令行默认值。
type fileConfig struct {
	Host       *string  `json:"host,omitempty"`
	Port       *int     `json:"port,omitempty"`
	Token      *string  `json:"token,omitempty"`
	WatchName  *string  `json:"watchName,omitempty"`
	WatchProcs []string `json:"watchProcs,omitempty"`
	WatchPorts []int    `json:"watchPorts,omitempty"`
	WatchTasks []string `json:"watchTasks,omitempty"`
}

// defaultConfigPath 取 exe 同目录下的 enf-collector.json。
func defaultConfigPath() string {
	exe, err := os.Executable()
	if err != nil {
		return "enf-collector.json"
	}
	return filepath.Join(filepath.Dir(exe), "enf-collector.json")
}

// loadConfigFile 读配置文件。不存在时返回 found=false 且不报错；
// 存在但读不动或解析失败才算错误。
func loadConfigFile(path string) (fileConfig, bool, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return fileConfig{}, false, nil
		}
		return fileConfig{}, false, err
	}
	var fc fileConfig
	if err := json.Unmarshal(data, &fc); err != nil {
		return fileConfig{}, false, fmt.Errorf("%s: %w", path, err)
	}
	return fc, true, nil
}

// applyFile 把文件里的项填到「命令行没显式指定」的字段上。
// 命令行永远优先，方便临时覆盖配置文件。
func applyFile(cfg *config, file fileConfig, set map[string]bool) {
	if !set["host"] && file.Host != nil {
		cfg.host = *file.Host
	}
	if !set["port"] && file.Port != nil {
		cfg.port = *file.Port
	}
	if !set["token"] && file.Token != nil {
		cfg.token = *file.Token
	}
	if !set["watch-name"] && file.WatchName != nil {
		cfg.watchName = *file.WatchName
	}
	if !set["watch-procs"] && file.WatchProcs != nil {
		cfg.watchProc = file.WatchProcs
	}
	if !set["watch-ports"] && file.WatchPorts != nil {
		cfg.watchPort = file.WatchPorts
	}
	if !set["watch-tasks"] && file.WatchTasks != nil {
		cfg.watchTask = file.WatchTasks
	}
}

// parsePorts 解析逗号分隔的端口，非法项打一行警告后跳过。
func parsePorts(csv string) []int {
	var out []int
	for _, s := range splitCSV(csv) {
		p, err := strconv.Atoi(s)
		if err != nil || p <= 0 || p > 65535 {
			fmt.Printf("[collector] 忽略无法解析的端口: %q\n", s)
			continue
		}
		out = append(out, p)
	}
	return out
}
