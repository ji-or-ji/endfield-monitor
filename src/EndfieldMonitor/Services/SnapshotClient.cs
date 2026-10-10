using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using EndfieldMonitor.Models;

namespace EndfieldMonitor.Services;

/// <summary>
/// 拉取服务端 /snapshot 的客户端。服务端只读，这里也只需要一个 GET。
/// </summary>
public sealed class SnapshotClient : IDisposable
{
    // 局域网直连：必须显式关掉系统代理，否则 192.168.x.x 的请求会被代理吞掉
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    /// <summary>服务端基地址，例如 http://192.168.1.10:8898</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>共享口令，服务端未设置时留空。</summary>
    public string Token { get; set; } = "";

    public async Task<Snapshot?> FetchAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)) return null;

        string url = BaseUrl.TrimEnd('/') + "/snapshot";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(Token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync<Snapshot>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取某个进程可执行文件的图标（只有增强版有这接口）。普通版回 404、进程没图标也会回
    /// 非图片，这些一律当「没有图标」返回 null，交给界面回退到本地字形。
    /// 取图标是锦上添花，拿不到不该报错，更不该拦住界面。
    /// </summary>
    public async Task<(byte[] Data, string Mime)?> FetchIconAsync(string baseUrl, string token, int pid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || pid <= 0) return null;

        string url = baseUrl.TrimEnd('/') + "/app/icon?pid=" + pid.ToString(CultureInfo.InvariantCulture);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var mime = resp.Content.Headers.ContentType?.MediaType;
        if (mime is null || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return data.Length > 0 ? (data, mime) : null;
    }

    public void Dispose() => _http.Dispose();
}
