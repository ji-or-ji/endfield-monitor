//go:build windows

package main

import "testing"

func TestAdapterKey(t *testing.T) {
	eng := "pid_14828_luid_0x00000000_0x0000F6F1_phys_0_eng_0_engtype_3D"
	if k := adapterKey(eng); k != "luid_0x00000000_0x0000F6F1_phys_0" {
		t.Fatalf("引擎实例的适配器键不符: %q", k)
	}
	mem := "luid_0x00000000_0x0000F6F1_phys_0"
	if k := adapterKey(mem); k != mem {
		t.Fatalf("显存实例的适配器键不符: %q", k)
	}
}

func TestEngineType(t *testing.T) {
	if k := engineType("pid_1_luid_0xA_phys_0_eng_0_engtype_VideoDecode"); k != "VideoDecode" {
		t.Fatalf("引擎类型不符: %q", k)
	}
}

func TestGPUAggregate(t *testing.T) {
	// 适配器 A：3D 引擎 30+25=55（最忙），Copy 5；适配器 B：3D 10
	utils := []pdhItem{
		{Name: "pid_1_luid_0xA_phys_0_eng_0_engtype_3D", Value: 30},
		{Name: "pid_2_luid_0xA_phys_0_eng_0_engtype_3D", Value: 25},
		{Name: "pid_1_luid_0xA_phys_0_eng_0_engtype_Copy", Value: 5},
		{Name: "pid_1_luid_0xB_phys_0_eng_0_engtype_3D", Value: 10},
	}
	mems := []pdhItem{
		{Name: "luid_0xA_phys_0", Value: 1048576 * 100}, // 适配器 A 用 100 MB
		{Name: "luid_0xB_phys_0", Value: 1048576 * 999}, // 适配器 B 用 999 MB
	}
	util, mem := gpuAggregate(utils, mems)
	if util != 55 {
		t.Fatalf("利用率应取最忙适配器的最忙引擎 55, got %v", util)
	}
	if mem != 100 {
		t.Fatalf("显存应只统计最忙适配器 A 的 100 MB, got %v", mem)
	}
}
