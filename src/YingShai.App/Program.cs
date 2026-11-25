using System.Windows;
using YingShai.App.Ui;
using YingShai.Core.Cli;

namespace YingShai.App;

/// <summary>程序入口：命令行模式（自检/基准）优先，其余进入 WPF 界面。</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CliHost.IsConsoleMode(args))
        {
            CliHost.EnsureConsole();
            return CliHost.Run(args);
        }

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        app.Startup += (_, _) => Ui.AppController.Start(args);
        return app.Run();
    }
}
