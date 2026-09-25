using System;
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

    /// <summary>服务端基地址，例如 http://192.168.101.14:8898</summary>
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

    public void Dispose() => _http.Dispose();
}
