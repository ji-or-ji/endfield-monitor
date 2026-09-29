//go:build !windows && !linux

package main

// 其它平台暂不采集电池，一律按“没有电池”处理。
func readBattery() BatteryInfo { return BatteryInfo{} }

func batteryCapacity() (design, full int) { return 0, 0 }
