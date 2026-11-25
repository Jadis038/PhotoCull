using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YingShai.Core.Cli;

/// <summary>生成「影·筛」程序图标：深色圆角底 + 白色「影」字 + 右下角绿色对勾。</summary>
public static class IconMaker
{
    public static int MakeIcon(string outIcoPath)
    {
        try
        {
            const int size = 256;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)), null,
                    new Rect(0, 0, size, size), 48, 48);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x33)), null,
                    new Rect(8, 8, size - 16, size - 16), 42, 42);

                var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Microsoft YaHei, SimHei"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var text = new FormattedText("影", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 132, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2 - 14));

                // 右下角绿色对勾
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x33, 0xC5, 0x7A)), null,
                    new Point(190, 190), 42, 42);
                var checkPen = new Pen(Brushes.White, 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(166, 190), false, false);
                    g.LineTo(new Point(184, 208), true, false);
                    g.LineTo(new Point(216, 172), true, false);
                }
                geo.Freeze();
                dc.DrawGeometry(null, checkPen, geo);
            }

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var png = ms.ToArray();

            // ICO 头 + 单个 PNG 帧（Vista 以后的图标格式，Windows 全支持）
            using var fs = new FileStream(outIcoPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write((ushort)0);      // reserved
            bw.Write((ushort)1);      // type = icon
            bw.Write((ushort)1);      // count = 1
            bw.Write((byte)0);        // width 256 -> 0
            bw.Write((byte)0);        // height 256 -> 0
            bw.Write((byte)0);        // color count
            bw.Write((byte)0);        // reserved
            bw.Write((ushort)1);      // planes
            bw.Write((ushort)32);     // bpp
            bw.Write(png.Length);     // bytes in resource
            bw.Write(22);             // offset
            bw.Write(png);
            Console.WriteLine($"ok {outIcoPath}（{png.Length} 字节 PNG 帧）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("生成图标失败：" + ex.Message);
            return 1;
        }
    }
}
