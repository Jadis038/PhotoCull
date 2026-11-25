namespace YingShai.Core.Model;

/// <summary>
/// 一张图片的元数据。字段取不到时为 null，UI 显示「—」。
/// 全部来自「一次打开」：RAW 走 LibRaw 的 iparams/imgother/lensinfo，
/// 补充字段从同一次拿到的内嵌 JPEG 的 EXIF 里读（不二次打开文件）。
/// </summary>
public sealed class PhotoMetadata
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Lens { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? ApertureF { get; init; }
    /// <summary>快门时间（秒）。</summary>
    public double? ShutterSeconds { get; init; }
    public int? Iso { get; init; }
    public DateTime? TakenAt { get; init; }
    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }
    public string? ColorSpace { get; init; }
    /// <summary>EXIF 方向 1..8。</summary>
    public int Orientation { get; init; } = 1;

    public string DisplayModel => string.IsNullOrWhiteSpace(Model) ? "—" : Model!;
    public string DisplayLens => string.IsNullOrWhiteSpace(Lens) ? "—" : Lens!;

    public string DisplayFocal => FocalLengthMm is > 0 ? $"{FocalLengthMm.Value:0.#} mm" : "—";

    public string DisplayAperture => ApertureF is > 0
        ? (ApertureF.Value % 1 == 0 ? $"f/{ApertureF.Value:0}" : $"f/{ApertureF.Value:0.0}")
        : "—";

    /// <summary>快门显示：1/250 s、2.5 s。</summary>
    public string DisplayShutter
    {
        get
        {
            if (ShutterSeconds is not > 0) return "—";
            double s = ShutterSeconds.Value;
            if (s >= 0.3) return $"{s:0.#} s";
            return $"1/{Math.Round(1 / s):0} s";
        }
    }

    public string DisplayIso => Iso is > 0 ? $"ISO {Iso}" : "—";
    public string DisplayTakenAt => TakenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string DisplayResolution => PixelWidth is > 0 && PixelHeight is > 0 ? $"{PixelWidth} × {PixelHeight}" : "—";
    public string DisplayColorSpace => string.IsNullOrWhiteSpace(ColorSpace) ? "—" : ColorSpace!;
}
