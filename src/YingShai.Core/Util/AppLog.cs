using System.Text;

namespace YingShai.Core.Util;

/// <summary>
/// 极简日志：写到 %LOCALAPPDATA%\影·筛\logs\app.log（UTF-8，单文件滚动到 2MB）。
/// 只在关键节点写（打开文件夹、标记/取消结果、异常），方便用户反馈问题时取证。
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            string file = Path.Combine(AppPaths.LogDir, "app.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Lock)
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string bak = file + ".1";
                    try { if (File.Exists(bak)) File.Delete(bak); File.Move(file, bak); } catch { }
                }
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
