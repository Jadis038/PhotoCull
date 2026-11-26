using System.Diagnostics;
using System.Windows.Media.Imaging;
using YingShai.Core.Model;

namespace YingShai.Core.Imaging;

public enum DecodeSource
{
    /// <summary>LibRaw 内嵌 JPEG（正常路径，最快）。</summary>
    LibRawThumb,
    /// <summary>LibRaw 内嵌未压缩位图（少数老机器，如 Hasselblad/Mamiya）。</summary>
    LibRawThumbBitmap,
    /// <summary>没有任何内嵌预览，只能做一次（半质量）demosaic 兜底——慢路径。</summary>
    LibRawFullSlow,
    /// <summary>非 RAW 文件走 WIC。</summary>
    WicFile
}

public sealed class DecodeOutcome
{
    public BitmapSource? Bitmap { get; init; }
    public PhotoMetadata Metadata { get; init; } = new();
    public DecodeSource Source { get; init; }
    public double ElapsedMs { get; init; }
    public string? Error { get; init; }
    public bool SlowPath => Source == DecodeSource.LibRawFullSlow;
    public bool Ok => Bitmap != null && Error == null;
}

/// <summary>
/// 解码编排：RAW 走 LibRaw（一次打开，取元数据 + 内嵌预览），其它格式走 WIC。
/// 绝不做全解，除非 (a) 文件没有内嵌预览、(b) 用户要 100%。
/// </summary>
public static class ImageDecoder
{
    public const int StripSize = 160;     // 缩略图条
    public const int PreviewSize = 1024;  // 大图预览

    /// <summary>解码一张的预览图（缩略图条/大图共用，只是 maxSide 不同）。</summary>
    public static DecodeOutcome DecodePreview(string path, int maxSide)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string ext = Path.GetExtension(path);
            if (LibRawImage.IsRawExtension(ext))
                return DecodeRaw(path, maxSide, sw);
            return DecodeWic(path, maxSide, sw);
        }
        catch (Exception ex)
        {
            return new DecodeOutcome { Error = ex.Message, Source = DecodeSource.WicFile, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }
    }

    private static DecodeOutcome DecodeRaw(string path, int maxSide, Stopwatch sw)
    {
        if (!LibRawImage.TryOpen(path, out var img, out var openError) || img == null)
        {
            // LibRaw 不认（例如 X3F）：退回 WIC 试试，不行就明确报错
            var wic = TryWicFallback(path, maxSide, sw, openError);
            return wic;
        }

        using (img)
        {
            var meta = img.ReadMetadata();
            var preview = img.TryReadPreview(out string thumbError);

            if (preview?.Kind == LibRawImageKind.Jpeg && preview.JpegData != null)
            {
                var d = WicImage.DecodeScaled(preview.JpegData, maxSide);
                var merged = Merge(meta, d.Exif, d.SourceWidth, d.SourceHeight);
                sw.Stop();
                return new DecodeOutcome
                {
                    Bitmap = d.Bitmap, Metadata = merged, Source = DecodeSource.LibRawThumb,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }

            if (preview?.Kind == LibRawImageKind.Bitmap && preview.RgbData != null && preview.Width > 0)
            {
                var bs = WicImage.FromRgb24(preview.RgbData, preview.Width, preview.Height, preview.Width * 3);
                var scaled = WicImage.ScaleToMaxSide(bs, maxSide);
                sw.Stop();
                return new DecodeOutcome
                {
                    Bitmap = scaled, Metadata = meta, Source = DecodeSource.LibRawThumbBitmap,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }

            // 慢路径：没有内嵌预览，只能解一次（用户看到的是「正在生成」，不能卡 UI）
            var full = img.TryDecodeFull(out string fullError);
            if (full == null)
            {
                sw.Stop();
                return new DecodeOutcome
                {
                    Error = $"没有内嵌预览（{thumbError}），完整解码也失败：{fullError}",
                    Metadata = meta, Source = DecodeSource.LibRawFullSlow, ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
            var small = WicImage.ScaleToMaxSide(full, maxSide);
            sw.Stop();
            return new DecodeOutcome
            {
                Bitmap = small, Metadata = meta, Source = DecodeSource.LibRawFullSlow,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    private static DecodeOutcome DecodeWic(string path, int maxSide, Stopwatch sw)
    {
        var d = WicImage.DecodeFileScaled(path, maxSide);
        var meta = Merge(new PhotoMetadata(), d.Exif, d.SourceWidth, d.SourceHeight);
        sw.Stop();
        return new DecodeOutcome
        {
            Bitmap = d.Bitmap, Metadata = meta, Source = DecodeSource.WicFile,
            ElapsedMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static DecodeOutcome TryWicFallback(string path, int maxSide, Stopwatch sw, string librawError)
    {
        try
        {
            var d = WicImage.DecodeFileScaled(path, maxSide);
            var meta = Merge(new PhotoMetadata(), d.Exif, d.SourceWidth, d.SourceHeight);
            sw.Stop();
            return new DecodeOutcome { Bitmap = d.Bitmap, Metadata = meta, Source = DecodeSource.WicFile, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DecodeOutcome
            {
                Error = $"无法解码：{librawError.Split('\n')[0]}；WIC 也不支持（{ex.Message}）",
                Source = DecodeSource.WicFile, ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>
    /// 100% 查看用的全尺寸解码（后台线程调用）。
    /// ① 优先用相机内嵌的全尺寸 JPEG：多数机型（Z8/R5/A7R2/D750…）内嵌预览就是原生分辨率，
    ///    相机自家去马赛克质量更好，还快约 8 倍；
    /// ② 内嵌预览缺失或明显小于原生分辨率（&lt;90%）时，才做完整 demosaic。
    ///    （实测踩坑：LibRaw 0.21.1 解 Nikon Z8 压缩 NEF 报 "data corrupted" 且输出花屏，
    ///    走内嵌 JPEG 可完全绕开。）
    /// </summary>
    public static DecodeOutcome DecodeFull(string path, int demosaicQuality = 3)
    {
        var sw = Stopwatch.StartNew();
        string ext = Path.GetExtension(path);
        if (LibRawImage.IsRawExtension(ext))
        {
            if (!LibRawImage.TryOpen(path, out var img, out var err) || img == null)
                return new DecodeOutcome { Error = err, ElapsedMs = sw.Elapsed.TotalMilliseconds };
            using (img)
            {
                var meta = img.ReadMetadata();

                // ① 内嵌全尺寸 JPEG 快路径
                var preview = img.TryReadPreview(out _);
                if (preview?.Kind == LibRawImageKind.Jpeg && preview.JpegData != null
                    && meta.PixelWidth is > 0 && meta.PixelHeight is > 0)
                {
                    var d = WicImage.DecodeScaled(preview.JpegData, int.MaxValue);
                    double rw = (double)d.SourceWidth / meta.PixelWidth.Value;
                    double rh = (double)d.SourceHeight / meta.PixelHeight.Value;
                    if (Math.Min(rw, rh) >= 0.9)
                    {
                        sw.Stop();
                        return new DecodeOutcome
                        {
                            Bitmap = d.Bitmap,
                            Metadata = Merge(meta, d.Exif, d.SourceWidth, d.SourceHeight),
                            Source = DecodeSource.LibRawThumb,
                            ElapsedMs = sw.Elapsed.TotalMilliseconds
                        };
                    }
                }

                // ② 完整 demosaic 兜底（内嵌预览缺失或太小）
                var full = img.TryDecodeFull(out string ferr, demosaicQuality);
                sw.Stop();
                if (full == null)
                    return new DecodeOutcome { Error = ferr, Metadata = meta, ElapsedMs = sw.Elapsed.TotalMilliseconds };
                return new DecodeOutcome
                {
                    Bitmap = full,
                    Metadata = meta,
                    Source = DecodeSource.LibRawFullSlow,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds
                };
            }
        }

        var data = File.ReadAllBytes(AppPaths.ToLongPath(path));
        var dec = WicImage.DecodeScaled(data, int.MaxValue);
        sw.Stop();
        return new DecodeOutcome
        {
            Bitmap = dec.Bitmap, Metadata = Merge(new PhotoMetadata(), dec.Exif, dec.SourceWidth, dec.SourceHeight),
            Source = DecodeSource.WicFile, ElapsedMs = sw.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>LibRaw 字段优先，缺的用内嵌 JPEG 的 EXIF 补齐。</summary>
    private static PhotoMetadata Merge(PhotoMetadata raw, ExifFields exif, int srcW, int srcH)
    {
        string? model = raw.Model ?? exif.Model;
        string? make = raw.Make ?? exif.Make;
        string? lens = raw.Lens ?? exif.LensModel;
        double? focal = raw.FocalLengthMm ?? exif.FocalLength;
        double? aperture = raw.ApertureF ?? exif.FNumber;
        double? shutter = raw.ShutterSeconds ?? exif.ExposureTime;
        int? iso = raw.Iso ?? (exif.Iso is > 0 ? (int)Math.Round(exif.Iso.Value) : null);
        DateTime? taken = raw.TakenAt ?? ExifReader.ParseExifDate(exif.DateTimeOriginal);
        int? w = raw.PixelWidth ?? (srcW > 0 ? srcW : null);
        int? h = raw.PixelHeight ?? (srcH > 0 ? srcH : null);

        return new PhotoMetadata
        {
            Make = make,
            Model = model,
            Lens = lens,
            FocalLengthMm = focal,
            ApertureF = aperture,
            ShutterSeconds = shutter,
            Iso = iso,
            TakenAt = taken,
            PixelWidth = w,
            PixelHeight = h,
            ColorSpace = ExifReader.ColorSpaceName(exif.ColorSpace),
            Orientation = exif.Orientation
        };
    }
}
