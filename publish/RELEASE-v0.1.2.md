## 本版做了什么

**采集端**
- 新增配置文件与 `--setup` 配置向导：问答式生成 `enf-collector.json`，并打印防火墙与开机自启命令
- 修好磁盘读写速率与 IO 忙率（此前读写恒为 0；忙率改用 Performance Counter，与容量占用分开）
- 网卡只挑物理网卡并读真实链路速率（不再写死 1000），链路速率 60 秒重探
- 内存类型补全 LPDDR 系列；CPU 频率改用实时值；新增 GPU 利用率与显存采集（过滤虚拟显示适配器）
- 进程补主窗口标题
- 关注服务改为扫全量进程（此前只看占用前 40，低占用服务会被误判离线）
- 所有外部调用加超时；进程与计划任务采集移出 1 秒采样循环

**客户端**
- 设备页新增 GPU 行
- 服务器地址自动剥离多余的 `http(s)://` 前缀与尾部斜杠
- 两层点云呼吸同步；清掉两个过时 API 警告（`Bitmap.Save` / `TextBox.Watermark`）

## 附件说明
- **EndfieldMonitor-v0.1.2-win-x64.exe** — 客户端。C# + Avalonia，单文件、自带运行时，双击即用
- **enf-collector-v0.1.2-windows-amd64.exe** — 服务端（Windows）。Go 单文件，零运行时依赖，只读 HTTP
- **enf-collector-v0.1.2-linux-amd64** — 服务端（Linux）。同一份代码交叉编译，**尚未在真机验证**

## 怎么用

1. 把服务端放到被监视的那台机器上，先跑一次配置向导：
   ```
   enf-collector.exe --setup
   ```
   它会写出 `enf-collector.json`，并打印防火墙规则和开机自启命令。
2. 之后启动只需 `enf-collector.exe --config enf-collector.json`。
3. 打开客户端，首次运行会弹出连接设置，服务器填 `那台机器的IP:8898`（同机就填 `127.0.0.1:8898`），口令与服务端一致。

## 已知问题
- Linux 版服务端未在真机验证
- GPU 显存总量尚未采集（利用率与专用显存占用已可用）
