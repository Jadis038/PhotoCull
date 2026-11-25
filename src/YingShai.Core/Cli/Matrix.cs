using System.Diagnostics;
using System.Text;
using YingShai.Core.Imaging;
using YingShai.Core.Scanning;

namespace YingShai.Core.Cli;

/// <summary>
/// 测试矩阵（影·筛.exe --matrix &lt;文件夹&gt; [--out x.csv] [--full-decode]）：
/// 一行一个文件，记录品牌机型、能否打开、有无内嵌预览、缩略图耗时、可选的全解耗时、信息栏字段齐全度。
/// 素材目录只读，不写入任何文件。
/// </summary>
public static class Matrix
{
    public static int Run(string folder, string? csvPath, bool doFullDecode)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(f => DirectoryScanner.IsImageExtension(Path.GetExtension(f)))
            .OrderBy(f => f, NaturalComparer.Instance).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("没有图片文件：" + folder); return 2; }

        var rows = new List<string[]>();
        Console.WriteLine($"{"文件名",-38}{"扩展",-7}{"机型",-26}{"MB",8}  {"打开",-5}{"预览",-11}{"缩略ms",8}{"全解ms",9} 字段");
        Console.WriteLine(new string('-', 140));

        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            double mb = new FileInfo(file).Length / 1048576.0;

            string model = "-", open = "否", preview = "无", fields = "-";
            double thumbMs = 0, fullMs = 0;
            string error = "";

            if (LibRawImage.IsRawExtension(ext))
            {
                if (!LibRawImage.TryOpen(file, out var img, out var openErr) || img == null)
                {
                    open = "否";
                    error = openErr;
                }
                else
                {
                    using (img)
                    {
                        open = "是";
                        var meta = img.ReadMetadata();
                        model = meta.Model ?? "(无)";
                        var sw = Stopwatch.StartNew();
                        var prev = img.TryReadPreview(out var perr);
                        sw.Stop();
                        thumbMs = sw.Elapsed.TotalMilliseconds;
                        preview = prev == null ? "无" : (prev.Kind == LibRawImageKind.Jpeg ? $"JPEG {prev.RawSize / 1024}KB" : $"RGB {prev.Width}x{prev.Height}");
                        fields = FieldsOf(meta);

                        if (doFullDecode)
                        {
                            var fsw = Stopwatch.StartNew();
                            var full = img.TryDecodeFull(out var ferr);
                            fsw.Stop();
                            fullMs = fsw.Elapsed.TotalMilliseconds;
                            if (full == null) error = ferr;
                        }
                    }
                }
            }
            else
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var d = WicImage.DecodeFileScaled(file, 1024);
                    sw.Stop();
                    thumbMs = sw.Elapsed.TotalMilliseconds;
                    open = "是";
                    preview = $"WIC {d.SourceWidth}x{d.SourceHeight}";
                    model = d.Exif.Model ?? "(无)";
                    fields = FieldsOf(new Model.PhotoMetadata
                    {
                        Model = d.Exif.Model, Lens = d.Exif.LensModel, FocalLengthMm = d.Exif.FocalLength,
                        ApertureF = d.Exif.FNumber, ShutterSeconds = d.Exif.ExposureTime,
                        Iso = d.Exif.Iso is > 0 ? (int)d.Exif.Iso.Value : null,
                        TakenAt = ExifReader.ParseExifDate(d.Exif.DateTimeOriginal),
                        PixelWidth = d.SourceWidth, PixelHeight = d.SourceHeight,
                        ColorSpace = ExifReader.ColorSpaceName(d.Exif.ColorSpace)
                    });
                }
                catch (Exception ex) { error = ex.Message; }
            }

            rows.Add(new[] { name, ext, model, mb.ToString("F1"), open, preview, thumbMs.ToString("F0"), fullMs > 0 ? fullMs.ToString("F0") : "-", fields, error });
            Console.WriteLine($"{Trunc(name, 36),-38}{ext,-7}{Trunc(model, 24),-26}{mb,8:F1}  {open,-5}{Trunc(preview, 9),-11}{thumbMs,8:F0}{(fullMs > 0 ? fullMs.ToString("F0") : "-"),9} {fields}");
        }

        csvPath ??= Path.Combine(Path.GetTempPath(), $"影筛_测试矩阵_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var sb = new StringBuilder("文件名,扩展名,品牌机型,大小MB,能否打开,内嵌预览,缩略图耗时ms,全解耗时ms,信息栏字段,错误\n");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', r.Select(Escape)));
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine();
        Console.WriteLine($"CSV：{csvPath}");
        return rows.Count(r => r[4] == "是") == rows.Count ? 0 : 1;
    }

    /// <summary>信息栏 10 个字段里有几个有值（机型/镜头/焦距/光圈/快门/ISO/拍摄时间/分辨率/大小/色彩空间）。</summary>
    private static string FieldsOf(Model.PhotoMetadata m)
    {
        int n = 0;
        if (!string.IsNullOrWhiteSpace(m.Model)) n++;
        if (!string.IsNullOrWhiteSpace(m.Lens)) n++;
        if (m.FocalLengthMm > 0) n++;
        if (m.ApertureF > 0) n++;
        if (m.ShutterSeconds > 0) n++;
        if (m.Iso > 0) n++;
        if (m.TakenAt != null) n++;
        if (m.PixelWidth > 0) n++;
        if (!string.IsNullOrWhiteSpace(m.ColorSpace)) n++;
        n++; // 文件大小总是有
        return $"{n}/10";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static string Escape(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
