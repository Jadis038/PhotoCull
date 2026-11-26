using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace YingShai.Core.Imaging;

/// <summary>
/// Windows 资源管理器同源缩略图：系统用自己的 RAW 编解码器 / 缩略图缓存生成，
/// 毫秒级且跨进程缓存（用户在资源管理器里看过一遍，这里就是秒出）。
/// 系统解不了（没装 RAW 编解码器）时返回 false，调用方回退到 LibRaw 内嵌 JPEG 路径。
/// </summary>
public static class ShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-8024-2a7c4c7c3ade"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        /// <summary>返回 HBITMAP；HRESULT 用 PreserveSig 手动检查，失败静默回退。</summary>
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_THUMBNAILONLY = 0x4;   // 拒绝文件类型图标，只要真缩略图

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid FactoryIid = new("bcc18b79-ba16-442f-8024-2a7c4c7c3ade");

    /// <summary>按扩展名记忆"系统解不了"的格式：没装编解码器时避免每张图白跑一次 shell 调用。</summary>
    private static readonly HashSet<string> _failedExts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _failLock = new();

    public static bool TryGet(string path, int sizePx, out BitmapSource? bmp)
    {
        bmp = null;
        var ext = Path.GetExtension(path);
        lock (_failLock)
        {
            if (_failedExts.Contains(ext)) return false;
        }
        try
        {
            SHCreateItemFromParsingName(AppPaths.ToLongPath(path), IntPtr.Zero, in FactoryIid, out var factory);
            if (factory == null) { lock (_failLock) _failedExts.Add(ext); return false; }
            int hr = factory.GetImage(new SIZE { CX = sizePx, CY = sizePx },
                SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero)
            {
                lock (_failLock) _failedExts.Add(ext);
                return false;
            }
            try
            {
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                bmp = bs;
                return true;
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch
        {
            lock (_failLock) _failedExts.Add(ext);
            return false;
        }
    }
}
