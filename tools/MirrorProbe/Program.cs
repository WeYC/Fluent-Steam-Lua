// 验证 GitHubMirror 的源排序与 CDN 回退能否真实取到数据
// 用法：dotnet run -c Release --project tools\MirrorProbe
using System.Text.Json;

var apiUrl = "https://api.github.com/repos/WeYC/SteamLua/releases/latest";
var rawUrl = "https://raw.githubusercontent.com/WeYC/SteamLua/main/README.md";

// 复刻 GitHubMirror 的排序逻辑（同一份表），验证"按序尝试直到成功"的链路
var apiPrefixes = new[] { "https://gh-proxy.com/", "https://gh-proxy.org/" };
var assetPrefixes = new[] { "https://gh-proxy.com/", "https://gh-proxy.org/", "https://ghfast.top/" };

List<string> Order(string direct, string? preferred, string[] prefixes)
{
    var list = new List<string>();
    string? prefPrefix = null;
    if (!string.IsNullOrWhiteSpace(preferred) && preferred != "direct")
        prefPrefix = prefixes.FirstOrDefault(p => p.Contains(preferred, StringComparison.OrdinalIgnoreCase));
    if (prefPrefix != null)
    {
        list.Add(prefPrefix + direct);
        foreach (var p in prefixes) if (p != prefPrefix) list.Add(p + direct);
        list.Add(direct);
    }
    else
    {
        list.Add(direct);
        foreach (var p in prefixes) list.Add(p + direct);
    }
    return list;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamLua/1.0");

async Task<bool> TryChainAsync(string label, List<string> sources, Func<string, bool> validate)
{
    Console.WriteLine($"\n== {label} ==");
    for (int i = 0; i < sources.Count; i++)
    {
        var url = sources[i];
        var host = url.Split("://", 2)[1].Split('/')[0];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var body = await http.GetStringAsync(url);
            sw.Stop();
            var ok = validate(body);
            Console.WriteLine($"  [{i}] {host,-26} {sw.ElapsedMilliseconds,5}ms  len={body.Length,8}  有效={ok}");
            if (ok) { Console.WriteLine($"  => 命中第 {i} 个源"); return true; }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  [{i}] {host,-26} {sw.ElapsedMilliseconds,5}ms  失败: {ex.GetType().Name}");
        }
    }
    Console.WriteLine("  => 全部失败");
    return false;
}

// 1) 默认偏好（直连优先）下的 API 链
await TryChainAsync("API 更新检查（偏好=direct）", Order(apiUrl, "direct", apiPrefixes),
    body => body.Contains("tag_name"));

// 2) 用户选了 gh-proxy.org
await TryChainAsync("API 更新检查（偏好=gh-proxy.org）", Order(apiUrl, "gh-proxy.org", apiPrefixes),
    body => body.Contains("tag_name"));

// 3) raw 文件链
await TryChainAsync("raw 文件（偏好=direct）", Order(rawUrl, "direct", assetPrefixes),
    body => body.Length > 100);

// 4) release 资产是否可下载（只取头部若干字节）
Console.WriteLine("\n== release 资产下载（前 64KB）==");
var assetUrl = "https://github.com/WeYC/SteamLua/releases/download/0.0.2/SteamLua-0.0.2-loose-file.zip";
foreach (var prefix in assetPrefixes)
{
    var url = prefix + assetUrl;
    var host = url.Split("://", 2)[1].Split('/')[0];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        sw.Stop();
        Console.WriteLine($"  {host,-26} {sw.ElapsedMilliseconds,5}ms  HTTP {(int)resp.StatusCode}  len={resp.Content.Headers.ContentLength}");
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  {host,-26} {sw.ElapsedMilliseconds,5}ms  失败: {ex.GetType().Name}");
    }
}

// 5) 验证解析出的下载地址（模拟 UpdateService 提取资产链接）
Console.WriteLine("\n== 从 API 响应提取资产地址 ==");
var json = await http.GetStringAsync(apiPrefixes[0] + apiUrl);
using var doc = JsonDocument.Parse(json);
var tag = doc.RootElement.GetProperty("tag_name").GetString();
Console.WriteLine($"  tag_name = {tag}");
if (doc.RootElement.TryGetProperty("assets", out var assets))
{
    foreach (var a in assets.EnumerateArray())
        Console.WriteLine($"  asset: {a.GetProperty("name").GetString()}");
}
else
{
    Console.WriteLine("  该 release 没有 assets");
}
