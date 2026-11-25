// 「影·筛」WiX 打包探针：只打印 hello 与参数，用来验证 MSI 装/卸和文件关联命令行。
using System;
using System.Reflection;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("hello from 影·筛 (WiX spike)");
Console.WriteLine($"exe = {Environment.ProcessPath}");
Console.WriteLine($"args count = {args.Length}");
for (int i = 0; i < args.Length; i++)
{
    Console.WriteLine($"  argv[{i}] = {args[i]}");
}
Console.WriteLine($"user = {Environment.UserName}");
Console.WriteLine($"version = {Assembly.GetExecutingAssembly().GetName().Version}");
return 0;
