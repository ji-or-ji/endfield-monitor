## 这一版是个预告

增强版（plus）的**服务端已经能用了**，客户端的增强界面还没接——打算对着真终末地的界面
看清楚该加在哪再动。所以这一版先把能力放出来，界面留给下一版。

## 增强版（plus）来了

同一份源码编出两个产物，普通版的行为跟以前完全一样：

| 产物 | 说明 |
| --- | --- |
| `enf-collector-v0.4.0-windows-amd64.exe` | 普通版，只读 |
| `enf-collector-plus-v0.4.0-windows-amd64.exe` | 增强版，多出三样动手能力 |

增强版会在快照里声明 `capabilities`（普通版连这个键都没有）：

| 能力 | 端点 | 作用 |
| --- | --- | --- |
| `app.stop` | `GET /app/stop?pid=` | 结束进程，连同它派生的子进程一起停 |
| `app.restart` | `GET /app/restart?pid=` | 用原路径与参数重启，并脱离服务端 |
| `app.icon` | `GET /app/icon?pid=` | 该进程可执行文件的图标（PNG） |

想先试试（服务端得配口令）：

```powershell
.\enf-collector-plus.exe --port 8898 --token 你的口令
curl.exe -H "Authorization: Bearer 你的口令" http://127.0.0.1:8898/snapshot
curl.exe -H "Authorization: Bearer 你的口令" "http://127.0.0.1:8898/app/icon?pid=1234" -o icon.png
```

### 安全门槛

服务端第一次能「动手」，门槛是按这个前提划的：

- 端点只在增强版注册；普通版是 404，连路由都不存在
- 启停要求服务端**必须配了共享口令**，否则一律 403。增强版启动时没配口令会打印警告
- 取图标只认快照里出现过的 pid，外部传不进任意路径
- 每次启停都会在服务端日志留一行，写明 pid 与来源地址

## 客户端

客户端已经能识别对面是不是增强版（快照里 `capabilities` 非空即增强），但**这一版界面上
没有任何变化**，增强模式长什么样留到下一版。取图标那条路也已就绪，应用列表暂时还用着
内置矢量图标。

## 附件说明

- **EndfieldMonitor-v0.4.0-win-x64.exe** — 客户端。单文件、自带运行时，双击即用
- **enf-collector-v0.4.0-windows-amd64.exe** — 服务端（Windows，普通版）
- **enf-collector-plus-v0.4.0-windows-amd64.exe** — 服务端（Windows，增强版）
- **enf-collector-v0.4.0-linux-amd64** — 服务端（Linux，普通版）

增强版目前只有 Windows：启停与取图标的实现都走 Windows 接口，其他平台上是桩。

## 已知问题

- Linux 版服务端未在真机验证
- 增强模式的界面还没做（这正是本次叫预告的原因）
- 点云批量路径与老的逐点画法观感略有差别，设置里的开关可切换
