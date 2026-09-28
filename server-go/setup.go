package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// runSetup 是 --setup 的交互式配置向导：问几个问题，写出一份
// enf-collector.json，再把后续要用的防火墙与计划任务命令打印出来。
// 它只在你要配置时跑一次，常驻运行时完全不会走到这里。
func runSetup(path string) error {
	in := bufio.NewReader(os.Stdin)

	fmt.Println("== EndfieldMonitor 采集端 · 配置向导 ==")
	fmt.Println("每项直接回车即取方括号里的默认值。")
	fmt.Println()

	port := askInt(in, "监听端口", 8898)
	token := askString(in, "共享口令（留空 = 不校验，建议只在本机调试时留空）", "")

	fc := fileConfig{Port: &port, Token: &token}

	if askBool(in, "要不要盯一个服务（进程 / 端口 / 计划任务）", false) {
		name := askString(in, "  服务显示名", "服务")
		procs := splitCSV(askString(in, "  进程名关键字（逗号分隔，可留空）", ""))
		ports := parsePorts(askString(in, "  要盯的端口（逗号分隔，可留空）", ""))
		tasks := splitCSV(askString(in, "  计划任务名（逗号分隔，可留空）", ""))
		fc.WatchName = &name
		if len(procs) > 0 {
			fc.WatchProcs = procs
		}
		if len(ports) > 0 {
			fc.WatchPorts = ports
		}
		if len(tasks) > 0 {
			fc.WatchTasks = tasks
		}
	}

	data, err := json.MarshalIndent(fc, "", "  ")
	if err != nil {
		return err
	}
	if err := os.WriteFile(path, append(data, '\n'), 0o644); err != nil {
		return err
	}

	fmt.Printf("\n已写入: %s\n", path)
	printNextSteps(path, port)
	return nil
}

// printNextSteps 打印启动命令、防火墙规则和开机自启的计划任务。
// 让用户不用再手抄一长串参数，也不用对着 README 抠 -Argument 的写法。
func printNextSteps(path string, port int) {
	exe, err := os.Executable()
	if err != nil {
		exe = "enf-collector.exe"
	}
	dir := filepath.Dir(exe)

	fmt.Println()
	fmt.Println("以后这样启动：")
	fmt.Printf("  \"%s\" --config \"%s\"\n", exe, path)
	fmt.Println()
	fmt.Println("放行防火墙（管理员 PowerShell 执行一次）：")
	fmt.Printf("  New-NetFirewallRule -DisplayName 'ENF Monitor %d' -Direction Inbound -Protocol TCP -LocalPort %d -Action Allow\n", port, port)
	fmt.Println()
	fmt.Println("开机自启（管理员 PowerShell 执行一次；-Argument 里不要再写一遍 exe 路径）：")
	fmt.Printf("  $exe = '%s'\n", exe)
	fmt.Printf("  $cfg = '%s'\n", path)
	fmt.Printf("  $a = New-ScheduledTaskAction -Execute $exe -Argument \"--config `\"$cfg`\"\" -WorkingDirectory '%s'\n", dir)
	fmt.Println("  $t = New-ScheduledTaskTrigger -AtStartup")
	fmt.Println("  $p = New-ScheduledTaskPrincipal -UserId 'Administrator' -LogonType S4U -RunLevel Highest")
	fmt.Println("  Register-ScheduledTask -TaskName 'EnfieldMonitor' -Action $a -Trigger $t -Principal $p")
	fmt.Println()
}

func askString(in *bufio.Reader, prompt, def string) string {
	if def != "" {
		fmt.Printf("%s [%s]: ", prompt, def)
	} else {
		fmt.Printf("%s: ", prompt)
	}
	line, _ := in.ReadString('\n')
	// 连 BOM 一起去掉：从管道或文件喂输入时，首行常会混进一个 U+FEFF
	line = strings.Trim(line, " \t\r\n\ufeff")
	if line == "" {
		return def
	}
	return line
}

func askInt(in *bufio.Reader, prompt string, def int) int {
	for {
		s := askString(in, prompt, strconv.Itoa(def))
		v, err := strconv.Atoi(s)
		if err == nil && v > 0 && v <= 65535 {
			return v
		}
		fmt.Println("  端口得是 1~65535 之间的数字。")
	}
}

func askBool(in *bufio.Reader, prompt string, def bool) bool {
	d := "n"
	if def {
		d = "y"
	}
	for {
		switch strings.ToLower(askString(in, prompt+" (y/n)", d)) {
		case "y", "yes", "是":
			return true
		case "n", "no", "否":
			return false
		}
		fmt.Println("  请输入 y 或 n。")
	}
}
