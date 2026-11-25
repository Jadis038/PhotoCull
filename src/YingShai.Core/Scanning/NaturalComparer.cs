using System.Globalization;
using System.Text;

namespace YingShai.Core.Scanning;

/// <summary>
/// 文件名自然排序：IMG_2 排在 IMG_10 前面（Windows 资源管理器风格）。
/// 规则：按「数字段 vs 数字段」比较数值，其它按字符比较（忽略大小写），
/// 完全相同的键再按原字符串 Ordinal 比较，保证排序稳定、可复现。
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            bool da = char.IsDigit(ca), db = char.IsDigit(cb);

            if (da && db)
            {
                // 跳过前导 0
                int si = i, sj = j;
                while (i < a.Length && a[i] == '0') i++;
                while (j < b.Length && b[j] == '0') j++;
                int ni = i, nj = j;
                while (ni < a.Length && char.IsDigit(a[ni])) ni++;
                while (nj < b.Length && char.IsDigit(b[nj])) nj++;
                int lenA = ni - i, lenB = nj - j;
                if (lenA != lenB) return lenA < lenB ? -1 : 1;
                int cmp = string.CompareOrdinal(a, i, b, j, lenA);
                if (cmp != 0) return cmp;
                i = ni; j = nj;
                // 全 0 的情况：长度视为 0，继续比较后面的字符
                if (lenA == 0) { i = si; j = sj; }
                continue;
            }

            if (char.ToUpperInvariant(ca) != char.ToUpperInvariant(cb))
                return char.ToUpperInvariant(ca) < char.ToUpperInvariant(cb) ? -1 : 1;
            i++; j++;
        }

        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return string.CompareOrdinal(a, b);
    }

    /// <summary>生成可比较的规范化键（也用于测试/调试）。</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        int i = 0;
        while (i < name.Length)
        {
            if (char.IsDigit(name[i]))
            {
                int start = i;
                while (i < name.Length && char.IsDigit(name[i])) i++;
                var digits = name.Substring(start, i - start).TrimStart('0');
                if (digits.Length == 0) digits = "0";
                sb.Append('\u0001').Append(digits.PadLeft(12, '0'));
            }
            else
            {
                sb.Append(char.ToUpperInvariant(name[i]));
                i++;
            }
        }
        return sb.ToString();
    }
}
