using System.IO;

namespace YingShai.Core;

/// <summary>
/// 全局路径约定。用户数据全部放在 %LOCALAPPDATA%\影·筛\ 下（中文目录，Windows 完全支持）。
/// 图片目录里只会出现一个「筛选」子目录，绝不写数据库文件。
/// </summary>
public static class AppPaths
{
    /// <summary>筛选输出子目录名（在图片目录内创建）。</summary>
    public const string FilterFolderName = "筛选";

    /// <summary>
    /// %LOCALAPPDATA%\影·筛
    /// 可用环境变量 YING_SHAI_DATA 覆盖（便携模式 / 自动化测试 / 沙箱环境用；默认不用）。
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("YING_SHAI_DATA");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "影·筛");
    }

    /// <summary>索引（SQLite）与设置所在目录。</summary>
    public static string StateDir => Path.Combine(Root, "state");

    /// <summary>缩略图磁盘缓存。</summary>
    public static string CacheDir => Path.Combine(Root, "cache");

    /// <summary>日志与崩溃转储。</summary>
    public static string LogDir => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>全局索引库（每文件夹一张表，按文件夹路径哈希分表）。</summary>
    public static string IndexDbPath => Path.Combine(StateDir, "index.db");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
    }

    /// <summary>把任意路径变成 Win32 长路径形式（&gt;260 字符时加 \\?\ 前缀）。</summary>
    public static string ToLongPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        if (path.Length >= 240 && Path.IsPathFullyQualified(path)) return @"\\?\" + path;
        return path;
    }

    /// <summary>相对路径显示用的短名。</summary>
    public static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }
}
