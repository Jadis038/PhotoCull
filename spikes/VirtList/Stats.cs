#nullable enable
using System.Diagnostics;

namespace VirtList;

/// <summary>一组耗时样本的统计结果（毫秒）。分位数用线性插值，样本量都在千级以上，足够稳定。</summary>
public sealed class Stats
{
    private Stats(double[] sorted)
    {
        Count = sorted.Length;
        Min = sorted[0];
        Max = sorted[^1];
        Avg = sorted.Average();
        P50 = Percentile(sorted, 0.50);
        P95 = Percentile(sorted, 0.95);
        P99 = Percentile(sorted, 0.99);
    }

    public int Count { get; }

    public double Min { get; }

    public double Max { get; }

    public double Avg { get; }

    public double P50 { get; }

    public double P95 { get; }

    public double P99 { get; }

    public static Stats From(IEnumerable<double> samples)
    {
        var arr = samples.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
        if (arr.Length == 0)
        {
            throw new ArgumentException("样本为空", nameof(samples));
        }

        Array.Sort(arr);
        return new Stats(arr);
    }

    /// <summary>四舍五入到 0.01 ms 的字符串。</summary>
    public string AvgText => Avg.ToString("F2");

    public string P95Text => P95.ToString("F2");

    public string MaxText => Max.ToString("F2");

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var pos = (sorted.Length - 1) * p;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi)
        {
            return sorted[lo];
        }

        var frac = pos - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}

/// <summary>掉帧统计：给定每帧耗时与预算（默认 16.67ms @60Hz），数出超预算的帧数。</summary>
public static class DropCounter
{
    public const double Budget60 = 1000.0 / 60.0;

    public static (int Over1, int Over2, int Total) Count(IReadOnlyList<double> frameMs, double budget = Budget60)
    {
        var over1 = 0;
        var over2 = 0;
        foreach (var ms in frameMs)
        {
            if (ms > budget)
            {
                over1++;
            }

            if (ms > budget * 2)
            {
                over2++;
            }
        }

        return (over1, over2, frameMs.Count);
    }

    public static string Percent(int part, int total) => total == 0 ? "0.0%" : $"{part * 100.0 / total:F1}%";
}

/// <summary>计时小工具：用 Stopwatch 高精度时间戳换算毫秒。</summary>
public static class Clock
{
    public static long Now => Stopwatch.GetTimestamp();

    public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
