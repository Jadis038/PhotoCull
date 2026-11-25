namespace VirtList;

/// <summary>三种被测场景。</summary>
public enum ScenarioKind
{
    /// <summary>虚拟化 + 回收（正式 UI 应该用的组合）。</summary>
    VirtualizedRecycling,

    /// <summary>虚拟化 + 标准模式（容器用完即弃，用于对照回收带来的收益）。</summary>
    VirtualizedStandard,

    /// <summary>关闭虚拟化（普通横向 StackPanel + 像素滚动，朴素实现的对照组）。</summary>
    NonVirtualized,
}

/// <summary>命令行参数。</summary>
public sealed class Options
{
    public ScenarioKind Scenario { get; private set; } = ScenarioKind.VirtualizedRecycling;

    public int Items { get; private set; } = 2000;

    public double Width { get; private set; } = 1280;

    public double Height { get; private set; } = 360;

    /// <summary>每帧水平推进的像素数；0 表示用「视口宽度 / 4」。</summary>
    public double Step { get; private set; }

    /// <summary>来回趟数：1 趟 = 向右滚到尾部再滚回起点（2 次平移）。</summary>
    public int Trips { get; private set; } = 2;

    /// <summary>真实帧回调（CompositionTarget.Rendering）采样帧数。</summary>
    public int RealFrames { get; private set; } = 90;

    /// <summary>强制离屏渲染（不创建窗口，只做 Measure/Arrange/RenderTargetBitmap）。</summary>
    public bool Offscreen { get; private set; }

    /// <summary>帧图 PNG 输出路径；null 表示按场景默认名输出到 out\ 目录。</summary>
    public string? PngPath { get; private set; }

    public bool ShowHelp { get; private set; }

    public double EffectiveStep => Step > 0 ? Step : Width / 4.0;

    public string ScenarioKey => Scenario switch
    {
        ScenarioKind.VirtualizedRecycling => "virt",
        ScenarioKind.VirtualizedStandard => "standard",
        _ => "nonvirt",
    };

    public string ScenarioLabel => Scenario switch
    {
        ScenarioKind.VirtualizedRecycling => "横向虚拟化 + 容器回收 (VirtualizingStackPanel / IsVirtualizing=True / Recycling / ScrollUnit=Pixel)",
        ScenarioKind.VirtualizedStandard => "横向虚拟化 + 标准模式 (VirtualizationMode=Standard，不回收)",
        _ => "关闭虚拟化 (普通 StackPanel + 像素滚动，2000 个容器全实例化)",
    };

    public static Options Parse(string[] args)
    {
        var opt = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--scenario":
                case "-s":
                    opt.Scenario = (Next() ?? "virt").ToLowerInvariant() switch
                    {
                        "virt" or "recycle" or "recycling" => ScenarioKind.VirtualizedRecycling,
                        "standard" or "std" => ScenarioKind.VirtualizedStandard,
                        "nonvirt" or "off" or "stack" => ScenarioKind.NonVirtualized,
                        var other => throw new ArgumentException($"未知场景: {other}"),
                    };
                    break;
                case "--items":
                case "-n":
                    opt.Items = int.Parse(Next() ?? throw new ArgumentException("--items 需要数字"));
                    break;
                case "--width":
                    opt.Width = double.Parse(Next() ?? throw new ArgumentException("--width 需要数字"));
                    break;
                case "--height":
                    opt.Height = double.Parse(Next() ?? throw new ArgumentException("--height 需要数字"));
                    break;
                case "--step":
                    opt.Step = double.Parse(Next() ?? throw new ArgumentException("--step 需要数字"));
                    break;
                case "--trips":
                    opt.Trips = int.Parse(Next() ?? throw new ArgumentException("--trips 需要数字"));
                    break;
                case "--real-frames":
                    opt.RealFrames = int.Parse(Next() ?? throw new ArgumentException("--real-frames 需要数字"));
                    break;
                case "--offscreen":
                    opt.Offscreen = true;
                    break;
                case "--png":
                    opt.PngPath = Next() ?? throw new ArgumentException("--png 需要路径");
                    break;
                case "--help":
                case "-h":
                case "/?":
                    opt.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数: {args[i]}");
            }
        }

        if (opt.Items < 1 || opt.Trips < 1)
        {
            throw new ArgumentException("--items 与 --trips 必须 >= 1");
        }

        return opt;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            影·筛 S4 spike：横向虚拟化缩略图列表性能测量

            用法:
              VirtList.exe --scenario virt|standard|nonvirt [选项]

            选项:
              -s, --scenario <virt|standard|nonvirt>  被测场景（默认 virt）
              -n, --items <数量>                      数据项数量（默认 2000）
                  --width <像素>                      窗口/视口宽度（默认 1280）
                  --height <像素>                     窗口/视口高度（默认 360）
                  --step <像素/帧>                     每帧水平推进量（默认视口宽度/4）
                  --trips <趟数>                       来回趟数（默认 2，即 4 次平移）
                  --real-frames <帧数>                 真实帧回调采样帧数（默认 90）
                  --offscreen                          强制离屏渲染（Measure+Arrange+RTB）
                  --png <路径>                         帧图输出路径
            """);
    }
}
