namespace HyperPlot;

/// <summary>
/// The ticks of linear, pow, sqrt, and transform scales: the extended search of Talbot, Lin, and Hanrahan, "An Extension of
/// Wilkinson's Algorithm for Positioning Tick Labels on Axes" (InfoVis 2010, §3 to §5), over the data units (design §4.6).
/// The weights are those of §4's text; legibility is the overlap term only; without nice the labels stay inside the domain
/// and fill it, with nice they cover it and the domain becomes [lmin, lmax].
/// </summary>
internal static class ExtendedTicks
{
    // The weights of §4's text: simplicity, coverage, density, legibility. §5's pseudocode swaps the first two.
    private const double WeightSimplicity = 0.25;
    private const double WeightCoverage = 0.2;
    private const double WeightDensity = 0.5;
    private const double WeightLegibility = 0.05;

    // Bounds on the loops, far past where the pruning ends them, so that a domain at the edge of double cannot loop on.
    private const int MaxSkip = 64;
    private const int MaxCount = 4096;
    private const int MaxPower = 64;

    // Q in order of preference (§3.2), as the integers 10q, so that a step is an integer times a power of ten.
    private static readonly int[] Q10 = [10, 50, 20, 25, 40, 30];

    /// <summary>Chooses the ticks of an axis whose domain maps through the scale (design §4.6).</summary>
    public static void Choose(TickAxis axis, bool nice)
    {
        var domain = axis.Domain;

        // The data range: in data units from the domain's origin without a transform, its inverse with one (origin 0).
        long origin;
        double low;
        double high;
        if (axis.Transform is null)
        {
            (origin, low, high) = (domain.Origin, domain.Lower, domain.Upper);
        }
        else
        {
            (origin, low, high) = (0, axis.Inverse(domain.Origin + domain.Lower), axis.Inverse(domain.Origin + domain.Upper));
        }

        Choose(axis, origin, low, high, nice);
    }

    /// <summary>Chooses the ticks over the data range [origin + low, origin + high].</summary>
    public static void Choose(TickAxis axis, long origin, double low, double high, bool nice)
    {
        if (!(high > low) || !double.IsFinite(low) || !double.IsFinite(high))
        {
            return;
        }

        var best = new Candidate(-2, 0, 0, 0, 0, 0);
        var range = high - low;
        var targetDensity = 1 / axis.Spacing;
        for (var j = 1; j <= MaxSkip; j++)
        {
            for (var i = 0; i < Q10.Length; i++)
            {
                var simplicityMax = 1 - ((double)i / (Q10.Length - 1)) - j + 1;
                if (Score(simplicityMax, 1, 1, 1) < best.Score)
                {
                    goto Done;
                }

                for (var k = 2; k <= MaxCount; k++)
                {
                    var densityMax = DensityMax(k, axis.Length, targetDensity);
                    if (Score(simplicityMax, 1, densityMax, 1) < best.Score)
                    {
                        break;
                    }

                    // z from ⌈log10((dmax − dmin) / (k + 1) / (j · q))⌉ (§5).
                    var delta = range / (k + 1) / (j * Q10[i] / 10.0);
                    var z0 = (int)Math.Ceiling(Math.Log10(delta));
                    for (var z = z0; z <= z0 + MaxPower; z++)
                    {
                        // lstep = q · j · 10^z = (10q · j) · 10^(z − 1); its 1/j is the unit u · 10^e.
                        var unit = (Int128)Q10[i];
                        var e = z - 1;
                        var step = Q10[i] * j * TickNumber.Pow10Signed(e);
                        var coverageMax = CoverageMax((k - 1) * step, range);
                        if (Score(simplicityMax, coverageMax, densityMax, 1) < best.Score)
                        {
                            break;
                        }

                        // start from ⌊dmax / lstep⌋ − (k − 1) to ⌈dmin / lstep⌉ by 1/j: a = start · j, in units.
                        var first = j * (TickNumber.FloorUnits(origin, high, unit * j, e) - (k - 1));
                        var last = j * TickNumber.CeilingUnits(origin, low, unit * j, e);
                        for (var a = first; a <= last; a++)
                        {
                            var lmin = new TickNumber(a * unit, e);
                            var lmax = new TickNumber((a + ((k - 1) * j)) * unit, e);
                            var lowTick = lmin.Difference(origin);
                            var highTick = lmax.Difference(origin);
                            if (!Admits(lowTick, highTick, step, low, high, nice))
                            {
                                continue;
                            }

                            var zero = a <= 0 && (-a) % j == 0 && (-a) / j <= k - 1;
                            var simplicity = 1 - ((double)i / (Q10.Length - 1)) - j + (zero ? 1 : 0);
                            var coverage = 1 - (0.5 * ((Square(high - highTick) + Square(low - lowTick)) / Square(0.1 * range)));
                            var span = nice
                                ? axis.Length
                                : Pixel(axis, lmax, origin, lowTick, highTick, nice) - Pixel(axis, lmin, origin, lowTick, highTick, nice);
                            var density = Density(k, span, targetDensity);
                            if (Score(simplicity, coverage, density, 1) < best.Score)
                            {
                                continue;
                            }

                            var legibility = Legibility(axis, a, unit, e, j, k, origin, lowTick, highTick, nice);
                            var score = Score(simplicity, coverage, density, legibility);
                            if (score > best.Score)
                            {
                                best = new Candidate(score, a, i, j, k, e);
                            }
                        }
                    }
                }
            }
        }

        Done:
        axis.ClearCandidate();
        if (best.Count == 0)
        {
            return;
        }

        var bestUnit = (Int128)Q10[best.QIndex];
        var bestLow = new TickNumber(best.A * bestUnit, best.Exponent).Difference(origin);
        var bestHigh = new TickNumber((best.A + ((best.Count - 1) * best.Skip)) * bestUnit, best.Exponent).Difference(origin);
        _ = Legibility(axis, best.A, bestUnit, best.Exponent, best.Skip, best.Count, origin, bestLow, bestHigh, nice);
        if (nice)
        {
            axis.SetFinalDomain(Widened(axis, origin, best, bestUnit));
        }
    }

    private static bool Admits(double lowTick, double highTick, double step, double low, double high, bool nice) =>
        nice ? lowTick <= low && highTick >= high : lowTick >= low && highTick <= high && lowTick - step < low && highTick + step > high;

    /// <summary>Writes a candidate's ticks into the axis's slots, labels them, and returns their overlap value.</summary>
    private static double Legibility(
        TickAxis axis,
        Int128 a,
        Int128 unit,
        int e,
        int j,
        int k,
        long origin,
        double lowTick,
        double highTick,
        bool nice
    )
    {
        axis.ClearCandidate();
        for (var n = 0; n < k; n++)
        {
            var tick = new TickNumber((a + (n * j)) * unit, e);
            axis.AddNumber(tick, Pixel(axis, tick, origin, lowTick, highTick, nice));
        }

        return axis.LabelNumbers(sameDecimals: true);
    }

    /// <summary>A tick's pixel over the domain, or over [lmin, lmax] when nice widens the domain to it.</summary>
    private static double Pixel(TickAxis axis, TickNumber tick, long origin, double lowTick, double highTick, bool nice)
    {
        if (!nice)
        {
            return axis.Pixel(tick, axis.Domain);
        }

        if (axis.Transform is null)
        {
            return axis.PixelOfDifference(tick.Difference(origin), new ScaleRange(origin, lowTick, highTick));
        }

        var low = axis.Forward(lowTick);
        var high = axis.Forward(highTick);
        return (axis.Forward(tick.Value) - low) / (high - low) * axis.Length;
    }

    private static ScaleRange Widened(TickAxis axis, long origin, in Candidate best, Int128 unit)
    {
        var lmin = new TickNumber(best.A * unit, best.Exponent);
        var lmax = new TickNumber((best.A + ((best.Count - 1) * best.Skip)) * unit, best.Exponent);
        if (axis.Transform is null)
        {
            return new ScaleRange(origin, lmin.Difference(origin), lmax.Difference(origin)).Anchor();
        }

        return ScaleRange.FromDouble(axis.Forward(lmin.Value), axis.Forward(lmax.Value));
    }

    private static double Score(double simplicity, double coverage, double density, double legibility) =>
        (WeightSimplicity * simplicity) + (WeightCoverage * coverage) + (WeightDensity * density) + (WeightLegibility * legibility);

    /// <summary>density = 2 − max(ρ / ρt, ρt / ρ), ρ = (k − 1) / the labels' span in pixels (Talbot et al. 2010 §4.3).</summary>
    private static double Density(int k, double span, double target)
    {
        var rho = (k - 1) / span;
        return 2 - Math.Max(rho / target, target / rho);
    }

    /// <summary>The density's bound for k labels: the sparsest k labels span the whole axis (design §4.6).</summary>
    private static double DensityMax(int k, double length, double target)
    {
        var sparsest = (k - 1) / length;
        return sparsest >= target ? 2 - (sparsest / target) : 1;
    }

    /// <summary>The coverage's bound for a label span: centered on the data range (§5; the formula is HyperPlot's, design §4.6).</summary>
    private static double CoverageMax(double span, double range) => span <= range ? 1 : 1 - (0.25 * Square(span - range) / Square(0.1 * range));

    private static double Square(double x) => x * x;

    private readonly record struct Candidate(double Score, Int128 A, int QIndex, int Skip, int Count, int Exponent);
}
