## 修一个我自己引入的回归：配置向导没了

`--setup` 这条参数在 v0.4.0 里被误删了——加 `--probe-icon` 那次改动把它那一行**替换**掉，
而不是在它后面追加。编译期看不出来（`setup` 变量还在被使用），于是一路带到 v0.4.0 和 v0.4.1，
而 README 还在教人用它。有人的一句「配置向导真跑过吗」才把它翻出来。

这版把参数补回来，并把向导从头到尾实跑了一遍：

- 非法端口（输入 `abc`）被拦住并重问，提示「端口得是 1~65535 之间的数字」
- 新增的「要不要开轻量模式」那一问正常，答案正确落进 JSON
- 生成的 JSON 正确，且防火墙规则与开机自启命令里的端口跟着变成所填的端口

用向导生成的配置启动，日志与行为都对得上：

```
[collector] 已加载配置: .../enf-collector.json
[collector] 采样模式: 轻量（没人拉快照时自动放慢）
[collector] 已监听 http://0.0.0.0:9000/snapshot (token=已设置)
```

带口令请求返回数据，不带口令一律 401。

## 附件说明

只发服务端，四个：

- **enf-collector-v0.4.2-windows-amd64.exe** — 普通版
- **enf-collector-plus-v0.4.2-windows-amd64.exe** — 增强版
- **enf-collector-v0.4.2-linux-amd64** — Linux 普通版
- **enf-collector-plus-v0.4.2-linux-amd64** — Linux 增强版

**客户端这一版没有任何改动**，继续用 v0.4.1 的 `EndfieldMonitor-v0.4.1-win-x64.exe` 即可。

## 已知问题

- 这一版只修了 `--setup`。v0.4.1 里那套 Linux 图标采集仍未在真机验证过，
  依然欢迎拿 `--probe-icon <pid>` 跑一次、把输出发回来
- 增强模式的界面还没做，客户端目前只识别、不显示
