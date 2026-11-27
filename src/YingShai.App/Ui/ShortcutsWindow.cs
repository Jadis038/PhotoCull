using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YingShai.App.Ui;

/// <summary>「快捷键说明」窗口：展示全部键盘与鼠标组合操作。</summary>
public static class ShortcutsWindow
{
    private static readonly (string Section, (string Keys, string Desc)[] Rows)[] Sections =
    {
        ("键盘 — 筛选", new[]
        {
            ("X / 空格", "标记 / 取消标记当前图（画面停留，再按可切换）"),
            ("← / →", "上一张 / 下一张"),
            ("Ctrl+Z", "撤销上一步操作"),
        }),
        ("键盘 — 星级与保护", new[]
        {
            ("1 ~ 5（含小键盘）", "给当前图标 1~5 星；再按同一数字清除"),
            ("S", "保护（锁定）当前图 / 解锁 —— 受保护的图不会被取消筛选"),
        }),
        ("键盘 — 大图", new[]
        {
            ("↑ / ↓", "大图放大 / 缩小（约 10%，超过 100% 自动换原图细节）"),
            ("Enter", "全屏显示（不覆盖任务栏）/ 还原，可快速连按"),
            ("0", "大图适应窗口"),
            ("+ / -", "大图放大 / 缩小"),
        }),
        ("键盘 — 其它", new[]
        {
            ("Ctrl+O", "打开文件夹"),
            ("F5", "刷新（重新核对索引与「筛选」内容）"),
            ("Home / End", "跳到第一张 / 最后一张"),
        }),
        ("鼠标与组合键", new[]
        {
            ("单击缩略图", "在上方查看这张图"),
            ("双击缩略图", "标记 / 取消标记"),
            ("双击大图", "100% 像素 1:1 ↔ 适应窗口（以双击点为锚）"),
            ("左键拖动大图", "平移画面"),
            ("滚轮", "大图上下平移"),
            ("Ctrl+滚轮", "大图左右平移"),
            ("Alt+滚轮", "以光标为中心缩放"),
            ("右键缩略图", "菜单：移入筛选 / 取消筛选 / 在资源管理器中显示"),
        }),
        ("帧针模式（工具栏勾选）", new[]
        {
            ("滚动缩略图条", "中线（帧针）对准哪张，上方就显示哪张"),
            ("空格 / 双击", "标记帧针当前指向的图"),
        }),
    };

    public static void Show(Window? owner)
    {
        var win = new Window
        {
            Title = "快捷键说明 — 影·筛",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29)),
            Owner = owner
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "快捷键说明",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });

        foreach (var (section, rows) in Sections)
        {
            root.Children.Add(new TextBlock
            {
                Text = section,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xA8, 0xFF)),
                Margin = new Thickness(0, 10, 0, 4)
            });
            foreach (var (keys, desc) in rows)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var k = new TextBlock { Text = keys, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)), FontSize = 12.5 };
                var d = new TextBlock { Text = desc, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(k, 0);
                Grid.SetColumn(d, 1);
                row.Children.Add(k);
                row.Children.Add(d);
                root.Children.Add(row);
            }
        }

        var close = new Button
        {
            Content = "关闭",
            Width = 96,
            Height = 28,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true
        };
        root.Children.Add(close);

        win.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        win.Show();
    }
}
