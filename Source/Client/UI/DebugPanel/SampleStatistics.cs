using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplayer.Client.DebugUi;

internal static class SampleStatistics
{
    public static float Percentile(IEnumerable<float> values, double percentile)
    {
        var sortedValues = values.OrderBy(value => value).ToList();
        if (sortedValues.Count == 0)
            return float.NaN;

        int rank = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count);
        int clampedRank = Math.Min(Math.Max(rank, 1), sortedValues.Count);
        return sortedValues[clampedRank - 1];
    }

    public static float Percentile(IEnumerable<int> values, double percentile)
        => Percentile(values.Select(value => (float)value), percentile);

    public static string FormatMs(float value)
        => float.IsNaN(value) ? "N/A" : $"{value:F0}ms";
}
