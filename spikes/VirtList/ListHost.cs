using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace VirtList;

/// <summary>
/// 把「横向缩略图条」搭出来：ListBox（自带滚动条）→ 横向 VirtualizingStackPanel → ThumbCell。
/// <para>
/// 实测踩坑记录（重要）：<b>ItemsControl 外面套一个 Shell 里的外部 ScrollViewer 是虚拟化不起来的</b>。
/// 外部 ScrollViewer 的 Content 是 ItemsControl 本身，而 ItemsControl 不实现 IScrollInfo，
/// ScrollContentPresenter 只能按「无限宽」测量内容，VirtualizingStackPanel 拿不到 ScrollOwner 和视口，
/// 只能把 2000 个容器全实例化（本 spike 的 A 组对照实测就是 2000 个）。
/// 因此这里改用 ListBox：它的 ControlTemplate 里自带 ScrollViewer + ItemsPresenter，
/// 面板能正确拿到视口，虚拟化才生效（实测可降到个位数~几十个容器）。
/// </para>
/// <para>
/// 两种宿主模式：
/// 1) 真窗口模式（默认，本机可用）：new Window + Show()，由 WPF 合成器真正上屏；
/// 2) 离屏模式（--offscreen，或 Show() 失败时）：不创建窗口，直接对根元素
///    Measure/Arrange，再用 RenderTargetBitmap 光栅化，布局/虚拟化管线与真窗口一致。
/// </para>
/// </summary>
public sealed class ListHost : IDisposable
{
    /// <summary>单元模板：每个数据项包一个 ThumbCell。用 XamlReader 解析，避免过时的 FrameworkElementFactory。</summary>
    private const string ThumbCellTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:local="clr-namespace:VirtList;assembly=VirtList">
          <local:ThumbCell/>
        </DataTemplate>
        """;

    /// <summary>
    /// 横向虚拟化面板。三个虚拟化开关直接写在面板上：
    /// 它们是附加属性，写在 ListBox 上不会自动传到面板（实测：面板仍是 ScrollUnit=Item / Standard）。
    /// </summary>
    private const string VirtualizingPanelXaml = """
        <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <VirtualizingStackPanel Orientation="Horizontal"
                                  VirtualizingPanel.IsVirtualizing="True"
                                  VirtualizingPanel.VirtualizationMode="Recycling"
                                  VirtualizingPanel.ScrollUnit="Pixel"/>
        </ItemsPanelTemplate>
        """;

    private const string PlainPanelXaml = """
        <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Orientation="Horizontal"/>
        </ItemsPanelTemplate>
        """;

    /// <summary>去掉 ListBoxItem 的选中底色/内边距，让每个单元就是干净的 168×196。</summary>
    private const string ListBoxItemStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
          <Setter Property="Padding" Value="0"/>
          <Setter Property="Margin" Value="0"/>
          <Setter Property="HorizontalContentAlignment" Value="Left"/>
          <Setter Property="VerticalContentAlignment" Value="Top"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <ContentPresenter/>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    private readonly double _width;
    private readonly double _height;

    private ListHost(Window? window, Border root, ListBox list, ScrollViewer scroller, Panel itemsHost,
        double width, double height, string hostMode, string? fallbackNote)
    {
        Window = window;
        Root = root;
        List = list;
        Scroller = scroller;
        ItemsHost = itemsHost;
        _width = width;
        _height = height;
        HostMode = hostMode;
        FallbackNote = fallbackNote;
    }

    public Window? Window { get; }

    public Border Root { get; }

    public ListBox List { get; }

    public ScrollViewer Scroller { get; }

    /// <summary>真正承载容器的那块面板（可视化树的 ItemsHost）。</summary>
    public Panel ItemsHost { get; }

    /// <summary>「真窗口」或「离屏」。</summary>
    public string HostMode { get; }

    /// <summary>离屏回退原因（真窗口模式为 null）。</summary>
    public string? FallbackNote { get; }

    /// <summary>离屏模式没有 PresentationSource，也就没有 CompositionTarget.Rendering 帧回调。</summary>
    public bool HasRealFrames => Window is not null;

    public static ListHost Create(Options opt, IReadOnlyList<ThumbItem> items)
    {
        var isVirtualized = opt.Scenario != ScenarioKind.NonVirtualized;

        var itemTemplate = (DataTemplate)XamlReader.Parse(ThumbCellTemplateXaml);
        var panelTemplate = (ItemsPanelTemplate)XamlReader.Parse(isVirtualized ? VirtualizingPanelXaml : PlainPanelXaml);
        var containerStyle = (Style)XamlReader.Parse(ListBoxItemStyleXaml);

        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = itemTemplate,
            ItemsPanel = panelTemplate,
            ItemContainerStyle = containerStyle,
            Focusable = false,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.White,
            // 缩略图条不需要选择高亮，但保留单选项语义（正式 UI 里就是选中态）
            SelectionMode = SelectionMode.Single,
        };

        // —— 虚拟化开关（面板上已经写了，这里再显式补一遍，便于对照阅读）——
        if (isVirtualized)
        {
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list,
                opt.Scenario == ScenarioKind.VirtualizedRecycling
                    ? VirtualizationMode.Recycling
                    : VirtualizationMode.Standard);
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
            ScrollViewer.SetCanContentScroll(list, true);
        }
        else
        {
            // 关闭虚拟化：普通 StackPanel + 按像素滚动（朴素实现就是这样）
            VirtualizingPanel.SetIsVirtualizing(list, false);
            ScrollViewer.SetCanContentScroll(list, false);
        }

        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        var root = new Border
        {
            Child = list,
            Background = Brushes.White,
            Width = opt.Width,
            Height = opt.Height,
        };

        Window? window = null;
        string? fallback = null;
        if (!opt.Offscreen)
        {
            window = new Window
            {
                Title = $"影·筛 S4 spike - {opt.ScenarioKey}",
                Width = opt.Width,
                Height = opt.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = root,
                Left = 0,
                Top = 0,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
            }
            catch (Exception ex)
            {
                // 没有桌面会话 / 无窗口站时回退到离屏渲染
                fallback = $"窗口创建失败（{ex.GetType().Name}: {ex.Message}），已自动回退离屏渲染";
                try
                {
                    window.Close();
                }
                catch
                {
                    // 忽略：窗口本来就没建起来
                }

                window = null;
                OffscreenLayout(root, opt.Width, opt.Height);
            }
        }
        else
        {
            OffscreenLayout(root, opt.Width, opt.Height);
        }

        var scroller = FindScroller(list)
            ?? throw new InvalidOperationException("找不到 ListBox 内部的 ScrollViewer（模板未应用？）");

        // 关闭虚拟化时，必须让内部 ScrollViewer 按像素滚动；否则它会在项单位里滚动，
        // ScrollToHorizontalOffset(像素) 会被当成项序号。
        if (!isVirtualized)
        {
            scroller.CanContentScroll = false;
        }

        var itemsHost = FindItemsHost(list)
            ?? throw new InvalidOperationException("找不到 ItemsHost 面板（模板未应用？）");

        return new ListHost(window, root, list, scroller, itemsHost, opt.Width, opt.Height,
            window is null ? "离屏" : "真窗口", fallback);
    }

    /// <summary>驱动一帧：改偏移 → 强制同步完成布局（测量/排列/容器增删）。</summary>
    public void ApplyScrollAndLayout(double offset)
    {
        Scroller.ScrollToHorizontalOffset(offset);
        if (Window is not null)
        {
            Window.UpdateLayout();
        }
        else
        {
            OffscreenLayout(Root, _width, _height);
        }
    }

    /// <summary>当前实际实例化的容器数量（ItemsHost 的直接子元素数）。</summary>
    public int RealizedContainerCount => ItemsHost.Children.Count;

    /// <summary>本帧第一个/最后一个已实现容器的数据项序号；无容器时返回 -1。</summary>
    public (int First, int Last) RealizedItemRange()
    {
        var first = -1;
        var last = -1;
        foreach (var child in ItemsHost.Children)
        {
            if (child is FrameworkElement { DataContext: ThumbItem item })
            {
                if (first < 0)
                {
                    first = item.Index;
                }

                last = item.Index;
            }
        }

        return (first, last);
    }

    public void Dispose()
    {
        try
        {
            Window?.Close();
        }
        catch
        {
            // 程序退出阶段，忽略关闭异常
        }
    }

    private static void OffscreenLayout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    /// <summary>在可视树里找 IsItemsHost=true 的 Panel。</summary>
    private static Panel? FindItemsHost(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Panel { IsItemsHost: true } panel)
            {
                return panel;
            }

            var found = FindItemsHost(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>在可视树里找 ListBox 模板里的 ScrollViewer。</summary>
    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            var found = FindScroller(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
