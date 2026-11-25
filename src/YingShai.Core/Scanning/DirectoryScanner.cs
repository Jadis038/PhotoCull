using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YingShai.Core.Scanning;

/// <summary>目录枚举得到的一行（零解码，只靠 FindFirstFileEx 的文件信息）。</summary>
public sealed class FileEntry
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public uint Attributes { get; init; }
    /// <summary>OneDrive / 云盘「仅在线」占位文件：没下载到本地。</summary>
    public bool IsPlaceholder { get; init; }

    public DateTime ModifiedLocal => ModifiedUtc.ToLocalTime();
    public DateTime CreatedLocal => CreatedUtc.ToLocalTime();
}

/// <summary>
/// 第一层目录扫描。用 FindFirstFileExW + FindExInfoBasic 直接读目录项，
/// 不做逐文件 FileInfo（那会为 2000 个文件各发一次系统调用，慢 5 倍以上）。
/// 只列图片，不递归，排除隐藏/系统/临时/占位文件。
/// </summary>
public static class DirectoryScanner
{
    /// <summary>支持的图片扩展名白名单（小写，含点）。</summary>
    public static readonly string[] ImageExtensions =
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".tif", ".tiff", ".webp", ".bmp", ".heic", ".heif", ".avif", ".jxl",
        ".nef", ".nrw", ".cr2", ".cr3", ".crw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".pef",
        ".dng", ".srw", ".raw", ".rwl", ".3fr", ".fff", ".dcr", ".kdc", ".mef", ".mos", ".mrw", ".erf",
        ".x3f", ".iiq", ".cap", ".pxn", ".r3d", ".ari", ".braw"
    };

    private static readonly HashSet<string> ImageExtSet =
        new(ImageExtensions, StringComparer.OrdinalIgnoreCase);

    public const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly long Value => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FileTime ftCreationTime;
        public FileTime ftLastAccessTime;
        public FileTime ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW data,
        int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    public static bool IsImageExtension(string ext) => ImageExtSet.Contains(ext);

    /// <summary>FILETIME → UTC。异常值（0 或损坏）回退到 MinValue，不让扫描整体失败。</summary>
    private static DateTime ToUtc(long fileTime)
    {
        if (fileTime <= 0) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// 扫描第一层图片。notes 收集被跳过的原因（UI 状态区可显示「跳过 3 个未下载的云文件」）。
    /// </summary>
    public static List<FileEntry> Scan(string folder, out List<string> notes, CancellationToken ct = default)
    {
        var list = new List<FileEntry>(2048);
        notes = new List<string>();
        int skippedHidden = 0, skippedPlaceholder = 0, skippedTemp = 0, skippedOther = 0;

        string pattern = AppPaths.ToLongPath(Path.Combine(folder, "*"));
        IntPtr h = FindFirstFileExW(pattern, FindExInfoBasic, out var data,
            FindExSearchNameMatch, IntPtr.Zero, 0);
        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            // 2 = ERROR_FILE_NOT_FOUND（空目录）
            if (err != 2) throw new IOException($"无法枚举目录（错误 {err}）：{folder}");
            return list;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string name = data.cFileName;
                if (name is "." or "..") { }
                else
                {
                    uint attr = data.dwFileAttributes;
                    bool isDir = (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    if (!isDir)
                    {
                        bool hidden = (attr & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM)) != 0;
                        bool placeholder = (attr & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0;
                        string ext = Path.GetExtension(name);
                        bool temp = name.StartsWith("~$", StringComparison.Ordinal) ||
                                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                                    name.StartsWith("._", StringComparison.Ordinal);

                        if (hidden) skippedHidden++;
                        else if (placeholder) skippedPlaceholder++;
                        else if (temp) skippedTemp++;
                        else if (!IsImageExtension(ext)) skippedOther++;
                        else
                        {
                            long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                            list.Add(new FileEntry
                            {
                                FullPath = Path.Combine(folder, name),
                                Name = name,
                                Extension = ext.ToLowerInvariant(),
                                Size = size,
                                ModifiedUtc = ToUtc(data.ftLastWriteTime.Value),
                                CreatedUtc = ToUtc(data.ftCreationTime.Value),
                                Attributes = attr,
                                IsPlaceholder = false
                            });
                        }
                    }
                }

                if (!FindNextFileW(h, out data)) break;
            }
        }
        finally
        {
            FindClose(h);
        }

        if (skippedHidden > 0) notes.Add($"跳过 {skippedHidden} 个隐藏/系统文件");
        if (skippedPlaceholder > 0) notes.Add($"跳过 {skippedPlaceholder} 个未下载的云盘占位文件");
        if (skippedTemp > 0) notes.Add($"跳过 {skippedTemp} 个临时文件");
        return list;
    }
}
