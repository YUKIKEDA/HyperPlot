namespace HyperPlot;

/// <summary>
/// The ticks of a time scale (design §4.6): calendar steps from nanoseconds to years, the finest whose nominal length spans
/// the target spacing and whose labels do not overlap. Instants are nanoseconds from 0001-01-01 in an Int128, so a tick is
/// exact in the column's unit; the calendar is <see cref="DateTime"/>'s. Allocates nothing.
/// </summary>
internal static class TimeTicks
{
    private const long Second = 1_000_000_000;
    private const long Day = 86_400 * Second;
    private const double YearDays = 365.2425;

    private static readonly Int128 MaxInstant = ((Int128)DateTime.MaxValue.Ticks + 1) * 100;

    // The steps, finest first: sub-day steps in nanoseconds, then days, months, and years (design §4.6).
    private static readonly Step[] Steps = MakeSteps();

    /// <summary>Chooses the ticks of a time scale over the axis.</summary>
    /// <exception cref="InvalidOperationException">The domain reaches outside 0001-01-01 to 9999-12-31.</exception>
    public static void Choose(TickAxis axis, TimeScaleSpec scale, bool nice)
    {
        var domain = axis.Domain;
        var unit = UnitNanoseconds(scale.Unit);
        var epoch = (Int128)scale.Epoch.Ticks * 100;
        var origin = epoch + ((Int128)domain.Origin * unit);
        var low = origin + (Int128)Math.Ceiling(domain.Lower * unit);
        var high = origin + (Int128)Math.Floor(domain.Upper * unit);
        if (low < 0 || high >= MaxInstant)
        {
            throw new InvalidOperationException(
                $"The time scale ({scale.Unit} from {scale.Epoch:O}) has the domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}], which reaches outside the years 1 to 9999 of its ticks."
            );
        }

        var span = (domain.Upper - domain.Lower) * unit;
        foreach (var step in Steps)
        {
            // A step finer than the column's unit has ticks between its values.
            if (step.Nanoseconds % unit != 0 && step.Kind == StepKind.SubDay)
            {
                continue;
            }

            if (step.Nominal / span * axis.Length < axis.Spacing)
            {
                continue;
            }

            var (first, last) = (low, high);
            if (nice)
            {
                first = Floor(step, low);
                last = Ceiling(step, high);
            }

            var final = nice ? Widened(first, last, epoch, unit, domain.Origin) : domain;
            axis.ClearCandidate();
            if (first >= 0 && last < MaxInstant)
            {
                for (var tick = Ceiling(step, first); tick <= last && tick < MaxInstant; tick = Next(step, tick))
                {
                    var d = (double)(tick - epoch - ((Int128)final.Origin * unit)) / unit;
                    axis.AddTime(tick, Boundary(tick), axis.PixelOfDifference(d, final));
                }
            }

            if (axis.Count < 2)
            {
                axis.ClearCandidate();
                return;
            }

            if (double.IsNegativeInfinity(axis.LabelTimes(scale, scale.Epoch.Ticks, unit)))
            {
                continue;
            }

            axis.SetFinalDomain(final);
            return;
        }

        axis.ClearCandidate();
    }

    /// <summary>
    /// Returns the edges of the bins of a time x (design §4.1, row G5b), in the column's unit: the finest calendar step of the
    /// ticks whose bins over [<paramref name="min"/>, <paramref name="max"/>] number at most <paramref name="maxBins"/>,
    /// from the step's boundary at or before the least value to the one past the greatest. Months and years are calendar
    /// boundaries, so the bins' widths differ. An empty array when no step fits, or the range leaves the years 1 to 9999.
    /// </summary>
    internal static long[] BinEdges(long min, long max, TimeScaleSpec scale, int maxBins)
    {
        var unit = UnitNanoseconds(scale.Unit);
        var epoch = (Int128)scale.Epoch.Ticks * 100;
        var (low, high) = (epoch + ((Int128)min * unit), epoch + ((Int128)max * unit));
        if (low < 0 || high >= MaxInstant)
        {
            return [];
        }

        foreach (var step in Steps)
        {
            if (step.Kind == StepKind.SubDay && step.Nanoseconds % unit != 0)
            {
                continue;
            }

            // The greatest value lies in the last bin, closed on both sides: on a boundary, it is the last edge.
            var first = Floor(step, low);
            var count = 0;
            var edge = first;
            while (true)
            {
                edge = Next(step, edge);
                count++;
                if (!(edge < high && count <= maxBins))
                {
                    break;
                }
            }

            if (count > maxBins || edge >= MaxInstant)
            {
                continue;
            }

            var edges = new long[count + 1];
            edge = first;
            for (var i = 0; i <= count; i++, edge = Next(step, edge))
            {
                edges[i] = (long)((edge - epoch) / unit);
            }

            return edges;
        }

        return [];
    }

    /// <summary>The coarsest calendar boundary an instant lies on.</summary>
    internal static TimeInterval Boundary(Int128 instant)
    {
        if (instant % 1000 != 0)
        {
            return TimeInterval.Nanosecond;
        }

        if (instant % 1_000_000 != 0)
        {
            return TimeInterval.Microsecond;
        }

        if (instant % Second != 0)
        {
            return TimeInterval.Millisecond;
        }

        if (instant % (60 * Second) != 0)
        {
            return TimeInterval.Second;
        }

        if (instant % (3600 * Second) != 0)
        {
            return TimeInterval.Minute;
        }

        if (instant % Day != 0)
        {
            return TimeInterval.Hour;
        }

        var date = new DateTime((long)(instant / 100));
        return date.Day != 1 ? TimeInterval.Day
            : date.Month != 1 ? TimeInterval.Month
            : TimeInterval.Year;
    }

    /// <summary>The nanoseconds of one unit of a time column.</summary>
    internal static long UnitNanoseconds(TimeUnit unit) =>
        unit switch
        {
            TimeUnit.Nanoseconds => 1,
            TimeUnit.Microseconds => 1_000,
            TimeUnit.Milliseconds => 1_000_000,
            TimeUnit.Seconds => Second,
            _ => 100,
        };

    private static ScaleRange Widened(Int128 first, Int128 last, Int128 epoch, long unit, long origin)
    {
        var low = (double)(first - epoch - ((Int128)origin * unit)) / unit;
        var high = (double)(last - epoch - ((Int128)origin * unit)) / unit;
        return new ScaleRange(origin, low, high).Anchor();
    }

    /// <summary>The first tick of a step at or after an instant.</summary>
    private static Int128 Ceiling(in Step step, Int128 instant)
    {
        var floor = Floor(step, instant);
        return floor == instant ? floor : Next(step, floor);
    }

    /// <summary>The last tick of a step at or before an instant.</summary>
    private static Int128 Floor(in Step step, Int128 instant)
    {
        switch (step.Kind)
        {
            case StepKind.SubDay:
                return FloorDivide(instant, step.Nanoseconds) * step.Nanoseconds;
            case StepKind.Day:
                return FloorDivide(FloorDivide(instant, Day), step.Count) * step.Count * Day;
            default:
                var date = new DateTime((long)(instant / 100));
                var month = step.Kind == StepKind.Month ? date.Month - 1 - ((date.Month - 1) % step.Count) : 0;
                var year = step.Kind == StepKind.Year ? date.Year - (date.Year % step.Count) : date.Year;
                return year < 1 ? 0 : (Int128)new DateTime(year, month + 1, 1).Ticks * 100;
        }
    }

    /// <summary>The tick after a tick.</summary>
    private static Int128 Next(in Step step, Int128 tick)
    {
        switch (step.Kind)
        {
            case StepKind.SubDay:
                return tick + step.Nanoseconds;
            case StepKind.Day:
                return tick + ((Int128)step.Count * Day);
            default:
                var date = new DateTime((long)(tick / 100));
                var months = step.Kind == StepKind.Month ? step.Count : 12 * step.Count;
                var year = step.Kind == StepKind.Year && date.Year % step.Count != 0 ? date.Year - (date.Year % step.Count) : date.Year;
                var total = ((year - 1) * 12) + (date.Month - 1) + months;
                return total / 12 >= 9999 ? MaxInstant : (Int128)new DateTime((total / 12) + 1, (total % 12) + 1, 1).Ticks * 100;
        }
    }

    private static Int128 FloorDivide(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static Step[] MakeSteps()
    {
        var steps = new List<Step>();
        foreach (var unit in (ReadOnlySpan<long>)[1, 1_000, 1_000_000])
        {
            foreach (var count in (ReadOnlySpan<int>)[1, 2, 5, 10, 20, 50, 100, 200, 500])
            {
                steps.Add(new Step(StepKind.SubDay, count, count * unit, count * unit));
            }
        }

        foreach (var unit in (ReadOnlySpan<long>)[Second, 60 * Second])
        {
            foreach (var count in (ReadOnlySpan<int>)[1, 5, 15, 30])
            {
                steps.Add(new Step(StepKind.SubDay, count, count * unit, count * unit));
            }
        }

        foreach (var count in (ReadOnlySpan<int>)[1, 3, 6, 12])
        {
            steps.Add(new Step(StepKind.SubDay, count, count * 3600 * Second, count * 3600 * Second));
        }

        foreach (var count in (ReadOnlySpan<int>)[1, 2, 7])
        {
            steps.Add(new Step(StepKind.Day, count, 0, count * (double)Day));
        }

        foreach (var count in (ReadOnlySpan<int>)[1, 3, 6])
        {
            steps.Add(new Step(StepKind.Month, count, 0, count * YearDays / 12 * Day));
        }

        for (var power = 1; power <= 1000; power *= 10)
        {
            foreach (var count in (ReadOnlySpan<int>)[1, 2, 5])
            {
                steps.Add(new Step(StepKind.Year, count * power, 0, count * power * YearDays * Day));
            }
        }

        return [.. steps];
    }

    private enum StepKind
    {
        SubDay,
        Day,
        Month,
        Year,
    }

    /// <summary>A calendar step: its kind, its count of the kind's unit, its length in ns for sub-day steps, and its nominal length.</summary>
    private readonly record struct Step(StepKind Kind, int Count, long Nanoseconds, double Nominal);
}
