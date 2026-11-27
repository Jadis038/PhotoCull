using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using YingShai.Core.Model;

namespace YingShai.Core.Model;

/// <summary>标记/取消的即时状态，UI 角标据此显示。</summary>
public enum MarkState
{
    None,
    Queued,     // 已入队（转圈）
    Working,    // 正在复制/删除
    Marked,     // 绿勾
    Failed      // 回滚为未标记并提示
}

/// <summary>
/// 列表里的一张图片。位置（Index）在会话内永不变动——标记后不重排、不消失。
/// 由于 UI 直接绑定，这里实现 INotifyPropertyChanged（只依赖 BCL）。
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }

    /// <summary>在本次会话中的固定位置。</summary>
    public int Index { get; set; }

    /// <summary>源文件已不存在（外部删除/移动）。</summary>
    private bool _sourceMissing;
    public bool SourceMissing { get => _sourceMissing; set => Set(ref _sourceMissing, value); }

    private MarkState _state = MarkState.None;
    public MarkState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsMarked));
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private bool _locked;
    /// <summary>保护（锁定）：受保护的图不会被取消筛选/删除副本。</summary>
    public bool Locked
    {
        get => _locked;
        set { if (Set(ref _locked, value)) OnPropertyChanged(nameof(LockedText)); }
    }

    /// <summary>信息栏「保护」行文字。</summary>
    public string LockedText => Locked ? "🔒 已保护（S 解锁）" : "—";

    private int _stars;
    /// <summary>星级 0~5。</summary>
    public int Stars
    {
        get => _stars;
        set { if (Set(ref _stars, value)) { OnPropertyChanged(nameof(StarsText)); OnPropertyChanged(nameof(HasStars)); } }
    }

    /// <summary>是否已标星（缩略图角标可见性绑定用）。</summary>
    public bool HasStars => Stars > 0;

    /// <summary>信息栏「星级」行文字。</summary>
    public string StarsText => Stars > 0 ? new string('★', Stars) : "—";

    public bool IsMarked => _state == MarkState.Marked;
    public bool IsBusy => _state is MarkState.Queued or MarkState.Working;

    /// <summary>角标文字：✓ / … / ✕ / 空。</summary>
    public string BadgeText => _state switch
    {
        MarkState.Marked => "✓",
        MarkState.Queued => "…",
        MarkState.Working => "…",
        MarkState.Failed => "✕",
        _ => ""
    };

    private string? _status;
    public string? Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => SourceMissing ? "源文件已不存在" : (_status ?? string.Empty);

    private BitmapSource? _thumb;
    private BitmapSource? _thumbRaw;
    private BitmapSource? _thumbGray;

    /// <summary>显示用的缩略图：已标记时是降饱和版本，未标记时是原图。</summary>
    public BitmapSource? Thumb
    {
        get => _thumb;
        set => Set(ref _thumb, value);
    }

    /// <summary>装载缩略图（原图），并按当前标记状态决定显示灰调还是彩色。</summary>
    public void SetThumb(BitmapSource? bmp)
    {
        _thumbRaw = bmp;
        _thumbGray = null;
        Thumb = bmp == null ? null : (IsMarked ? Gray(bmp) : bmp);
    }

    /// <summary>标记状态变化后刷新缩略图外观（降饱和 = 已标记）。</summary>
    public void ApplyMarkVisual()
    {
        if (_thumbRaw == null) { return; }
        Thumb = IsMarked ? Gray(_thumbRaw) : _thumbRaw;
    }

    private BitmapSource Gray(BitmapSource src) => _thumbGray ??= YingShai.Core.Imaging.WicImage.Desaturate(src);

    /// <summary>离开可视区时彻底释放（缓存里还有，滚回来秒出）。</summary>
    public void ClearThumb()
    {
        _thumbRaw = null;
        _thumbGray = null;
        Thumb = null;
    }

    private PhotoMetadata? _metadata;
    public PhotoMetadata? Metadata
    {
        get => _metadata;
        set { if (Set(ref _metadata, value)) OnPropertyChanged(nameof(Info)); }
    }

    private MarkRecord? _record;
    /// <summary>当前生效的索引记录（标记成功后才非空）。</summary>
    public MarkRecord? Record
    {
        get => _record;
        set { if (Set(ref _record, value)) OnPropertyChanged(nameof(IsMarked)); }
    }

    public PhotoMetadata Info => _metadata ?? new PhotoMetadata();

    /// <summary>缓存键：路径 + 大小 + 修改时间 + 解码器版本。</summary>
    public string CacheKeySeed => $"{FullPath}|{Size}|{ModifiedUtc.Ticks}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}

/// <summary>索引里的一条「已移入筛选」记录。操作类型逐条记录，切换复制/移动模式不影响旧记录的撤销方式。</summary>
public sealed class MarkRecord
{
    public int FolderId { get; set; }
    public required string ItemName { get; init; }
    public required string SourcePath { get; init; }
    public required string DestPath { get; set; }
    public MarkOpKind Kind { get; init; }
    /// <summary>副本的大小与修改时间（用来核对用户是否在资源管理器动过）。</summary>
    public long DestSize { get; set; }
    public long DestMtimeTicks { get; set; }
    public DateTime CreatedLocal { get; init; } = DateTime.Now;
    /// <summary>伴生文件：源路径 → 副本路径。</summary>
    public List<CompanionCopy> Companions { get; init; } = new();

    public bool IsCopy => Kind == MarkOpKind.Copy;
    public override string ToString() => $"{ItemName} -> {DestPath} ({Kind})";
}

public sealed class CompanionCopy
{
    public required string SourcePath { get; init; }
    public required string DestPath { get; init; }
}

public enum MarkOpKind { Copy = 0, Move = 1 }
