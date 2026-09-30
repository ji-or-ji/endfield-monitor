//go:build !plus

package main

// plusBuild 表示当前这份是不是增强版。
//
// 普通版与增强版共用同一份源码，靠 `go build -tags plus` 编出两个产物：
//
//	go build -ldflags "-s -w" -o enf-collector.exe .
//	go build -tags plus -ldflags "-s -w" -o enf-collector-plus.exe .
//
// 增强版多出「启停应用」和「取应用图标」，并在快照里声明自己的 capabilities，
// 客户端据此切增强模式。普通版行为与以前完全一致。
const plusBuild = false
