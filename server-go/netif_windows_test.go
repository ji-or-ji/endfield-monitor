//go:build windows

package main

import "testing"

// 验证单接口读取这条路真的走通了：名字解析得出索引，计数器也读得到。
// 一旦快路径失效（接口改名、索引过期之类），就会静默退回每次 10ms 的全量枚举，
// 所以这里主动拦一道，让优化失效变成测试失败，而不是悄无声息。
func TestNetIfFastPath(t *testing.T) {
	name, _ := detectNetwork()
	if name == "" {
		t.Skip("没有可用的主网卡")
	}
	prepareNetInterface(name)
	s, ok := readNetIf()
	if !ok {
		t.Fatalf("主网卡 %q 没走通单接口读取，会退回全量枚举（每次约 10ms）", name)
	}
	t.Logf("主网卡 %q：累计收 %d 字节 / 发 %d 字节", name, s.recv, s.sent)
}
