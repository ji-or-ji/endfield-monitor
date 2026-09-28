package main

import "sync"

// 主网卡的名字与链路速率。启动时探测一次，之后定期刷新：
// Wi-Fi 会中途重新协商速率，只取一次的话，占用率的分母会一直是旧值。

var (
	netMu   sync.RWMutex
	netName string
	netLink float64
)

func setNetwork(name string, link float64) {
	netMu.Lock()
	netName, netLink = name, link
	netMu.Unlock()
}

func currentNetwork() (string, float64) {
	netMu.RLock()
	defer netMu.RUnlock()
	return netName, netLink
}
