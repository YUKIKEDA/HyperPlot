using System.Runtime.CompilerServices;

namespace HyperPlot.Rendering;

/// <summary>How a polyline turns at a point between two segments (design §5.1, row R4).</summary>
public enum LineJoin
{
    /// <summary>A circle the width of the line around the point.</summary>
    Round,

    /// <summary>The outer edges carried on to where they meet, unless that is past <see cref="LineStyle.MiterLimit"/>; then a bevel.</summary>
    Miter,

    /// <summary>A straight cut across the outer corners of the two segments.</summary>
    Bevel,
}

/// <summary>How a polyline ends, at each end of a run and of each dash (design §5.1, row R4).</summary>
public enum LineCap
{
    /// <summary>A half circle past the end.</summary>
    Round,

    /// <summary>A straight cut at the end.</summary>
    Butt,

    /// <summary>A half square past the end: the line goes on by half its width.</summary>
    Square,
}

/// <summary>
/// A dash pattern (design §5.1): up to <see cref="MaxIntervals"/> lengths, dash and gap in turn, measured along the line in
/// physical pixels, and how far into the pattern the line starts. Held inline: making or copying one allocates nothing.
/// </summary>
public readonly record struct LineDash
{
    /// <summary>The most intervals a pattern holds.</summary>
    public const int MaxIntervals = 8;

    private readonly Intervals _intervals;

    /// <summary>Initializes a new instance of the <see cref="LineDash"/> struct with one dash and one gap.</summary>
    /// <param name="on">The length of a dash; 0 with a round or square cap draws dots.</param>
    /// <param name="off">The length of a gap.</param>
    /// <param name="offset">How far into the pattern the line starts.</param>
    /// <remarks><see cref="RenderList.DrawPolyline"/> checks the lengths: finite, at least 0, with a positive sum.</remarks>
    public LineDash(float on, float off, float offset = 0)
    {
        _intervals[0] = on;
        _intervals[1] = off;
        Count = 2;
        Offset = offset;
    }

    private LineDash(ReadOnlySpan<float> intervals)
    {
        // An odd count is repeated to make it even, as SVG's stroke-dasharray and Canvas's setLineDash do (design §5.1).
        var count = intervals.Length % 2 == 0 ? intervals.Length : 2 * intervals.Length;
        for (var i = 0; i < count; i++)
        {
            _intervals[i] = intervals[i % intervals.Length];
        }

        Count = count;
    }

    /// <summary>Gets the number of intervals: even, from 2 to <see cref="MaxIntervals"/>; 0 for <see langword="default"/>, which no line takes.</summary>
    public int Count { get; }

    /// <summary>Gets how far into the pattern the line starts, in physical pixels.</summary>
    public float Offset { get; init; }

    /// <summary>Gets the sum of the intervals: the length of one pattern.</summary>
    public float Period
    {
        get
        {
            var sum = 0f;
            for (var i = 0; i < Count; i++)
            {
                sum += _intervals[i];
            }

            return sum;
        }
    }

    /// <summary>Gets an interval: a dash at an even index, a gap at an odd one.</summary>
    /// <param name="index">From 0 to <see cref="Count"/> − 1.</param>
    /// <returns>The length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 to <see cref="Count"/> − 1.</exception>
    public float this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return _intervals[index];
        }
    }

    /// <summary>Returns a pattern of intervals, dash and gap in turn; an odd count is repeated to make it even.</summary>
    /// <param name="intervals">1 to <see cref="MaxIntervals"/> lengths, and 4 at most when odd.</param>
    /// <returns>The pattern, with an offset of 0; set another with <c>with</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No interval, or more than <see cref="MaxIntervals"/> once repeated.</exception>
    /// <remarks><see cref="RenderList.DrawPolyline"/> checks the lengths: finite, at least 0, with a positive sum.</remarks>
    public static LineDash Of(params ReadOnlySpan<float> intervals)
    {
        var count = intervals.Length % 2 == 0 ? intervals.Length : 2 * intervals.Length;
        if (intervals.IsEmpty || count > MaxIntervals)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervals),
                intervals.Length,
                $"A dash pattern has 1 to {MaxIntervals} intervals, an odd count counted twice."
            );
        }

        return new LineDash(intervals);
    }

    /// <summary>Returns whether two patterns have the same intervals and offset.</summary>
    /// <param name="other">The other pattern.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(LineDash other)
    {
        if (Count != other.Count || !Offset.Equals(other.Offset))
        {
            return false;
        }

        for (var i = 0; i < Count; i++)
        {
            if (!_intervals[i].Equals(other._intervals[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns a hash of the intervals and the offset.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Count);
        hash.Add(Offset);
        for (var i = 0; i < Count; i++)
        {
            hash.Add(_intervals[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns the pattern with every length multiplied by a scale: intervals and offset (design §4.6).</summary>
    internal LineDash Scaled(float scale) => new(this, scale);

    private LineDash(in LineDash source, float scale)
    {
        for (var i = 0; i < source.Count; i++)
        {
            _intervals[i] = source._intervals[i] * scale;
        }

        Count = source.Count;
        Offset = source.Offset * scale;
    }

    /// <summary>The intervals, held inline.</summary>
    [InlineArray(MaxIntervals)]
    private struct Intervals
    {
        private float _element;
    }
}

/// <summary>How a polyline is stroked (design §5.1): its color, width, dashes, joins, and ends, anti-aliased.</summary>
/// <param name="Color">The color.</param>
/// <param name="Width">The width in physical pixels, finite and at least 0. 0 draws nothing; below 1, the line is fainter by the share of the pixel it covers.</param>
/// <param name="Dash">The dash pattern, or null for a solid line. The pattern runs on across joins.</param>
public readonly record struct LineStyle(Color Color, float Width, LineDash? Dash = null)
{
    /// <summary>Gets how the line turns at a point between two segments: <see cref="LineJoin.Round"/> by default.</summary>
    public LineJoin Join { get; init; }

    /// <summary>Gets how the line ends, at each end of a run and of each dash: <see cref="LineCap.Round"/> by default.</summary>
    public LineCap Cap { get; init; }

    /// <summary>
    /// Gets the most a miter may reach past its point, as a multiple of half the width: 4 by default, finite and at least 1
    /// when <see cref="Join"/> is <see cref="LineJoin.Miter"/>. Past it, the join is a bevel. The same ratio as SVG's
    /// <c>stroke-miterlimit</c>.
    /// </summary>
    public float MiterLimit { get; init; } = 4;
}
