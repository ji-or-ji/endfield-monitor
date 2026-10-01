package main

// version 是这一份二进制的版本号，由构建脚本在链接时注入：
//
//	go build -ldflags "-X main.version=0.5.0"
//
// 源码里直接 go run / go test 时它就是 dev。采集端会把它放进快照的
// server.collector_version，客户端据此判断对面那一端要不要更新。
var version = "dev"
