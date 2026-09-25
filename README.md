# EnfieldMonitor · 局域网设备监视台

界面风格致敬《明日方舟：终末地》，用来在局域网里实时查看一台 Windows 机器的运行状况。
不开远程桌面，打开客户端就能看见。

> 界面设计（配色、电量环、版式、动效语言）参考并致敬 **zmd-manager / 终末地管理器**：
> https://github.com/QinAnze/zmd-manager
>
> 本项目是**独立实现**：客户端用 C# + Avalonia 重写，服务端采集逻辑自行编写，
> 未复制原项目源代码。想复用那套原版界面，请直接前往原项目。

---

## 结构

```
├── src/EndfieldMonitor/        C# / Avalonia 客户端
│   ├── Controls/               自绘控件（电量环、粒子点云、走势图）
│   ├── Models/                 与服务端 JSON 对齐的数据模型
│   ├── Services/               网络层（拉取快照）
│   ├── Theme/                  设计令牌（配色、字体）
│   ├── ViewModels/             视图模型
│   └── Views/                  窗口与页面
├── server/collector_server.py  服务端采集端（只读 HTTP，Python + psutil）
└── reference/                  仅本地研究用的原项目资料，不随仓库分发
```

## 运行原理

```
[被监视机器]                          [局域网任意设备]
collector_server.py  ──HTTP/JSON──▶  EnfieldMonitor.exe
(psutil 采样, 1s 一次)                (Avalonia 渲染, 1s 轮询)
```

服务端只读、无写操作，向局域网提供一个 `/snapshot` 端点；客户端凭共享口令拉取，
掉线时自动回落演示数据并标记为「离线」。

## 服务端部署

```powershell
# 依赖
pip install psutil

# 启动（--token 为空则不校验口令，仅建议本地调试时使用）
python collector_server.py --port 8898 --token <共享口令>
```

放行防火墙入站 TCP：

```powershell
New-NetFirewallRule -DisplayName 'ENF Monitor 8898' -Direction Inbound -Protocol TCP -LocalPort 8898 -Action Allow
```

建议再挂一个开机自启的计划任务：

```powershell
$dir = 'C:\path\to\collector'
$a = New-ScheduledTaskAction -Execute 'C:\path\to\python.exe' `
     -Argument "$dir\collector_server.py --port 8898 --token <共享口令>" -WorkingDirectory $dir
$t = New-ScheduledTaskTrigger -AtStartup
$p = New-ScheduledTaskPrincipal -UserId 'Administrator' -LogonType S4U -RunLevel Highest
Register-ScheduledTask -TaskName 'EnfieldMonitor' -Action $a -Trigger $t -Principal $p
```

## 客户端构建

需要 .NET SDK 10 与 Avalonia 模板：

```powershell
dotnet new install Avalonia.Templates
dotnet build  src/EndfieldMonitor            # 调试构建
dotnet publish src/EndfieldMonitor -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true                  # 单文件发布，可直接分发
```

## 口令

客户端与服务端用同一个共享口令。口令不对，服务端返回 `401`，客户端只会停在「离线」。
客户端内可修改服务器地址与口令（设置入口在顶栏）。

---

## 协议

待定。

原项目 **zmd-manager 未附许可证文件**，按默认版权规则属「保留所有权利」；
本项目与其为「风格致敬、独立实现」的关系，不包含其源代码。
