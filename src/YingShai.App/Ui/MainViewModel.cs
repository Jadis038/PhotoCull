using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using YingShai.Core.Model;
using YingShai.Core.Scanning;
using YingShai.Core.Session;
using YingShai.Core.Settings;

namespace YingShai.App.Ui;

/// <summary>主窗口的视图模型：把 ViewerSession 的状态翻译成界面文字与图片。</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public ViewerSession Session { get; }

    public ObservableCollection<ImageItem> Items => Session.Items;

    public MainViewModel(ViewerSession session)
    {
        Session = session;
        Session.CountsChanged += (_, _) => RaiseCounts();
        Session.CurrentChanged += (_, _) => RaiseCurrent();
        // 状态栏开机就显示版本，方便确认装的是哪个版本
        var ver = typeof(MainViewModel).Assembly.GetName().Version;
        _statusText = $"影·筛 {(ver != null ? ver.ToString(3) : "")} · 拖入文件夹或用「打开文件夹」开始";
    }

    // ——— 当前项（唯一数据源是 ViewerSession.Current，UI 与代码都改它） ———

    public ImageItem? Current
    {
        get => Session.Current;
        set
        {
            if (ReferenceEquals(Session.Current, value)) return;
            Session.SetCurrent(value);
            RaiseCurrent();
        }
    }

    private void RaiseCurrent()
    {
        Raise(nameof(Current));
        Raise(nameof(CurrentInfo));
        Raise(nameof(FilePathText));
        Raise(nameof(SizeText));
    }

    /// <summary>元数据补齐后刷信息栏。</summary>
    public void RefreshInfo()
    {
        Raise(nameof(CurrentInfo));
        Raise(nameof(SizeText));
    }

    public PhotoMetadata CurrentInfo => Current?.Metadata ?? new PhotoMetadata();

    // ——— 大图 ———

    private BitmapSource? _largeImage;
    public BitmapSource? LargeImage
    {
        get => _largeImage;
        set { _largeImage = value; Raise(); Raise(nameof(HasImage)); }
    }

    public bool HasImage => _largeImage != null;

    private BitmapSource? _fullImage;
    /// <summary>100% 查看时的全解图像（可能为 null）。</summary>
    public BitmapSource? FullImage
    {
        get => _fullImage;
        set { _fullImage = value; Raise(); }
    }

    private bool _imageLoading;
    public bool ImageLoading
    {
        get => _imageLoading;
        set { _imageLoading = value; Raise(); }
    }

    // ——— 计数 ———

    public string CountText => $"已筛 {Session.MarkedCount} / 共 {Session.TotalCount}";
    public string RemainingText => $"待筛 {Session.RemainingCount}";
    public string FilePathText => Current?.FullPath ?? "";

    public string SizeText => Current == null ? "—" : FormatSize(Current.Size);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1048576.0:0.#} MB";
        return $"{bytes / 1073741824.0:0.##} GB";
    }

    // ——— 排序 ———

    public SortMode SortMode
    {
        get => Session.SortMode;
        set
        {
            if (Session.SortMode == value) return;
            Session.SetSort(value, Session.SortDescending);
            Raise();
        }
    }

    public bool SortDescending
    {
        get => Session.SortDescending;
        set
        {
            if (Session.SortDescending == value) return;
            Session.SetSort(Session.SortMode, value);
            Raise();
        }
    }

    // ——— 复制/移动 ———

    public bool MarkByMove
    {
        get => Session.Settings.MarkMode == MarkMode.Move;
        set
        {
            Session.Settings.MarkMode = value ? MarkMode.Move : MarkMode.Copy;
            Session.Settings.Save();
            Raise();
            Raise(nameof(MarkModeText));
        }
    }

    public string MarkModeText => MarkByMove ? "当前：移动" : "当前：复制";

    // ——— 状态 ———

    private string _statusText = "拖入文件夹或用「打开文件夹」开始";
    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set { _progressText = value; Raise(); } }

    private string _zoomText = "适应窗口";
    public string ZoomText { get => _zoomText; set { _zoomText = value; Raise(); } }

    public bool CanUndo => Session.Marks.UndoDepth > 0;
    public string UndoText => Session.Marks.UndoDepth == 0 ? "撤销" : $"撤销 ({Session.Marks.UndoDepth})";

    public void RaiseCounts()
    {
        Raise(nameof(CountText));
        Raise(nameof(RemainingText));
        Raise(nameof(CanUndo));
        Raise(nameof(UndoText));
        Raise(nameof(SizeText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
