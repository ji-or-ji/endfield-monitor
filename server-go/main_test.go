package main

import "testing"

func TestSplitCSV(t *testing.T) {
	got := splitCSV(" a , b ,, c ")
	want := []string{"a", "b", "c"}
	if len(got) != len(want) {
		t.Fatalf("长度不符: got %v", got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("第 %d 项不符: got %q want %q", i, got[i], want[i])
		}
	}
	if len(splitCSV("")) != 0 {
		t.Fatalf("空串应返回空切片")
	}
}

func TestOrDefault(t *testing.T) {
	if orDefault("  ", "服务") != "服务" {
		t.Fatal("空白应回落默认值")
	}
	if orDefault("麦麦", "服务") != "麦麦" {
		t.Fatal("非空应原样返回")
	}
}

func TestRound(t *testing.T) {
	if round1(1.25) != 1.3 || round1(1.24) != 1.2 {
		t.Fatalf("round1 不符: %v %v", round1(1.25), round1(1.24))
	}
	if round2(3.14159) != 3.14 || round2(2.71828) != 2.72 {
		t.Fatalf("round2 不符: %v %v", round2(3.14159), round2(2.71828))
	}
}

func TestRateMbps(t *testing.T) {
	// 计数器回绕/重置：cur < prev 一律当 0
	if v := rateMbps(0, 100, 1); v != 0 {
		t.Fatalf("回绕应归零, got %v", v)
	}
	if v := rateMbps(100, 100, 0); v != 0 {
		t.Fatalf("dt 为 0 应归零, got %v", v)
	}
	// 1 秒涨 1000000 字节 = 8 Mbps
	if v := rateMbps(2_000_000, 1_000_000, 1); v != 8 {
		t.Fatalf("正常速率算错, got %v", v)
	}
}
