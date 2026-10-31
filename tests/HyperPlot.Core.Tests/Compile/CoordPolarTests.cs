using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The polar coordinate system of row G11 (design §4.1, §4.4, §4.6, §4.8, §10 G11 row).</summary>
public sealed class CoordPolarTests
{
    /// <summary>
    /// <c>Coord.Polar</c> has the defaults of design §4.1 and checks its arguments; a spec made with <c>with</c> is checked
    /// when it is drawn (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over 800 points on a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Read Coord.Polar(); call it with a start that is not finite, inner radii below 0, at 1, and NaN, and a theta that is
    ///   not named; render specs whose polar system has those values set with <c>with</c>.
    ///
    /// Expected:
    /// - Theta X, Start 0, Clockwise, InnerRadius 0; ArgumentOutOfRangeException from each call naming its parameter;
    ///   ArgumentException from each render naming the member.
    /// </remarks>
    [Fact]
    public void Polar_Spec_DefaultsAndChecks()
    {
        var polar = Coord.Polar();
        Assert.Equal((PolarTheta.X, 0.0, true, 0.0), (polar.Theta, polar.Start, polar.Clockwise, polar.InnerRadius));
        Assert.True(new PolarCoordSpec().Clockwise);
        var given = Coord.Polar(PolarTheta.Y, 1.5, clockwise: false, innerRadius: 0.25);
        Assert.Equal((PolarTheta.Y, 1.5, false, 0.25), (given.Theta, given.Start, given.Clockwise, given.InnerRadius));

        Assert.Equal("start", Assert.Throws<ArgumentOutOfRangeException>(() => Coord.Polar(start: double.NaN)).ParamName);
        Assert.Equal("start", Assert.Throws<ArgumentOutOfRangeException>(() => Coord.Polar(start: double.PositiveInfinity)).ParamName);
        foreach (var inner in new[] { -0.1, 1, double.NaN })
        {
            Assert.Equal("innerRadius", Assert.Throws<ArgumentOutOfRangeException>(() => Coord.Polar(innerRadius: inner)).ParamName);
        }

        Assert.Equal("theta", Assert.Throws<ArgumentOutOfRangeException>(() => Coord.Polar((PolarTheta)2)).ParamName);

        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Build();
        var spec = Spec(table, new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") });
        using var plan = TestPlans.Create(pool, Size, Size);
        var bad = new (PolarCoordSpec Coord, string Member)[]
        {
            (polar with { Start = double.NaN }, "Start"),
            (polar with { InnerRadius = 1 }, "InnerRadius"),
            (polar with { InnerRadius = -0.5 }, "InnerRadius"),
            (polar with { Theta = (PolarTheta)2 }, "Theta"),
        };
        foreach (var (coord, member) in bad)
        {
            var error = Assert.Throws<ArgumentException>(() => plan.Render(spec with { Coord = coord }));
            Assert.Contains(member, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A change of kind, theta, or hole lays out, extracts, and records once; a change of start or direction lays out and
    /// records without extracting; the same values, in the same node or a new one, do nothing (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over 800 points with learned domains, drawn once in cartesian.
    ///
    /// Steps:
    /// - Render it polar; again; a new node of the same values; theta Y; an inner radius of 0.4; a start of π / 2;
    ///   anticlockwise; cartesian.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes: (1, 1, 1), (0, 0, 0), (0, 0, 0), (1, 1, 1), (1, 1, 1), (1, 0, 1),
    ///   (1, 0, 1), (1, 1, 1).
    /// </remarks>
    [Fact]
    public void Polar_Diff_StagesByMember()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Build();
        var spec = Plot.Create(table).Line(table.Col<double>("x"), table.Col<double>("y")).Theme(TestPlans.Bare).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(spec);
        var polar = spec with { Coord = Coord.Polar() };

        Assert.Equal((1, 1, 1), Counts(polar));
        Assert.Equal((0, 0, 0), Counts(polar));
        Assert.Equal((0, 0, 0), Counts(spec with { Coord = Coord.Polar() }));
        Assert.Equal((1, 1, 1), Counts(spec with { Coord = Coord.Polar(PolarTheta.Y) }));
        Assert.Equal((1, 1, 1), Counts(spec with { Coord = Coord.Polar(PolarTheta.Y, innerRadius: 0.4) }));
        Assert.Equal((1, 0, 1), Counts(spec with { Coord = Coord.Polar(PolarTheta.Y, Math.PI / 2, innerRadius: 0.4) }));
        Assert.Equal((1, 0, 1), Counts(spec with { Coord = Coord.Polar(PolarTheta.Y, Math.PI / 2, clockwise: false, innerRadius: 0.4) }));
        Assert.Equal((1, 1, 1), Counts(spec));

        (int, int, int) Counts(PlotSpec next)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            return (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings);
        }
    }

    /// <summary>
    /// A user's geom reads the stretched space from its frame, and Map takes it around the circle by the formula of design
    /// §4.4, in every combination of theta, direction, start, hole, and reverse; M4 runs over L + 1 boundaries when x is the
    /// angle and R − r0 + 1 when y is.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains [0, 100]: R = 50, L = ⌈2π · 50⌉ = 315; a geom that reads its frame at
    ///   four points; and a line over 800 points.
    ///
    /// Steps:
    /// - Render the geom with theta X and Y, clockwise and not, Start 0 and π / 2, InnerRadius 0 and 0.4, both scales
    ///   reversed and not; render the line with theta X and Y.
    ///
    /// Expected:
    /// - Polar true and Flipped false; the panel (0, 0, 100, 100); PixelX and PixelY the data's share of their axis's pixels
    ///   (L for the angle, R − r0 for the radius; from the other end when reversed); Map within 1e-3 of center + r · (sin θ,
    ///   −cos θ) with θ = σ · (Start + a / L · 2π) and r = r0 + c. The line's pixel columns are 315, then 50 − 0 = 50.
    /// </remarks>
    [Fact]
    public void Polar_Map_MatchesTheFormula()
    {
        using var pool = new PlotWorkerPool(1);
        using var few = Column.Wrap(new double[] { 1 });
        var table = DataTable.Create().Add("v", few).Build();
        var geom = new ProbeGeom();
        using var plan = TestPlans.Create(pool, Size, Size);
        foreach (var theta in new[] { PolarTheta.X, PolarTheta.Y })
        {
            foreach (var clockwise in new[] { true, false })
            {
                foreach (var start in new[] { 0, Math.PI / 2 })
                {
                    foreach (var inner in new[] { 0, 0.4 })
                    {
                        foreach (var reverse in new[] { false, true })
                        {
                            var spec = Spec(table, new CustomLayerSpec(geom)) with
                            {
                                Coord = Coord.Polar(theta, start, clockwise, inner),
                                ScaleX = Scale.Linear().WithDomain(0, Size) with { Reverse = reverse },
                                ScaleY = Scale.Linear().WithDomain(0, Size) with { Reverse = reverse },
                            };
                            _ = plan.Render(spec);
                            Check(theta, clockwise, start, inner, reverse);
                        }
                    }
                }
            }
        }

        using var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
        var lines = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Build();
        var line = Spec(lines, new LineLayerSpec { X = lines.Col<double>("x"), Y = lines.Col<double>("y") });
        _ = plan.Render(line with { Coord = Coord.Polar() });
        Assert.Equal(315, plan.PixelColumns);
        _ = plan.Render(line with { Coord = Coord.Polar(PolarTheta.Y, innerRadius: 0.4) });
        Assert.Equal(50 - 20, plan.PixelColumns);

        void Check(PolarTheta theta, bool clockwise, double start, double inner, bool reverse)
        {
            const int R = 50;
            const int L = 315;
            var r0 = (int)Math.Floor(inner * R);
            var (along, across) = theta == PolarTheta.X ? (L, R - r0) : (R - r0, L);
            Assert.Equal((true, false, (0, 0, 100, 100)), (geom.Polar, geom.Flipped, geom.Panel));
            for (var i = 0; i < ProbeGeom.Points.Length; i++)
            {
                var (x, y) = ProbeGeom.Points[i];
                var px = (reverse ? Size - x : x) / Size * along;
                var py = (reverse ? Size - y : y) / Size * across;
                Close(px, geom.Read[i].PixelX);
                Close(py, geom.Read[i].PixelY);
                var (a, c) = theta == PolarTheta.X ? (px, py) : (py, px);
                var angle = (clockwise ? 1 : -1) * (start + (a / L * 2 * Math.PI));
                var r = r0 + c;
                Close(50 + (r * Math.Sin(angle)), geom.Read[i].MapX);
                Close(50 - (r * Math.Cos(angle)), geom.Read[i].MapY);
            }
        }
    }

    /// <summary>
    /// A segment is cut into n = max(1, ⌈√((2|Δr||Δθ| + r Δθ²) / (8ε))⌉) pieces, and each piece's chord lies within 0.25
    /// pixels of the curve along which the angle and the radius change linearly (design §4.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Polar transforms of R = 540 with r0 0 and 216, theta X and Y, both directions; 400 seeded segments, from a few
    ///   pixels to several turns long, and a circle and a radial line.
    ///
    /// Steps:
    /// - Write each segment as a polyline; measure the distance from 16 points of each piece's curve to its chord.
    ///
    /// Expected:
    /// - 1 + n points a segment, with n from the formula computed here; every distance at most 0.25; the radial line 1
    ///   piece and the circle of radius 540 104. A point that is not finite breaks the line: a NaN in its place, and no
    ///   piece across it.
    /// </remarks>
    [Fact]
    public void Polar_Pieces_FollowTheCurve()
    {
        var random = new Random(11);
        var output = new Vector2[8];
        foreach (var theta in new[] { PolarTheta.X, PolarTheta.Y })
        {
            foreach (var clockwise in new[] { true, false })
            {
                foreach (var inner in new[] { 0, 216 })
                {
                    var polar = new PolarTransform(960, 540, 540, inner, Coord.Polar(theta, 0.3, clockwise));
                    var (l, ring) = (polar.Circumference, polar.Radius - polar.Inner);
                    for (var s = 0; s < 100; s++)
                    {
                        var scale = random.Next(3) switch
                        {
                            0 => 4.0,
                            1 => 200.0,
                            _ => 3.0 * l,
                        };
                        var (a0, c0) = (random.NextDouble() * l, random.NextDouble() * ring);
                        var (a1, c1) = (a0 + ((random.NextDouble() - 0.5) * scale), random.NextDouble() * ring);
                        Check(polar, a0, c0, a1, c1, output);
                    }
                }
            }
        }

        var turn = new PolarTransform(960, 540, 540, 0, Coord.Polar());
        Assert.Equal(1, turn.Pieces(10, 0, 10, 540));
        Assert.Equal(104, turn.Pieces(0, 540, turn.Circumference, 540));

        // A break: the runs before and after it are cut alone, and nothing joins them.
        Span<Vector2> broken = [new(0, 100), new(400, 300), new(float.NaN), new(900, 50), new(1500, 500)];
        var written = turn.Polyline(broken, ref output);
        var (first, second) = (turn.Pieces(0, 100, 400, 300), turn.Pieces(900, 50, 1500, 500));
        Assert.Equal(1 + first + 1 + 1 + second, written);
        Assert.True(float.IsNaN(output[1 + first].X));
        Assert.Equal(turn.Point(400, 300), output[first]);
        Assert.Equal(turn.Point(900, 50), output[2 + first]);

        static void Check(PolarTransform polar, double a0, double c0, double a1, double c1, Vector2[] output)
        {
            var dTheta = Math.Abs(a1 - a0) * 2 * Math.PI / polar.Circumference;
            var dRadius = Math.Abs(c1 - c0);
            var r = polar.Inner + Math.Max(c0, c1);
            var n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(((2 * dRadius * dTheta) + (r * dTheta * dTheta)) / (8 * 0.25))));
            Assert.Equal(n, polar.Pieces(a0, c0, a1, c1));
            Span<Vector2> stretched = polar.ThetaIsX
                ? [new((float)a0, (float)c0), new((float)a1, (float)c1)]
                : [new((float)c0, (float)a0), new((float)c1, (float)a1)];
            var count = polar.Polyline(stretched, ref output);
            Assert.Equal(1 + n, count);

            // The points the polyline holds are of the floats it was given; the curve is measured from the same floats.
            var (fa0, fc0, fa1, fc1) = polar.ThetaIsX
                ? (stretched[0].X, stretched[0].Y, stretched[1].X, stretched[1].Y)
                : (stretched[0].Y, stretched[0].X, stretched[1].Y, stretched[1].X);
            for (var k = 0; k < n; k++)
            {
                var (from, to) = (output[k], output[k + 1]);
                for (var j = 1; j <= 16; j++)
                {
                    var t = (k + (j / 17.0)) / n;
                    var curve = polar.Point(fa0 + ((fa1 - fa0) * t), fc0 + ((fc1 - fc0) * t));
                    Assert.InRange(Distance(curve, from, to), 0, 0.25 + 1e-3);
                }
            }
        }

        static double Distance(Vector2 p, Vector2 a, Vector2 b)
        {
            var (ab, ap) = (b - a, p - a);
            var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(ap, ab) / ab.LengthSquared(), 0, 1) : 0;
            return (ap - (ab * (float)t)).Length();
        }
    }

    /// <summary>
    /// With y the angle, a column trapezoid of the stretched space (sides along the radius, edges in the angle) becomes polar
    /// trapezoids that cover the same region: flat, sloped, and crossing edges (design §4.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A polar transform of R = 540, theta Y, clockwise and not; column trapezoids one pixel of radius wide: flat edges,
    ///   both edges sloped one way, sloped toward each other, and edges that cross inside the column.
    ///
    /// Steps:
    /// - Write each as polar trapezoids; test 4,000 seeded points of the stretched space around it against the region and
    ///   against the trapezoids (the point's angle and radius, folded into each trapezoid's turn).
    ///
    /// Expected:
    /// - At most 6 trapezoids; a point inside the region is inside one of them, and a point outside is in none, except within
    ///   1e-3 pixels of an edge (measured across the edge: on a steep one, a step in angle is a far smaller one in radius).
    /// </remarks>
    [Fact]
    public void Polar_ThetaYColumns_CoverTheRegion()
    {
        var random = new Random(13);
        var output = new SectorTrapezoid[6];
        ColumnTrapezoid[] columns =
        [
            new(100, 101, 500, 500, 800, 800),
            new(200, 201, 300, 900, 1200, 1700),
            new(300, 301, 2000, 1500, 1400, 2100),
            new(400, 401, 600, 1300, 1000, 700),
        ];
        foreach (var clockwise in new[] { true, false })
        {
            var polar = new PolarTransform(960, 540, 540, 0, Coord.Polar(PolarTheta.Y, 0.2, clockwise));
            foreach (var column in columns)
            {
                var count = polar.Columns(column, output);
                Assert.InRange(count, 1, 6);
                for (var k = 0; k < 4000; k++)
                {
                    var c = column.Left + (random.NextDouble() * (column.Right - column.Left));
                    var t = (c - column.Left) / (column.Right - column.Left);
                    var top = column.TopLeft + ((column.TopRight - column.TopLeft) * t);
                    var bottom = column.BottomLeft + ((column.BottomRight - column.BottomLeft) * t);
                    var a = Math.Min(top, bottom) - 50 + (random.NextDouble() * (Math.Abs(top - bottom) + 100));
                    var inRegion = a >= Math.Min(top, bottom) && a <= Math.Max(top, bottom);
                    var (topSlope, bottomSlope) = (
                        (column.TopRight - column.TopLeft) / (column.Right - column.Left),
                        (column.BottomRight - column.BottomLeft) / (column.Right - column.Left)
                    );
                    var nearEdge =
                        Math.Min(
                            Math.Abs(a - top) / Math.Sqrt(1 + (topSlope * topSlope)),
                            Math.Abs(a - bottom) / Math.Sqrt(1 + (bottomSlope * bottomSlope))
                        ) < 1e-3;
                    var inSectors = output[..count].Any(s => Inside(s, polar.Theta(a), polar.RadiusOf(c)));
                    Assert.True(inRegion == inSectors || nearEdge, $"Column {column}, clockwise {clockwise}: (a {a}, c {c}).");
                }
            }
        }

        static bool Inside(SectorTrapezoid s, double angle, double r)
        {
            var span = (double)s.To - s.From;
            var t = angle - s.From;
            t -= 2 * Math.PI * Math.Floor(t / (2 * Math.PI));
            if (t > span + 1e-6)
            {
                return false;
            }

            var share = Math.Min(1, t / span);
            var outer = s.OuterFrom + ((s.OuterTo - s.OuterFrom) * share);
            var inner = s.InnerFrom + ((s.InnerTo - s.InnerFrom) * share);
            return r >= Math.Min(inner, outer) - 1e-3 && r <= Math.Max(inner, outer) + 1e-3;
        }
    }

    /// <summary>
    /// With x the angle, an M4 line's pieces are at most twice its segments (design §4.4): a segment inside a pixel of angle
    /// or to the next one bends little.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A seeded noisy line over 20,000 points, domains [0, 100], without guides; a 100 × 100 target polar (R = 50,
    ///   L = 315) and a 315 × 50 target cartesian, so that M4 runs over the same 316 boundaries.
    ///
    /// Steps:
    /// - Render both; count each list's segments.
    ///
    /// Expected:
    /// - The polar segments at least the cartesian ones (M4's) and at most twice them.
    /// </remarks>
    [Fact]
    public void Polar_M4Line_AtMostTwicePieces()
    {
        using var pool = new PlotWorkerPool(1);
        var random = new Random(5);
        using var y = Column.Wrap(
            Enumerable.Range(0, 20_000).Select(i => 50 + (30 * Math.Sin(i * 0.001)) + (random.NextDouble() * 30) - 15).ToArray()
        );
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.005, 20_000)).Add("y", y).Build();
        var spec = Spec(table, new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") });
        using var polar = TestPlans.Create(pool, Size, Size);
        using var cartesian = TestPlans.Create(pool, 315, 50);

        var pieces = polar.Render(spec with { Coord = Coord.Polar() }).Segments.Length;
        var segments = cartesian.Render(spec).Segments.Length;
        Assert.Equal(polar.PixelColumns, cartesian.PixelColumns);
        Assert.InRange(pieces, segments, 2 * segments);
    }

    /// <summary>
    /// Bars, rects, areas, and bands record polar trapezoids, a layer's fills as one call (design §4.4, §5.1); an area has a
    /// trapezoid an angle pixel at most. A vertical rule is a line out from the center and a horizontal one a circle.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains [0, 100], Coord.Polar(): R = 50, L = 315. Layers alone: 4 bars; 800 bars
    ///   (each one, under 4L); an area and a band over 800 points; 4 rects; a vertical rule at 25 and a horizontal one at
    ///   40.
    ///
    /// Steps:
    /// - Render each; read the commands, the sector calls, and the rules' points.
    ///
    /// Expected:
    /// - Each fill layer: the panel's rectangle, then one sector call in its fill, clipped to the panel, its trapezoids cut
    ///   in pieces of at most π / 16: 4 bars 4 wide, 0.25 radians each, 8 pieces; 4 rects 5, 10, 10, and 5 wide, 12; the
    ///   area 300 to 315 (a trapezoid an angle pixel, each holding points). A rect from −50 to 150 is cut to a full turn:
    ///   32 pieces spanning under 2π. The vertical rule's points on the line through the center at θ = 25 / 100 · 2π, from
    ///   the center to radius 50; the horizontal rule's at radius 20 from the center, around the whole circle.
    /// </remarks>
    [Fact]
    public void Polar_FillsAndRules()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
        using var lo = Column.Wrap(Enumerable.Range(0, 800).Select(i => 30 + (10 * Math.Sin(i * 0.07))).ToArray());
        using var fx = Column.Wrap(new double[] { 10, 30, 60, 90 });
        using var fy = Column.Wrap(new double[] { 20, 70, 45, 90 });
        using var rule = Column.Wrap(new double[] { 25 });
        using var hrule = Column.Wrap(new double[] { 40 });
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Add("lo", lo).Build();
        var few = DataTable.Create().Add("x", fx).Add("y", fy).Build();
        var rules = DataTable.Create().Add("v", rule).Add("h", hrule).Build();
        var fill = new Color(10, 20, 30, 200);
        var (x, yy, fxr, fyr) = (table.Col<double>("x"), table.Col<double>("y"), few.Col<double>("x"), few.Col<double>("y"));
        var fills = new (DataTable Table, LayerSpec Layer, int? Pieces)[]
        {
            (
                few,
                new BarLayerSpec
                {
                    X = fxr,
                    Y = fyr,
                    Width = 4,
                    Fill = fill,
                },
                8
            ),
            (
                table,
                new BarLayerSpec
                {
                    X = x,
                    Y = yy,
                    Fill = fill,
                },
                null
            ),
            (
                table,
                new AreaLayerSpec
                {
                    X = x,
                    Y = yy,
                    Fill = fill,
                },
                null
            ),
            (
                table,
                new BandLayerSpec
                {
                    X = x,
                    YMin = table.Col<double>("lo"),
                    YMax = yy,
                    Fill = fill,
                },
                null
            ),
        };
        using var plan = TestPlans.Create(pool, Size, Size);
        foreach (var (data, layer, pieces) in fills)
        {
            var list = plan.Render(Spec(data, layer) with { Coord = Coord.Polar() });
            Assert.Equal([new RenderCommand(RenderCommandKind.Rect, 0), new RenderCommand(RenderCommandKind.Sectors, 0)], list.Commands.ToArray());
            var call = list.SectorCalls[0];
            Assert.Equal(new ClipRect(0, 0, Size, Size), call.Clip);
            Assert.Equal(new Vector2(50, 50), call.Center);
            Assert.Equal(200 / 255f, call.Color.W);
            if (pieces is { } expected)
            {
                Assert.Equal(expected, call.Count);
            }

            if (layer is AreaLayerSpec)
            {
                // Every angle pixel holds points: a trapezoid each, nearly all of the 315.
                Assert.InRange(call.Count, 300, 315);
            }
        }

        using var x1 = Column.Wrap(new double[] { 15, 40, 70, 95 });
        var wide = DataTable.Create().Add("x0", fx).Add("x1", x1).Add("y", fy).Build();
        var rectList = plan.Render(
            Spec(
                wide,
                new RectLayerSpec
                {
                    XMin = wide.Col<double>("x0"),
                    XMax = wide.Col<double>("x1"),
                    YMin = wide.Col<double>("y"),
                    YMax = wide.Col<double>("x1"),
                    Fill = fill,
                }
            ) with
            {
                Coord = Coord.Polar(),
            }
        );
        Assert.Equal([new RenderCommand(RenderCommandKind.Rect, 0), new RenderCommand(RenderCommandKind.Sectors, 0)], rectList.Commands.ToArray());
        Assert.Equal(2 + 4 + 4 + 2, rectList.SectorCalls[0].Count);

        // A rect from −50 to 150 on [0, 100], not clamped, is cut to a full turn: 32 pieces under 2π.
        using var past = Column.Wrap(new double[] { -50 });
        using var beyond = Column.Wrap(new double[] { 150 });
        var around = DataTable.Create().Add("a", past).Add("b", beyond).Build();
        var turn = plan.Render(
            Spec(
                around,
                new RectLayerSpec
                {
                    XMin = around.Col<double>("a"),
                    XMax = around.Col<double>("b"),
                    YMin = around.Col<double>("a"),
                    YMax = around.Col<double>("b"),
                    Fill = fill,
                }
            ) with
            {
                Coord = Coord.Polar(),
            }
        );
        Assert.Equal(32, turn.SectorCalls[0].Count);
        Assert.InRange(turn.Sectors[^1].To - turn.Sectors[0].From, 2 * MathF.PI * 0.999f, 2 * MathF.PI);

        var vertical = plan.Render(Spec(rules, new RuleLayerSpec { X = rules.Col<double>("v") }) with { Coord = Coord.Polar() });
        var direction = new Vector2(MathF.Sin(MathF.PI / 2), -MathF.Cos(MathF.PI / 2));
        var points = Points(vertical);
        Assert.All(points, p => Assert.InRange(Cross(direction, p - new Vector2(50, 50)), -1e-3, 1e-3));
        Close(0, (points[0] - new Vector2(50, 50)).Length());
        Close(50, (points[^1] - new Vector2(50, 50)).Length());

        var horizontal = Points(plan.Render(Spec(rules, new RuleLayerSpec { Y = rules.Col<double>("h") }) with { Coord = Coord.Polar() }));
        Assert.All(horizontal, p => Close(20, (p - new Vector2(50, 50)).Length()));
        Close(0, (horizontal[0] - horizontal[^1]).Length());
        Assert.True(horizontal.Length > 8);

        static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

        static Vector2[] Points(RenderList list)
        {
            var segments = SegmentsOf(list, 0);
            return [.. segments.Select(s => s.Start), segments[^1].End];
        }
    }

    /// <summary>
    /// The polar layout of design §4.6: the circle in the panel's center, R = ⌊min(W, H) / 2⌋ − Θ with Θ the band of the
    /// angle's labels, the radius's axis a vertical line at x = center − R − Θ, the angle's labels outside the circle, the
    /// last tick of a full turn not drawn, the angle's ticks chosen around a circle with the horizontal spacing and the
    /// radius's over a radius with the vertical one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme and the fake text backend; a line with x's domain [0, 100] (its ticks end on
    ///   100, a full turn back at 0) and y's [0, 10]; axis titles and a title.
    ///
    /// Steps:
    /// - Render; read the guides, the texts, and the polylines.
    ///
    /// Expected:
    /// - The center (Left + W / 2, Top + H / 2); Θ = T + G + max(⌈widest⌉, ⌈line height⌉) + G; R as above. X circular over
    ///   ⌈2π Rw⌉ with the spacing 100, Rw = ⌊min(W, H) / 2⌋ − (T + G + ⌈line height⌉ + G); Y over Rh − 0 with 60,
    ///   Rh = ⌊H / 2⌋ − the same band. A vertical axis line at the snapped center − R − Θ − w / 2 from the center's height
    ///   to R above. Each angle label but the last at its place outside the circle; the label of 100 not drawn, nor its grid
    ///   line: one line out of the center for each other tick. The second tick's label is right of the center, and left of
    ///   it with x reversed.
    /// </remarks>
    [Fact]
    public void Polar_Layout()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 5 + (4 * Math.Sin(i * 0.05))).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Build();
        var theme = Theme.Light;
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<double>("y"))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("X"))
            .ScaleY(Scale.Linear().WithDomain(0, 10).WithTitle("Y"))
            .Title("Polar")
            .Coord(Coord.Polar())
            .Build();
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var list = plan.Render(spec);
        var g = plan.Guides;
        var polar = g.Polar!.Value;

        int Round(float v) => (int)Math.Floor(v + 0.5);
        double Width(string s) => s.Length * FakeTextBackend.Advance(theme.TickLabelSize);
        var (gap, tick) = (Round(theme.Spacing), Round(theme.TickLength));
        var line = (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.TickLabelSize));
        var band = tick + gap + Math.Max((int)Math.Ceiling(g.X.TickLabels.ToArray().Max(Width)), line) + gap;
        var provisional = tick + gap + line + gap;
        Assert.Equal(band, g.ThetaBand);
        Assert.Equal((g.Left + (g.Width / 2.0), g.Top + (g.Height / 2.0)), (polar.CenterX, polar.CenterY));
        Assert.Equal((Math.Min(g.Width, g.Height) / 2) - band, polar.Radius);
        Assert.Equal(
            (true, (double)PolarTransform.Turn((Math.Min(g.Width, g.Height) / 2) - provisional), theme.TickSpacingX),
            (g.X.Circular, g.X.Length, g.X.Spacing)
        );
        Assert.Equal((false, (double)((g.Height / 2) - provisional), theme.TickSpacingY), (g.Y.Circular, g.Y.Length, g.Y.Spacing));

        // The radius's axis: a vertical line left of the circle and its labels, from the center's height up R.
        var edge = polar.CenterX - polar.Radius - band;
        var axisWidth = Round(theme.AxisWidth);
        var x = (float)(axisWidth % 2 == 1 ? Math.Floor(edge - (axisWidth / 2.0)) + 0.5 : Math.Floor(edge - (axisWidth / 2.0) + 0.5));
        var segments = list.Segments.ToArray();
        Assert.Contains(
            segments,
            s => s.Start.X == x && s.End.X == x && s.Start.Y == (float)polar.CenterY && s.End.Y == (float)(polar.CenterY - polar.Radius)
        );

        // The angle's labels: the last tick, 100, is the first's angle and is not drawn.
        var labels = g.X.TickLabels.ToArray();
        Assert.Equal("100", labels[^1]);
        Assert.True(g.X.WrapsToFirst());
        var texts = list.Texts.ToArray();
        Assert.DoesNotContain(texts, t => t.Text == "100");
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var a = g.X.Pixels[i] / g.X.Length * polar.Circumference;
            var angle = polar.Theta(a);
            var at = polar.Point(a, polar.Radius - polar.Inner + tick + gap);
            var width = (float)Width(labels[i]);
            var height = FakeTextBackend.LineHeight(theme.TickLabelSize);
            var position = new Vector2(
                at.X + (MathF.Sin((float)angle) * width / 2) - (width / 2),
                at.Y - (MathF.Cos((float)angle) * height / 2) - (height / 2)
            );
            Assert.Contains(texts, t => t.Text == labels[i] && Vector2.Distance(t.Position, position) < 1e-3);
        }

        // The grid's lines out of the center: one a tick but the last.
        var center = new Vector2((float)polar.CenterX, (float)polar.CenterY);
        var outOfCenter = list
            .Polylines.ToArray()
            .Count(p => p.SegmentCount == 1 && Vector2.Distance(list.Segments[p.SegmentStart].Start, center) < 1e-3);
        Assert.Equal(labels.Length - 1, outOfCenter);

        // The second tick lies within the first half turn: right of the center, and left of it reversed. The angle's labels
        // are the texts aligned by their top; the radius's are aligned by their middle.
        Assert.InRange(g.X.Pixels[1] / g.X.Length, 0.01, 0.49);
        Assert.True(texts.Single(t => t.Text == labels[1] && t.Style.Vertical == TextBaseline.Top).Position.X > center.X);
        using var reversed = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var mirrored = reversed.Render(spec with { ScaleX = Scale.Linear().WithDomain(0, 100).WithTitle("X") with { Reverse = true } });
        Assert.True(mirrored.Texts.ToArray().Single(t => t.Text == labels[1] && t.Style.Vertical == TextBaseline.Top).Position.X < center.X);
    }

    /// <summary>
    /// A target too small for a ring of a pixel draws only the background (design §4.6); data outside a domain that does not
    /// clamp goes past the circle, clipped to the panel (design §4.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 40 target with the light theme (a ring below 0); a 100 × 1 target without guides (a ring of 0); a 200 × 100
    ///   target without guides, a line through y = 150 over the domain [0, 100].
    ///
    /// Steps:
    /// - Render a line polar on each.
    ///
    /// Expected:
    /// - No command on the small targets. On the wide one, the line's clip the panel (0, 0, 200, 100), and a point 75 from
    ///   the center: past R = 50, inside the panel.
    /// </remarks>
    [Fact]
    public void Polar_SmallTarget_AndOutsideTheDomain()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new double[] { 50, 150, 50 });
        var table = DataTable.Create().Add("x", Column.Uniform(20, 10, 3)).Add("y", y).Build();
        var line = new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        using var small = new PlotPlan(pool, 640, 40, new TextEngine(new FakeTextBackend()), 1);
        Assert.Empty(small.Render(Spec(table, line) with { Theme = Theme.Light, Coord = Coord.Polar() }).Commands.ToArray());

        // A ring of exactly 0: a panel 1 pixel high without guides, R = ⌊1 / 2⌋ = 0.
        using var flat = TestPlans.Create(pool, 100, 1);
        Assert.Empty(flat.Render(Spec(table, line) with { Coord = Coord.Polar() }).Commands.ToArray());

        using var wide = TestPlans.Create(pool, 200, 100);
        var list = wide.Render(Spec(table, line) with { Coord = Coord.Polar() });
        Assert.Equal(new ClipRect(0, 0, 200, 100), list.Polylines[0].Clip);
        var farthest = list.Segments.ToArray().Max(s => Math.Max((s.Start - new Vector2(100, 50)).Length(), (s.End - new Vector2(100, 50)).Length()));
        Close(75, farthest);
    }

    /// <summary>
    /// A radius pixel below 0, inside the inner edge, is outside the radius's domain (design §4.4, PR #150's review): a line
    /// is cut where it crosses the inner edge and does not pass the center, a fill stops at the inner edge, and a user's
    /// geom maps such a point to NaN.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains [0, 100], R = 50: a line through (20, 50), (30, −50), (40, 50), and
    ///   (50, 150); a rect from y −50 to 50; an area of baseline −50 over 800 points. Polar transforms of R = 540 with r0 0
    ///   and 216, theta X and Y; column trapezoids whose edges cross c = 0 or whose sides lie below it.
    ///
    /// Steps:
    /// - Render the layers with InnerRadius 0 and 0.4; write the trapezoids; test 4,000 seeded points of each against the
    ///   region above c = 0 and against the polar trapezoids; map points below c = 0.
    ///
    /// Expected:
    /// - The line: every point at least r0 from the center, and every point right of it (its angles are 72° to 180°: before
    ///   the cut, the point at −50 was drawn left of the center); points on the inner edge where it is cut, and with the
    ///   hole a break between them. The rect's and the area's trapezoids: no radius below r0. A point inside the region above c = 0
    ///   is in a trapezoid and one outside is in none, within 1e-3 pixels of an edge. Map gives NaN below c = 0.
    /// </remarks>
    [Fact]
    public void Polar_InnerEdge_CutsLinesAndFills()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new double[] { 50, -50, 50, 150 });
        using var low = Column.Wrap(new double[] { -50 });
        using var high = Column.Wrap(new double[] { 50 });
        using var left = Column.Wrap(new double[] { 40 });
        using var right = Column.Wrap(new double[] { 60 });
        using var a = Column.Wrap(Enumerable.Range(0, 800).Select(i => 30 + (40 * Math.Sin(i * 0.05))).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(20, 10, 4)).Add("y", y).Build();
        var rects = DataTable.Create().Add("lo", low).Add("hi", high).Add("left", left).Add("right", right).Build();
        var area = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("a", a).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var center = new Vector2(50, 50);
        foreach (var inner in new[] { 0, 0.4 })
        {
            var r0 = (float)Math.Floor(inner * 50);
            var coord = Coord.Polar(innerRadius: inner);
            var list = plan.Render(Spec(table, new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") }) with { Coord = coord });
            var points = list.Segments.ToArray().SelectMany(s => new[] { s.Start, s.End }).ToArray();
            Assert.All(points, p => Assert.True((p - center).Length() >= r0 - 1e-3 && p.X >= center.X - 1e-3, $"{p} with r0 {r0}."));
            Assert.Contains(points, p => Math.Abs((p - center).Length() - r0) < 1e-3);

            // With a hole, the break shows: the line leaves the inner edge at one angle and comes back at another. Without
            // one, both cuts are the center.
            var segments = list.Segments.ToArray();
            Assert.Equal(inner > 0, Enumerable.Range(1, segments.Length - 1).Any(i => segments[i].Start != segments[i - 1].End));

            var rect = new RectLayerSpec
            {
                XMin = rects.Col<double>("left"),
                XMax = rects.Col<double>("right"),
                YMin = rects.Col<double>("lo"),
                YMax = rects.Col<double>("hi"),
            };
            var under = new AreaLayerSpec
            {
                X = area.Col<double>("x"),
                Y = area.Col<double>("a"),
                Baseline = -50,
            };
            foreach (var (data, layer) in new (DataTable, LayerSpec)[] { (rects, rect), (area, under) })
            {
                var fills = plan.Render(Spec(data, layer) with { Coord = coord }).Sectors.ToArray();
                Assert.NotEmpty(fills);
                Assert.All(
                    fills,
                    s => Assert.True(Math.Min(Math.Min(s.InnerFrom, s.InnerTo), Math.Min(s.OuterFrom, s.OuterTo)) >= r0 - 1e-3, $"{s}")
                );
            }
        }

        var random = new Random(17);
        var output = new SectorTrapezoid[6];
        ColumnTrapezoid[] columns =
        [
            new(100, 103, -200, 300, 250, 250),
            new(200, 203, -100, -50, 300, -80),
            new(300, 302, -100, -50, -20, -10),
            new(-40, 30, 500, 900, 1200, 1100),
            new(-40, -5, 500, 900, 1200, 1100),
        ];
        foreach (var theta in new[] { PolarTheta.X, PolarTheta.Y })
        {
            foreach (var inner in new[] { 0, 216 })
            {
                var polar = new PolarTransform(960, 540, 540, inner, Coord.Polar(theta, 0.2));

                // The radius is y with theta X and x with theta Y: below 0 it maps to NaN, the angle may be anything.
                var (inside, outside) = theta == PolarTheta.X ? (polar.Map(-1, 10), polar.Map(10, -1)) : (polar.Map(10, -1), polar.Map(-1, 10));
                Assert.True(float.IsFinite(inside.X) && float.IsNaN(outside.X) && float.IsNaN(outside.Y));
                foreach (var column in columns)
                {
                    var count = polar.Columns(column, output);
                    for (var k = 0; k < 4000; k++)
                    {
                        var along = column.Left + (random.NextDouble() * (column.Right - column.Left));
                        var t = (along - column.Left) / (column.Right - column.Left);
                        var top = column.TopLeft + ((column.TopRight - column.TopLeft) * t);
                        var bottom = column.BottomLeft + ((column.BottomRight - column.BottomLeft) * t);
                        var across = Math.Min(top, bottom) - 50 + (random.NextDouble() * (Math.Abs(top - bottom) + 100));
                        var (pa, pc) = theta == PolarTheta.X ? (along, across) : (across, along);
                        var inRegion = across >= Math.Min(top, bottom) && across <= Math.Max(top, bottom) && pc >= 0;
                        var (topSlope, bottomSlope) = (
                            (column.TopRight - column.TopLeft) / (column.Right - column.Left),
                            (column.BottomRight - column.BottomLeft) / (column.Right - column.Left)
                        );
                        var nearEdge =
                            Math.Abs(across - top) / Math.Sqrt(1 + (topSlope * topSlope)) < 1e-3
                            || Math.Abs(across - bottom) / Math.Sqrt(1 + (bottomSlope * bottomSlope)) < 1e-3
                            || Math.Abs(pc) < 1e-3;
                        var inSectors = output[..count].Any(s => InSector(s, polar.Theta(pa), polar.RadiusOf(pc)));
                        Assert.True(
                            inRegion == inSectors || nearEdge,
                            $"Column {column}, theta {theta}, r0 {inner}: (along {along}, across {across})."
                        );
                    }
                }
            }
        }

        static bool InSector(SectorTrapezoid s, double angle, double r)
        {
            var span = (double)s.To - s.From;
            var t = angle - s.From;
            t -= 2 * Math.PI * Math.Floor(t / (2 * Math.PI));
            if (t > span + 1e-6)
            {
                return false;
            }

            var share = Math.Min(1, t / span);
            var outer = s.OuterFrom + ((s.OuterTo - s.OuterFrom) * share);
            var inner = s.InnerFrom + ((s.InnerTo - s.InnerFrom) * share);
            return r >= Math.Min(inner, outer) - 1e-3 && r <= Math.Max(inner, outer) + 1e-3;
        }
    }

    /// <summary>
    /// Once warm, a polar spec drawn again allocates nothing, its geoms, the pieces of its lines, its fills, and its guides
    /// included (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme; a polar spec of a line, an area, a band, bars, error bars, and rules over
    ///   800 points with a hole, axis titles, and a title, drawn once.
    ///
    /// Steps:
    /// - Render it five times; then a new node of the same spec five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread both times.
    /// </remarks>
    [Fact]
    public void Polar_Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var yc = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
        using var loc = Column.Wrap(Enumerable.Range(0, 800).Select(i => 30 + (10 * Math.Sin(i * 0.07))).ToArray());
        var t = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", yc).Add("lo", loc).Build();
        var (x, y, lo) = (t.Col<double>("x"), t.Col<double>("y"), t.Col<double>("lo"));
        var spec = Plot.Create(t)
            .Line(x, y)
            .Area(x, y, baseline: 50)
            .Band(x, lo, y)
            .Bar(x, y)
            .ErrorBar(x, lo, y)
            .VRule(x)
            .HRule(x)
            .ScaleX(Scale.Linear().WithTitle("X"))
            .ScaleY(Scale.Linear().WithTitle("Y"))
            .Title("Polar")
            .Coord(Coord.Polar(innerRadius: 0.2))
            .Build();
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        _ = plan.Render(spec);
        _ = plan.Render(spec with { Title = "Polar" });

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var again = spec with { Title = "Polar" };
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(again);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A geom that reads its frame at four points and draws nothing.</summary>
    private sealed class ProbeGeom : IGeom
    {
        public static readonly (double X, double Y)[] Points = [(10, 20), (35.5, 70), (60, 15), (95, 88)];

        public (double PixelX, double PixelY, double MapX, double MapY)[] Read { get; } = new (double, double, double, double)[4];

        public bool Polar { get; private set; }

        public bool Flipped { get; private set; }

        public (int Left, int Top, int Width, int Height) Panel { get; private set; }

        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list)
        {
            (Polar, Flipped, Panel) = (frame.Polar, frame.Flipped, (frame.Left, frame.Top, frame.Width, frame.Height));
            for (var i = 0; i < Points.Length; i++)
            {
                frame.Map(Points[i].X, Points[i].Y, out var mx, out var my);
                Read[i] = (frame.PixelX(Points[i].X), frame.PixelY(Points[i].Y), mx, my);
            }
        }
    }
}
