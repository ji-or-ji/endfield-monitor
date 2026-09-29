//go:build windows

package main

import (
	"unsafe"

	"github.com/yusufpapurcu/wmi"
	"golang.org/x/sys/windows"
)

// 电池容量：满充与设计容量得从 root\WMI 的这两个类取。
// 不是每台机器都有（有些固件不暴露），拿不到就当未知。
type batteryFullCharged struct {
	FullChargedCapacity uint32
}

type batteryStaticData struct {
	DesignedCapacity uint32
}

func batteryCapacity() (design, full int) {
	var f []batteryFullCharged
	if err := wmi.QueryNamespace("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", &f, `root\WMI`); err == nil && len(f) > 0 {
		full = int(f[0].FullChargedCapacity)
	}
	var s []batteryStaticData
	if err := wmi.QueryNamespace("SELECT DesignedCapacity FROM BatteryStaticData", &s, `root\WMI`); err == nil && len(s) > 0 {
		design = int(s[0].DesignedCapacity)
	}
	return design, full
}

// SYSTEM_POWER_STATUS：一次系统调用拿全，不必走 WMI / PowerShell。
type systemPowerStatus struct {
	ACLineStatus        byte
	BatteryFlag         byte
	BatteryLifePercent  byte
	SystemStatusFlag    byte
	BatteryLifeTime     uint32
	BatteryFullLifeTime uint32
}

var procGetSystemPowerStatus = windows.NewLazySystemDLL("kernel32.dll").NewProc("GetSystemPowerStatus")

const (
	batFlagCharging  = 8
	batFlagNoBattery = 128
	batUnknownByte   = 255
	batUnknownTime   = 0xFFFFFFFF
)

func readBattery() BatteryInfo {
	var st systemPowerStatus
	r, _, _ := procGetSystemPowerStatus.Call(uintptr(unsafe.Pointer(&st)))
	if r == 0 {
		return BatteryInfo{}
	}

	// 台式机没有电池，这一位会置上
	if st.BatteryFlag&batFlagNoBattery != 0 {
		return BatteryInfo{}
	}

	b := BatteryInfo{Present: true}
	b.OnAC = st.ACLineStatus == 1
	b.Charging = b.OnAC && st.BatteryFlag&batFlagCharging != 0

	if st.BatteryLifePercent != batUnknownByte {
		b.Percent = float64(st.BatteryLifePercent)
	} else {
		b.Percent = -1
	}

	// 剩余时间经常给不出，0xFFFFFFFF 表示未知
	if st.BatteryLifeTime != batUnknownTime {
		b.SecondsLeft = float64(st.BatteryLifeTime)
	}
	return b
}
