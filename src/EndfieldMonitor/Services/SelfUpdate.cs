using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldMonitor.Services;

/// <summary>
/// 自更新：把新版套件下到 &lt;安装目录&gt;\.update\，然后交给一段临时脚本在本进程退出后
/// 替换文件并重启。
///
/// 为什么必须靠脚本：Windows 上运行中的 exe 不能覆盖自己；套件里那个采集端还可能
/// 正被本进程拉着，得先停掉。脚本干的活是「等本进程退出 → 停采集端 → 备份旧文件 →
/// 换上新文件 → 重启客户端」。
///
/// 退路：新版全部下载并检查过内容才算数；替换前把旧文件复制成 .bak，出问题还能换回来。
/// </summary>
public static class SelfUpdate
{
    /// <summary>放下载与替换脚本的地方。</summary>
    public static string UpdateDir => Path.Combine(AppContext.BaseDirectory, ".update");

    /// <summary>下载套件并解压，返回解压目录；任何一步不放心就返回 null。</summary>
    public static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<string>? say, CancellationToken ct = default)
    {
        try
        {
            var name = UpdateChecker.BundleNameFor(info.Tag);
            var asset = info.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                say?.Report("发行版里没有本机平台的套件包");
                return null;
            }

            Directory.CreateDirectory(UpdateDir);
            var zip = Path.Combine(UpdateDir, name);
            var dest = Path.Combine(UpdateDir, info.Version);

            say?.Report($"正在下载 {name} …");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EnfieldMonitor");
                using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zip);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // 下完先看大小：套件一直都有几十兆，太小就是不完整
            if (new FileInfo(zip).Length < 1024 * 1024)
            {
                say?.Report("下载下来的包不完整");
                return null;
            }

            say?.Report("正在解压 …");
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, true);
            }
            Directory.CreateDirectory(dest);
            ZipFile.ExtractToDirectory(zip, dest);
            File.Delete(zip);

            var files = Directory.GetFiles(dest).Select(Path.GetFileName).ToList();
            var hasClient = files.Any(f => f!.StartsWith("EndfieldMonitor-", StringComparison.OrdinalIgnoreCase));
            var hasCollector = files.Any(f => f!.StartsWith("enf-collector", StringComparison.OrdinalIgnoreCase));
            if (!hasClient || !hasCollector)
            {
                say?.Report("包里缺客户端或采集端，不换");
                return null;
            }

            return dest;
        }
        catch (Exception ex)
        {
            say?.Report("下载失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 写替换脚本并启动它。返回 true 表示脚本已经起来，调用方应当立刻退出程序。
    /// oldFiles 是当前目录里这一版的客户端与采集端文件名，用来备份与清理。
    /// </summary>
    public static bool Apply(string newDir, IEnumerable<string> oldFiles)
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var pid = Environment.ProcessId;

            var newFiles = Directory.GetFiles(newDir).Select(Path.GetFileName).ToList();
            var clientNew = newFiles.FirstOrDefault(f =>
                f!.StartsWith("EndfieldMonitor-", StringComparison.OrdinalIgnoreCase));
            if (clientNew is null)
            {
                return false;
            }
            var oldNames = oldFiles.Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
            var collectorOld = oldNames.FirstOrDefault(n =>
                n.StartsWith("enf-collector", StringComparison.OrdinalIgnoreCase));

            Directory.CreateDirectory(UpdateDir);
            var script = Path.Combine(UpdateDir, OperatingSystem.IsWindows() ? "update.cmd" : "update.sh");
            var body = OperatingSystem.IsWindows()
                ? WindowsScript(dir, pid, newDir, clientNew, collectorOld, oldNames)
                : LinuxScript(dir, pid, newDir, clientNew, collectorOld, oldNames);
            File.WriteAllText(script, body);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script,
                    File.GetUnixFileMode(script) | UnixFileMode.UserExecute
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            Process.Start(new ProcessStartInfo(script)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = UpdateDir,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string WindowsScript(string dir, int pid, string newDir, string clientNew,
        string? collectorOld, List<string> oldNames)
    {
        var backup = "";
        var clean = "";
        if (collectorOld is not null)
        {
            // 只按映像名结束它在同目录的那个进程
            backup += $"taskkill /IM \"{collectorOld}\" /F >nul 2>&1\r\n";
        }
        foreach (var n in oldNames)
        {
            // 只留一代备份：先清掉上一代的同名 .bak，再备份
            backup += $"if exist \"%DIR%\\{n}.bak\" del /Q \"%DIR%\\{n}.bak\" >nul\r\n";
            backup += $"if exist \"%DIR%\\{n}\" copy /Y \"%DIR%\\{n}\" \"%DIR%\\{n}.bak\" >nul\r\n";
        }
        foreach (var n in oldNames)
        {
            clean += $"del /Q \"%DIR%\\{n}\" 2>nul\r\n";
        }

        return "@echo off\r\n" +
               "setlocal\r\n" +
               $"set \"DIR={dir}\"\r\n" +
               $"set \"PID={pid}\"\r\n" +
               "rem 等客户端退出：运行中的 exe 换不了自己\r\n" +
               ":wait\r\n" +
               "tasklist /FI \"PID eq %PID%\" 2>nul | findstr /R /C:\" %PID% \" >nul\r\n" +
               "if not errorlevel 1 (\r\n" +
               "  timeout /t 1 /nobreak >nul\r\n" +
               "  goto wait\r\n" +
               ")\r\n" +
               "rem 停掉旧版采集端（可能是客户端拉起来的）\r\n" +
               backup +
               "rem 换上新文件\r\n" +
               $"copy /Y \"{newDir}\\*\" \"%DIR%\\\" >nul\r\n" +
               "rem 清掉旧版本的文件，.bak 留着\r\n" +
               clean +
               "rem 起新版客户端\r\n" +
               $"start \"\" \"%DIR%\\{clientNew}\"\r\n" +
               "rem 收尾：清掉解压出来的那一份和脚本自己\r\n" +
               $"rmdir /S /Q \"{newDir}\" 2>nul\r\n" +
               "del /Q \"%~f0\"\r\n";
    }

    private static string LinuxScript(string dir, int pid, string newDir, string clientNew,
        string? collectorOld, List<string> oldNames)
    {
        var stop = collectorOld is null
            ? ""
            : $"pkill -f \"{dir}/{collectorOld}\" 2>/dev/null\r\nsleep 1\r\n";
        var backup = string.Join("", oldNames.Select(n =>
            $"rm -f \"$DIR/{n}.bak\"\r\n[ -f \"$DIR/{n}\" ] && cp -f \"$DIR/{n}\" \"$DIR/{n}.bak\"\r\n"));
        var clean = string.Join("", oldNames.Select(n => $"rm -f \"$DIR/{n}\"\r\n"));

        return "#!/bin/sh\r\n" +
               $"DIR=\"{dir}\"\r\n" +
               $"PID={pid}\r\n" +
               "# 等客户端退出\r\n" +
               "while kill -0 \"$PID\" 2>/dev/null; do sleep 1; done\r\n" +
               stop +
               backup +
               $"cp -f \"{newDir}\"/* \"$DIR\"/ 2>/dev/null\r\n" +
               clean +
               $"chmod +x \"$DIR\"/{clientNew} \"$DIR\"/enf-collector-* 2>/dev/null\r\n" +
               $"cd \"$DIR\" && nohup \"./{clientNew}\" >/dev/null 2>&1 &\r\n" +
               $"rm -rf \"{newDir}\"\r\n" +
               "rm -f \"$0\"\r\n";
    }
}
