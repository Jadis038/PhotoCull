using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace YingShai.Core.Update;

/// <summary>GitHub Releases 检测到的最新版本信息。</summary>
public sealed class UpdateInfo
{
    public Version? LatestVersion { get; init; }
    public string? TagName { get; init; }
    public string? Notes { get; init; }
    public string? MsiUrl { get; init; }

    /// <summary>当前运行程序的版本（Core 与 App 同版本号）。</summary>
    public static Version CurrentVersion { get; } =
        new(typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public bool HasUpdate => LatestVersion != null && LatestVersion > CurrentVersion;
}

/// <summary>
/// 启动更新检查：GitHub Releases 为准，raw latest.json（含 ghproxy 镜像）兜底。
/// 任何网络失败都静默返回 null，绝不影响启动。
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "Jadis038";
    public const string RepoName = "PhotoCull";
    public const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YingShai-Updater/1.0");   // GitHub API 必须带 UA
        return c;
    }

    public static Version CurrentVersion { get; } =
        new(typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public static async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        try { var info = await FromApiAsync(ct); if (info != null) return info; } catch { }
        try { var info = await FromMirrorAsync(ct); if (info != null) return info; } catch { }
        return null;
    }

    /// <summary>GitHub Releases API（返回最新非 prerelease release）。</summary>
    private static async Task<UpdateInfo?> FromApiAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        string tag = json.GetProperty("tag_name").GetString() ?? "";
        string body = json.GetProperty("body").GetString() ?? "";
        string? url = null;
        if (json.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        return Build(tag, body, url);
    }

    /// <summary>仓库根目录 latest.json（raw.githubusercontent 直连 + ghproxy 镜像），API 被墙时的兜底。</summary>
    private static async Task<UpdateInfo?> FromMirrorAsync(CancellationToken ct)
    {
        foreach (var prefix in new[] { "https://raw.githubusercontent.com/", "https://ghproxy.net/https://raw.githubusercontent.com/" })
        {
            try
            {
                using var resp = await Http.GetAsync(prefix + $"{RepoOwner}/{RepoName}/main/latest.json", ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                var info = Build(json.GetProperty("tag").GetString() ?? "",
                                 json.GetProperty("notes").GetString() ?? "",
                                 json.GetProperty("msi").GetString());
                if (info != null) return info;
            }
            catch { }
        }
        return null;
    }

    private static UpdateInfo? Build(string tag, string notes, string? url)
    {
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        return new UpdateInfo { LatestVersion = v, TagName = tag, Notes = notes, MsiUrl = url };
    }

    /// <summary>
    /// 下载 MSI 到指定路径：先直连 GitHub，失败自动走 ghproxy.net 镜像（国内网络兜底）。
    /// </summary>
    public static async Task<string> DownloadAsync(string url, string destPath,
        Action<long, long>? progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var u in new[] { url, "https://ghproxy.net/" + url })
        {
            try
            {
                using var resp = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(destPath);
                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    progress?.Invoke(read, total);
                }
                return u;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new Exception("下载失败：GitHub 直连与镜像均不可达（" + last?.Message + "）");
    }

    /// <summary>启动安装程序：先给 2 秒让旧进程退出，再跑 msiexec（每用户安装，无 UAC）。</summary>
    public static void LaunchInstaller(string msiPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & msiexec /i \"{msiPath}\" /qb",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        System.Diagnostics.Process.Start(psi);
    }
}
