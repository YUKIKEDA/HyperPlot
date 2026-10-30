namespace HyperPlot;

/// <summary>
/// The ticks of log and symlog scales (design §4.6): powers of ten, {1, 2, 5} · 10^k while the domain spans fewer than three
/// decades, every j-th power while the labels do not fit, and the linear choice when the domain spans less than a decade.
/// Symlog adds 0 and the negative side, and keeps |value| ≥ C. Allocates nothing: candidates go into the axis's slots.
/// </summary>
internal static class LogTicks
{
    // The skips of the powers of ten, in order: 1, 2, 3, 5, then 10, 20, 30, 50, … (design §4.6).
    private static readonly int[] Skips = [1, 2, 3, 5];

    private static readonly int[] OneTwoFive = [1, 2, 5];

    private static readonly int[] One = [1];

    /// <summary>Chooses the ticks of a log scale (constant 0) or a symlog scale of a constant C.</summary>
    public static void Choose(TickAxis axis, bool nice, double constant)
    {
        var domain = axis.Domain;
        var low = axis.Inverse(domain.Origin + domain.Lower);
        var high = axis.Inverse(domain.Origin + domain.Upper);
        var linear = constant == 0 ? Math.Log10(high / low) < 1 : Generate(axis, low, high, constant, One, skip: 1) < 2;
        if (linear)
        {
            ExtendedTicks.Choose(axis, 0, low, high, nice);
            return;
        }

        if (Decades(low, high, constant) < 3 && TryCandidate(axis, low, high, constant, OneTwoFive, skip: 1, nice))
        {
            return;
        }

        for (var power = 1; power <= 100_000; power *= 10)
        {
            foreach (var basis in Skips)
            {
                var skip = basis * power;
                if (Generate(axis, low, high, constant, One, skip) < 2)
                {
                    axis.ClearCandidate();
                    return;
                }

                if (TryCandidate(axis, low, high, constant, One, skip, nice))
                {
                    return;
                }
            }
        }

        axis.ClearCandidate();
    }

    /// <summary>The decades the domain spans: of the data for log, of |value| / C on each side for symlog.</summary>
    private static double Decades(double low, double high, double constant)
    {
        if (constant == 0)
        {
            return Math.Log10(high / low);
        }

        var positive = high > constant ? Math.Log10(high / Math.Max(constant, Math.Max(low, 0))) : 0;
        var negative = low < -constant ? Math.Log10(-low / Math.Max(constant, Math.Max(-high, 0))) : 0;
        return positive + negative;
    }

    private static bool TryCandidate(TickAxis axis, double low, double high, double constant, int[] multiples, int skip, bool nice)
    {
        // With nice, the domain widens to the nearest candidate ticks at or outside it, and the candidate is judged there.
        var (widenedLow, widenedHigh) = nice ? Outer(axis, low, high, constant, multiples, skip) : (low, high);
        var count = Generate(axis, widenedLow, widenedHigh, constant, multiples, skip);
        if (count < 2 || count - 1 > axis.Length / axis.Spacing)
        {
            return false;
        }

        var scaleLow = axis.Forward(widenedLow);
        var scaleHigh = axis.Forward(widenedHigh);
        for (var i = 0; i < count; i++)
        {
            axis.SetPixel(i, (axis.Forward(axis.NumberAt(i).Value) - scaleLow) / (scaleHigh - scaleLow) * axis.Length);
        }

        if (double.IsNegativeInfinity(axis.LabelNumbers(sameDecimals: false)))
        {
            return false;
        }

        if (nice)
        {
            axis.SetFinalDomain(ScaleRange.FromDouble(scaleLow, scaleHigh));
        }

        return true;
    }

    /// <summary>The nearest candidate ticks at or outside the domain's ends: the ticks of a range wide enough to hold them.</summary>
    private static (double Low, double High) Outer(TickAxis axis, double low, double high, double constant, int[] multiples, int skip)
    {
        var factor = Math.Pow(10, skip + 1);
        var (wideLow, wideHigh) = constant == 0 ? (low / factor, high * factor) : (low > 0 ? 0 : low * factor, high < 0 ? 0 : high * factor);
        var count = Generate(axis, wideLow, wideHigh, constant, multiples, skip);
        var (below, above) = (low, high);
        for (var i = 0; i < count; i++)
        {
            var value = axis.NumberAt(i).Value;
            if (value <= low)
            {
                below = value;
            }

            if (value >= high)
            {
                above = value;
                break;
            }
        }

        return (below, above);
    }

    /// <summary>
    /// Puts the candidate ticks inside [low, high] into the axis's slots in increasing order, with no pixel yet, and returns
    /// their count: m · 10^k for m in the multiples and k a multiple of the skip, on the positive side for log, on both sides
    /// and 0 for symlog (|value| ≥ C).
    /// </summary>
    private static int Generate(TickAxis axis, double low, double high, double constant, int[] multiples, int skip)
    {
        axis.ClearCandidate();
        var smallest = constant == 0 ? (int)Math.Floor(Math.Log10(low)) - 1 : (int)Math.Floor(Math.Log10(constant)) - 1;
        var largest = (int)Math.Ceiling(Math.Log10(Math.Max(Math.Abs(low), Math.Abs(high)))) + 1;
        smallest = Math.Max(smallest, -320);
        largest = Math.Min(largest, 310);
        if (constant != 0)
        {
            // The negative side, from the largest magnitude down to C.
            for (var k = largest; k >= smallest; k--)
            {
                for (var m = multiples.Length - 1; m >= 0; m--)
                {
                    AddIfInside(axis, -multiples[m], k, low, high, constant, skip);
                }
            }

            if (low <= 0 && high >= 0)
            {
                axis.AddNumber(new TickNumber(0, 0), 0);
            }
        }

        for (var k = smallest; k <= largest; k++)
        {
            foreach (var m in multiples)
            {
                AddIfInside(axis, m, k, low, high, constant, skip);
            }
        }

        return axis.Count;
    }

    private static void AddIfInside(TickAxis axis, int m, int k, double low, double high, double constant, int skip)
    {
        if (k % skip != 0)
        {
            return;
        }

        var tick = new TickNumber(m, k);
        var value = tick.Value;
        if (value >= low && value <= high && (constant == 0 || Math.Abs(value) >= constant))
        {
            axis.AddNumber(tick, 0);
        }
    }
}
