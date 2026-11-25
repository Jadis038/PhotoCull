using System.Collections.ObjectModel;
using System.IO.Pipes;
using System.Text;

namespace YingShai.App.Ui;

/// <summary>
/// 单实例：第二个进程把命令行参数通过命名管道交给已有窗口，然后退出。
/// 管道名用 ASCII（YingShai.Pipe），显示名仍然是「影·筛」。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\YingShai.SingleInstance";
    private const string PipeName = "YingShai.Pipe.v1";

    private static Mutex? _mutex;

    /// <summary>尝试成为主实例。返回 false 表示已有实例（参数已转发）。</summary>
    public static bool TryBecomePrimary(string[] args, out Mutex mutex)
    {
        mutex = new Mutex(true, MutexName, out bool created);
        _mutex = mutex;
        if (created) return true;

        // 已有实例：转发参数后退出
        try { ForwardToPrimary(args).GetAwaiter().GetResult(); } catch { }
        return false;
    }

    private static async Task ForwardToPrimary(string[] args)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        await client.ConnectAsync(1500).ConfigureAwait(false);
        using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(string.Join("\u0001", args)).ConfigureAwait(false);
    }

    /// <summary>后台线程监听新实例请求。</summary>
    public static void StartServer(Action<string[]> onArgs)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    string? line = reader.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        var parts = line.Split('\u0001', StringSplitOptions.RemoveEmptyEntries);
                        onArgs(parts);
                    }
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "影筛单实例管道" };
        t.Start();
    }
}
