//go:build windows

package main

import (
	"net"

	"golang.org/x/sys/windows"
)

// 只读主网卡一个接口的收发计数。
//
// gopsutil 的 net.IOCounters 会先 net.Interfaces() 枚举全部网卡
// （含一堆虚拟 / VPN / 隧道网卡），再对每一块调一次 GetIfEntry2，
// 本机实测每次 10ms。可我们只关心一块网卡：拿到接口索引后直接查一次，
// 只要几微秒。
var netIfIndex uint32

// prepareNetInterface 记下目标接口的索引；名字对不上就清 0，让上层退回全量读取。
func prepareNetInterface(name string) {
	netIfIndex = 0
	if name == "" {
		return
	}
	if ifi, err := net.InterfaceByName(name); err == nil {
		netIfIndex = uint32(ifi.Index)
	}
}

// readNetIf 读该接口的 64 位收发字节数。索引未就绪或接口已消失时返回 false。
func readNetIf() (netSample, bool) {
	idx := netIfIndex
	if idx == 0 {
		return netSample{}, false
	}
	row := windows.MibIfRow2{InterfaceIndex: idx}
	if err := windows.GetIfEntry2Ex(windows.MibIfEntryNormal, &row); err != nil {
		return netSample{}, false
	}
	return netSample{recv: row.InOctets, sent: row.OutOctets}, true
}
