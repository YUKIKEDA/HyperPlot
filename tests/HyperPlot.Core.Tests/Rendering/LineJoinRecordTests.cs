using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>Row R4's line styles and the pieces RenderList records for them (design §5.1, §10 R4 row).</summary>
public sealed class LineJoinRecordTests
{
    private static readonly Color Black = new(0, 0, 0, 255);

    /// <summary>
    /// LineDash holds up to 8 intervals inline, repeats an odd count, and compares by its intervals and offset (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Patterns of 2, 3, 4, and 8 intervals.
    ///
    /// Steps:
    /// - Read their counts, intervals, and periods; compare equal and unequal patterns; make patterns of 0, 9, and 5 (10 once
    ///   repeated) intervals; read an index past the count.
    ///
    /// Expected:
    /// - Counts 2, 6, 4, 8; (4, 2, 1) reads 4, 2, 1, 4, 2, 1 with period 14; equal patterns are equal with equal hashes, and a
    ///   change of one interval or of the offset is not; ArgumentOutOfRangeException for 0, 9, 5, and the index.
    /// </remarks>
    [Fact]
    public void LineDash_IntervalsRepeatAndCompare()
    {
        var odd = LineDash.Of(4, 2, 1);
        var eight = LineDash.Of(1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal((2, 6, 4, 8), (new LineDash(6, 3).Count, odd.Count, LineDash.Of(1, 2, 3, 4).Count, eight.Count));
        Assert.Equal([4f, 2, 1, 4, 2, 1], Enumerable.Range(0, odd.Count).Select(i => odd[i]));
        Assert.Equal((14f, 36f), (odd.Period, eight.Period));
        Assert.Equal(LineDash.Of(1, 2, 3, 4, 5, 6, 7, 8), eight);
        Assert.Equal(LineDash.Of(1, 2, 3, 4, 5, 6, 7, 8).GetHashCode(), eight.GetHashCode());
        Assert.NotEqual(LineDash.Of(1, 2, 3, 4, 5, 6, 7, 9), eight);
        Assert.NotEqual(eight with { Offset = 1 }, eight);
        Assert.Equal(new LineDash(6, 3, 1), LineDash.Of(6, 3) with { Offset = 1 });
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LineDash.Of());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LineDash.Of(1, 2, 3, 4, 5, 6, 7, 8, 9));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LineDash.Of(1, 2, 3, 4, 5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => odd[6]);
    }

    /// <summary>
    /// DrawPolyline checks the new members (design §5.1): the pattern's intervals and period, the join and cap, and the miter
    /// limit only for a miter. The defaults are round, round, and 4, and record as row R2 did.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list; two points.
    ///
    /// Steps:
    /// - Record with default(LineDash), a negative interval among 8, a period of 0, two intervals whose sum overflows, an
    ///   offset of NaN, join and cap 3, a miter with a limit of 0.99 and of NaN; record a round and a bevel join with a limit
    ///   of 0, default(LineStyle), and a default style.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the first group; the second records, with no pieces; the default style is round,
    ///   round, and 4.
    /// </remarks>
    [Fact]
    public void Arguments_OutOfRange_Throw()
    {
        var list = new RenderList();
        Vector2[] two = [new(0, 0), new(4, 3)];
        var solid = new LineStyle(Black, 2);
        LineStyle[] bad =
        [
            solid with
            {
                Dash = default(LineDash),
            },
            solid with
            {
                Dash = LineDash.Of(1, 2, 3, 4, 5, -1, 7, 8),
            },
            solid with
            {
                Dash = LineDash.Of(0, 0),
            },
            solid with
            {
                Dash = LineDash.Of(float.MaxValue, float.MaxValue),
            },
            solid with
            {
                Dash = new LineDash(1, 1, float.NaN),
            },
            solid with
            {
                Join = (LineJoin)3,
            },
            solid with
            {
                Cap = (LineCap)3,
            },
            solid with
            {
                Join = LineJoin.Miter,
                MiterLimit = 0.99f,
            },
            solid with
            {
                Join = LineJoin.Miter,
                MiterLimit = float.NaN,
            },
        ];
        foreach (var style in bad)
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawPolyline(two, style));
        }

        list.DrawPolyline(two, solid with { MiterLimit = 0 });
        list.DrawPolyline(two, solid with { Join = LineJoin.Bevel, MiterLimit = 0 });
        list.DrawPolyline(two, default(LineStyle) with { Width = 1 });

        Assert.Equal((LineJoin.Round, LineCap.Round, 4f), (solid.Join, solid.Cap, solid.MiterLimit));
        Assert.Equal(3, list.Polylines.Length);
        Assert.Equal(2, list.Pieces.Length);
        Assert.Equal(0, list.Polylines[0].PieceCount);
        Assert.Equal(0, list.Polylines[2].PieceCount);
    }

    /// <summary>
    /// A polyline that is not round records a join at each point inside a run and a cap at each end, by the kind of its join
    /// and cap (design §5.1); a round one records none, unless its pattern has more than one dash and gap.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A zigzag of 4 points: turns of 90° and of 45°.
    ///
    /// Steps:
    /// - Record it as miter with square caps, bevel with butt caps, round with round caps, and round with a 4-interval pattern
    ///   that is on at both corners.
    ///
    /// Expected:
    /// - Miter, square: a square cap pointing back from the start, miter, miter, a square cap pointing on from the end, with
    ///   the joins' incoming and outgoing directions. Bevel, butt: two bevels. Round: nothing. Round, 4 intervals: two disks
    ///   and two disk caps.
    /// </remarks>
    [Fact]
    public void Pieces_JoinsAndCaps()
    {
        Vector2[] zigzag = [new(0, 0), new(10, 0), new(10, 10), new(20, 20)];
        var diagonal = Vector2.Normalize(new Vector2(1, 1));

        Assert.Equal(
            [
                new PieceRecord(new(0, 0), -Vector2.UnitX, -Vector2.UnitX, PieceKind.SquareCap),
                new PieceRecord(new(10, 0), Vector2.UnitX, Vector2.UnitY, PieceKind.Miter),
                new PieceRecord(new(10, 10), Vector2.UnitY, diagonal, PieceKind.Miter),
                new PieceRecord(new(20, 20), diagonal, diagonal, PieceKind.SquareCap),
            ],
            Pieces(zigzag, new LineStyle(Black, 2) { Join = LineJoin.Miter, Cap = LineCap.Square })
        );
        Assert.Equal(
            [PieceKind.Bevel, PieceKind.Bevel],
            Pieces(zigzag, new LineStyle(Black, 2) { Join = LineJoin.Bevel, Cap = LineCap.Butt }).Select(p => p.Kind)
        );
        Assert.Empty(Pieces(zigzag, new LineStyle(Black, 2)));
        Assert.Equal(
            [PieceKind.Disk, PieceKind.Disk, PieceKind.Disk, PieceKind.Disk],
            Pieces(zigzag, new LineStyle(Black, 2, LineDash.Of(15, 1, 30, 1))).Select(p => p.Kind)
        );
    }

    /// <summary>
    /// A miter becomes a bevel past MiterLimit (1 / cos(θ / 2) above it) and stays a miter at it; a reversal is a bevel; a
    /// turn of nearly nothing records no join; segments of length 0 are skipped when the join's directions are found (design
    /// §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 90° turn, whose miter is √2 half widths; a reversal; a turn of 1e-5 radians; a turn whose corner point repeats.
    ///
    /// Steps:
    /// - Record the 90° turn with limits 1.41 and 1.42, and the others with a miter join and butt caps.
    ///
    /// Expected:
    /// - Bevel, then miter; the reversal a bevel; nothing for the turn of 1e-5; one miter from +x to +y at the repeated corner.
    /// </remarks>
    [Fact]
    public void Pieces_MiterLimitReversalAndDegenerateSegments()
    {
        var miter = new LineStyle(Black, 2) { Join = LineJoin.Miter, Cap = LineCap.Butt };
        Vector2[] square = [new(0, 0), new(10, 0), new(10, 10)];

        Assert.Equal(PieceKind.Bevel, Assert.Single(Pieces(square, miter with { MiterLimit = 1.41f })).Kind);
        Assert.Equal(PieceKind.Miter, Assert.Single(Pieces(square, miter with { MiterLimit = 1.42f })).Kind);
        Assert.Equal(PieceKind.Bevel, Assert.Single(Pieces([new(0, 0), new(10, 0), new(5, 0)], miter)).Kind);
        Assert.Empty(Pieces([new(0, 0), new(10, 0), new(20, 1e-4f)], miter));
        Assert.Equal(
            new PieceRecord(new(10, 0), Vector2.UnitX, Vector2.UnitY, PieceKind.Miter),
            Assert.Single(Pieces([new(0, 0), new(10, 0), new(10, 0), new(10, 0), new(10, 10)], miter))
        );
    }

    /// <summary>
    /// A run of length 0 is a dot by the cap, its square along +x (SVG 2 §9.4); a NaN break caps both sides; a polyline of
    /// 2-point runs, as a segment layer records, makes two caps a segment, 2S in all (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three equal points; a line broken by NaN; three 2-point runs.
    ///
    /// Steps:
    /// - Record the equal points with round, square, and butt caps and a miter join; the others with square caps.
    ///
    /// Expected:
    /// - A disk, a square along +x, nothing; 4 square caps for the broken line, the two at the break pointing away from it; 6
    ///   for the three runs.
    /// </remarks>
    [Fact]
    public void Pieces_DotsBreaksAndShortRuns()
    {
        var miter = new LineStyle(Black, 2) { Join = LineJoin.Miter };
        Vector2[] still = [new(5, 5), new(5, 5), new(5, 5)];
        Vector2[] broken = [new(0, 0), new(10, 0), new(float.NaN, 0), new(20, 0), new(30, 0)];
        Vector2[] runs = [new(0, 0), new(1, 0), new(float.NaN, 0), new(2, 0), new(3, 0), new(float.NaN, 0), new(4, 0), new(5, 0)];

        Assert.Equal(new PieceRecord(new(5, 5), Vector2.UnitX, Vector2.UnitX, PieceKind.Disk), Assert.Single(Pieces(still, miter)));
        Assert.Equal(
            new PieceRecord(new(5, 5), Vector2.UnitX, Vector2.UnitX, PieceKind.Square),
            Assert.Single(Pieces(still, miter with { Cap = LineCap.Square }))
        );
        Assert.Empty(Pieces(still, miter with { Cap = LineCap.Butt }));
        Assert.Equal(
            [
                (new Vector2(0, 0), -Vector2.UnitX),
                (new Vector2(10, 0), Vector2.UnitX),
                (new Vector2(20, 0), -Vector2.UnitX),
                (new Vector2(30, 0), Vector2.UnitX),
            ],
            Pieces(broken, miter with { Cap = LineCap.Square }).Select(p => (p.Point, p.In))
        );
        Assert.Equal(6, Pieces(runs, miter with { Cap = LineCap.Square }).Length);
    }

    /// <summary>
    /// With a dash pattern, a join lies only where a dash covers the point; a dash that ends or starts exactly at the point
    /// gets a cap there, along the segment it leaves by; a dash of length 0 there is a dot (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A right-angle turn at length 10, square caps and miter joins.
    ///
    /// Steps:
    /// - Record with patterns (20, 5) (on at the corner), (5, 10) (in a gap), (10, 5) (a dash ends at the corner), (5, 5) (a dash
    ///   starts at the corner), and (0, 10) (a dot at the corner).
    ///
    /// Expected:
    /// - A miter at the corner; nothing at it; a cap along +x at it; a cap along −y at it; a square along +y at it.
    /// </remarks>
    [Fact]
    public void Pieces_DashesAtTheCorner()
    {
        var style = new LineStyle(Black, 2) { Join = LineJoin.Miter, Cap = LineCap.Square };
        Vector2[] turn = [new(0, 0), new(10, 0), new(10, 20)];
        var corner = new Vector2(10, 0);

        PieceRecord[] At(LineDash dash) => [.. Pieces(turn, style with { Dash = dash }).Where(p => p.Point == corner)];

        Assert.Equal(PieceKind.Miter, Assert.Single(At(new LineDash(20, 5))).Kind);
        Assert.Empty(At(new LineDash(5, 10)));
        Assert.Equal(new PieceRecord(corner, Vector2.UnitX, Vector2.UnitX, PieceKind.SquareCap), Assert.Single(At(new LineDash(10, 5))));
        Assert.Equal(new PieceRecord(corner, -Vector2.UnitY, -Vector2.UnitY, PieceKind.SquareCap), Assert.Single(At(new LineDash(5, 5))));
        Assert.Equal(new PieceRecord(corner, Vector2.UnitY, Vector2.UnitY, PieceKind.Square), Assert.Single(At(new LineDash(0, 10))));
    }

    /// <summary>Recording lines that are not round again allocates nothing once the buffers have grown (design §5.1, invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - 16 M4-like polylines of 7,680 points, mitered and dashed with 8 intervals; one frame recorded to grow the buffers.
    ///
    /// Steps:
    /// - Clear and record the 16 polylines 10 times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Record_SteadyState_AllocatesNothing()
    {
        var random = new Random(4);
        var points = Enumerable.Range(0, 7680).Select(i => new Vector2(i / 4f, random.NextSingle() * 500)).ToArray();
        var style = new LineStyle(Black, 1.5f, LineDash.Of(6, 2, 1, 2, 3, 1, 1, 1)) { Join = LineJoin.Miter, Cap = LineCap.Square };
        var list = new RenderList();
        Frame();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            Frame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Frame()
        {
            list.Clear();
            for (var series = 0; series < 16; series++)
            {
                list.DrawPolyline(points, style);
            }
        }
    }

    /// <summary>
    /// The builder's join, cap, and miter limit enter the spec, and a change of them only records again (design §4.1, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line layer built with a miter join, square caps, limit 3, and an 8-interval pattern.
    ///
    /// Steps:
    /// - Read its style; render it, then with a bevel join, counting extractions and recordings.
    ///
    /// Expected:
    /// - The style holds them; the bevel records once and extracts nothing.
    /// </remarks>
    [Fact]
    public void Builder_JoinCapAndLimit()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new float[] { 1, 3, 2 });
        var table = DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("y", y).Build();
        var dash = LineDash.Of(4, 2, 1, 2, 3, 1, 1, 1);
        var spec = Plot.Create(table)
            .Line(table.Col<double>("t"), table.Col<float>("y"), l => l.Join(LineJoin.Miter).Cap(LineCap.Square).MiterLimit(3).Dash(dash))
            .Theme(TestPlans.Bare)
            .Build();
        var line = (LineLayerSpec)spec.Layers[0];
        using var plan = TestPlans.Create(pool, 100, 100);

        Assert.Equal(
            (LineJoin.Miter, LineCap.Square, 3f, (LineDash?)dash),
            (line.Style.Join, line.Style.Cap, line.Style.MiterLimit, line.Style.Dash)
        );
        _ = plan.Render(spec);
        var before = (plan.Extractions, plan.Recordings);
        _ = plan.Render(spec with { Layers = [line with { Style = line.Style with { Join = LineJoin.Bevel } }] });
        Assert.Equal((0, 1), (plan.Extractions - before.Extractions, plan.Recordings - before.Recordings));
    }

    private static PieceRecord[] Pieces(Vector2[] points, LineStyle style)
    {
        var list = new RenderList();
        list.DrawPolyline(points, style);
        return list.Pieces.ToArray();
    }
}
