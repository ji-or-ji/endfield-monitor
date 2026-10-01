using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldMonitor.Services;

/// <summary>发行版里的一个附件。</summary>
public sealed record UpdateAsset(string Name, string Url);

/// <summary>远端最新的那个发行版。</summary>
public sealed record UpdateInfo(string Version, string Tag, string PageUrl, IReadOnlyList<UpdateAsset> Assets);

/// <summary>
/// 更新检查：拉 Gitee 上最新的发行版，跟本机版本比大小。
///
/// 这里不碰界面、也不动文件；下载与替换在 SelfUpdate 里。
/// 检查失败（没外网、超时、接口变了）一律当"没有新版本"，绝不干扰正常使用。
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "ji-or-ji/endfield-monitor";

    /// <summary>本机版本。构建时注入（build.ps1 传 -p:Version=），源码里跑就是 0.0.0。</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "0.0.0";
            }
            // 有些构建会接上 +<commit>，去掉
            var v = raw.Split('+')[0].Trim();
            return v.Length == 0 ? "0.0.0" : v;
        }
    }

    /// <summary>本机平台在产物名里的写法，如 win-x64 / linux-arm64。</summary>
    public static string PlatformTag
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : "linux";
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>本机平台对应的套件包名。</summary>
    public static string BundleNameFor(string tag) => $"EndfieldMonitor-{tag}-{PlatformTag}-bundle.zip";

    /// <summary>
    /// 拉最新发行版。网络不通、超时、接口变动都返回 null——检查更新不该影响任何正常功能。
    /// </summary>
    public static async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EnfieldMonitor");
            var json = await http.GetStringAsync($"https://gitee.com/api/v5/repos/{Repo}/releases", ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // 取 created_at 最新的那个。ISO 时间戳按字符串比就是按时间比。
            string? bestTag = null;
            string bestAt = "";
            var best = default(JsonElement);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }
                var at = r.TryGetProperty("created_at", out var c) ? c.GetString() ?? "" : "";
                if (bestTag is null || string.CompareOrdinal(at, bestAt) > 0)
                {
                    bestTag = tag;
                    bestAt = at;
                    best = r;
                }
            }
            if (bestTag is null)
            {
                return null;
            }

            var assets = new List<UpdateAsset>();
            if (best.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        assets.Add(new UpdateAsset(name!, url!));
                    }
                }
            }

            return new UpdateInfo(
                bestTag.TrimStart('v', 'V'), bestTag,
                $"https://gitee.com/{Repo}/releases/tag/{bestTag}",
                assets);
        }
        catch
        {
            return null; // 查不到就当没有新版本
        }
    }

    /// <summary>a 是否比 b 新。任一段解析不出来就返回 false——宁可漏报，别误报。</summary>
    public static bool IsNewer(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        if (x is null || y is null)
        {
            return false;
        }
        for (var i = 0; i < 3; i++)
        {
            if (x[i] != y[i])
            {
                return x[i] > y[i];
            }
        }
        return false;
    }

    private static int[]? Parse(string v)
    {
        var parts = v.Split('-')[0].Split('.'); // 去掉 -dev 之类后缀
        if (parts.Length < 2)
        {
            return null;
        }
        var n = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
            {
                n[i] = 0;
                continue;
            }
            if (!int.TryParse(parts[i], out n[i]))
            {
                return null;
            }
        }
        return n;
    }
}
