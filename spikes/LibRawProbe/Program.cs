// spike 入口：s1 <file> [repeat] / s2 <dir> / probe <file>
using LibRawProbe;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "s1":
        S1.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
        break;
    case "s2":
        S2.Run(args[1]);
        break;
    default:
        Console.WriteLine("usage: LibRawProbe s1 <rawfile> [repeat] | s2 <dir>");
        break;
}
