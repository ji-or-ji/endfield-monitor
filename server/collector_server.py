# -*- coding: utf-8 -*-
"""
终末地监视器 · 服务端采集端（只读）

在 Windows 服务器上运行，向局域网提供一份 /snapshot JSON 快照。

设计前提：目标机器是双核赛扬（G1840），因此
  1. 静态信息（CPU 名、内存条、磁盘型号与映射）只在启动时采集一次；
  2. 之后所有动态数据全部走 psutil 原生调用，绝不调用 PowerShell / WMI 性能计数器；
  3. 采样频率按"够用就好"压到最低，慢查询（计划任务）每 60 秒一次。

用法：
  python collector_server.py --port 8898 --token <口令>
"""

import argparse
import json
import os
import re
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    import psutil
except ImportError:
    print("缺少 psutil，请先执行: pip install psutil", flush=True)
    sys.exit(1)

# ---------------- 采样节奏（单位：秒） ----------------
FAST_INTERVAL = 1.0     # CPU / 内存 / 磁盘 / 网络
PROC_INTERVAL = 2.0     # 进程列表
TASK_INTERVAL = 60.0    # 计划任务等慢查询

PROC_LIMIT = 40
PORTS_OF_INTEREST = (6099, 7998, 8765)
MAIBOT_TASKS = ("MaiBotStart", "MaiBotMCP", "MaiBot_Daily_Backup")

state = {
    "static": {},
    "snapshot": None,
    "tasks": [],
    "maibot": {},
    "prev_net": None,
    "prev_disk": None,
    "prev_time": None,
    "task_at": 0.0,
}
lock = threading.Lock()


# ================= 静态信息（仅启动时一次） =================
def _run_ps(cmd, timeout=20):
    """启动阶段专用的一次性 PowerShell 调用，返回 stdout 文本。

    注意：PowerShell 5.1 在 stdout 被重定向时设置 Console.OutputEncoding 会抛
    “句柄无效”，所以这里用 try/catch 包住；取回的字节再依次尝试 UTF-8 / GBK 解码。
    """
    full = "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}; " + cmd
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", full],
            capture_output=True, timeout=timeout,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except Exception:
        return ""
    data = r.stdout or b""
    for enc in ("utf-8", "gbk"):
        try:
            return data.decode(enc).strip()
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace").strip()


def _ps_json(cmd, timeout=20):
    out = _run_ps("(%s) | ConvertTo-Json -Depth 5 -Compress" % cmd, timeout)
    if not out:
        return None
    try:
        return json.loads(out)
    except Exception:
        return None


def _as_list(j):
    if j is None:
        return []
    return j if isinstance(j, list) else [j]


MEM_TYPE = {20: "DDR", 21: "DDR2", 24: "DDR3", 26: "DDR4", 34: "DDR5"}


def collect_static():
    s = {"host": socket.gethostname()}

    cpu = _as_list(_ps_json(
        "Get-CimInstance Win32_Processor | Select-Object Name,MaxClockSpeed,NumberOfLogicalProcessors,CurrentClockSpeed"))
    cpu = cpu[0] if cpu else {}
    s["cpu_name"] = (cpu.get("Name") or "处理器").strip()
    s["cpu_threads"] = cpu.get("NumberOfLogicalProcessors") or psutil.cpu_count(logical=True) or 1
    s["cpu_max"] = round((cpu.get("MaxClockSpeed") or 0) / 1000.0, 2)
    s["cpu_base"] = round((cpu.get("CurrentClockSpeed") or 0) / 1000.0, 2) or s["cpu_max"]

    mems = _as_list(_ps_json(
        "Get-CimInstance Win32_PhysicalMemory | Select-Object Speed,SMBIOSMemoryType,Capacity"))
    speeds = [m.get("Speed") for m in mems if m.get("Speed")]
    s["mem_speed"] = "%d MT/s" % max(speeds) if speeds else "—"
    mt = next((m.get("SMBIOSMemoryType") for m in mems if m.get("SMBIOSMemoryType")), None)
    s["mem_type"] = MEM_TYPE.get(mt, "物理内存")

    # 逻辑盘 -> 物理盘映射
    mapping = {}
    raw = _run_ps("""
$d = Get-CimInstance Win32_DiskDrive
foreach ($x in $d) {
  $parts = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='$($x.DeviceID)'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"
  foreach ($p in $parts) {
    $logs = Get-CimInstance -Query "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='$($p.DeviceID)'} WHERE AssocClass=Win32_LogicalDiskToPartition"
    foreach ($l in $logs) { "$($l.DeviceID)|PhysicalDrive$($x.Index)|$($x.Model)" }
  }
}""", timeout=25)
    for line in (raw or "").splitlines():
        seg = [x.strip() for x in line.split("|")]
        if len(seg) >= 2:
            mapping[seg[0]] = seg[1:]

    phys = _as_list(_ps_json("Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType", 20))
    disks = []
    for p in psutil.disk_partitions():
        if "cdrom" in (p.opts or "") or not p.fstype:
            continue
        letter = p.device.rstrip("\\")
        m = mapping.get(letter, [])
        phys_name = m[0] if len(m) > 0 else ""
        model = m[1] if len(m) > 1 else ""
        # 介质类型：部分盘（尤其机械盘/U盘）的 MediaType 会报 Unspecified，需要靠总线类型兜底
        media = "本地磁盘"
        for ph in phys:
            fn = ph.get("FriendlyName") or ""
            if fn and (fn in model or model in fn):
                mtp = ph.get("MediaType") or ""
                bt = ph.get("BusType") or ""
                if mtp == "SSD":
                    media = "NVMe SSD" if bt == "NVMe" else "SATA SSD"
                elif bt == "USB":
                    media = "USB 存储"
                elif mtp and mtp != "Unspecified":
                    media = mtp
                elif bt == "ATA":
                    media = "SATA 硬盘"
                else:
                    media = "本地磁盘"
                break
        disks.append({"letter": letter, "mount": p.mountpoint, "phys": phys_name,
                      "model": model or "—", "media": media})
    s["disks"] = disks

    nets = _as_list(_ps_json(
        "Get-CimInstance Win32_NetworkAdapter | Where-Object {$_.NetEnabled -eq $true} | "
        "Select-Object NetConnectionID,Speed", 15))
    n = nets[0] if nets else {}
    s["net_name"] = (n.get("NetConnectionID") or "网络").strip() or "网络"
    s["net_link"] = round((n.get("Speed") or 0) / 1e6) or 1000

    return s


# ================= 麦麦业务（低频） =================
def collect_tasks():
    """计划任务状态。schtasks 走 cmd，比 PowerShell 起得轻，且 60 秒才一次。"""
    result = []
    for name in MAIBOT_TASKS:
        entry = {"name": name, "status": "未知", "last_run": "—", "last_result": "—"}
        try:
            r = subprocess.run(["schtasks", "/query", "/tn", name, "/fo", "csv", "/nh", "/v"],
                               capture_output=True, text=True, encoding="gbk", errors="ignore",
                               timeout=8, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            lines = [l for l in (r.stdout or "").splitlines() if l.strip().startswith('"')]
            if lines:
                cells = re.findall(r'"([^"]*)"', lines[0])
                if len(cells) > 6:
                    entry["status"] = cells[3] or "未知"
                    entry["last_run"] = (cells[5] or "—").strip()
                    entry["last_result"] = (cells[6] or "—").strip()
        except Exception:
            pass
        result.append(entry)
    return result


def collect_maibot(procs):
    """从进程快照里挑出麦麦相关进程，并查关注端口的监听状态。"""
    info = {"running": False, "core_pid": None, "shell_pid": None, "ports": {}}
    for name, pid in procs:
        low = name.lower()
        if "maibot" in low or "napcat" in low:
            info["running"] = True
            if info["shell_pid"] is None:
                info["shell_pid"] = pid
            info["core_pid"] = pid
    if info["running"] and info["core_pid"] == info["shell_pid"]:
        info["core_pid"] = None

    listening = set()
    try:
        for c in psutil.net_connections(kind="inet"):
            if c.status == psutil.CONN_LISTEN and c.laddr:
                listening.add(c.laddr.port)
    except Exception:
        pass
    for p in PORTS_OF_INTEREST:
        info["ports"][str(p)] = p in listening
    return info


# ================= 动态采样 =================
# 进程 CPU 百分比依赖同一 Process 对象的两次采样差值，因此按 pid 缓存对象
_proc_objs = {}


def collect_procs(ncpu):
    out = []
    seen = set()
    for p in psutil.process_iter(["pid", "name", "exe", "memory_info"]):
        try:
            pid = p.pid
            seen.add(pid)
            obj = _proc_objs.get(pid)
            if obj is None:
                obj = psutil.Process(pid)
                obj.cpu_percent(None)      # 预热，下一次调用才有差值
                _proc_objs[pid] = obj
                cpu = 0.0
            else:
                cpu = obj.cpu_percent(None)
            mi = p.info.get("memory_info")
            rss = (mi.rss / 1048576.0) if mi else 0.0
            if rss < 20:
                continue
            out.append({"pid": pid,
                        "name": p.info.get("name") or "?",
                        "exe": p.info.get("exe") or "",
                        "cpu": round(cpu / ncpu, 1),
                        "mem": round(rss)})
        except Exception:
            continue
    for pid in list(_proc_objs):
        if pid not in seen:
            _proc_objs.pop(pid, None)
    return out


def sampler():
    ncpu = psutil.cpu_count(logical=True) or 1
    prev_net = psutil.net_io_counters()
    prev_disk = psutil.disk_io_counters(perdisk=True) or {}
    prev_t = time.time()
    psutil.cpu_percent(None)          # 预热

    proc_cache = []
    proc_at = 0.0
    procs = {}

    while True:
        now = time.time()
        dt = max(0.2, now - prev_t)
        prev_t = now

        # --- CPU / 内存 ---
        cpu_util = psutil.cpu_percent(None)
        try:
            f = psutil.cpu_freq()
            freq = round((f.current or 0) / 1000.0, 2) if f else 0.0
        except Exception:
            freq = 0.0
        vm = psutil.virtual_memory()

        # --- 网络 ---
        net = psutil.net_io_counters()
        down = max(0.0, (net.bytes_recv - prev_net.bytes_recv) * 8 / 1e6 / dt)
        up = max(0.0, (net.bytes_sent - prev_net.bytes_sent) * 8 / 1e6 / dt)
        prev_net = net

        # --- 磁盘 IO（物理盘差分，再映射回逻辑盘） ---
        cur_disk = {}
        try:
            cur_disk = psutil.disk_io_counters(perdisk=True) or {}
        except Exception:
            pass

        disks = []
        st = state["static"]
        for i, d in enumerate(st.get("disks", [])):
            try:
                u = psutil.disk_usage(d["mount"])
            except Exception:
                continue
            rw = 0.0
            phys = d.get("phys")
            if phys and phys in cur_disk and phys in prev_disk:
                a, b = prev_disk[phys], cur_disk[phys]
                rw = ((b.read_bytes - a.read_bytes) + (b.write_bytes - a.write_bytes)) / dt / 1048576.0
            disks.append({
                "name": "磁盘 %d (%s)" % (i, d["letter"]),
                "used": round(u.used / 2 ** 30, 1),
                "total": round(u.total / 2 ** 30, 1),
                "pct": round(u.percent, 1),
                "util": round(u.percent, 1),
                "rw": "%.0f MB/s" % max(0.0, rw),
                "media": d["media"],
                "model": d["model"],
            })
        prev_disk = cur_disk

        # --- 进程（降频） ---
        if now - proc_at >= PROC_INTERVAL:
            proc_at = now
            ps = collect_procs(ncpu)
            ps.sort(key=lambda x: (-x["cpu"], -x["mem"]))
            proc_cache = ps[:PROC_LIMIT]

        # --- 麦麦业务 ---
        maibot = collect_maibot([(p["name"], p["pid"]) for p in proc_cache])

        if now - state["task_at"] >= TASK_INTERVAL:
            state["task_at"] = now
            try:
                state["tasks"] = collect_tasks()
            except Exception:
                pass

        snap = {
            "ts": now,
            "interval": FAST_INTERVAL,
            "live": True,
            "cpu": {"name": st.get("cpu_name"), "threads": st.get("cpu_threads"),
                    "util": round(cpu_util, 1), "freq": freq,
                    "base": st.get("cpu_base"), "max": st.get("cpu_max")},
            "gpu": {"name": "—", "util": 0.0, "freq": None, "mem_used": None,
                    "mem_total": 0.0, "ok": False},
            "mem": {"used": round(vm.used / 2 ** 30, 1), "total": round(vm.total / 2 ** 30, 1),
                    "pct": round(vm.percent, 1), "speed": st.get("mem_speed"),
                    "type": st.get("mem_type")},
            "disks": disks,
            "net": {"name": st.get("net_name"), "down": round(down, 2), "up": round(up, 2),
                    "link": st.get("net_link", 1000),
                    "util": round(min(100.0, max(down, up) / max(1, st.get("net_link", 1000)) * 100), 1)},
            "procs": proc_cache,
            "server": {
                "host": st.get("host"),
                "uptime_hours": round((time.time() - psutil.boot_time()) / 3600.0, 1),
                "maibot": maibot,
                "tasks": state["tasks"],
            },
        }
        with lock:
            state["snapshot"] = snap

        time.sleep(FAST_INTERVAL)


# ================= HTTP =================
class Handler(BaseHTTPRequestHandler):
    token = ""

    def _send(self, code, body):
        data = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _authorized(self):
        if not Handler.token:
            return True
        auth = self.headers.get("Authorization", "")
        if auth.startswith("Bearer ") and auth[7:].strip() == Handler.token:
            return True
        q = self.path.split("?", 1)[1] if "?" in self.path else ""
        return ("token=" + Handler.token) in q

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/health":
            self._send(200, json.dumps({"ok": True, "service": "enf-collector"}))
            return
        if not self._authorized():
            self._send(401, json.dumps({"error": "unauthorized"}, ensure_ascii=False))
            return
        if path == "/snapshot":
            with lock:
                snap = state["snapshot"]
            if snap is None:
                self._send(503, json.dumps({"live": False, "error": "采样尚未就绪"}))
            else:
                self._send(200, json.dumps(snap, ensure_ascii=False))
        else:
            self._send(200, json.dumps({"service": "enf-collector", "endpoint": "/snapshot"},
                                       ensure_ascii=False))

    def log_message(self, *a):
        pass


def main():
    ap = argparse.ArgumentParser(description="终末地监视器 · 服务端采集端")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8898)
    ap.add_argument("--token", default="", help="共享口令，留空则不校验（仅本地调试用）")
    a = ap.parse_args()

    Handler.token = a.token.strip()

    print("[采集端] 读取静态硬件信息（仅此一次调用 PowerShell）…", flush=True)
    state["static"] = collect_static()
    st = state["static"]
    print("[采集端] 主机: %s" % st.get("host"), flush=True)
    print("[采集端] CPU: %s（%s 线程）" % (st.get("cpu_name"), st.get("cpu_threads")), flush=True)
    print("[采集端] 内存: %.1f GB %s %s" % (
        psutil.virtual_memory().total / 2 ** 30, st.get("mem_type"), st.get("mem_speed")), flush=True)
    print("[采集端] 磁盘: %s" % ", ".join("%s(%s)" % (d["letter"], d["media"])
                                        for d in st.get("disks", [])), flush=True)

    threading.Thread(target=sampler, daemon=True).start()
    srv = ThreadingHTTPServer((a.host, a.port), Handler)
    print("[采集端] 已监听 http://%s:%d/snapshot  (token=%s)"
          % (a.host, a.port, "已设置" if Handler.token else "未设置"), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
