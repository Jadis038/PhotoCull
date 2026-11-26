using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YingShai.Core.Settings;

public enum SortMode { Name = 0, TakenTime = 1, ModifiedTime = 2, Size = 3 }
public enum MarkMode { Copy = 0, Move = 1 }

/// <summary>用户设置，存 %LOCALAPPDATA%\影·筛\settings.json（UTF-8，可读可改）。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    public string? LastFolder { get; set; }
    public SortMode SortMode { get; set; } = SortMode.Name;
    public bool SortDescending { get; set; }
    /// <summary>启动恢复上次会话，默认关。</summary>
    public bool RestoreLastSession { get; set; }

    /// <summary>标记动作：复制（默认）或移动。</summary>
    public MarkMode MarkMode { get; set; } = MarkMode.Copy;
    /// <summary>磁盘缩略图缓存上限（GB）。</summary>
    public double CacheLimitGb { get; set; } = 5.0;
    /// <summary>内存预览缓存上限（MB）。</summary>
    public int MemoryCacheMb { get; set; } = 512;

    /// <summary>解码并发：0 = 自动探测（SSD 4 / 机械 2 / 网络 2）。</summary>
    public int DecodeConcurrency { get; set; }
    /// <summary>启动后即低优先级预热全部缩略图。</summary>
    public bool WarmAllOnStartup { get; set; } = true;
    /// <summary>首次启动已提示过 SmartScreen 说明。</summary>
    public bool FirstRunDone { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 860;

    // ——— 读写 ———

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch { /* 设置损坏时用默认值，不阻塞启动 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch { /* 设置写失败不影响使用 */ }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
