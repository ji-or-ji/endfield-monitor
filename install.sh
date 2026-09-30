#!/usr/bin/env bash
#
# EndfieldMonitor 采集端 · Linux 一键安装
#
# 把采集端装到 /opt/enf-monitor/，注册成 systemd 服务并开机自启。
# 适用于 Ubuntu / Debian 这类用 systemd 的发行版。
#
# 用法（脚本和采集端二进制放在同一个目录里）：
#   chmod +x install.sh
#   sudo ./install.sh                      # 交互式问端口与口令
#   sudo ./install.sh --port 8898 --token 你的口令 --lite
#   sudo ./install.sh --plus               # 用增强版（可启停应用、取图标）
#   sudo ./install.sh --uninstall
#
set -euo pipefail

PREFIX=/opt/enf-monitor
SVC=enf-collector
UNIT="/etc/systemd/system/$SVC.service"

PORT=8898
TOKEN=""
LITE=0
PLUS=0
UNINSTALL=0

while [ $# -gt 0 ]; do
  case "$1" in
    --port)      PORT="$2"; shift 2 ;;
    --token)     TOKEN="$2"; shift 2 ;;
    --lite)      LITE=1; shift ;;
    --plus)      PLUS=1; shift ;;
    --prefix)    PREFIX="$2"; shift 2 ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help)   sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "未知参数：$1（--help 看用法）"; exit 1 ;;
  esac
done

[ "$(id -u)" -eq 0 ] || { echo "请用 sudo 运行"; exit 1; }

if [ "$UNINSTALL" -eq 1 ]; then
  systemctl disable --now "$SVC" 2>/dev/null || true
  rm -f "$UNIT"
  systemctl daemon-reload 2>/dev/null || true
  rm -rf "$PREFIX"
  echo "已卸载 $SVC，$PREFIX 也一并删掉了"
  exit 0
fi

command -v systemctl >/dev/null 2>&1 || {
  echo "这台机器没有 systemd，装不了服务。可以直接运行二进制："
  echo "  ./enf-collector-*-linux-* --config ./enf-collector.json"
  exit 1
}

case "$(uname -m)" in
  x86_64|amd64)   ARCH=x64 ;;
  aarch64|arm64)  ARCH=arm64 ;;
  *) echo "暂不支持这个架构：$(uname -m)"; exit 1 ;;
esac

HERE="$(cd "$(dirname "$0")" && pwd)"
if [ "$PLUS" -eq 1 ]; then
  PATTERN="enf-collector-plus-*-linux-$ARCH"
else
  PATTERN="enf-collector-*-linux-$ARCH"
fi

SRC=""
for cand in "$HERE"/$PATTERN; do
  [ -f "$cand" ] && { SRC="$cand"; break; }
done
if [ -z "$SRC" ]; then
  echo "在这个目录里没找到采集端二进制（找的是 $PATTERN）"
  echo "请把对应架构的那个文件放到 $HERE，再跑一次。"
  ls -1 "$HERE" 2>/dev/null | sed 's/^/  /'
  exit 1
fi

echo "安装到 $PREFIX"
install -d -m 0755 "$PREFIX"
install -m 0755 "$SRC" "$PREFIX/enf-collector"
echo "  用的二进制：$(basename "$SRC")"

# 配置交给采集端自己的向导或这里生成，两处不各写一份逻辑
CFG="$PREFIX/enf-collector.json"
if [ -n "$TOKEN" ]; then
  if [ "$LITE" -eq 1 ]; then LITE_JSON=',
  "lite": true'; else LITE_JSON=''; fi
  ESC_TOKEN=$(printf '%s' "$TOKEN" | sed 's/[\\"]/\\&/g')
  cat > "$CFG" <<EOF
{
  "port": $PORT,
  "token": "$ESC_TOKEN"$LITE_JSON
}
EOF
  echo "  已写入配置 $CFG"
else
  echo
  echo "没有给 --token，走交互式向导（跟着问就行）："
  echo
  "$PREFIX/enf-collector" --setup --config "$CFG"
fi

echo
echo "注册 systemd 服务"
cat > "$UNIT" <<EOF
[Unit]
Description=EndfieldMonitor 采集端
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$PREFIX/enf-collector --config $CFG
WorkingDirectory=$PREFIX
Restart=on-failure
RestartSec=5
# 要采集整机资源（增强版还要启停别的进程），所以用 root。
# 只想看当前用户自己的进程，可以把这行改成 User=你的用户名
User=root

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SVC"

# 放行防火墙（只认 ufw，其它防火墙自己加规则）
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
  ufw allow "$PORT"/tcp >/dev/null 2>&1 && echo "  ufw 已放行 $PORT/tcp"
fi

sleep 1
systemctl --no-pager --lines=0 status "$SVC" || true
IP="$(hostname -I 2>/dev/null | awk '{print $1}')"

cat <<EOF

装好了。

  快照地址：http://${IP:-<本机IP>}:$PORT/snapshot
  看日志：  journalctl -u $SVC -f
  重启：    systemctl restart $SVC
  卸载：    sudo $0 --uninstall

另一台机器上打开客户端，地址填上面那个，口令填配置里的，就能看了。
EOF
