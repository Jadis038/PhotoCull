using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VirtList;

/// <summary>
/// 缩略图条里的一个可视单元：160×160 的占位色块 + 一行文件名。
/// <para>
/// 刻意不加载任何图片文件、不做解码：占位块用 DrawingContext 画两块色阶（模拟照片的天空/地面），
/// 相当于一个轻量的 DrawingVisual。spike 要测的是虚拟化列表在滚动时的
/// 「容器生成 + 测量 + 排列 + 重绘」成本，而不是 JPEG 解码成本。
/// </para>
/// </summary>
public sealed class ThumbCell : FrameworkElement
{
    /// <summary>占位图边长（任务要求 160×160）。</summary>
    public const double ImageSize = 160;

    /// <summary>文件名文字区高度。</summary>
    public const double CaptionHeight = 28;

    /// <summary>单元之间的水平间隙。</summary>
    public const double Gap = 8;

    /// <summary>单元总宽 = 图宽 + 间隙。</summary>
    public const double CellWidth = ImageSize + Gap;

    /// <summary>单元总高 = 图高 + 文字区 + 间隙。</summary>
    public const double CellHeight = ImageSize + CaptionHeight + Gap;

    /// <summary>整个进程里真正 new 出来的单元数——用来证明 Recycling 是否真的复用了容器。</summary>
    public static int CreatedCount;

    /// <summary>OnRender 被调用的累计次数。</summary>
    public static int RenderedCount;

    // 预建调色板（冻结画刷），避免每帧 OnRender 里 new 画刷，干扰测量
    private static readonly Brush[] FillBrushes = new Brush[64];
    private static readonly Brush[] ShadeBrushes = new Brush[64];
    private static readonly Pen BorderPen;
    private static readonly Brush CaptionBrush;
    private static readonly Typeface CaptionTypeface = new("Segoe UI");

    private double _pixelsPerDip;

    static ThumbCell()
    {
        for (var i = 0; i < FillBrushes.Length; i++)
        {
            var color = ItemFactory.ColorFromHue(i * 5);
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            FillBrushes[i] = fill;

            // 下方 40% 画成暗一些的色阶，模拟照片「地面」部分
            var shade = new SolidColorBrush(Color.FromRgb(
                (byte)(color.R * 0.62), (byte)(color.G * 0.62), (byte)(color.B * 0.62)));
            shade.Freeze();
            ShadeBrushes[i] = shade;
        }

        BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 1);
        BorderPen.Freeze();
        var caption = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        caption.Freeze();
        CaptionBrush = caption;
    }

    public ThumbCell()
    {
        Interlocked.Increment(ref CreatedCount);
        // 容器被回收复用时 DataContext 会换人，必须重绘
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>固定尺寸，不随内容变化（真实缩略图条也是这样，避免测量阶段额外开销）。</summary>
    protected override Size MeasureOverride(Size availableSize) => new(CellWidth, CellHeight);

    protected override void OnRender(DrawingContext dc)
    {
        Interlocked.Increment(ref RenderedCount);

        if (_pixelsPerDip <= 0)
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }

        if (DataContext is not ThumbItem item)
        {
            return;
        }

        var paletteIndex = (uint)item.Index % (uint)FillBrushes.Length;
        var imageRect = new Rect(0, 0, ImageSize, ImageSize);

        // 占位「照片」：整块填色 + 下方 40% 暗色阶 + 边框
        dc.DrawRectangle(FillBrushes[paletteIndex], null, imageRect);
        dc.DrawRectangle(ShadeBrushes[paletteIndex], null,
            new Rect(0, ImageSize * 0.6, ImageSize, ImageSize * 0.4));
        dc.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, ImageSize - 1, ImageSize - 1));

        // 文件名（超出宽度省略号截断）
        var text = new FormattedText(
            item.FileName,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            CaptionTypeface,
            12,
            CaptionBrush,
            _pixelsPerDip)
        {
            MaxTextWidth = CellWidth - 2,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(text, new Point(0, ImageSize + 4));
    }
}
