using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YingShai.Core.Update;

namespace YingShai.App.Ui;

/// <summary>
/// 更新对话框（纯代码构建）：发现新版本 → 展示版本与更新说明 → 用户同意后
/// 下载 MSI（带进度文字）→ 启动安装器 → 应用退出交给安装。
/// </summary>
public static class UpdateDialog
{
    /// <summary>返回 true = 用户同意并已下载完成、安装器已启动。</summary>
    public static async Task<bool> ShowAndInstallAsync(Window owner, UpdateInfo info)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var win = new Window
        {
            Title = "影·筛 更新",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29)),
            Owner = owner
        };

        var titleText = new TextBlock
        {
            Text = $"发现新版本 {info.TagName}（当前 v{UpdateInfo.CurrentVersion.ToString(3)}）",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0xC5, 0x7A)),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var notesBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "（无更新说明）" : TrimNotes(info.Notes),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xE0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)),
            Padding = new Thickness(8),
            FontSize = 12.5
        };

        var statusText = new TextBlock
        {
            Text = "下载完成后将自动启动安装（每用户安装，无需管理员）。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        var okBtn = new Button { Content = "下载并安装", Width = 110, Height = 30, Margin = new Thickness(0, 12, 8, 0), IsDefault = true };
        var noBtn = new Button { Content = "以后再说", Width = 90, Height = 30, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };

        okBtn.Click += async (_, _) =>
        {
            okBtn.IsEnabled = false;
            noBtn.IsEnabled = false;
            statusText.Text = "正在下载…";
            try
            {
                var dest = Path.Combine(Path.GetTempPath(), $"PhotoCull-Setup-{info.LatestVersion}.msi");
                await UpdateChecker.DownloadAsync(info.MsiUrl!, dest,
                    (done, total) => win.Dispatcher.Invoke(() =>
                        statusText.Text = total > 0
                            ? $"正在下载… {done * 100 / Math.Max(1, total)}%（{done / 1048576.0:F1} / {total / 1048576.0:F1} MB）"
                            : $"正在下载… {done / 1048576.0:F1} MB"),
                    CancellationToken.None);
                UpdateChecker.LaunchInstaller(dest);
                tcs.TrySetResult(true);
                win.Close();
            }
            catch (Exception ex)
            {
                statusText.Text = "下载失败：" + ex.Message;
                okBtn.IsEnabled = true;
                noBtn.IsEnabled = true;
            }
        };
        noBtn.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };

        win.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children = { titleText, notesBox, statusText,
                new StackPanel { Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, noBtn } } }
        };

        win.Closed += (_, _) => tcs.TrySetResult(tcs.Task.IsCompleted && tcs.Task.Result);
        win.Show();
        return await tcs.Task;
    }

    private static string TrimNotes(string notes)
    {
        notes = notes.Replace("\r\n", "\n").Trim();
        return notes.Length <= 800 ? notes : notes[..800] + "…";
    }
}
