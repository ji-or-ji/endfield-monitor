//go:build linux

package main

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Linux 的电池信息就在 /sys/class/power_supply 下，每块电池一个目录，
// 读文件即可，不依赖任何外部命令。多块电池时取平均容量、累加剩余时间。
func readBattery() BatteryInfo {
	const root = "/sys/class/power_supply"
	entries, err := os.ReadDir(root)
	if err != nil {
		return BatteryInfo{}
	}

	var (
		found      bool
		percentSum float64
		count      int
		charging   bool
		onAC       bool
		seconds    float64
	)

	for _, e := range entries {
		dir := filepath.Join(root, e.Name())
		if readSysFile(filepath.Join(dir, "type")) != "Battery" {
			continue
		}
		found = true

		if v, ok := readSysInt(filepath.Join(dir, "capacity")); ok {
			percentSum += float64(v)
			count++
		}

		switch readSysFile(filepath.Join(dir, "status")) {
		case "Charging":
			charging = true
			onAC = true
		case "Full", "Not charging":
			onAC = true
		}

		// 剩余时间 = 存量 / 功耗。energy_* 单位 µWh，power_now 单位 µW；
		// 老驱动只有 charge_*/current_now（µAh / µA），同样相除得小时。
		now, nowOK := readSysInt(filepath.Join(dir, "energy_now"))
		rate, rateOK := readSysInt(filepath.Join(dir, "power_now"))
		if !nowOK || !rateOK {
			now, nowOK = readSysInt(filepath.Join(dir, "charge_now"))
			rate, rateOK = readSysInt(filepath.Join(dir, "current_now"))
		}
		if nowOK && rateOK && rate > 0 {
			seconds += float64(now) / float64(rate) * 3600
		}
	}

	if !found {
		return BatteryInfo{}
	}

	b := BatteryInfo{Present: true, Charging: charging, OnAC: onAC, SecondsLeft: seconds}
	if count > 0 {
		b.Percent = percentSum / float64(count)
	} else {
		b.Percent = -1
	}
	return b
}

// 容量：满充与设计容量，统一折算成 mWh。sysfs 里能量是 µWh，电量是 µAh。
func batteryCapacity() (design, full int) {
	const root = "/sys/class/power_supply"
	entries, err := os.ReadDir(root)
	if err != nil {
		return 0, 0
	}
	for _, e := range entries {
		dir := filepath.Join(root, e.Name())
		if readSysFile(filepath.Join(dir, "type")) != "Battery" {
			continue
		}
		full = sysCapacityMWh(dir, "energy_full", "charge_full")
		design = sysCapacityMWh(dir, "energy_full_design", "charge_full_design")
		return design, full // 多块电池的情况很少，取第一块
	}
	return 0, 0
}

// sysCapacityMWh 优先读 energy_*（µWh，除 1000 得 mWh）；
// 退而求其次读 charge_*（µAh），乘电压折算成能量。
func sysCapacityMWh(dir, energyKey, chargeKey string) int {
	if v, ok := readSysInt(filepath.Join(dir, energyKey)); ok && v > 0 {
		return int(v / 1000)
	}
	v, ok := readSysInt(filepath.Join(dir, chargeKey))
	if !ok || v <= 0 {
		return 0
	}
	volt, ok := readSysInt(filepath.Join(dir, "voltage_now")) // µV
	if !ok || volt <= 0 {
		volt = 3700000 // 拿不到电压就按 3.7V 估
	}
	return int(float64(v) / 1e6 * (float64(volt) / 1e6) * 1000)
}

func readSysFile(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(data))
}

func readSysInt(path string) (int64, bool) {
	s := readSysFile(path)
	if s == "" {
		return 0, false
	}
	v, err := strconv.ParseInt(s, 10, 64)
	if err != nil {
		return 0, false
	}
	return v, true
}
