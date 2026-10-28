namespace HyperPlot.Tests;

/// <summary>
/// The answers of the pyramid and of M4 computed by plain loops over every element, with no pyramid (design §10: brute force).
/// </summary>
internal static class Reference
{
    public readonly record struct Range(bool Found, double Min, double Max, long Count, bool HasNaN);

    public readonly record struct Point(long Index, double X, double Y, M4Roles Roles, bool BreakBefore);

    public static Range MinMax(double[] y, long start, long end)
    {
        var found = false;
        double min = 0;
        double max = 0;
        long count = 0;
        var hasNaN = false;
        for (var i = start; i < end; i++)
        {
            var v = y[i];
            if (double.IsNaN(v))
            {
                hasNaN = true;
                continue;
            }

            count++;
            min = found ? Math.Min(min, v) : v;
            max = found ? Math.Max(max, v) : v;
            found = true;
        }

        return new Range(found, found ? min : 0, found ? max : 0, count, hasNaN);
    }

    /// <summary>The least i with x[i] ≥ b, by a walk from the start.</summary>
    public static long LowerBound(double[] x, double b, long from)
    {
        var i = from;
        while (i < x.Length && x[i] < b)
        {
            i++;
        }

        return i;
    }

    public static List<Point> M4(double[] x, double[] y, double[] boundaries, M4Gaps gaps)
    {
        var points = new List<Point>();
        var pending = false;

        void Emit(long i, M4Roles roles)
        {
            var v = y[i];
            if (double.IsNaN(v))
            {
                if (gaps == M4Gaps.Break)
                {
                    pending = points.Count > 0;
                }

                return;
            }

            if (points.Count > 0 && points[^1].Index == i)
            {
                points[^1] = points[^1] with { Roles = points[^1].Roles | roles };
                return;
            }

            points.Add(new Point(i, x[i], v, roles, pending));
            pending = false;
        }

        long FirstNumber(long from, long to)
        {
            for (var i = from; i < to; i++)
            {
                if (!double.IsNaN(y[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        long LastNumber(long from, long to)
        {
            for (var i = to - 1; i >= from; i--)
            {
                if (!double.IsNaN(y[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        var start = LowerBound(x, boundaries[0], 0);
        if (start > 0)
        {
            var before = gaps == M4Gaps.Break ? start - 1 : LastNumber(0, start);
            if (before >= 0)
            {
                Emit(before, M4Roles.Before);
            }
        }

        for (var k = 1; k < boundaries.Length; k++)
        {
            var end = LowerBound(x, boundaries[k], start);
            var n = end - start;
            if (n > 0 && n <= 4)
            {
                for (var i = start; i < end; i++)
                {
                    Emit(i, M4Roles.Raw);
                }
            }
            else if (n > 4)
            {
                long minIndex = -1;
                long maxIndex = -1;
                for (var i = start; i < end; i++)
                {
                    var v = y[i];
                    if (double.IsNaN(v))
                    {
                        continue;
                    }

                    if (minIndex < 0 || v < y[minIndex])
                    {
                        minIndex = i;
                    }

                    if (maxIndex < 0 || v > y[maxIndex])
                    {
                        maxIndex = i;
                    }
                }

                if (minIndex < 0)
                {
                    if (gaps == M4Gaps.Break)
                    {
                        pending = points.Count > 0;
                    }
                }
                else
                {
                    var first = gaps == M4Gaps.Break ? start : FirstNumber(start, end);
                    var last = gaps == M4Gaps.Break ? end - 1 : LastNumber(start, end);
                    var roles = new (long Index, M4Roles Role)[]
                    {
                        (first, M4Roles.First),
                        (minIndex, M4Roles.Min),
                        (maxIndex, M4Roles.Max),
                        (last, M4Roles.Last),
                    };
                    foreach (var (index, role) in roles.OrderBy(r => r.Index))
                    {
                        Emit(index, role);
                    }
                }
            }

            start = end;
        }

        if (start < x.Length)
        {
            var after = gaps == M4Gaps.Break ? start : FirstNumber(start, x.Length);
            if (after >= 0)
            {
                Emit(after, M4Roles.After);
            }
        }

        return points;
    }
}
