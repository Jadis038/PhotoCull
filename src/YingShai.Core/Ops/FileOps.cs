using System.Diagnostics;
using System.Runtime.InteropServices;
using YingShai.Core.Imaging;
using YingShai.Core.Scanning;

namespace YingShai.Core.Ops;

/// <summary>文件操作底层：带进度/可取消的复制、原子改名、冲突改名、回收站删除、跨卷移动。</summary>
public static class FileOps
{
    // ——— Win32 ———

    private delegate int CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint dwStreamNumber, int dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

    private const int PROGRESS_CONTINUE = 0;
    private const int PROGRESS_CANCEL = 1;
    private const int CALLBACK_CHUNK_FINISHED = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExW(string lpExistingFileName, string lpNewFileName,
        CopyProgressRoutine lpProgressRoutine, IntPtr lpData, ref int pbCancel, int dwCopyFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, int dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    private const int ERROR_NOT_SAME_DEVICE = 17;

    // ——— 空间 ———

    public static long GetFreeBytes(string anyPathInVolume)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(anyPathInVolume))!;
            if (GetDiskFreeSpaceExW(root, out ulong free, out _, out _)) return (long)free;
        }
        catch { }
        return -1;
    }

    // ——— 复制 ———

    /// <summary>把源文件复制到目标（目标必须不存在）。带进度回调与取消。</summary>
    public static bool CopyFile(string source, string dest,
        Action<long, long>? progress, CancellationToken ct, out string error)
    {
        error = string.Empty;
        int cancel = 0;
        var state = new CopyState { Progress = progress, Ct = ct };
        var handle = GCHandle.Alloc(state);
        try
        {
            CopyProgressRoutine routine = (total, transferred, _, _, _, _, _, _, _) =>
            {
                try
                {
                    state.Progress?.Invoke(transferred, total);
                }
                catch { }
                if (state.Ct.IsCancellationRequested) return PROGRESS_CANCEL;
                return PROGRESS_CONTINUE;
            };
            // 进度回调至少每 8MB 一次，避免 50MB 文件里每 64KB 一次跨托管回调
            bool ok = CopyFileExW(AppPaths.ToLongPath(source), AppPaths.ToLongPath(dest),
                routine, IntPtr.Zero, ref cancel, 0);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 1235 /*ERROR_REQUEST_ABORTED*/ || state.Ct.IsCancellationRequested)
                {
                    error = "已取消";
                    return false;
                }
                error = DescribeWin32(err);
                return false;
            }
            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed class CopyState
    {
        public Action<long, long>? Progress;
        public CancellationToken Ct;
    }

    public static string DescribeWin32(int err) => err switch
    {
        2 => "源文件不存在",
        3 => "路径不存在",
        5 => "没有权限",
        32 => "文件正被其它程序占用",
        33 => "文件被锁定（另一程序正在写入）",
        39 => "磁盘空间不足",
        112 => "磁盘空间不足",
        1235 => "操作已取消",
        53 => "找不到网络路径（网络盘可能已断开）",
        64 => "网络名不再可用（网络盘已断开）",
        19 => "磁盘写保护",
        _ => $"系统错误 {err}：{new System.ComponentModel.Win32Exception(err).Message}"
    };

    // ——— 改名/冲突 ———

    /// <summary>同卷原子改名（.part → 正式名）。</summary>
    public static void AtomicRename(string tempPath, string finalPath)
        => File.Move(AppPaths.ToLongPath(tempPath), AppPaths.ToLongPath(finalPath), overwrite: false);

    /// <summary>
    /// 同名冲突时生成「原名 (1).ext」、「原名 (2).ext」……绝不覆盖。
    /// </summary>
    public static string UniquePath(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(AppPaths.ToLongPath(path)) && !Directory.Exists(AppPaths.ToLongPath(path))) return path;
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10000; i++)
        {
            path = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(AppPaths.ToLongPath(path))) return path;
        }
        throw new IOException($"同名文件过多，无法自动改名：{fileName}");
    }

    // ——— 移动 ———

    /// <summary>同卷秒移；跨卷退化成复制+删源（调用方负责提示）。</summary>
    public static bool TryMoveSameVolume(string source, string dest, out bool needCopy, out string error)
    {
        needCopy = false;
        error = string.Empty;
        if (MoveFileExW(AppPaths.ToLongPath(source), AppPaths.ToLongPath(dest), 0)) return true;
        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_NOT_SAME_DEVICE) { needCopy = true; return false; }
        error = DescribeWin32(err);
        return false;
    }

    // ——— 删除到回收站 ———

    /// <summary>批量删除到回收站（可恢复）。任何一项不是用户目录时不做处理，只删传入的路径。</summary>
    public static bool RecycleToTrash(IEnumerable<string> paths, out string error)
    {
        error = string.Empty;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return true;

        // SHFileOperation 要求每个路径以 \0 结尾，整个串以 \0\0 结尾
        string from = string.Join("\0", list) + "\0\0";
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
        };
        int rc = SHFileOperationW(ref op);
        if (rc != 0)
        {
            error = rc switch
            {
                0x71 => "回收站已满或被禁用，无法删除",
                0x78 => "操作被系统拒绝（可能需要权限）",
                _ => $"删除失败（代码 0x{rc:X}）"
            };
            return false;
        }
        if (op.fAnyOperationsAborted != 0)
        {
            error = "删除被中断";
            return false;
        }
        return true;
    }

    // ——— 伴生文件 ———

    private static readonly HashSet<string> SidecarExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xmp", ".aae", ".thm", ".dop", ".pp3", ".pp2"
    };

    private static readonly HashSet<string> JpegLikeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff"
    };

    /// <summary>
    /// 找出「同主文件名」的伴生文件：.xmp/.aae/.thm 等，以及 RAW↔JPG/HEIF 双拍的另一半。
    /// 只扫描同目录一次（目录枚举），不会为每个文件发多次系统调用。
    /// </summary>
    public static List<string> FindCompanions(string mainPath)
    {
        var result = new List<string>();
        try
        {
            string dir = Path.GetDirectoryName(mainPath)!;
            string baseName = Path.GetFileNameWithoutExtension(mainPath);
            string mainExt = Path.GetExtension(mainPath);
            bool mainIsRaw = LibRawImage.IsRawExtension(mainExt);
            bool mainIsJpegLike = JpegLikeExts.Contains(mainExt);

            if (!Directory.Exists(AppPaths.ToLongPath(dir))) return result;

            foreach (var file in Directory.EnumerateFiles(AppPaths.ToLongPath(dir)))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, Path.GetFileName(mainPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileNameWithoutExtension(name).Length != baseName.Length) continue; // 必须是同主名

                string ext = Path.GetExtension(name);
                bool sidecar = SidecarExts.Contains(ext);
                bool pair = (mainIsRaw && JpegLikeExts.Contains(ext)) || (mainIsJpegLike && LibRawImage.IsRawExtension(ext));
                if (sidecar || pair) result.Add(file);
            }
        }
        catch { }
        result.Sort(NaturalComparer.Instance.Compare);
        return result;
    }

    // ——— 资源管理器 ———

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(AppPaths.ToLongPath(path)))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static bool Exists(string path) => File.Exists(AppPaths.ToLongPath(path));
}
