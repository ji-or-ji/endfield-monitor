//go:build windows

package main

import (
	"unsafe"

	"golang.org/x/sys/windows"
)

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
