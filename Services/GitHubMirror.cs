namespace SteamLua.Services;

// GitHub 访问国内加速：release 包与 API 请求走公共 gh-proxy 前缀链，直连永远第一位，
// 镜像挂了自动退化成直连行为（清单获取已走 ManifestHub 直连，不再经这里）。
// 注意：jsDelivr 指望不上——大仓库规模下它的包 loader 根本建不出来，别再接回来。
internal static class GitHubMirror
{
    public const string DirectKey = "direct";

    // 前缀式镜像：按顺序尝试，全部失败才报错。
    // 只保留实测可用的：ghfast.top 实测 API 返回 403、资产下载 25 秒超时，
    // 留在链里会让每次失败都白等一轮，已剔除；日后恢复可再加回。
    public static readonly string[] ReleaseAssetMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://gh-proxy.org/",
    ];

    // 能可靠代理 api.github.com 的前缀：更新检查必须用它，否则国内直连 api 常常超时
    public static readonly string[] ApiMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://gh-proxy.org/",
    ];

    // 偏好值归一化：未知主机（含已下线的）回直连，保证下拉框与排序逻辑不出现空选中
    public static string NormalizePreferredHost(string? preferredHost)
    {
        if (string.IsNullOrWhiteSpace(preferredHost) || preferredHost.Equals(DirectKey, StringComparison.OrdinalIgnoreCase))
            return DirectKey;
        return ReleaseAssetMirrorPrefixes.Any(p => p.Contains(preferredHost, StringComparison.OrdinalIgnoreCase))
            ? preferredHost
            : DirectKey;
    }

    // 源显示名：直连与各镜像主机（测速与进度文案共用）。
    // 镜像 URL 里嵌着源站域名，先取实际 host 再比对，否则代理地址会被误标为直连
    public static string SourceDisplayName(string url)
    {
        var host = url.Split(["://"], 2, StringSplitOptions.None).Last().Split('/').First();
        if (host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return L.Get("Svc_RawDirect");
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return L.Get("Svc_GitHubDirect");
        return host;
    }

    // 是否直连源：raw 直连的 404 是权威的"不存在"，镜像不可能有源站没有的文件，可直接短路
    public static bool IsDirectUrl(string url) =>
        url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    // 按用户偏好排序下载源：默认直连首位；选了镜像则该镜像首位、直连垫底，其余镜像居中。
    // 偏好值对不上已知镜像时回退默认顺序。
    public static List<string> OrderSources(string directUrl, string? preferredHost)
    {
        return OrderSources(directUrl, preferredHost, ReleaseAssetMirrorPrefixes);
    }

    // 通用排序：prefixes 决定可用镜像集合（API 用更严格的那张表）
    private static List<string> OrderSources(string directUrl, string? preferredHost, string[] prefixes)
    {
        preferredHost = NormalizePreferredHost(preferredHost);
        string? preferredPrefix = null;
        if (!preferredHost.Equals(DirectKey, StringComparison.OrdinalIgnoreCase))
            preferredPrefix = prefixes.FirstOrDefault(p =>
                p.Contains(preferredHost, StringComparison.OrdinalIgnoreCase));
        if (preferredPrefix == null)
        {
            var urls = new List<string> { directUrl };
            foreach (var prefix in prefixes)
                urls.Add(prefix + directUrl);
            return urls;
        }
        var ordered = new List<string> { preferredPrefix + directUrl };
        foreach (var prefix in prefixes)
            if (prefix != preferredPrefix)
                ordered.Add(prefix + directUrl);
        ordered.Add(directUrl);
        return ordered;
    }

    // release asset 下载源
    public static List<string> WithAssetMirrors(string downloadUrl, string? preferredHost = null) =>
        OrderSources(downloadUrl, preferredHost);

    // manifest raw 文件下载源（拼 raw 直链，不要拼 blob 页面）
    public static List<string> ManifestRawMirrors(string rawUrl, string? preferredHost = null) =>
        OrderSources(rawUrl, preferredHost);

    // GitHub API 请求源：更新检查与内核信息查询用。
    // 直连排第一（能通就最快），其后是实测可代理 API 的镜像，避免国内直连 api.github.com 卡死
    public static List<string> ApiMirrors(string apiUrl, string? preferredHost = null) =>
        OrderSources(apiUrl, preferredHost, ApiMirrorPrefixes);
}
