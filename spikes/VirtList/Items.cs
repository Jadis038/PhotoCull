using System.Windows.Media;

namespace VirtList;

/// <summary>
/// 缩略图条里的一个数据项（正式 UI 里对应一张照片的元数据）。
/// spike 只关心「列表控件 + 布局管线」的开销，所以这里只有文件名和一个用于
/// 派生占位颜色的种子，不持有任何解码后的位图。
/// </summary>
public sealed class ThumbItem
{
    public ThumbItem(int index, string fileName, int hue)
    {
        Index = index;
        FileName = fileName;
        Hue = hue;
    }

    /// <summary>在列表中的序号（0 起），也用于从调色板取色。</summary>
    public int Index { get; }

    /// <summary>文件名，渲染成单元下方的说明文字。</summary>
    public string FileName { get; }

    /// <summary>0-359 的色相种子，让相邻占位块颜色不同，方便肉眼确认滚动。</summary>
    public int Hue { get; }
}

/// <summary>2000 条假数据。文件名前缀轮换，模拟真实照片库的命名风格。</summary>
public static class ItemFactory
{
    private static readonly string[] Prefixes = { "IMG", "DSC", "DSCF", "_MG", "P10", "D810" };
    private static readonly string[] Extensions = { ".CR3", ".ARW", ".NEF", ".RAF", ".DNG", ".JPG" };

    public static List<ThumbItem> Create(int count)
    {
        var list = new List<ThumbItem>(count);
        for (var i = 0; i < count; i++)
        {
            var prefix = Prefixes[i % Prefixes.Length];
            var ext = Extensions[(i / Prefixes.Length) % Extensions.Length];
            // 色相按黄金角步进，相邻项颜色差异明显
            var hue = (i * 47) % 360;
            list.Add(new ThumbItem(i, $"{prefix}_{i + 1:D4}{ext}", hue));
        }

        return list;
    }

    /// <summary>由色相生成占位色：明度固定、饱和度固定，保证所有块亮度接近（避免颜色影响渲染成本）。</summary>
    public static Color ColorFromHue(int hue)
    {
        // 手写 HSV→RGB（只取 S=0.45, V=0.78），避免引入 System.Drawing 依赖
        const double s = 0.45;
        const double v = 0.78;
        var h = (hue % 360) / 60.0;
        var c = v * s;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = v - c;
        double r, g, b;
        switch ((int)h % 6)
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
