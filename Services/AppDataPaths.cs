using System;
using System.IO;
using System.Linq;

namespace SteamLua.Services;

/// <summary>
/// 应用的可写数据目录。
/// 为安装程序做准备：安装目录（Program Files）对普通用户只读，
/// 所有运行期写入都必须落在用户可写的位置，否则保存设置、缓存会失败。
///
/// 布局：
///   %LocalAppData%\SteamLua\            数据根
///       settings.json                   用户配置
///       app.log / bridge.log            日志
///       bindings.json                   修改器绑定
///       cache\                          各类缓存
///
/// 旧版本把上述文件写在 exe 同目录。首次取用数据根时做一次迁移，
/// 把老文件搬到新位置，避免升级后用户配置与缓存"凭空消失"。
/// </summary>
public static class AppDataPaths
{
    private const string AppFolderName = "SteamLua";
    private const string LegacyFolderName = "FluentSteamLua";
    private static readonly object InitLock = new();
    private static string? _root;

    /// <summary>可写数据根目录，首次访问时创建并执行旧位置迁移。</summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            lock (InitLock)
            {
                if (_root != null) return _root;
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    // 极端情况下取不到 LocalAppData（精简/损坏的用户配置），
                    // 退回安装目录保证程序仍能运行，而不是抛异常直接起不来
                    _root = AppContext.BaseDirectory;
                    return _root;
                }
                var root = Path.Combine(localAppData, AppFolderName);
                try { Directory.CreateDirectory(root); } catch { }
                MigrateLegacyData(root);
                _root = root;
                return _root;
            }
        }
    }

    /// <summary>数据根下的相对路径，目录由调用方按需创建。</summary>
    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>数据根下的缓存目录（统一入口，省得各处再拼小写/大写 cache）。</summary>
    public static string CacheDir => Combine(CacheFolderName);

    /// <summary>确保目录存在后返回该路径。</summary>
    public static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }

    // 缓存目录名历史上大小写混用（cache / Cache），统一用小写，顺带兼容老路径
    private const string CacheFolderName = "cache";

    /// <summary>
    /// 把旧版写在安装目录里的用户数据搬到新数据根。
    /// 只搬不改名、只做一次：目标已存在则跳过，绝不覆盖用户新数据。
    /// 迁移失败不影响启动，最坏情况是用户配置回到默认值。
    /// </summary>
    private static void MigrateLegacyData(string newRoot)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(exeDir)) return;
            // 装在同一处（数据根就在 exe 旁）时没有迁移的必要
            if (string.Equals(Path.GetFullPath(exeDir).TrimEnd('\\'),
                    Path.GetFullPath(newRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;

            // 单文件迁移
            foreach (var name in new[] { "settings.json", "bindings.json", "app.log", "bridge.log" })
                TryMoveFile(Path.Combine(exeDir, name), Path.Combine(newRoot, name));

            // 整目录迁移：cache
            TryMoveDirectory(Path.Combine(exeDir, CacheFolderName), Path.Combine(newRoot, CacheFolderName));
            TryMoveDirectory(Path.Combine(exeDir, "Cache"), Path.Combine(newRoot, CacheFolderName));

            // 更早的版本把数据放在 %LocalAppData%\FluentSteamLua
            var legacyLocalRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyFolderName);
            if (Directory.Exists(legacyLocalRoot) &&
                !string.Equals(Path.GetFullPath(legacyLocalRoot).TrimEnd('\\'),
                    Path.GetFullPath(newRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                TryMoveFile(Path.Combine(legacyLocalRoot, "settings.json"), Path.Combine(newRoot, "settings.json"));
                TryMoveDirectory(Path.Combine(legacyLocalRoot, CacheFolderName), Path.Combine(newRoot, CacheFolderName));
            }
        }
        catch
        {
            // 迁移属于尽力而为：失败就让程序用默认配置继续跑
        }
    }

    private static void TryMoveFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(destination)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // 先复制再删除：跨卷移动或源文件被占用时也不会丢数据
            File.Copy(source, destination, overwrite: false);
            try { File.Delete(source); } catch { }
        }
        catch { }
    }

    private static void TryMoveDirectory(string source, string destination)
    {
        try
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(destination);
            // 逐项合并，目标已有的同名文件保留（新数据优先）
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var dest = Path.Combine(destination, rel);
                try
                {
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: false);
                    try { File.Delete(file); } catch { }
                }
                catch { }
            }
            try
            {
                // 自底向上清理空目录：文件搬走后子目录会留下空壳，
                // 只在目录确实为空时删，被占用的文件连同其父目录留给下次
                DeleteEmptyDirectories(source);
            }
            catch { }
        }
        catch { }
    }

    /// <summary>自底向上删除空目录；遇到非空目录即停止向上，避免误删有内容的目录。</summary>
    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root, recursive: false);
        }
        catch { }
    }
}
