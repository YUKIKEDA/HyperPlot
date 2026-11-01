using System.Buffers;

namespace HyperPlot;

/// <summary>
/// The workers' passes of the stats of rows G5b and G5c (design §5.5.3): bins, counts a cell, summaries a cell, a density's
/// spread and grid, a moving average's windows, and 2D bins. Each reads its columns once a pass, a chunk a turn; sums join in
/// fixed groups of chunks, in their order, so that a result is the same on any number of workers.
/// </summary>
internal static class StatWork
{
    /// <summary>The bytes the partial results of a pass take at most.</summary>
    private const long PartialBytes = 256L << 20;

    /// <summary>The most groups of chunks a pass works on apart: a number of the cells only.</summary>
    private const int MaxPartials = 8;

    /// <summary>The bytes the histograms of a quantile's radix select take at most (design §5.5.3): 2 KB a cell and rank.</summary>
    internal const long QuantileBytes = 256L << 20;

    /// <summary>
    /// Returns the edges of the bins of finite values in [<paramref name="min"/>, <paramref name="max"/>] of the scale space
    /// (design §4.1): Vega's nice steps of 10^k × 1, 2, or 5, at most the stat's MaxBins, or its Step; through its Anchor.
    /// </summary>
    /// <exception cref="ArgumentException">More than 10,000 bins.</exception>
    public static double[] Edges(double min, double max, BinStatSpec bin, int layer)
    {
        // Vega's span (vega-statistics/src/bin.js): the range, or |min|, or 1.
        var span = max - min;
        span =
            span != 0 ? span
            : min != 0 ? Math.Abs(min)
            : 1;
        double step;
        if (bin.Step is { } given)
        {
            step = given;
        }
        else
        {
            var level = Math.Ceiling(Math.Log10(bin.MaxBins));
            step = Math.Pow(10, Math.Round(Math.Log10(span)) - level);
            while (Math.Ceiling(span / step) > bin.MaxBins)
            {
                step *= 10;
            }

            foreach (var divide in (ReadOnlySpan<double>)[5, 2])
            {
                var smaller = step / divide;
                if (span / smaller <= bin.MaxBins)
                {
                    step = smaller;
                }
            }
        }

        var anchor = bin.Anchor ?? 0;
        var start = anchor + (Math.Floor(((min - anchor) / step) + 1e-14) * step);
        start = start > min ? start - step : start;

        // Vega's stop: start + ⌈(max − start) / step⌉ · step, at least one bin. A greatest value on the last edge lies in the
        // last bin, closed on both sides.
        var count = Math.Max(1, Math.Ceiling((max - start) / step));

        if (count > LayerPlan.MaxBins)
        {
            throw new ArgumentException(
                $"The bins of layer {layer} would be {count}; a stat makes at most {LayerPlan.MaxBins} (design §4.1). Give a larger Step."
            );
        }

        var edges = new double[(int)count + 1];
        for (var i = 0; i < edges.Length; i++)
        {
            edges[i] = start + (i * step);
        }

        return edges;
    }

    /// <summary>
    /// Returns the least and the greatest finite value of a column in the scale space, by one pass of the workers, or
    /// (+∞, −∞) for none (design §4.1): the range of bins when the statistics' ends are not finite or a transform maps them.
    /// </summary>
    public static (double Min, double Max) FiniteRange(Column x, IScale? transform, long origin, PlotWorkerPool pool)
    {
        var chunks = x.ChunkCount;
        var lows = new double[Math.Max(chunks, 1)];
        var highs = new double[Math.Max(chunks, 1)];
        pool.RunChunks(
                chunks,
                chunk =>
                {
                    var length = ChunkLength(x, chunk);
                    var buffer = ArrayPool<double>.Shared.Rent(length);
                    try
                    {
                        var values = buffer.AsSpan(0, length);
                        x.ReadChunk(chunk, 0, values, origin);
                        transform?.Forward(values, values);
                        var (low, high) = (double.PositiveInfinity, double.NegativeInfinity);
                        foreach (var v in values)
                        {
                            if (double.IsFinite(v))
                            {
                                (low, high) = (Math.Min(low, v), Math.Max(high, v));
                            }
                        }

                        (lows[chunk], highs[chunk]) = (low, high);
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(buffer);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
        return chunks == 0 ? (double.PositiveInfinity, double.NegativeInfinity) : (lows.Min(), highs.Max());
    }

    /// <summary>
    /// Counts the values of each group in each bin (design §4.1): [a, b), the last [a, b]; NaN, infinite values, values
    /// outside the edges, and rows of no group are not counted. The edges are in the scale space; values are read as
    /// differences from <paramref name="origin"/> and mapped by <paramref name="transform"/>. Counts at group · bins + bin.
    /// </summary>
    public static long[] Bin(Column x, CategoricalColumn? groups, IScale? transform, long origin, double[] edges, bool uniform, PlotWorkerPool pool)
    {
        var bins = edges.Length - 1;
        var nk = groups?.CategoryCount ?? 1;
        var (start, stop, step) = (edges[0], edges[^1], (edges[^1] - edges[0]) / bins);
        return CountInGroups(
            x.ChunkCount,
            nk * bins,
            pool,
            (chunk, mine) =>
            {
                var length = ChunkLength(x, chunk);
                var buffer = ArrayPool<double>.Shared.Rent(length);
                try
                {
                    var values = buffer.AsSpan(0, length);
                    x.ReadChunk(chunk, 0, values, origin);
                    transform?.Forward(values, values);
                    var codes = groups is null ? default : groups.GetCodes(chunk);
                    for (var i = 0; i < values.Length; i++)
                    {
                        var g = codes.IsEmpty ? 0 : codes[i];
                        var b = g < 0 ? -1 : BinOf(values[i], edges, start, stop, step, uniform);
                        if (b >= 0)
                        {
                            mine[(g * bins) + b]++;
                        }
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(buffer);
                }
            }
        );
    }

    /// <summary>
    /// Returns the bin of a value in the scale space (design §4.1): [a, b), the last [a, b]; −1 for NaN, an infinite value, or
    /// a value outside the edges. The uniform path's division and the edges may round apart by an ulp: the edges decide, as
    /// numpy's histogram does (an edge's value in the bin to its right).
    /// </summary>
    private static int BinOf(double v, double[] edges, double start, double stop, double step, bool uniform)
    {
        if (!double.IsFinite(v) || v < start || v > stop)
        {
            return -1;
        }

        var bins = edges.Length - 1;
        var b = uniform ? Math.Clamp((int)Math.Floor((v - start) / step), 0, bins - 1) : Upper(edges, v) - 1;
        if (b > 0 && v < edges[b])
        {
            b--;
        }
        else if (b < bins - 1 && v >= edges[b + 1])
        {
            b++;
        }

        return Math.Clamp(b, 0, bins - 1);
    }

    /// <summary>
    /// Counts the values of each cell of 2D bins (design §5.5.3, row G5c): a row counts when both its x and its y lie in their
    /// bins (<see cref="BinOf"/>). Counts at y bin · x bins + x bin.
    /// </summary>
    public static long[] Bin2D(Column x, Column y, Axis2D ofX, Axis2D ofY, PlotWorkerPool pool)
    {
        var (bx, by) = (ofX.Edges.Length - 1, ofY.Edges.Length - 1);
        var (sx, tx, stepX) = (ofX.Edges[0], ofX.Edges[^1], (ofX.Edges[^1] - ofX.Edges[0]) / bx);
        var (sy, ty, stepY) = (ofY.Edges[0], ofY.Edges[^1], (ofY.Edges[^1] - ofY.Edges[0]) / by);
        return CountInGroups(
            x.ChunkCount,
            bx * by,
            pool,
            (chunk, mine) =>
            {
                var length = ChunkLength(x, chunk);
                var xs = ArrayPool<double>.Shared.Rent(length);
                var ys = ArrayPool<double>.Shared.Rent(length);
                try
                {
                    var a = xs.AsSpan(0, length);
                    var b = ys.AsSpan(0, length);
                    x.ReadChunk(chunk, 0, a, ofX.Origin);
                    y.ReadChunk(chunk, 0, b, ofY.Origin);
                    ofX.Transform?.Forward(a, a);
                    ofY.Transform?.Forward(b, b);
                    for (var i = 0; i < length; i++)
                    {
                        var column = BinOf(a[i], ofX.Edges, sx, tx, stepX, ofX.Uniform);
                        var row = column < 0 ? -1 : BinOf(b[i], ofY.Edges, sy, ty, stepY, ofY.Uniform);
                        if (row >= 0)
                        {
                            mine[(row * bx) + column]++;
                        }
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(xs);
                    ArrayPool<double>.Shared.Return(ys);
                }
            }
        );
    }

    /// <summary>
    /// Returns each group's number, mean, and sum of squared deviations of its finite values in the scale space, its first
    /// row's value, and the range of all (design §5.5.3, row G5c): a density's bandwidth and range. A chunk's mean and squared
    /// deviations are found apart and joined by Chan's formula in a fixed order of groups of chunks; sums and sums of squares
    /// would lose the digits on 1e9 rows.
    /// </summary>
    public static Spread SpreadOf(Column x, CategoricalColumn? groups, IScale? transform, long origin, PlotWorkerPool pool)
    {
        var nk = groups?.CategoryCount ?? 1;
        var chunks = x.ChunkCount;
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(96L * nk, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var parts = new Spread[partials];
        pool.RunChunks(
                partials,
                part =>
                {
                    var mine = parts[part] = new Spread(nk);
                    var chunkSpread = new Spread(nk);
                    var raw = ArrayPool<double>.Shared.Rent(Column.ChunkLength);
                    var mapped = ArrayPool<double>.Shared.Rent(Column.ChunkLength);
                    try
                    {
                        var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                        for (var chunk = first; chunk < end; chunk++)
                        {
                            var length = ChunkLength(x, chunk);
                            var rawValues = raw.AsSpan(0, length);
                            var values = mapped.AsSpan(0, length);
                            x.ReadChunk(chunk, 0, rawValues, origin);
                            rawValues.CopyTo(values);
                            transform?.Forward(values, values);
                            var codes = groups is null ? default : groups.GetCodes(chunk);
                            chunkSpread.Clear();
                            chunkSpread.AddChunk(values, rawValues, codes, (long)chunk << Column.ChunkShift);
                            mine.Merge(chunkSpread);
                        }
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(raw);
                        ArrayPool<double>.Shared.Return(mapped);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
        var total = parts[0];
        for (var p = 1; p < partials; p++)
        {
            total.Merge(parts[p]);
        }

        return total;
    }

    /// <summary>
    /// Spreads each group's finite values linearly onto its grid of <paramref name="m"/> points from <paramref name="lo"/> by
    /// <paramref name="delta"/> (design §4.1, row G5c), as R's <c>BinDist</c> does (<c>src/library/stats/src/massdist.c</c>):
    /// a value at p = (v − lo) / δ adds 1 − f to point ⌊p⌋ and f to the next, the last point keeping 1 − f. A group whose delta is
    /// NaN is not drawn and spreads nothing. Weights at group · m + point, not yet over the group's number.
    /// </summary>
    public static double[] SpreadOnGrids(
        Column x,
        CategoricalColumn? groups,
        IScale? transform,
        long origin,
        double[] lo,
        double[] delta,
        int m,
        PlotWorkerPool pool
    )
    {
        var nk = lo.Length;
        return SumInGroups(
            x.ChunkCount,
            nk * m,
            pool,
            (chunk, mine) =>
            {
                var length = ChunkLength(x, chunk);
                var buffer = ArrayPool<double>.Shared.Rent(length);
                try
                {
                    var values = buffer.AsSpan(0, length);
                    x.ReadChunk(chunk, 0, values, origin);
                    transform?.Forward(values, values);
                    var codes = groups is null ? default : groups.GetCodes(chunk);
                    for (var i = 0; i < values.Length; i++)
                    {
                        var (v, g) = (values[i], codes.IsEmpty ? 0 : codes[i]);
                        if (g < 0 || !double.IsFinite(v) || double.IsNaN(delta[g]))
                        {
                            continue;
                        }

                        // The values lie in [from, to] and lo is below from, so the position is at least 0 (R's left end does
                        // not occur). It reaches the last point when the range is so much wider than the bandwidth that up
                        // rounds to to: that point keeps 1 − f, as R's right end does.
                        var position = (v - lo[g]) / delta[g];
                        var at = (int)Math.Floor(position);
                        var f = position - at;
                        var row = g * m;
                        if (at <= m - 2)
                        {
                            mine[row + at] += 1 - f;
                            mine[row + at + 1] += f;
                        }
                        else
                        {
                            mine[row + m - 1] += 1 - f;
                        }
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(buffer);
                }
            }
        );
    }

    /// <summary>
    /// Returns a density on a grid (design §4.1, row G5c): the weights convolved with a Gaussian kernel of standard deviation
    /// <paramref name="h"/>, Σ_i w_i · φ((j − i) δ / h) / h, as R's FFT does since R 4.4.0, negative values made 0.
    /// </summary>
    public static double[] Convolve(ReadOnlySpan<double> weights, double delta, double h)
    {
        var m = weights.Length;
        var kernel = new double[m];
        for (var d = 0; d < m; d++)
        {
            var z = d * delta / h;
            kernel[d] = Math.Exp(-0.5 * z * z) / (h * Math.Sqrt(2 * Math.PI));
        }

        var result = new double[m];
        for (var j = 0; j < m; j++)
        {
            var sum = 0.0;
            for (var i = 0; i < m; i++)
            {
                sum += weights[i] * kernel[Math.Abs(j - i)];
            }

            result[j] = Math.Max(0, sum);
        }

        return result;
    }

    /// <summary>
    /// Returns each group's sum and number of y values in each interval [e_r, e_r+1) of the windows' edges (design §5.5.3,
    /// row G5c): a row counts when its x is finite and below the last edge, and its y is not NaN. Sums at (group · intervals +
    /// interval) · 2, numbers after them.
    /// </summary>
    public static double[] WindowSums(
        Column x,
        Column y,
        CategoricalColumn? groups,
        IScale? transform,
        long origin,
        double[] edges,
        PlotWorkerPool pool
    )
    {
        var intervals = edges.Length - 1;
        var nk = groups?.CategoryCount ?? 1;
        return SumInGroups(
            x.ChunkCount,
            nk * intervals * 2,
            pool,
            (chunk, mine) =>
            {
                var length = ChunkLength(x, chunk);
                var xs = ArrayPool<double>.Shared.Rent(length);
                var ys = ArrayPool<double>.Shared.Rent(length);
                try
                {
                    var a = xs.AsSpan(0, length);
                    var b = ys.AsSpan(0, length);
                    x.ReadChunk(chunk, 0, a, origin);
                    transform?.Forward(a, a);
                    y.ReadChunk(chunk, 0, b, 0);
                    var codes = groups is null ? default : groups.GetCodes(chunk);
                    for (var i = 0; i < length; i++)
                    {
                        var (v, g) = (a[i], codes.IsEmpty ? 0 : codes[i]);

                        // The windows' first edge lies below the range, so x is at or above it. x reaches the last edge when
                        // the range is so much wider than the window that to + w / 2 rounds to to: it lies in no window.
                        if (g < 0 || !double.IsFinite(v) || double.IsNaN(b[i]) || v >= edges[^1])
                        {
                            continue;
                        }

                        var at = ((g * intervals) + Upper(edges, v) - 1) * 2;
                        mine[at] += b[i];
                        mine[at + 1]++;
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(xs);
                    ArrayPool<double>.Shared.Return(ys);
                }
            }
        );
    }

    /// <summary>Counts the rows of each cell (x category, group): at group · nx + category; rows of no category or group are not counted.</summary>
    public static long[] Count(CategoricalColumn x, CategoricalColumn? groups, PlotWorkerPool pool)
    {
        var nx = x.CategoryCount;
        return CountInGroups(
            x.ChunkCount,
            (groups?.CategoryCount ?? 1) * nx,
            pool,
            (chunk, mine) =>
            {
                var codes = x.GetCodes(chunk);
                var groupCodes = groups is null ? default : groups.GetCodes(chunk);
                for (var i = 0; i < codes.Length; i++)
                {
                    var g = groupCodes.IsEmpty ? 0 : groupCodes[i];
                    if (codes[i] >= 0 && g >= 0)
                    {
                        mine[(g * nx) + codes[i]]++;
                    }
                }
            }
        );
    }

    /// <summary>
    /// Counts over the chunks in a fixed number of groups (design §5.5.3): a group counts its chunks into an array of its own,
    /// and the arrays join once all have ended. The groups' arrays take at most <see cref="PartialBytes"/>.
    /// </summary>
    private static long[] CountInGroups(int chunks, int cells, PlotWorkerPool pool, Action<int, long[]> count)
    {
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(8L * cells, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var parts = new long[partials][];
        pool.RunChunks(
                partials,
                part =>
                {
                    var mine = parts[part] = new long[cells];
                    var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                    for (var chunk = first; chunk < end; chunk++)
                    {
                        count(chunk, mine);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
        var total = parts[0];
        for (var p = 1; p < partials; p++)
        {
            for (var c = 0; c < cells; c++)
            {
                total[c] += parts[p][c];
            }
        }

        return total;
    }

    /// <summary>
    /// Sums over the chunks in a fixed number of groups (row G5c), as <see cref="CountInGroups"/> counts: a group adds its
    /// chunks into an array of its own, in their order, and the arrays join in the groups' order.
    /// </summary>
    private static double[] SumInGroups(int chunks, int cells, PlotWorkerPool pool, Action<int, double[]> sum)
    {
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(8L * cells, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var parts = new double[partials][];
        pool.RunChunks(
                partials,
                part =>
                {
                    var mine = parts[part] = new double[cells];
                    var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                    for (var chunk = first; chunk < end; chunk++)
                    {
                        sum(chunk, mine);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
        var total = parts[0];
        for (var p = 1; p < partials; p++)
        {
            for (var c = 0; c < cells; c++)
            {
                total[c] += parts[p][c];
            }
        }

        return total;
    }

    /// <summary>
    /// Returns each cell's number of values, sum, least, and greatest value, and the states of the custom summaries (design
    /// §5.5.3): NaN values and rows of no category or group are left out. The chunks join in fixed groups in their order.
    /// </summary>
    public static Moments Summarize(CategoricalColumn x, CategoricalColumn? groups, Column y, IReadOnlyList<IStat> customs, PlotWorkerPool pool)
    {
        var nx = x.CategoryCount;
        var cells = (groups?.CategoryCount ?? 1) * nx;
        var states = 0;
        foreach (var custom in customs)
        {
            states += custom.StateLength;
        }

        var chunks = x.ChunkCount;
        var perPartial = (long)cells * (32 + (8 * states) + (customs.Count > 0 ? 4 : 0));
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(perPartial, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var parts = new Moments[partials];
        pool.RunChunks(
                partials,
                part =>
                {
                    var mine = parts[part] = new Moments(cells, states);
                    var buffer = ArrayPool<double>.Shared.Rent(Column.ChunkLength);
                    var cellOf = customs.Count > 0 ? ArrayPool<int>.Shared.Rent(Column.ChunkLength) : [];
                    var sorted = customs.Count > 0 ? ArrayPool<double>.Shared.Rent(Column.ChunkLength) : [];
                    var starts = customs.Count > 0 ? new int[cells] : [];
                    var touched = customs.Count > 0 ? ArrayPool<int>.Shared.Rent(Column.ChunkLength) : [];
                    try
                    {
                        var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                        for (var chunk = first; chunk < end; chunk++)
                        {
                            var codes = x.GetCodes(chunk);
                            var groupCodes = groups is null ? default : groups.GetCodes(chunk);
                            var values = buffer.AsSpan(0, codes.Length);
                            y.ReadChunk(chunk, 0, values, 0);
                            var kept = 0;
                            for (var i = 0; i < codes.Length; i++)
                            {
                                var (code, g, v) = (codes[i], groupCodes.IsEmpty ? 0 : groupCodes[i], values[i]);
                                if (code < 0 || g < 0 || double.IsNaN(v))
                                {
                                    continue;
                                }

                                var cell = (g * nx) + code;
                                mine.Add(cell, v);
                                if (customs.Count > 0)
                                {
                                    (cellOf[kept], values[kept]) = (cell, v);
                                    kept++;
                                }
                            }

                            if (customs.Count > 0)
                            {
                                FoldCells(customs, mine, cellOf, values, sorted, starts, touched, kept);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(buffer);
                        if (customs.Count > 0)
                        {
                            ArrayPool<int>.Shared.Return(cellOf);
                            ArrayPool<double>.Shared.Return(sorted);
                            ArrayPool<int>.Shared.Return(touched);
                        }
                    }
                }
            )
            .GetAwaiter()
            .GetResult();

        var total = parts[0];
        for (var p = 1; p < partials; p++)
        {
            total.Merge(parts[p], customs);
        }

        return total;
    }

    /// <summary>
    /// Folds a chunk's kept values into their cells' custom states (design §5.5.3): a counting sort over the cells the chunk
    /// touches (their counts, their starts, the values scattered to them), so that each cell's values are one span; then the
    /// touched cells' counts go back to 0. The work is the chunk's rows and touched cells, not all the cells.
    /// </summary>
    private static void FoldCells(
        IReadOnlyList<IStat> customs,
        Moments mine,
        int[] cellOf,
        Span<double> values,
        double[] sorted,
        int[] starts,
        int[] touched,
        int kept
    )
    {
        // The counts of the cells, in starts, and the cells in the order they first come.
        var cells = 0;
        for (var i = 0; i < kept; i++)
        {
            if (starts[cellOf[i]]++ == 0)
            {
                touched[cells++] = cellOf[i];
            }
        }

        // Each touched cell's start in sorted, then the values scattered in their rows' order.
        var position = 0;
        for (var t = 0; t < cells; t++)
        {
            var count = starts[touched[t]];
            starts[touched[t]] = position;
            position += count;
        }

        for (var i = 0; i < kept; i++)
        {
            sorted[starts[cellOf[i]]++] = values[i];
        }

        // After the scatter, each start is its cell's end: the span is the count before it.
        var from = 0;
        for (var t = 0; t < cells; t++)
        {
            var cell = touched[t];
            var span = sorted.AsSpan(from, starts[cell] - from);
            var offset = 0;
            foreach (var custom in customs)
            {
                custom.Fold(span, mine.State(cell, offset, custom.StateLength));
                offset += custom.StateLength;
            }

            from = starts[cell];
            starts[cell] = 0;
        }
    }

    /// <summary>
    /// Returns the values at the given 0-based ranks of each cell's values in order (design §5.5.3), by a radix select of
    /// the values' keys a byte a pass, high byte first: a pass counts, for each cell and rank, the values whose higher bytes
    /// match the rank's so far by their next byte, and the rank's byte is the bucket its count falls in. Ranks at cell ·
    /// ranks.Count + r; a rank at or past a cell's count is NaN. With no x, the cells are the groups (row G5c); keys outside
    /// [<paramref name="lowest"/>, <paramref name="highest"/>] are not counted, as values a scale's transform does not map.
    /// </summary>
    public static double[] Select(
        CategoricalColumn? x,
        CategoricalColumn? groups,
        Column y,
        long[] counts,
        IReadOnlyList<double> ps,
        PlotWorkerPool pool,
        ulong lowest = 0,
        ulong highest = ulong.MaxValue
    )
    {
        var nx = x?.CategoryCount ?? 1;
        var cells = counts.Length;
        var targets = 2 * ps.Count;
        var prefixes = new ulong[cells * targets];
        var remaining = new long[cells * targets];
        var live = new bool[cells * targets];
        for (var c = 0; c < cells; c++)
        {
            for (var q = 0; q < ps.Count; q++)
            {
                if (counts[c] == 0)
                {
                    continue;
                }

                var low = (long)Math.Floor((counts[c] - 1) * ps[q]);
                foreach (var (t, rank) in (ReadOnlySpan<(int, long)>)[(2 * q, low), ((2 * q) + 1, Math.Min(low + 1, counts[c] - 1))])
                {
                    var at = (c * targets) + t;
                    (remaining[at], live[at]) = (rank, true);
                }
            }
        }

        var floating = y is Column<float> or Column<double>;
        var chunks = y.ChunkCount;
        for (var pass = 0; pass < y.KeyBytes; pass++)
        {
            var shift = 56 - (8 * pass);
            var histogramBytes = (long)cells * targets * 256 * 8;
            var partials = (int)Math.Min(Math.Clamp(QuantileBytes / Math.Max(histogramBytes, 1), 1, MaxPartials), Math.Max(chunks, 1));
            var parts = new long[partials][];
            var p = pass;
            pool.RunChunks(
                    partials,
                    part =>
                    {
                        var mine = parts[part] = new long[cells * targets * 256];
                        var keys = ArrayPool<ulong>.Shared.Rent(Column.ChunkLength);
                        try
                        {
                            var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                            for (var chunk = first; chunk < end; chunk++)
                            {
                                var codes = x is null ? default : x.GetCodes(chunk);
                                var groupCodes = groups is null ? default : groups.GetCodes(chunk);
                                var span = keys.AsSpan(0, ChunkLength(y, chunk));
                                y.ReadKeys(chunk, 0, span);
                                for (var i = 0; i < span.Length; i++)
                                {
                                    var (code, g, key) = (codes.IsEmpty ? 0 : codes[i], groupCodes.IsEmpty ? 0 : groupCodes[i], span[i]);
                                    if (code < 0 || g < 0 || (floating && key == ulong.MaxValue) || key < lowest || key > highest)
                                    {
                                        continue;
                                    }

                                    var cell = (g * nx) + code;
                                    var bucket = (int)((key >> shift) & 0xFF);
                                    for (var t = 0; t < targets; t++)
                                    {
                                        var at = (cell * targets) + t;

                                        // The bytes above this pass match the rank's: the first pass has none.
                                        if (live[at] && (p == 0 || (key >> (shift + 8)) == (prefixes[at] >> (shift + 8))))
                                        {
                                            mine[(at * 256) + bucket]++;
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<ulong>.Shared.Return(keys);
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            var histogram = parts[0];
            for (var part = 1; part < partials; part++)
            {
                for (var i = 0; i < histogram.Length; i++)
                {
                    histogram[i] += parts[part][i];
                }
            }

            // Each rank's byte: the bucket its remaining rank falls in.
            for (var at = 0; at < live.Length; at++)
            {
                if (!live[at])
                {
                    continue;
                }

                var below = 0L;
                for (var bucket = 0; bucket < 256; bucket++)
                {
                    var count = histogram[(at * 256) + bucket];
                    if (remaining[at] < below + count)
                    {
                        prefixes[at] |= (ulong)bucket << shift;
                        remaining[at] -= below;
                        break;
                    }

                    below += count;
                }
            }
        }

        var values = new double[cells * targets];
        for (var at = 0; at < values.Length; at++)
        {
            values[at] = live[at] ? y.ValueOfKey(prefixes[at]) : double.NaN;
        }

        return values;
    }

    /// <summary>Returns the first index of an edge above a value: the bin of the value is the index minus 1.</summary>
    private static int Upper(double[] edges, double value)
    {
        var (low, high) = (0, edges.Length);
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (edges[middle] <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int ChunkLength(Column column, int chunk) => (int)Math.Min(Column.ChunkLength, column.Length - ((long)chunk << Column.ChunkShift));

    /// <summary>The scale of a 2D bins' axis: its edges in the scale space, whether they are equal, its values' origin and transform.</summary>
    internal readonly record struct Axis2D(double[] Edges, bool Uniform, long Origin, IScale? Transform);

    /// <summary>
    /// Each group's number, mean, and sum of squared deviations of its finite values in the scale space, and its first row's
    /// value; the least and greatest of all in the scale space, and as read (row G5c).
    /// </summary>
    internal sealed class Spread
    {
        public Spread(int groups)
        {
            (Count, Mean, Squares, First, FirstRow) = (
                new long[groups],
                new double[groups],
                new double[groups],
                new double[groups],
                new long[groups]
            );
            Clear();
        }

        public long[] Count { get; }

        public double[] Mean { get; }

        public double[] Squares { get; }

        public double[] First { get; }

        public long[] FirstRow { get; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public double RawMin { get; private set; }

        public double RawMax { get; private set; }

        public void Clear()
        {
            Count.AsSpan().Clear();
            Mean.AsSpan().Clear();
            Squares.AsSpan().Clear();
            FirstRow.AsSpan().Fill(long.MaxValue);
            (Min, Max, RawMin, RawMax) = (double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity);
        }

        /// <summary>Adds a chunk's values: the means first, then the squared deviations from them.</summary>
        public void AddChunk(ReadOnlySpan<double> values, ReadOnlySpan<double> raw, ReadOnlySpan<int> codes, long firstRow)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var (v, g) = (values[i], codes.IsEmpty ? 0 : codes[i]);
                if (g < 0 || !double.IsFinite(v))
                {
                    continue;
                }

                if (Count[g]++ == 0)
                {
                    (First[g], FirstRow[g]) = (v, firstRow + i);
                }

                Mean[g] += v;
                (Min, Max) = (Math.Min(Min, v), Math.Max(Max, v));
                (RawMin, RawMax) = (Math.Min(RawMin, raw[i]), Math.Max(RawMax, raw[i]));
            }

            for (var g = 0; g < Count.Length; g++)
            {
                Mean[g] = Count[g] > 0 ? Mean[g] / Count[g] : 0;
            }

            for (var i = 0; i < values.Length; i++)
            {
                var (v, g) = (values[i], codes.IsEmpty ? 0 : codes[i]);
                if (g >= 0 && double.IsFinite(v))
                {
                    var d = v - Mean[g];
                    Squares[g] += d * d;
                }
            }
        }

        /// <summary>Joins a later part into this one by Chan's formula.</summary>
        public void Merge(Spread other)
        {
            for (var g = 0; g < Count.Length; g++)
            {
                var (a, b) = (Count[g], other.Count[g]);
                if (b == 0)
                {
                    continue;
                }

                if (a == 0)
                {
                    (Mean[g], Squares[g]) = (other.Mean[g], other.Squares[g]);
                }
                else
                {
                    var n = (double)(a + b);
                    var d = other.Mean[g] - Mean[g];
                    Mean[g] += d * b / n;
                    Squares[g] += other.Squares[g] + (d * d * a * b / n);
                }

                Count[g] = a + b;
                if (other.FirstRow[g] < FirstRow[g])
                {
                    (First[g], FirstRow[g]) = (other.First[g], other.FirstRow[g]);
                }
            }

            (Min, Max) = (Math.Min(Min, other.Min), Math.Max(Max, other.Max));
            (RawMin, RawMax) = (Math.Min(RawMin, other.RawMin), Math.Max(RawMax, other.RawMax));
        }
    }

    /// <summary>Each cell's number of values, sum, least and greatest value, and custom states.</summary>
    internal sealed class Moments
    {
        private readonly int _states;

        public Moments(int cells, int states)
        {
            _states = states;
            (Count, Sum, Min, Max) = (new long[cells], new double[cells], new double[cells], new double[cells]);
            Min.AsSpan().Fill(double.PositiveInfinity);
            Max.AsSpan().Fill(double.NegativeInfinity);
            States = new double[cells * states];
        }

        public long[] Count { get; }

        public double[] Sum { get; }

        public double[] Min { get; }

        public double[] Max { get; }

        public double[] States { get; }

        public void Add(int cell, double value)
        {
            Count[cell]++;
            Sum[cell] += value;
            (Min[cell], Max[cell]) = (Math.Min(Min[cell], value), Math.Max(Max[cell], value));
        }

        public Span<double> State(int cell, int offset, int length) => States.AsSpan((cell * _states) + offset, length);

        /// <summary>Merges a later group of chunks into this one.</summary>
        public void Merge(Moments other, IReadOnlyList<IStat> customs)
        {
            for (var c = 0; c < Count.Length; c++)
            {
                if (other.Count[c] == 0)
                {
                    continue;
                }

                Count[c] += other.Count[c];
                Sum[c] += other.Sum[c];
                (Min[c], Max[c]) = (Math.Min(Min[c], other.Min[c]), Math.Max(Max[c], other.Max[c]));
                var offset = 0;
                foreach (var custom in customs)
                {
                    custom.Merge(other.State(c, offset, custom.StateLength), State(c, offset, custom.StateLength));
                    offset += custom.StateLength;
                }
            }
        }
    }
}
