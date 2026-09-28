//go:build windows

package main

import "testing"

func TestMediaOf(t *testing.T) {
	phys := []winPhysDisk{{FriendlyName: "Samsung SSD 980", MediaType: "SSD", BusType: "NVMe"}}
	if m := mediaOf("Samsung SSD 980 PRO 1TB", phys); m != "NVMe SSD" {
		t.Fatalf("正常匹配应得 NVMe SSD, got %q", m)
	}
	// 型号为空不能误命中第一块物理盘
	if m := mediaOf("", phys); m != "本地磁盘" {
		t.Fatalf("空型号应回落通用名, got %q", m)
	}
}

func TestIsVirtualDisplay(t *testing.T) {
	if !isVirtualDisplay("Todesk Virtual Display Adapter") {
		t.Fatal("Todesk 虚拟适配器应被识别")
	}
	if isVirtualDisplay("Intel(R) UHD Graphics") {
		t.Fatal("核显不该被当成虚拟适配器")
	}
}

func TestIsWiredAdapter(t *testing.T) {
	if !isWiredAdapter(winAdapter{Description: "Realtek PCIe GbE Family Controller", Type: "Ethernet"}) {
		t.Fatal("有线网卡应识别为 wired")
	}
	if isWiredAdapter(winAdapter{Description: "Intel(R) Wi-Fi 6 AX201 160MHz", Type: "Wireless80211"}) {
		t.Fatal("无线网卡不该识别为 wired")
	}
}

func TestIsVirtualIface(t *testing.T) {
	if !isVirtualIface("Famatech Radmin VPN Ethernet Adapter") {
		t.Fatal("Radmin VPN 应被识别为虚拟网卡")
	}
	if !isVirtualIface("VirtualBox Host-Only Ethernet Adapter") {
		t.Fatal("VirtualBox 网卡应被识别为虚拟网卡")
	}
	if isVirtualIface("Intel(R) Wi-Fi 6 AX201 160MHz") {
		t.Fatal("真实无线网卡不该被识别为虚拟")
	}
}
