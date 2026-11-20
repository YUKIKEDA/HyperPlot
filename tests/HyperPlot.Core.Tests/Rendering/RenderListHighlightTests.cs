using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// A rectangle cut by a lasso's mask (design §6.4 "密度の強調", §10 R3c row, Issue #576): the runs read from the mask's
/// words against <see cref="RenderList.Holds"/> asked at every pixel's center.
/// </summary>
public sealed class RenderListHighlightTests
{
    private const int MaskLeft = 7;
    private const int MaskTop = 5;
    private const int MaskHeight = 9;
    private static readonly Color Kept = new(10, 20, 30, 200);
    private static readonly Color Faded = new(10, 20, 30, 40);

    /// <summary>The bits a test's mask holds.</summary>
    public enum Shape
    {
        /// <summary>A concave polygon: a box with a notch cut down from its top.</summary>
        Notched,

        /// <summary>A star of five points, concave at each inner vertex, whose points reach past the mask.</summary>
        Star,

        /// <summary>A triangle wider and taller than the mask, cut by its four edges.</summary>
        CutByTheEdges,

        /// <summary>Every other pixel of a row, the rows offset in turn.</summary>
        Comb,

        /// <summary>Every pixel.</summary>
        Full,

        /// <summary>No pixel.</summary>
        Empty,

        /// <summary>Seeded random bits.</summary>
        Random,
    }

    /// <summary>
    /// <see cref="RenderList.DrawRectCutByMask"/> draws each pixel of the mask's box as <see cref="RenderList.Holds"/> judges
    /// its center: the rectangle's part of the pixel in the color of the inside when the region holds the center, of the
    /// outside when not, from one rectangle a pixel, and no more rectangles than the runs of the rows; it draws nothing
    /// outside the box (Issue #576).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A mask at (7, 5), 9 rows high and 1, 31, 32, 33, 64, or 70 pixels wide (one word a row, a full word, a word and a
    ///   bit, two full words), whose bits are: a notched polygon, a five-pointed star reaching past the mask, a triangle cut
    ///   by the mask's four edges (each by the even-odd rule at the pixels' centers), a comb of every other pixel, every
    ///   pixel, none, or seeded random bits.
    /// - Rectangles with edges at quarters of a pixel: inside the mask, the mask's own, past every edge of it, across its
    ///   left and top, across its right and bottom, left of it, above it, below it, inside one pixel, one pixel row high
    ///   across the whole mask, and of no width.
    ///
    /// Steps:
    /// - For each rectangle: clear the list's commands, cut the rectangle by the mask, and read the recorded rectangles.
    /// - Over every pixel from 8 left of and above the mask to 14 right of and below it, sum the area of each recorded
    ///   rectangle on the pixel by its color, and count the recorded rectangles that touch it.
    /// - By a loop over the same pixels, ask Holds at each center, and count a rectangle a run of a row's pixels of the box
    ///   that the rectangle touches and that are judged alike.
    ///
    /// Expected:
    /// - A mask wider than a pixel has a bit set and a bit clear, but the full and the empty ones.
    /// - Each pixel of the box: the area in the inside's color is the rectangle's area on the pixel when Holds, 0 when not;
    ///   the area in the outside's color is the rest; exactly, the edges being quarters. No pixel is touched by two
    ///   rectangles. Each pixel outside the box: no area.
    /// - The count of recorded rectangles is the loop's count.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Masks))]
    public void DrawRectCutByMask_EveryPixel_IsDrawnAsHoldsJudgesItsCenter(Shape shape, int width)
    {
        var list = new RenderList();
        var number = list.AddHighlight(MaskLeft, MaskTop, width, MaskHeight, out var mask);
        var (set, clear) = Fill(mask, shape, width);
        Assert.True(set || shape == Shape.Empty || width == 1, "No bit of the mask is set.");
        Assert.True(clear || shape == Shape.Full || width == 1, "No bit of the mask is clear.");
        var (right, bottom) = (MaskLeft + width, MaskTop + MaskHeight);
        (float Left, float Top, float Right, float Bottom)[] rectangles =
        [
            (MaskLeft + 0.25f, MaskTop + 1.5f, right - 0.75f, bottom - 1.75f),
            (MaskLeft, MaskTop, right, bottom),
            (MaskLeft - 3.5f, MaskTop - 2.25f, right + 9.75f, bottom + 4.5f),
            (MaskLeft - 5.75f, MaskTop - 3f, MaskLeft + (width / 2f), MaskTop + 4.25f),
            (MaskLeft + (width / 4f), MaskTop + 2.5f, right + 6f, bottom + 2.75f),
            (MaskLeft - 6.5f, MaskTop + 1f, MaskLeft - 1.25f, bottom - 1f),
            (MaskLeft + 0.5f, MaskTop - 4.75f, right - 0.5f, MaskTop - 0.25f),
            (MaskLeft + 0.5f, bottom + 0.25f, right - 0.5f, bottom + 3.5f),
            (MaskLeft + 0.25f, MaskTop + 3.25f, MaskLeft + 0.75f, MaskTop + 3.5f),
            (MaskLeft - 2f, MaskTop + 4f, right + 2f, MaskTop + 5f),
            (MaskLeft + 0.5f, MaskTop + 1f, MaskLeft + 0.5f, bottom - 1f),
        ];

        foreach (var rectangle in rectangles)
        {
            list.Clear();

            // Clear takes the regions too: the same region again, with the same bits.
            number = list.AddHighlight(MaskLeft, MaskTop, width, MaskHeight, out mask);
            _ = Fill(mask, shape, width);
            list.DrawRectCutByMask(number, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom, Kept, Faded);
            var recorded = list.Rects.ToArray();

            // By the alpha: 200 of 255 inside, 40 outside.
            RectInstance[] kept = [.. recorded.Where(r => r.Color.W > 0.5f)];
            RectInstance[] faded = [.. recorded.Where(r => r.Color.W <= 0.5f)];

            var runs = 0;
            var empty = !(rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top);
            for (var py = MaskTop - 8; py < bottom + 14; py++)
            {
                bool? before = null;
                for (var px = MaskLeft - 8; px < right + 14; px++)
                {
                    var holds = list.Holds(number, new Vector2(px + 0.5f, py + 0.5f));
                    var inBox = px >= MaskLeft && px < right && py >= MaskTop && py < bottom;
                    var area = empty || !inBox ? 0 : Overlap(rectangle.Left, rectangle.Right, px) * Overlap(rectangle.Top, rectangle.Bottom, py);
                    var where = $"The pixel ({px}, {py}) of the rectangle {rectangle} under the mask {shape} of width {width}";
                    Assert.True((holds ? area : 0) == Area(kept, px, py), $"{where}: the area in the inside's color.");
                    Assert.True((holds ? 0 : area) == Area(faded, px, py), $"{where}: the area in the outside's color.");
                    Assert.True(recorded.Count(r => Area([r], px, py) > 0) <= 1, $"{where}: two rectangles touch it.");

                    // A run of a row: the box's pixels that the rectangle touches, judged alike.
                    if (area > 0)
                    {
                        runs += before == holds ? 0 : 1;
                        before = holds;
                    }
                }
            }

            Assert.True(runs == recorded.Length, $"{recorded.Length} rectangles for {runs} runs: {rectangle}, the mask {shape} of width {width}.");
        }

        // The length of a pixel's side that a range covers.
        static double Overlap(double low, double high, int pixel) => Math.Max(0, Math.Min(high, pixel + 1) - Math.Max(low, pixel));

        static double Area(RectInstance[] rects, int px, int py)
        {
            var sum = 0.0;
            foreach (var r in rects)
            {
                sum += Overlap(r.X, (double)r.X + r.Width, px) * Overlap(r.Y, (double)r.Y + r.Height, py);
            }

            return sum;
        }
    }

    /// <summary>
    /// A rectangle far wider and taller than the mask is cut as the mask's own box is, with work that follows the mask and
    /// not the rectangle's pixels (Issue #576): a density's cell under a view zoomed far into it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A mask at (7, 5) of 70 × 9 pixels with a notched polygon's bits; a rectangle from −3e9 to 3e9 on both axes, past
    ///   the range of an int and of the floats that hold every whole pixel.
    ///
    /// Steps:
    /// - Cut the mask's box by the mask and read the recorded rectangles; clear, and cut the far rectangle.
    ///
    /// Expected:
    /// - The second call returns, with the first call's rectangles, field for field; more than one a row.
    /// </remarks>
    [Fact]
    public void DrawRectCutByMask_RectangleFarPastTheMask_IsCutAsTheMasksBox()
    {
        const int Width = 70;
        var list = new RenderList();
        var number = list.AddHighlight(MaskLeft, MaskTop, Width, MaskHeight, out var mask);
        _ = Fill(mask, Shape.Notched, Width);
        list.DrawRectCutByMask(number, MaskLeft, MaskTop, MaskLeft + Width, MaskTop + MaskHeight, Kept, Faded);
        var expected = list.Rects.ToArray();

        list.Clear();
        number = list.AddHighlight(MaskLeft, MaskTop, Width, MaskHeight, out mask);
        _ = Fill(mask, Shape.Notched, Width);
        list.DrawRectCutByMask(number, -3e9f, -3e9f, 3e9f, 3e9f, Kept, Faded);

        Assert.True(expected.Length > MaskHeight, $"{expected.Length} rectangles in {MaskHeight} rows.");
        Assert.Equal(expected, list.Rects.ToArray());
    }

    /// <summary>Gets each shape at each width.</summary>
    public static TheoryData<Shape, int> Masks()
    {
        var data = new TheoryData<Shape, int>();
        foreach (var shape in Enum.GetValues<Shape>())
        {
            foreach (var width in (int[])[1, 31, 32, 33, 64, 70])
            {
                data.Add(shape, width);
            }
        }

        return data;
    }

    // Sets the mask's bits to a shape, and returns whether a pixel of it is set and whether one is clear.
    private static (bool Set, bool Clear) Fill(Span<uint> mask, Shape shape, int width)
    {
        var stride = (width / 32) + 1;
        var random = new Random(576);
        Vector2[] polygon = shape switch
        {
            // As shares of the mask's box, y down.
            Shape.Notched => [new(0.1f, 0.9f), new(0.9f, 0.9f), new(0.9f, 0.1f), new(0.5f, 0.7f), new(0.1f, 0.1f)],
            Shape.Star =>
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(k =>
                        new Vector2(0.5f, 0.5f)
                        + ((k % 2 == 0 ? 0.7f : 0.2f) * new Vector2(MathF.Sin(k * MathF.PI / 5), -MathF.Cos(k * MathF.PI / 5)))
                    ),
            ],
            Shape.CutByTheEdges => [new(-0.4f, 1.3f), new(1.5f, 0.8f), new(0.3f, -0.6f)],
            _ => [],
        };
        var (set, clear) = (false, false);
        for (var row = 0; row < MaskHeight; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var bit = shape switch
                {
                    Shape.Comb => (column + row) % 2 == 0,
                    Shape.Full => true,
                    Shape.Empty => false,
                    Shape.Random => random.Next(3) > 0,
                    _ => EvenOdd(polygon, new Vector2((column + 0.5f) / width, (row + 0.5f) / MaskHeight)),
                };
                if (bit)
                {
                    mask[(row * stride) + (column >> 5)] |= 1u << (column & 31);
                }

                (set, clear) = (set || bit, clear || !bit);
            }
        }

        return (set, clear);
    }

    private static bool EvenOdd(Vector2[] polygon, Vector2 point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                inside ^= point.X < a.X + ((point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
        }

        return inside;
    }
}
