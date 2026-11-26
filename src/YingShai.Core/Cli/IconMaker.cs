using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YingShai.Core.Cli;

/// <summary>生成「影·筛」程序图标：深色圆角底 + 白色「影」字 + 右下角绿色对勾。</summary>
public static class IconMaker
{
    public static int MakeIcon(string outIcoPath) => MakeIcon(outIcoPath, null);

    /// <summary>
    /// 生成图标：sourcePng 为空时画默认的「影」字图标；给了就用该图片（等比缩到 256×256）。
    /// </summary>
    public static int MakeIcon(string outIcoPath, string? sourcePng)
    {
        try
        {
            byte[] png;
            if (!string.IsNullOrWhiteSpace(sourcePng))
            {
                // 用用户提供的图片做图标：等比缩到 256 以内，编码成 PNG 帧塞进 ICO
                using var fs = new FileStream(AppPaths.ToLongPath(sourcePng), FileMode.Open, FileAccess.Read, FileShare.Read);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                double scale = Math.Min(1.0, 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
                BitmapSource src = frame;
                if (scale < 1.0)
                {
                    var tb = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                    tb.Freeze();
                    src = tb;
                }
                var pe = new PngBitmapEncoder();
                pe.Frames.Add(BitmapFrame.Create(src));
                using var pms = new MemoryStream();
                pe.Save(pms);
                png = pms.ToArray();
            }
            else
            {
                png = RenderDefaultLogo();
            }

            // ICO 头 + 单个 PNG 帧（Vista 以后的图标格式，Windows 全支持）
            using var outFile = new FileStream(outIcoPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(outFile);
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
            Console.WriteLine($"ok {outIcoPath}（{png.Length} 字节 PNG 帧，来源：{sourcePng ?? "内置默认图"}）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("生成图标失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>默认图标：深色圆角底 + 白色「影」字 + 右下角绿色对勾。</summary>
    private static byte[] RenderDefaultLogo()
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
        return ms.ToArray();
    }
}
