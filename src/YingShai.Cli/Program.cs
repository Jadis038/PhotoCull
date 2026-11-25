using System.Text;
using YingShai.Core.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
if (args.Length == 0)
{
    Console.WriteLine("用法：YingShai.Cli --selftest | --bench <文件夹> [--csv x.csv] [--cold] [--nowarm] | --version");
    return 1;
}
return CliHost.Run(args);
