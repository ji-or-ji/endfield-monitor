## 这一版补上 Linux 的图标采集，并求人帮忙测

v0.4.0 里增强版只有 Windows。这版把 Linux 那条路也铺上了，但**没有在真机上跑过**——
手上没有 Linux 机器。所以这版的重点其实在后半段：**恳请有 Linux 的开发者帮忙验一把**。

## Linux 上取图标的做法

Linux 的 ELF 里没有图标资源（这点和 Windows 的 PE 完全不同），图标是登记在 `.desktop` 文件里、
再由图标主题按名字解析到具体文件。所以链条是：

```
pid -> /proc/<pid>/exe -> 匹配 .desktop 的 Exec= -> Icon= -> 图标主题里的文件
```

沿路处理了这些：

- XDG 目录按规范展开（`$XDG_DATA_HOME` / `$XDG_DATA_DIRS`，未设置时用默认值），
  另外补上 Flatpak 与 Snap 的导出目录
- `.desktop` 只看 `[Desktop Entry]` 段；匹配时优先看 `TryExec=`，其次看 `Exec=` 的首个程序名
  （按 basename 比对，两边目录不必相同）
- 图标名先在主题里按 `<主题>/<尺寸>/<apps>/<名字>.<后缀>` 找，`hicolor` 优先，
  最后兜底到 `/usr/share/pixmaps`
- 后缀按 png > svg > xpm 试；**命中 SVG 时如实返回 `image/svg+xml`**，不假装是 PNG

## 请帮忙测试

需要一台 Linux，有没有桌面环境都行——没有桌面环境反而更接近采集端的真实处境。步骤：

```bash
chmod +x enf-collector-plus-v0.4.1-linux-amd64
./enf-collector-plus-v0.4.1-linux-amd64 --probe-icon <任意进程的 pid>
```

`--probe-icon` 只打印，不常驻、不改动任何东西。输出大致是这个形状（下面是示意，不是真实结果）：

```
[probe] pid = 12345
[probe] /proc/12345/exe -> "/usr/lib/firefox/firefox"
[probe] .desktop 目录：
[probe]   /root/.local/share/applications                                   0 个
[probe]   /usr/share/applications                                         137 个
[probe] 共载入 137 个条目（去重后）
[probe] 匹配到条目: /usr/share/applications/firefox.desktop
[probe]   Name="Firefox" Exec="/usr/bin/firefox %u" TryExec="/usr/bin/firefox" Icon="firefox"
[probe] 图标主题根目录：
[probe]   /usr/share/icons                  主题=[hicolor Adwaita]
[probe] 图标名="firefox"，试了 60 个路径
[probe] 命中: /usr/share/icons/hicolor/48x48/apps/firefox.png  (image/png)
```

**想要你回报的：**

1. 这段输出的**真实内容**，尤其是没命中的情况——是哪一步断的
2. 你机器上的图标分布和预期差多少（比如是不是只有 SVG、主题目录结构是不是不按常见布局）
3. 有 Flatpak 或 Snap 应用的话，它们的 pid 能不能取到图标

## 已知还没做的

- **Flatpak 应用大概率取不到**。这类进程的可执行文件是 `bwrap`，按名字匹配不上；
  正路是读 `/proc/<pid>/environ` 里的 `FLATPAK_ID`，这一步还没实现。
- 没有解析各主题的 `index.theme`，而是直接按 `<主题>/<尺寸>/<apps>/` 的常见布局遍历。
  结构特殊的主题会漏，代价是多几次 `stat`。
- 图标解析不出来时不报错，回落到客户端自己的内置图标。

## 服务端也补了 Linux 的启停

`app.stop` / `app.restart` 在 Linux 上原本是桩，这版补上了（重启用 `Setsid` 让新进程
脱离服务端会话，免得服务端一停就把应用带走）。同样**未在真机验证**。

## 附件说明

- 客户端：**EndfieldMonitor-v0.4.1-win-x64.exe**
- Windows 采集端：**enf-collector-v0.4.1-windows-amd64.exe**（普通）
  / **enf-collector-plus-v0.4.1-windows-amd64.exe**（增强）
- 新增 Linux 增强版：**enf-collector-plus-v0.4.1-linux-amd64**
- Linux 普通版：**enf-collector-v0.4.1-linux-amd64**

## 已知问题

- Linux 相关实现全部未在真机验证过（本版重点就是求验证）
- 增强模式的界面还没做，客户端目前只识别、不显示
- 点云批量路径与逐点画法观感略有差别，设置里的开关可切换
