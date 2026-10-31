using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The recording of polar trapezoids, <see cref="RenderList.DrawSectors"/> (design §5.1, §5.3, row G11).</summary>
public sealed class SectorListTests
{
    /// <summary>
    /// A call records one command of its pieces: each trapezoid cut into pieces of at most π / 16 with its radii taken
    /// linearly at the cuts; trapezoids that are not finite, empty, or past a full turn, and a center that is not finite,
    /// record nothing (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list clipped to (1, 2, 30, 40); a color of alpha 128.
    ///
    /// Steps:
    /// - Draw one call of a trapezoid from 0 to π / 2 with outer radii 10 to 30 and inner 0 to 4, one of π / 32, one with
    ///   NaN, one with From = To, one wider than 2π; then a call of only those dropped; then a call at a NaN center.
    ///
    /// Expected:
    /// - One command of kind Sectors; 8 + 1 pieces, the first 8 from 0 to π / 2 by π / 16 with radii linear in the angle
    ///   and the last trapezoid as given, each with its sides' directions (sin θ, −cos θ); the call's center, premultiplied color, clip, and a box that holds its pieces.
    ///   The other calls record nothing.
    /// </remarks>
    [Fact]
    public void DrawSectors_CutsAndDrops()
    {
        var list = new RenderList();
        list.SetClip(1, 2, 30, 40);
        ReadOnlySpan<SectorTrapezoid> sectors =
        [
            new(0, MathF.PI / 2, 10, 30, 0, 4),
            new(1, 1 + (MathF.PI / 32), 5, 5, 2, 2),
            new(float.NaN, 1, 5, 5, 2, 2),
            new(1, 1, 5, 5, 2, 2),
            new(0, 7, 5, 5, 2, 2),
        ];
        list.DrawSectors(sectors, new Vector2(50, 60), new Color(255, 0, 0, 128));

        Assert.Equal([new RenderCommand(RenderCommandKind.Sectors, 0)], list.Commands.ToArray());
        var call = list.SectorCalls[0];
        Assert.Equal((0, 9, new Vector2(50, 60)), (call.Start, call.Count, call.Center));
        Assert.Equal(new Vector4(128 / 255f, 0, 0, 128 / 255f), call.Color);
        Assert.Equal(new ClipRect(1, 2, 31, 42), call.Clip);
        var pieces = list.Sectors.ToArray();
        for (var j = 0; j < 8; j++)
        {
            var (s, e) = (j / 8f, (j + 1) / 8f);
            Assert.Equal(MathF.PI / 2 * s, pieces[j].From, 1e-5f);
            Assert.Equal(MathF.PI / 2 * e, pieces[j].To, 1e-5f);
            Assert.Equal(10 + (20 * s), pieces[j].OuterFrom, 1e-4f);
            Assert.Equal(10 + (20 * e), pieces[j].OuterTo, 1e-4f);
            Assert.Equal(4 * s, pieces[j].InnerFrom, 1e-4f);
            Assert.Equal(4 * e, pieces[j].InnerTo, 1e-4f);
        }

        Assert.Equal(MathF.PI / 2, pieces[7].To);
        Assert.Equal(
            new SectorInstance(1, 1 + (MathF.PI / 32), 5, 5, 2, 2, RenderList.Direction(1), RenderList.Direction(1 + (MathF.PI / 32)), 0, 0),
            pieces[8]
        );
        Assert.All(pieces, p => Assert.Equal((RenderList.Direction(p.From), RenderList.Direction(p.To)), (p.Start, p.End)));
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (var piece in pieces)
        {
            RenderList.SectorQuad(piece, call.Center, RenderList.SectorMargin, corners);
            foreach (var corner in corners)
            {
                Assert.InRange(corner.X, call.Min.X, call.Max.X);
                Assert.InRange(corner.Y, call.Min.Y, call.Max.Y);
            }
        }

        list.DrawSectors(sectors[2..], new Vector2(50, 60), new Color(255, 0, 0, 128));
        list.DrawSectors(sectors[..1], new Vector2(float.NaN, 60), new Color(255, 0, 0, 128));
        Assert.Single(list.Commands.ToArray());
        Assert.Equal(9, list.Sectors.Length);

        list.Clear();
        Assert.Equal((0, 0, 0), (list.Commands.Length, list.Sectors.Length, list.SectorCalls.Length));
    }

    /// <summary>
    /// Each call is its own step of the backend's draw: two calls that follow each other under the same clip are not joined,
    /// unlike rectangles and column trapezoids, since each call is one shape (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list with two sector calls under one clip, then a rectangle.
    ///
    /// Steps:
    /// - Prepare the steps with a text engine.
    ///
    /// Expected:
    /// - Steps (Sectors, 0, 1), (Sectors, 1, 1), (Rects, 0, 1).
    /// </remarks>
    [Fact]
    public void DrawSectors_EachCallIsAStep()
    {
        var list = new RenderList();
        ReadOnlySpan<SectorTrapezoid> one = [new(0, 1, 10, 10, 0, 0)];
        list.DrawSectors(one, new Vector2(20, 20), new Color(0, 0, 0, 255));
        list.DrawSectors(one, new Vector2(30, 20), new Color(0, 0, 0, 255));
        list.DrawRect(0, 0, 4, 4, new Color(0, 0, 0, 255));
        var engine = new TextEngine(new FakeTextBackend());

        engine.Prepare(list, 100, 100);

        Assert.Equal(
            [(DrawStepKind.Sectors, 0, 1), (DrawStepKind.Sectors, 1, 1), (DrawStepKind.Rects, 0, 1)],
            engine.Steps.ToArray().Select(s => (s.Kind, s.Index, s.Count)).ToArray()
        );
    }

    /// <summary>
    /// A piece's quad holds every point of the piece pushed out by the margin (design §5.3): the GPU covers each sample that
    /// lies in a piece.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 seeded pieces up to π / 16 wide at angles around the whole turn, with radii from −5 to 400 in any order, at a
    ///   center (300.5, 200.25).
    ///
    /// Steps:
    /// - Take 64 seeded points of each piece (an angle in it, a radius between its edges there) moved by up to √2 / 2 in a
    ///   seeded direction; test them against the quad's four edges.
    ///
    /// Expected:
    /// - Every point inside the quad.
    /// </remarks>
    [Fact]
    public void SectorQuad_HoldsThePiece()
    {
        var random = new Random(9);
        var center = new Vector2(300.5f, 200.25f);
        Span<Vector2> corners = stackalloc Vector2[4];
        for (var n = 0; n < 2000; n++)
        {
            var from = (float)((random.NextDouble() * 16) - 8);
            var span = (float)(random.NextDouble() * Math.PI / 16);
            var radii = Enumerable.Range(0, 4).Select(_ => (float)((random.NextDouble() * 405) - 5)).ToArray();
            var piece = new SectorInstance(
                from,
                from + span,
                radii[0],
                radii[1],
                radii[2],
                radii[3],
                RenderList.Direction(from),
                RenderList.Direction(from + span),
                0,
                0
            );
            RenderList.SectorQuad(piece, center, RenderList.SectorMargin, corners);
            ReadOnlySpan<Vector2> quad = [corners[0], corners[1], corners[3], corners[2]];
            for (var k = 0; k < 64; k++)
            {
                var t = (float)random.NextDouble();
                var angle = from + (span * t);
                var (a, b) = (radii[0] + ((radii[1] - radii[0]) * t), radii[2] + ((radii[3] - radii[2]) * t));
                var r = Math.Max(0, Math.Min(a, b) + ((float)random.NextDouble() * Math.Abs(b - a)));
                var push = (float)(random.NextDouble() * 2 * Math.PI);
                var point =
                    center + (new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * r) + (new Vector2(MathF.Cos(push), MathF.Sin(push)) * 0.7071f);
                Assert.True(Inside(quad, point), $"Piece {piece}, point {point}.");
            }
        }

        // Inside a convex quad whichever way it winds: every edge sees the point on the same side, within rounding.
        static bool Inside(ReadOnlySpan<Vector2> quad, Vector2 point)
        {
            var (positive, negative) = (false, false);
            for (var i = 0; i < 4; i++)
            {
                var (a, b) = (quad[i], quad[(i + 1) % 4]);
                var side = ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
                var tolerance = 1e-3f * (b - a).Length();
                positive |= side > tolerance;
                negative |= side < -tolerance;
            }

            return !(positive && negative);
        }
    }

    /// <summary>
    /// Around a circle, the overlap value counts the gap from the last label back to the first, unless the last tick lies
    /// within a pixel of the first; a label's extent along the circle is the larger of its width and the line height (design
    /// §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An axis of length 100 with the fake text backend at 12 px (em 12, 1.5 em = 18, line height 15, a digit 6.75 wide).
    ///
    /// Steps:
    /// - Measure labels at 0, 40, 80 straight and circular; at 0, 50, 99.5 circular.
    ///
    /// Expected:
    /// - Straight 1. Circular: the gap 20 − 15 = 5 gives 2 − 18 / 5 = −1.6. The ticks 0.5 apart around the circle wrap: 1,
    ///   and the axis says the last is not drawn.
    /// </remarks>
    [Fact]
    public void Circular_CountsTheGapBackToTheFirst()
    {
        var axis = new TickAxis
        {
            Scale = Scale.Linear(),
            Text = new TextEngine(new FakeTextBackend()),
            Labels = new LabelTable(),
            Style = new TextStyle(
                new Color(0, 0, 0, 255),
                12,
                "Fake",
                FontWeight.Normal,
                TextAlign.Left,
                TextBaseline.Top,
                TextRotation.None,
                "en-US"
            ),
            LineHeight = FakeTextBackend.LineHeight(12),
            Em = 12,
            Length = 100,
            Horizontal = true,
        };

        Assert.Equal(1, Measure(0, 40, 80));
        axis.Circular = true;
        Assert.Equal(2 - (18 / 5.0), Measure(0, 40, 80), 1e-9);
        Assert.False(axis.WrapsToFirst());
        Assert.Equal(1, Measure(0, 50, 99.5));
        Assert.True(axis.WrapsToFirst());

        double Measure(double a, double b, double c)
        {
            axis.ClearCandidate();
            axis.AddNumber(new TickNumber(1, 0), a);
            axis.AddNumber(new TickNumber(2, 0), b);
            axis.AddNumber(new TickNumber(3, 0), c);
            return axis.LabelNumbers(sameDecimals: true);
        }
    }
}
