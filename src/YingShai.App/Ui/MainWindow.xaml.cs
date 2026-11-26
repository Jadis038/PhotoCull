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
    private double _viewScale = 1.0;          // 虚拟画布缩放
    private double _viewTx, _viewTy;           // 整图左上角在屏幕上的位置（虚拟画布平移）
    private Int32Rect _cropRect = Int32Rect.Empty;  // 当前视口裁剪矩形（整图显示时为空）
    private string? _fullResFor;               // FullImage 属于哪个文件（换图时必须作废）
    private const double MaxDirectDisplayPixels = 4000;  // 超过这个显示尺寸就必须走视口裁剪
    private CancellationTokenSource? _imageCts;
    private bool _panning;
    private bool _panArmed;
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

        // 滚轮用 Preview 事件 + handledEventsToo 兜底注册：即使以后大图区里加了 ScrollViewer 之类
        // 会吞掉 MouseWheel 的子元素，缩放/平移也一定生效
        ImageArea.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnImageWheel), true);

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
        // 换图了：100% 全解缓存必须作废，否则下一张双击放大时会显示上一张的全解图
        if (_fullResFor != item.FullPath)
        {
            _vm.FullImage = null;
            _fullResFor = null;
        }
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
                // 切图后回到适应窗口，避免上一张的缩放状态套到新图上
                ApplyFit();
                _ = Dispatcher.BeginInvoke(new Action(() => { if (_mode == ZoomMode.Fit) ApplyFit(); }),
                        DispatcherPriority.Loaded);
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
        // 只有"当前这张图"的全解缓存才有效（_fullResFor 记录它属于哪个文件）
        if (_vm.FullImage != null && _fullResFor == item.FullPath) return;
        _vm.StatusText = "正在后台解码原图（100%）…";
        try
        {
            var full = await Session.GetFullResAsync(item, CancellationToken.None);
            if (!ReferenceEquals(_vm.Current, item)) return;
            if (full != null)
            {
                _vm.FullImage = full;
                _fullResFor = item.FullPath;
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
        _viewScale = scale;
        _viewTx = (availW - img.PixelWidth * scale) / 2;
        _viewTy = (availH - img.PixelHeight * scale) / 2;
        _vm.ZoomText = "适应窗口";
        ApplyView();
    }

    private void ApplyActual()
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        _mode = ZoomMode.Actual;
        _viewScale = 1.0;
        _viewTx = (ImageArea.ActualWidth - img.PixelWidth) / 2;
        _viewTy = (ImageArea.ActualHeight - img.PixelHeight) / 2;
        _vm.ZoomText = "100%";
        ApplyView();
    }

    private void ApplyFree()
    {
        if (_vm.LargeImage == null) return;
        _vm.ZoomText = $"自由缩放 {_viewScale * 100:F0}%";
        ApplyView();
    }

    /// <summary>
    /// 把"虚拟画布"（_viewScale/_viewTx/_viewTy 描述整张图在屏幕上的位置）落到 WPF 元素上。
    ///
    /// 关键点：WPF 对"一个 8000+ 像素的元素 + 负向平移"这条渲染路径会直接画出空白（实测
    /// 8191×5463 的位图平移 -1846px 时有效像素 0%），所以大图放大时不能把整图元素塞进窗口，
    /// 而是按当前视口裁一块（CroppedBitmap）来显示，并相应补偿平移量。
    /// </summary>
    private void ApplyView()
    {
        var src = _vm.LargeImage;
        if (src == null) return;
        double s = _viewScale;
        ImgScale.ScaleX = s;
        ImgScale.ScaleY = s;

        double dispW = src.PixelWidth * s;
        double dispH = src.PixelHeight * s;
        // 显示尺寸不大时直接用整张图（截图/小图/缩放到很小的情况）
        if (dispW <= MaxDirectDisplayPixels && dispH <= MaxDirectDisplayPixels)
        {
            if (!ReferenceEquals(MainImage.Source, src)) MainImage.Source = src;
            // 关键：显式给元素尺寸（Stretch=Fill + 宽高=位图像素），否则父容器会把元素限制成视口大小，
            // Stretch=None/受限尺寸下只会画出源图左上角一块，平移就"看不到图"。
            MainImage.Width = src.PixelWidth;
            MainImage.Height = src.PixelHeight;
            _cropRect = Int32Rect.Empty;
            ImgTranslate.X = _viewTx;
            ImgTranslate.Y = _viewTy;
            return;
        }

        // 视口在图像像素坐标里的范围（外扩一圈，减少拖动时反复裁剪）
        double viewW = Math.Max(1, ImageArea.ActualWidth);
        double viewH = Math.Max(1, ImageArea.ActualHeight);
        double ix = -_viewTx / s;
        double iy = -_viewTy / s;
        const int margin = 400;
        int cx = (int)Math.Max(0, Math.Floor(ix) - margin);
        int cy = (int)Math.Max(0, Math.Floor(iy) - margin);
        int right = (int)Math.Min(src.PixelWidth, Math.Ceiling(ix + viewW / s) + margin);
        int bottom = (int)Math.Min(src.PixelHeight, Math.Ceiling(iy + viewH / s) + margin);
        int cw = Math.Max(1, right - cx);
        int ch = Math.Max(1, bottom - cy);
        var rect = new Int32Rect(cx, cy, cw, ch);
        if (rect != _cropRect || MainImage.Source == null)
        {
            try
            {
                var crop = new CroppedBitmap(src, rect);
                crop.Freeze();
                MainImage.Source = crop;
                _cropRect = rect;
            }
            catch (Exception ex)
            {
                YingShai.Core.Util.AppLog.Error("视口裁剪失败，退回整图显示", ex);
                MainImage.Source = src;
                _cropRect = Int32Rect.Empty;
                ImgTranslate.X = _viewTx;
                ImgTranslate.Y = _viewTy;
                return;
            }
        }
        // 裁剪图的左上角在屏幕上的位置 = 整图平移 + 裁剪原点×缩放
        MainImage.Width = cw;
        MainImage.Height = ch;
        ImgTranslate.X = _viewTx + cx * s;
        ImgTranslate.Y = _viewTy + cy * s;
    }

    private void ZoomAt(Point mouse, double factor)
    {
        var img = _vm.LargeImage;
        if (img == null) return;
        double oldScale = _viewScale;
        double newScale = Math.Clamp(oldScale * factor, 0.02, 12.0);
        if (Math.Abs(newScale - oldScale) < 1e-6) return;
        // 以鼠标位置为中心缩放
        double imgX = (mouse.X - _viewTx) / oldScale;
        double imgY = (mouse.Y - _viewTy) / oldScale;
        _mode = ZoomMode.Free;
        _viewScale = newScale;
        _viewTx = mouse.X - imgX * newScale;
        _viewTy = mouse.Y - imgY * newScale;
        _vm.ZoomText = $"自由缩放 {newScale * 100:F0}%";
        ApplyView();
    }

    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        ApplyWheel(e.GetPosition(ImageArea), e.Delta, alt, ctrl);
        e.Handled = true;
    }

    /// <summary>
    /// 滚轮规则（Photoshop 手感）：Alt 缩放、Ctrl 左右平移、无修饰上下平移。
    /// 抽成独立方法，验收脚本可以直接用确定的参数调它（注入真实滚轮在无桌面环境下不可靠）。
    /// </summary>
    public void ApplyWheel(Point pos, int delta, bool alt, bool ctrl)
    {
        int notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);
        const double panStep = 90;
        if (alt)
        {
            ZoomAt(pos, Math.Pow(1.15, notches));
        }
        else if (ctrl)
        {
            PanBy(notches * panStep, 0);
        }
        else
        {
            PanBy(0, notches * panStep);
        }
    }

    /// <summary>平移画布；在「适应窗口」状态下平移会切到自由模式（避免下一帧被重新适配覆盖）。</summary>
    private void PanBy(double dx, double dy)
    {
        if (_vm.LargeImage == null) return;
        if (_mode == ZoomMode.Fit) _mode = ZoomMode.Free;   // 100% 下平移仍然是 100%，不要切走
        _viewTx += dx;
        _viewTy += dy;
        _vm.ZoomText = Math.Abs(_viewScale - 1.0) < 0.005 ? "100%" : $"自由缩放 {_viewScale * 100:F0}%";
        ApplyView();
        _vm.StatusText = $"滚轮：上下平移 · Ctrl+滚轮：左右平移 · Alt+滚轮：缩放（{_viewScale * 100:F0}%）";
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击大图 = 在「适应窗口」和「100%（像素 1:1）」之间切换（不做标记，防误触）
            e.Handled = true;
            _ = ToggleZoomAsync(e.GetPosition(ImageArea));
            return;
        }
        // 注意：这里先不 CaptureMouse——只有真的开始拖动才捕获。
        // 否则一旦捕获没释放（例如按下后窗口失焦），缩略图条的点击和滚轮会被大图区整个吞掉。
        _panArmed = true;
        _panStart = e.GetPosition(ImageArea);
        _panOrigin = new Vector(_viewTx, _viewTy);
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (_panArmed && !_panning && e.LeftButton == MouseButtonState.Pressed)
        {
            var p0 = e.GetPosition(ImageArea);
            if (Math.Abs(p0.X - _panStart.X) + Math.Abs(p0.Y - _panStart.Y) > 3)
            {
                _panning = true;
                ImageArea.CaptureMouse();
                ImageArea.Cursor = Cursors.SizeAll;
            }
        }
        if (!_panning) return;
        var p = e.GetPosition(ImageArea);
        _viewTx = _panOrigin.X + (p.X - _panStart.X);
        _viewTy = _panOrigin.Y + (p.Y - _panStart.Y);
        if (_mode == ZoomMode.Fit) _mode = ZoomMode.Free;
        ApplyView();
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        _panArmed = false;
        if (!_panning) return;
        _panning = false;
        ImageArea.ReleaseMouseCapture();
        ImageArea.Cursor = Cursors.Arrow;
    }

    /// <summary>捕获丢失（窗口失焦等）时复位拖动状态，避免卡在"一直拖动"。</summary>
    private void OnImageLostCapture(object sender, MouseEventArgs e)
    {
        _panning = false;
        _panArmed = false;
        ImageArea.Cursor = Cursors.Arrow;
    }

    private void OnImageAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mode == ZoomMode.Fit) ApplyFit();
        else ApplyView();   // 视口大小变了，裁剪范围也要跟着变
    }

    private void OnZoomFit(object sender, RoutedEventArgs e) => ApplyFit();

    /// <summary>
    /// 双击切换：100%（像素 1:1，必要时后台全解原图）↔ 适应窗口。
    /// 放大时以双击点为锚，让光标下那块内容留在光标位置。
    /// </summary>
    private async Task ToggleZoomAsync(Point anchor)
    {
        if (_vm.LargeImage == null) return;
        if (_mode == ZoomMode.Actual)
        {
            ApplyFit();
            return;
        }

        // 记录双击点落在图的哪个相对位置（0~1），用于放大后把该点锚回鼠标处
        var item = _vm.Current;
        var cur = _vm.LargeImage;
        double shownW = cur.PixelWidth * _viewScale;
        double shownH = cur.PixelHeight * _viewScale;
        double fracX = shownW > 0 ? Math.Clamp((anchor.X - _viewTx) / shownW, 0, 1) : 0.5;
        double fracY = shownH > 0 ? Math.Clamp((anchor.Y - _viewTy) / shownH, 0, 1) : 0.5;

        if (_vm.FullImage == null || item == null || _fullResFor != item.FullPath) await EnsureFullResAsync();
        var full = _vm.FullImage;
        // 兜底：全解失败，或者这份缓存属于别的文件（曾经踩过：换图后沿用上一张的全解图），都退回适应窗口
        if (full == null || item == null || _fullResFor != item.FullPath) { ApplyFit(); return; }

        _vm.LargeImage = full;
        _mode = ZoomMode.Actual;
        _viewScale = 1.0;
        _viewTx = anchor.X - fracX * full.PixelWidth;
        _viewTy = anchor.Y - fracY * full.PixelHeight;
        _vm.ZoomText = "100%";
        _vm.StatusText = $"100% 像素 1:1（{full.PixelWidth}×{full.PixelHeight}），再双击回到适应窗口";
        ApplyView();
    }

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
        {
            Session.SetCurrent(item);
            YingShai.Core.Util.AppLog.Info($"单击缩略图：{item.Name}（SelectionChanged）");
        }
    }

    /// <summary>
    /// 兜底：鼠标按下就先定位到被点中的缩略图，不依赖 ListBox 的选中机制
    /// （个别情况下（容器回收/焦点设置）选中事件可能不触发，这里直接看命中元素）。
    /// </summary>
    private void OnThumbPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = ResolveThumbItem(e.OriginalSource as DependencyObject);
        if (item == null)
        {
            YingShai.Core.Util.AppLog.Info("缩略图点击：未命中条目（可能点在滚动条/空白）");
            return;
        }
        if (ReferenceEquals(Session.Current, item)) return;
        Session.SetCurrent(item);
        YingShai.Core.Util.AppLog.Info($"单击缩略图：{item.Name}（PreviewMouseDown 兜底）");
    }

    private static ImageItem? ResolveThumbItem(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ListBoxItem lbi && lbi.DataContext is ImageItem it) return it;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
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
        $"src={(int)(_vm.LargeImage?.PixelWidth ?? 0)}x{(int)(_vm.LargeImage?.PixelHeight ?? 0)} " +
        $"elem={MainImage.Width:F0}x{MainImage.Height:F0} render={MainImage.RenderSize.Width:F0}x{MainImage.RenderSize.Height:F0} " +
        $"scale={ImgScale.ScaleX:F3} tx={ImgTranslate.X:F1} ty={ImgTranslate.Y:F1} " +
        $"crop={(Int32Rect.Empty == _cropRect ? "无" : $"{_cropRect.X},{_cropRect.Y} {_cropRect.Width}x{_cropRect.Height}")}";

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

    /// <summary>缩略图在屏幕上的中心点（物理像素），供真实鼠标点击验收用。</summary>
    public (double X, double Y)? GetThumbItemCenterOnScreen(ImageItem item)
    {
        ThumbList.ScrollIntoView(item);
        ThumbList.UpdateLayout();
        if (ThumbList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) return null;
        var p = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
        return (p.X, p.Y);
    }

    /// <summary>大图区域尺寸（验收用）。</summary>
    public Size GetImageAreaRect() => new(ImageArea.ActualWidth, ImageArea.ActualHeight);

    /// <summary>验收：把大图区渲染出来，统计"明显亮于背景"的像素占比（判断大图到底有没有画出来）。</summary>
    public double MeasureImageAreaBrightness()
    {
        int w = (int)Math.Max(1, ImageArea.ActualWidth), h = (int)Math.Max(1, ImageArea.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ImageArea);
        int stride = w * 4;
        var buf = new byte[stride * h];
        rtb.CopyPixels(buf, stride, 0);
        int bright = 0;
        for (int i = 0; i < buf.Length; i += 4)
        {
            int b = buf[i], g = buf[i + 1], r = buf[i + 2];
            if (r + g + b > 120) bright++;   // 背景 #101014 三通道和只有 0x38=56
        }
        return 100.0 * bright / (w * h);
    }


    /// <summary>大图区域中心的屏幕坐标（物理像素）。</summary>
    public (double X, double Y) GetImageAreaCenterOnScreen()
    {
        var p = ImageArea.PointToScreen(new Point(ImageArea.ActualWidth / 2, ImageArea.ActualHeight / 2));
        return (p.X, p.Y);
    }

    /// <summary>当前缩放/平移状态（验收断言用）。</summary>
    public (string Text, double Scale, double Tx, double Ty) ZoomState
        => (_vm.ZoomText, _viewScale, _viewTx, _viewTy);

    /// <summary>验收：当前 100% 全解缓存属于哪个文件（null = 没有缓存）。</summary>
    public string? FullResOwnerForTest => _fullResFor;

    /// <summary>重置为适应窗口（验收用）。</summary>
    public void ResetFit() => ApplyFit();

    /// <summary>验收：在 100% 状态下平移，用来验证"平移动的是画布而不是被裁死的画面"。</summary>
    public void PanForTest(double dx, double dy) => PanBy(dx, dy);

    /// <summary>
    /// 验收：在缩略图容器上真正抛一次 PreviewMouseLeftButtonDown 路由事件，
    /// 走的是和鼠标单击完全相同的处理链（OnThumbPreviewMouseDown → 命中元素 → 切图）。
    /// </summary>
    public bool RaiseThumbPreviewDown(ImageItem item)
    {
        ThumbList.ScrollIntoView(item);
        ThumbList.UpdateLayout();
        // 虚拟化回收时容器可能刚被复用、DataContext 还没换成目标项，要等它绑定好再抛事件
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (ThumbList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c
                && ReferenceEquals(c.DataContext, item))
            {
                // 从 ListBox 发起（真实鼠标点击也是这条路由：Window → ListBox → 条目），
                // Source 指向条目容器，等价于"点在条目 c 上"
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = c
                };
                ThumbList.RaiseEvent(args);
                return true;
            }
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            ThumbList.UpdateLayout();
        }
        return false;
    }

    /// <summary>验收：模拟在大图中央双击，返回切换后的缩放状态。</summary>
    public async Task<(string Text, double Scale)> TestDoubleClickZoomAsync()
    {
        await ToggleZoomAsync(new Point(ImageArea.ActualWidth / 2, ImageArea.ActualHeight / 2));
        return (_vm.ZoomText, ImgScale.ScaleX);
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
