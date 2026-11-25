using System.Windows;

namespace VirtList;

/// <summary>
/// 入口。WPF 的布局/渲染管线需要 Application 实例（字体、合成等全局资源），
/// 但这里不调 Application.Run：帧循环由 Bench 手动驱动，
/// 这样测量点完全可控，也便于在无桌面的会话里跑。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("参数错误: " + ex.Message);
            Options.PrintHelp();
            return 1;
        }

        if (opt.ShowHelp)
        {
            Options.PrintHelp();
            return 0;
        }

        // 显式关闭模式：进程由方法返回自然结束，不依赖 Application.Run
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        try
        {
            _ = app;
            new Bench(opt).Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("运行失败: " + ex);
            return 2;
        }
    }
}
