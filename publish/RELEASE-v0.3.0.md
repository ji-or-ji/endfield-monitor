## 本版做了什么

### 电池监测（笔记本）

采集端新增电池采集，客户端在设备页多出一行「电池」——**只有真有电池的机器才显示**，台式机不会多出这一行。

- 显示：电量、充电状态、剩余时间
- Windows 侧走一次 `GetSystemPowerStatus` 系统调用，不碰 WMI / PowerShell
- Linux 侧读 `/sys/class/power_supply`（`capacity` / `status` / `energy_now` / `power_now`）
- 快照新增 `battery` 块：`present` / `percent` / `charging` / `on_ac` / `seconds_left`

### 掉线不再回落演示数据

以前连不上服务端时，客户端会摆出一套假的演示数据（假的应用列表、假的占用率），看着像真的。
现在掉线（或尚未连上）时，所有数据位一律显示 `-`：

- 中心数字、运行时长、上次刷新、监控目标、`CPU - / MEM - / 综合 -`
- 设备页保留固定骨架（处理器 / 显卡 / 内存 / 网络），数值全为 `-`；磁盘行不显示（无从得知有几块盘）
- 应用列表清空，圆环上那两条历史弧清零

### 其它

- README 开头的定位句改准：不再限定「Windows」与「局域网」，把「本机自看」和「Linux 版（未真机验证）」都写清楚
- 构建段修正 Go 版本要求（以 go.mod 为准），补上 Linux 交叉编译命令

## 附件说明

- **EndfieldMonitor-v0.3.0-win-x64.exe** — 客户端。C# + Avalonia，单文件、自带运行时，双击即用
- **enf-collector-v0.3.0-windows-amd64.exe** — 服务端（Windows）
- **enf-collector-v0.3.0-linux-amd64** — 服务端（Linux），尚未在真机验证

## 已知问题

- Linux 版服务端未在真机验证（电池采集的 Linux 分支同理）
- 电池的「规格」一栏留空：容量与健康度尚未采集
- GPU 显存总量尚未采集；CPU 的「最高频率」显示的是标称值，不是睿频上限
- 点云批量路径与老的逐点画法观感略有差别（前者逐三角形混合，密集处略实），设置里的开关可切换
