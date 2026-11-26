using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YingShai.Core.Imaging;
using YingShai.Core.Jobs;
using YingShai.Core.Model;
using YingShai.Core.Ops;
using YingShai.Core.Session;
using YingShai.Core.Settings;

namespace YingShai.App.Ui;

public partial class MainWindow : Window
{
    private enum ZoomMode { Fit, Actual, Free }

    private readonly MainViewModel _vm;
    private ViewerSession Session => _vm.Session;

    private ZoomMode _mode = ZoomMode.Fit;
    private double _freeScale = 1.0;
    private CancellationTokenSource? _imageCts;
    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;
    private readonly DispatcherTimer _priorityTimer;
    private readonly DispatcherTimer _progressTimer;
    private CancellationTokenSource? _warmupCts;
    private string _lastFolder = "";

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        // 窗口图标：从内嵌资源读（XAML 里写相对路径会因中文资源名解析失败，这里用 pack URI + 兜底）
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/影筛.ico", UriKind.Absolute));
        }
        catch { }

        // 无颜色 / 亮色主题下也保证可读
        DataContext = vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Current)) OnCurrentChanged();
            if (e.PropertyName == nameof(MainViewModel.LargeImage)) UpdateEmptyHint();
        };

        _priorityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _priorityTimer.Tick += (_, _) => { _priorityTimer.Stop(); UpdateThumbPriorities(); };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _progressTimer.Start();

        Session.StatusMessage += (_, text) => Dispatcher.Invoke(() => _vm.StatusText = text);
        Session.ItemsReset += (_, _) => Dispatcher.Invoke(AfterItemsReset);
        Session.Queue.Progress += (_, p) => Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(p.Message))
                _vm.ProgressText = p.Total > 0
                    ? $"{p.Message}  {p.Done * 100 / Math.Max(1, p.Total)}%"
                    : p.Message;
        });
        Session.Queue.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            bool busy = Session.Queue.IsBusy;
            Session.OnQueueActivity(busy);
            if (!busy && Session.Queue.CurrentTitle == null) _vm.ProgressText = "";
        });

        // 拖动窗口大小/多显示器 DPI 变化后重新适配
        DpiChanged += (_, _) => { if (_mode == ZoomMode.Fit) ApplyFit(); };
    }

    // —————————————— 打开 / 扫描 ——————————————

    public async Task<bool> OpenFolderAsync(string folderOrFile, string? selectPath = null)
    {
        var old = Session.Queue.IsBusy
            ? MessageBox.Show(this, "当前还有复制/移动任务在进行，打开新文件夹会切换会话。要继续吗？",
                "影·筛", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBoxResult.Yes;
        if (old != MessageBoxResult.Yes) return false;

        _imageCts?.Cancel();
        _vm.LargeImage = null;
        _vm.StatusText = "正在打开…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = await Session.OpenFolderAsync(folderOrFile);
        sw.Stop();
        if (!ok) return false;

        _lastFolder = Session.FolderPath;
        Session.Settings.LastFolder = _lastFolder;
        Session.Settings.Save();

        var target = selectPath != null ? Session.SelectByPath(selectPath) : null;
        if (target == null && Session.Items.Count > 0) Session.SetCurrent(Session.Items[0]);
        if (Session.Items.Count == 0) _vm.StatusText = "这个文件夹里没有图片（只列第一层）";

        _vm.RaiseCounts();
        _vm.StatusText = $"{Path.GetFileName(Session.FolderPath)}：共 {Session.TotalCount} 张，已筛 {Session.MarkedCount} 张（枚举 {Session.LastScanMs:F0} ms）";
        AfterItemsReset();
        return true;
    }

    private void AfterItemsReset()
    {
        ThumbList.SelectedItem = _vm.Current;
        UpdateThumbPriorities();
        StartWarmupSweep();
        ScheduleTakenTimeFill();
        if (_mode == ZoomMode.Fit) ApplyFit();
    }

    /// <summary>如果用户按拍摄时间排序，先补一次拍摄时间（只 open 不解码，2000 张约 2~4 秒）。</summary>
    private async void ScheduleTakenTimeFill()
    {
        if (Session.SortMode != SortMode.TakenTime) return;
        try
        {
            _vm.StatusText = "正在读取拍摄时间…";
            await Session.EnsureTakenTimesAsync();
            _vm.StatusText = $"已按拍摄时间排序（共 {Session.TotalCount} 张）";
        }
        catch { }
    }

    /// <summary>
    /// 给当前项附近（±60 张）的缩略图排优先级。
    /// 注意：绝不在这里遍历全部几千张——那会在 UI 线程上做几千次入队，点击就卡住了；
    /// 全量预热由 StartWarmupSweep 在后台分批做。
    /// </summary>
    private void UpdateThumbPriorities()
    {
        var cur = _vm.Current;
        int n = Session.Items.Count;
        if (cur == null)
        {
            foreach (var item in Session.Items.Take(24)) Session.RequestThumb(item, DecodePriority.VisibleStrip);
            return;
        }
        int ci = cur.Index;
        int lo = Math.Max(0, ci - 60), hi = Math.Min(n - 1, ci + 60);
        for (int i = lo; i <= hi; i++)
        {
            var item = Session.Items[i];
            if (item.Thumb != null) continue;
            int d = Math.Abs(item.Index - ci);
            int prio = d <= 12 ? DecodePriority.VisibleStrip : DecodePriority.Prefetch;
            Session.RequestThumb(item, prio);
            if (item.Thumb == null) Session.Reprioritize(item, prio);
        }
    }

    /// <summary>
    /// 全量缩略图预热：后台线程分批（每批 100 张）排队，优先级最低，不打扰交互；
    /// 打开新文件夹/关窗时取消。
    /// </summary>
    private void StartWarmupSweep()
    {
        _warmupCts?.Cancel();
        _warmupCts = new CancellationTokenSource();
        var ct = _warmupCts.Token;
        var items = Session.Items.ToList();
        if (items.Count == 0) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < items.Count; i += 100)
            {
                if (ct.IsCancellationRequested) return;
                foreach (var it in items.Skip(i).Take(100))
                {
                    if (ct.IsCancellationRequested) return;
                    Session.RequestThumb(it, DecodePriority.Warmup);
                }
                try { await Task.Delay(40, ct); } catch (TaskCanceledException) { return; }
            }
        }, ct);
    }

    private void UpdateProgress()
    {
        // 缩略图生成进度：可见区附近已就绪的张数
        int withThumb = 0;
        foreach (var item in Session.Items) if (item.Thumb != null) withThumb++;
        if (Session.TotalCount > 0 && withThumb < Session.TotalCount)
            _vm.ProgressText = $"正在生成缩略图 {withThumb}/{Session.TotalCount}";
        else if (!Session.Queue.IsBusy)
            _vm.ProgressText = "";
    }

    // —————————————— 当前项与大图 ——————————————

    private async void OnCurrentChanged()
    {
        ThumbList.SelectedItem = _vm.Current;
        _priorityTimer.Stop();
        _priorityTimer.Start();
        await LoadImageAsync();
    }

    private async Task LoadImageAsync()
    {
        var item = _vm.Current;
        _imageCts?.Cancel();
        if (item == null) { _vm.LargeImage = null; return; }
        var cts = new CancellationTokenSource();
        _imageCts = cts;
        _vm.ImageLoading = true;
        LoadingHint.Visibility = Visibility.Visible;
        try
        {
            var preview = await Session.GetPreviewAsync(item, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_vm.Current, item)) return;
            if (preview != null)
            {
                _vm.LargeImage = preview;
                if (_mode == ZoomMode.Fit)
                {
                    // 布局可能还没跑完（ActualWidth 还是 0），延后到布局结束再适配一次
                    ApplyFit();
                    _ = Dispatcher.BeginInvoke(new Action(() => { if (_mode == ZoomMode.Fit) ApplyFit(); }),
                        DispatcherPriority.Loaded);
                }
                else ApplyFree();
            }
            else
            {
                _vm.LargeImage = null;
                _vm.StatusText = $"无法解码：{item.Name}";
            }
            if (item.Metadata == null) { await FillMetadataAsync(item); _vm.RefreshInfo(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _vm.StatusText = "加载失败：" + ex.Message; }
        finally
        {
            if (ReferenceEquals(_imageCts, cts))
            {
                _vm.ImageLoading = false;
                LoadingHint.Visibility = Visibility.Collapsed;
                UpdateEmptyHint();
            }
        }
    }

    private async Task FillMetadataAsync(ImageItem item)
    {
        try
        {
            await Task.Run(() =>
            {
                var outcome = ImageDecoder.DecodePreview(item.FullPath, ImageDecoder.StripSize);
                if (outcome.Metadata != null) item.Metadata = outcome.Metadata;
            });
            _vm.StatusText = "";
        }
        catch { }
    }

    private void UpdateEmptyHint()
        => EmptyHint.Visibility = _vm.LargeImage == null && Session.TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>切到 100%：后台全解（45MP 约 1~3 秒），期间界面照常可用。</summary>
    private async Task EnsureFullResAsync()
    {
        var item = _vm.Current;
        if (item == null) return;
        if (_vm.FullImage != null) return;
        _vm.StatusText = "正在后台解码原图（100%）…";
        try
        {
            var full = await Session.GetFullResAsync(item, CancellationToken.None);
            if (!ReferenceEquals(_vm.Current, item)) return;
            if (full != null)
            {
                _vm.FullImage = full;
                _vm.LargeImage = full;
                _vm.StatusText = $"100% 原图已就绪（{full.PixelWidth}×{full.PixelHeight}）";
                ApplyActual();
            }
            else _vm.StatusText = "原图解码失败";
        }
        catch (Exception ex) { _vm.StatusText = "原图解码失败：" + ex.Message; }
    }

    // —————————————— 缩放 / 平移 ——————————————

    private void ApplyFit()
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        double availW = Math.Max(1, ImageArea.ActualWidth - 8);
        double availH = Math.Max(1, ImageArea.ActualHeight - 8);
        if (availW < 40 || availH < 40)
        {
            // 布局未完成时的兜底估算（右侧信息栏 270 + 缩略图条 200 + 工具栏/状态栏）
            availW = Math.Max(200, ActualWidth - 270 - 8);
            availH = Math.Max(200, ActualHeight - 200 - 90);
        }
        double scale = Math.Min(availW / Math.Max(1, img.PixelWidth), availH / Math.Max(1, img.PixelHeight));
        scale = Math.Min(scale, 1.0);
        _mode = ZoomMode.Fit;
        _freeScale = scale;
        ImgScale.ScaleX = scale;
        ImgScale.ScaleY = scale;
        ImgTranslate.X = (availW - img.PixelWidth * scale) / 2;
        ImgTranslate.Y = (availH - img.PixelHeight * scale) / 2;
        _vm.ZoomText = "适应窗口";
    }

    private void ApplyActual()
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        _mode = ZoomMode.Actual;
        _freeScale = 1.0;
        ImgScale.ScaleX = 1;
        ImgScale.ScaleY = 1;
        ImgTranslate.X = (ImageArea.ActualWidth - img.PixelWidth) / 2;
        ImgTranslate.Y = (ImageArea.ActualHeight - img.PixelHeight) / 2;
        _vm.ZoomText = "100%";
    }

    private void ApplyFree()
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        ImgScale.ScaleX = _freeScale;
        ImgScale.ScaleY = _freeScale;
        _vm.ZoomText = $"自由缩放 {_freeScale * 100:F0}%";
    }

    private void ZoomAt(Point mouse, double factor)
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        double oldScale = ImgScale.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.02, 12.0);
        if (Math.Abs(newScale - oldScale) < 1e-6) return;
        // 以鼠标位置为中心缩放
        double imgX = (mouse.X - ImgTranslate.X) / oldScale;
        double imgY = (mouse.Y - ImgTranslate.Y) / oldScale;
        _mode = ZoomMode.Free;
        _freeScale = newScale;
        ImgScale.ScaleX = newScale;
        ImgScale.ScaleY = newScale;
        ImgTranslate.X = mouse.X - imgX * newScale;
        ImgTranslate.Y = mouse.Y - imgY * newScale;
        _vm.ZoomText = $"自由缩放 {newScale * 100:F0}%";
    }

    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(ImageArea);
        ZoomAt(pos, e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击大图 = 适应窗口（不做标记，防误触）
            ApplyFit();
            e.Handled = true;
            return;
        }
        _panning = true;
        _panStart = e.GetPosition(ImageArea);
        _panOrigin = new Vector(ImgTranslate.X, ImgTranslate.Y);
        ImageArea.CaptureMouse();
        ImageArea.Cursor = Cursors.SizeAll;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(ImageArea);
        ImgTranslate.X = _panOrigin.X + (p.X - _panStart.X);
        ImgTranslate.Y = _panOrigin.Y + (p.Y - _panStart.Y);
        if (_mode == ZoomMode.Fit) _mode = ZoomMode.Free;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImageArea.ReleaseMouseCapture();
        ImageArea.Cursor = Cursors.Arrow;
    }

    private void OnImageAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mode == ZoomMode.Fit) ApplyFit();
    }

    private void OnZoomFit(object sender, RoutedEventArgs e) => ApplyFit();

    private async void OnZoomActual(object sender, RoutedEventArgs e)
    {
        await EnsureFullResAsync();
        if (_vm.LargeImage != null) ApplyActual();
    }

    // —————————————— 标记 / 撤销 ——————————————

    private async void MarkOrUnmarkCurrent()
    {
        var item = _vm.Current;
        if (item == null) return;
        if (item.State is MarkState.Queued or MarkState.Working) return;

        bool wasMarked = item.State == MarkState.Marked;
        var result = await Session.ToggleMarkAsync(item);
        item.ApplyMarkVisual();
        _vm.RaiseCounts();
        if (!result.Ok)
        {
            _vm.StatusText = $"{(wasMarked ? "取消筛选" : "移入筛选")}失败：{result.Error}";
            if (!string.IsNullOrEmpty(result.Error) && result.Error != "已取消")
                MessageBox.Show(this, result.Error, "影·筛", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (result.Warning != null) _vm.StatusText = result.Warning;
        else _vm.StatusText = wasMarked ? $"已取消筛选：{item.Name}" : $"已移入筛选：{item.Name}";
        // 标记后停留在当前图上（用户可确认绿勾），用 → / 单击继续看下一张
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        var r = await Session.Marks.UndoAsync();
        _vm.RaiseCounts();
        _vm.StatusText = r.Ok ? "已撤销上一步" : $"撤销失败：{r.Error}";
        if (!r.Ok && r.Error != "已取消" && r.Error != "没有可撤销的操作")
            MessageBox.Show(this, r.Error ?? "", "影·筛", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 单击缩略图 = 立即在上方查看这张图。
    /// 不依赖 SelectedItem 的 TwoWay 绑定，直接处理 SelectionChanged，保证单击一定切换大图。
    /// </summary>
    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is ImageItem item && !ReferenceEquals(Session.Current, item))
            Session.SetCurrent(item);   // CurrentChanged → ViewModel/大图自动跟着切
    }

    private void OnThumbDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击缩略图 = 标记/取消标记（双击大图才是适应窗口）
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) is ListBoxItem lbi
            && lbi.DataContext is ImageItem item)
        {
            Session.SetCurrent(item);
            _vm.Current = item;
            MarkOrUnmarkCurrent();
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void OnMenuMark(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is ImageItem item)
        {
            Session.SetCurrent(item);
            _vm.Current = item;
            if (item.State != MarkState.Marked) MarkOrUnmarkCurrent();
        }
    }

    private void OnMenuUnmark(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is ImageItem item)
        {
            Session.SetCurrent(item);
            _vm.Current = item;
            if (item.State == MarkState.Marked) MarkOrUnmarkCurrent();
        }
    }

    private void OnMenuReveal(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuTarget(sender) is ImageItem item) FileOps.RevealInExplorer(item.FullPath);
    }

    private static ImageItem? ResolveMenuTarget(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ItemsControl ic && ic is ContextMenu)
        {
            var cm = (ContextMenu)ic;
            return (cm.PlacementTarget as FrameworkElement)?.DataContext as ImageItem;
        }
        return null;
    }

    // —————————————— 工具栏 / 快捷键 / 拖放 ——————————————

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择图片文件夹", Multiselect = false };
        if (Directory.Exists(Session.Settings.LastFolder ?? "")) dlg.InitialDirectory = Session.Settings.LastFolder;
        if (dlg.ShowDialog(this) == true) await OpenFolderAsync(dlg.FolderName);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Session.FolderPath)) return;
        string? sel = _vm.Current?.FullPath;
        await OpenFolderAsync(Session.FolderPath, sel);
        _vm.StatusText = "已刷新（重新核对索引与「筛选」内容）";
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var mode = SortCombo.SelectedIndex switch
        {
            1 => SortMode.TakenTime,
            2 => SortMode.ModifiedTime,
            3 => SortMode.Size,
            _ => SortMode.Name
        };
        Session.SetSort(mode, Session.SortDescending);
        OnSortDirChanged();
        _ = FillTimesIfNeeded(mode);
    }

    private async Task FillTimesIfNeeded(SortMode mode)
    {
        if (mode != SortMode.TakenTime) return;
        _vm.StatusText = "正在读取拍摄时间…";
        await Session.EnsureTakenTimesAsync();
        _vm.StatusText = $"已按拍摄时间排序（共 {Session.TotalCount} 张）";
    }

    private void OnToggleSortDir(object sender, RoutedEventArgs e)
    {
        Session.SetSort(Session.SortMode, !Session.SortDescending);
        OnSortDirChanged();
    }

    private void OnSortDirChanged()
    {
        SortDirButton.Content = Session.SortDescending ? "降序" : "升序";
        SortCombo.SelectedIndex = Session.SortMode switch
        {
            SortMode.TakenTime => 1,
            SortMode.ModifiedTime => 2,
            SortMode.Size => 3,
            _ => 0
        };
    }

    private void OnThumbWheel(object sender, MouseWheelEventArgs e)
    {
        // 滚轮横向滚动缩略图条
        if (ThumbList.Template.FindName("ItemsHost", ThumbList) is Panel) { }
        var sv = FindScrollViewer(ThumbList);
        if (sv != null)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - Math.Sign(e.Delta) * 3 * 60);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.X:
            case Key.Enter:
                if (!ctrl) { MarkOrUnmarkCurrent(); e.Handled = true; }
                break;
            case Key.Z:
                if (ctrl) { OnUndo(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Key.O:
                if (ctrl) { OnOpenFolder(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case Key.F5:
                OnRefresh(this, new RoutedEventArgs()); e.Handled = true;
                break;
            case Key.Left:
                Navigate(-1); e.Handled = true;
                break;
            case Key.Right:
            case Key.Space:
                Navigate(1); e.Handled = true;
                break;
            case Key.Home:
                if (Session.Items.Count > 0) SetCurrent(Session.Items[0]);
                e.Handled = true;
                break;
            case Key.End:
                if (Session.Items.Count > 0) SetCurrent(Session.Items[^1]);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                ApplyFit(); e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                OnZoomActual(this, new RoutedEventArgs()); e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomAt(new Point(ImageArea.ActualWidth / 2, ImageArea.ActualHeight / 2), 1.25); e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomAt(new Point(ImageArea.ActualWidth / 2, ImageArea.ActualHeight / 2), 0.8); e.Handled = true;
                break;
        }
    }

    private void Navigate(int delta)
    {
        var cur = _vm.Current;
        if (cur == null) return;
        int i = Session.Items.IndexOf(cur);
        int n = Math.Clamp(i + delta, 0, Session.Items.Count - 1);
        if (n != i) SetCurrent(Session.Items[n]);
    }

    private void SetCurrent(ImageItem item)
    {
        Session.SetCurrent(item);
        _vm.Current = item;
        ThumbList.ScrollIntoView(item);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (files.Length == 0) return;
        string first = files[0];
        if (Directory.Exists(first)) await OpenFolderAsync(first);
        else await OpenFolderAsync(Path.GetDirectoryName(first)!, first);
        Activate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnSortDirChanged();
        UpdateEmptyHint();
        Activate();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (Session.Queue.IsBusy)
        {
            var r = MessageBox.Show(this, "还有复制/移动任务在进行。确定退出吗？（未完成的任务会被取消，.part 会被清理）",
                "影·筛", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        Session.Settings.WindowWidth = ActualWidth;
        Session.Settings.WindowHeight = ActualHeight;
        Session.Settings.Save();
        _progressTimer.Stop();
        _priorityTimer.Stop();
        _warmupCts?.Cancel();
        Session.Dispose();
    }

    public string LayoutDebug =>
        $"ImageArea={ImageArea.ActualWidth:F0}x{ImageArea.ActualHeight:F0} " +
        $"img={(int)(_vm.LargeImage?.PixelWidth ?? 0)}x{(int)(_vm.LargeImage?.PixelHeight ?? 0)} " +
        $"scale={ImgScale.ScaleX:F3} tx={ImgTranslate.X:F1} ty={ImgTranslate.Y:F1} " +
        $"render={MainImage.RenderSize.Width:F0}x{MainImage.RenderSize.Height:F0}";

    /// <summary>验收：模拟单击缩略图（与鼠标单击同一条选中路径），等大图切换后返回结果。</summary>
    public async Task<(bool CurrentOk, bool ImageOk, double Ms)> SimulateThumbClickAsync(ImageItem item)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ThumbList.ScrollIntoView(item);
        ThumbList.SelectedItem = item;   // 触发 SelectionChanged → 查看这张图
        // 等当前项与大图就绪（最多 8 秒）
        for (int i = 0; i < 160 && (_vm.Current != item || _vm.LargeImage == null); i++)
            await Task.Delay(50);
        sw.Stop();
        return (_vm.Current == item, _vm.LargeImage != null && _vm.Current == item, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>验收用：切到 100%，触发后台全解并返回耗时与结果。</summary>
    public async Task<(bool Ok, double Ms, int W, int H)> TestFullResAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await EnsureFullResAsync();
        sw.Stop();
        var img = _vm.FullImage;
        return (img != null, sw.Elapsed.TotalMilliseconds, img?.PixelWidth ?? 0, img?.PixelHeight ?? 0);
    }

    /// <summary>调试/验收：把当前窗口渲染成 PNG（无交互桌面也能出图）。</summary>
    public void RenderToPng(string path)
    {
        if (_mode == ZoomMode.Fit) ApplyFit();
        UpdateLayout();
        int w = (int)Math.Max(1, ActualWidth), h = (int)Math.Max(1, ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
