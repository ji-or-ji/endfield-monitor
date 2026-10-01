## 加了 Linux 客户端

客户端一直是 Windows 独占，但它其实没什么平台特有的东西——这次**零逻辑改动**，
直接编出了两个 Linux 版本：

| 产物 | 说明 |
| --- | --- |
| `EndfieldMonitor-v0.4.3-linux-x64` | 常见 x86_64 Linux 桌面 |
| `EndfieldMonitor-v0.4.3-linux-arm64` | ARM64，比如树莓派、ARM 笔记本 |

唯一的改动在**字体**：原来的字体栈是 `Microsoft YaHei, Segoe UI, PingFang SC`，
后两个在 Linux 上都不存在，会整个落到系统默认。现在补上了 Linux 上常见的中文与西文字体
（Noto Sans CJK、思源黑体、文泉驿、Ubuntu、DejaVu），显示更接近原样。

## 跑之前需要知道

**需要桌面环境。** Avalonia 走 X11 或 Wayland，纯服务器（没有图形界面）跑不起来。

**系统得有中文字体。** 最小安装的 Ubuntu 可能没装 CJK 字体，界面会显示成方块，装一个就好：

```bash
sudo apt install fonts-noto-cjk
```

**缺 X11 库的话**（最小安装常见）：

```bash
sudo apt install libx11-6 libxrandr2 libxi6 libxcursor1 libxext6 libxcomposite1 \
                 libice6 libsm6 libgl1 libfontconfig1 libfreetype6
```

## 请帮忙测试

```bash
chmod +x EndfieldMonitor-v0.4.3-linux-x64
./EndfieldMonitor-v0.4.3-linux-x64
```

启动后在设置里填被监视机器的地址与口令即可。**想请有 Linux 桌面的朋友回报三件事：**

1. 能不能起来、窗口是否正常（这版是无边框自绘窗口，想看它在 X11 与 Wayland 上分别什么样）
2. 中文字形是否正常，跟仓库截图里的差别大不大
3. 有没有控件错位、点不动、或者动画卡顿

## 附件说明

- 客户端三平台：**win-x64** / **linux-x64** / **linux-arm64**
- **服务端这一版没有任何改动**，继续用 v0.4.2 的四个采集端即可

## 已知问题

- Linux 客户端同样**没有真机验证**（编得出来，但没跑过）
- Android 暂不支持：Avalonia 技术上能做，但要 Android SDK + JDK 一整套，
  且交互要按触屏重做，这次先不碰
- Linux 采集端的图标采集仍未在真机验证，欢迎用 `--probe-icon <pid>` 跑一次
- 增强模式的界面还没做，客户端目前只识别、不显示
