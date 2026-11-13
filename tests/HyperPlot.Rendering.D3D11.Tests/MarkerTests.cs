using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The point markers on WARP against a CPU reference, within 1 a channel (design §5.1, §5.3, §10, rows R3a and R3f). Windows only.</summary>
public sealed class MarkerTests
{
    private const int Size = 32;
    private static readonly Color Background = new(250, 245, 235, 255);

    /// <summary>
    /// A disc, a square, and a cross cover each pixel by their shape (design §5.1): the disc by its distance from the center,
    /// the square by the area it shares with the pixel, the cross by its two bars less the square they share.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 32 × 32 WARP target cleared to an opaque background.
    ///
    /// Steps:
    /// - Draw a disc of diameter 9 at a fractional center, a square of side 6.5, a cross of span 9 and stroke 2, and a disc
    ///   of diameter 0.8 inside one pixel; read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference.
    /// </remarks>
    [Fact]
    public void Coverage_DiscSquareAndCross_MatchTheReference() =>
        Check(
            ([new Vector2(8.3f, 7.6f)], null, new MarkerStyle(new Color(20, 40, 160, 255), 9), null),
            ([new Vector2(22.25f, 8.5f)], null, new MarkerStyle(new Color(200, 60, 20, 255), 6.5f, MarkerShape.Square), null),
            ([new Vector2(9.5f, 22.5f)], null, new MarkerStyle(new Color(0, 0, 0, 255), 9, MarkerShape.Cross, 2), null),
            ([new Vector2(24.5f, 24.5f)], null, new MarkerStyle(new Color(0, 0, 0, 255), 0.8f), null)
        );

    /// <summary>
    /// Each of the six shapes covers each pixel as the design's formula gives it at the pixel's center (design §5.1 "形",
    /// "行 R3f の形"), small and large, wherever its center lies in a pixel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 32 × 32 WARP target cleared to an opaque background.
    ///
    /// Steps:
    /// - For every value of MarkerShape, each diameter of 4, 9, and 24 pixels, and each of three centers with other
    ///   fractions of a pixel: draw one opaque marker with a stroke of 1; read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference, in all 54 images.
    /// </remarks>
    [Fact]
    public void Coverage_EveryShape_MatchesTheReferenceAtThreeSizes()
    {
        Vector2[] centers = [new(16, 16), new(15.3f, 16.7f), new(16.5f, 15.25f)];
        foreach (var shape in Enum.GetValues<MarkerShape>())
        {
            foreach (var size in (float[])[4, 9, 24])
            {
                foreach (var center in centers)
                {
                    Check(([center], null, new MarkerStyle(new Color(20, 40, 160, 255), size, shape), null));
                }
            }
        }
    }

    /// <summary>
    /// The formula's coverage adds up to the shape's area, to within a share of the box's perimeter (design §5.1 "被覆と面積",
    /// §10 R3f row): the formula is taken at pixel centers, so the sum is not the area.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The reference's coverage of each shape, with a stroke of 1 pixel; the shape's area counted on a grid of 16 × 16
    ///   samples a pixel from the shape's own definition (the disc, the box, the bars, the triangle, the diamond, the
    ///   diagonal bands cut by the box).
    ///
    /// Steps:
    /// - For every value of MarkerShape, each diameter of 4, 9, and 24 pixels, and each of 9 centers over a pixel: add the
    ///   coverage of every pixel.
    ///
    /// Expected:
    /// - The sum differs from the area by at most 4 × diameter × 0.15 square pixels.
    /// </remarks>
    [Fact]
    public void Coverage_EveryShape_AddsUpToItsArea()
    {
        foreach (var shape in Enum.GetValues<MarkerShape>())
        {
            foreach (var size in (float[])[4, 9, 24])
            {
                var style = new MarkerStyle(default, size, shape);
                for (var step = 0; step < 9; step++)
                {
                    var center = new Vector2(16 + ((step % 3) / 3f), 16 + ((step / 3) / 3f));
                    var (sum, area) = (0.0, 0.0);
                    for (var row = 0; row < Size; row++)
                    {
                        for (var column = 0; column < Size; column++)
                        {
                            sum += Coverage(style, center, column, row);
                            for (var sample = 0; sample < 256; sample++)
                            {
                                var x = column + (((sample % 16) + 0.5) / 16) - center.X;
                                var y = row + (((sample / 16) + 0.5) / 16) - center.Y;
                                area += Inside(shape, x, y, size / 2.0, 0.5) ? 1 / 256.0 : 0;
                            }
                        }
                    }

                    Assert.True(
                        Math.Abs(sum - area) <= 4 * size * 0.15,
                        $"{shape} of {size} at {center}: the coverage adds up to {sum:F3}, and the area is {area:F3}."
                    );
                }
            }
        }
    }

    /// <summary>One call draws markers of their own sizes, shapes, and colors (design §5.1 "マーカー", row R3f).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Six centers on a 32 × 32 target, with the six shapes, sizes from 4 to 9 pixels, and a color each, two of them
    ///   half transparent; a second call with sizes alone, and a third with shapes alone.
    ///
    /// Steps:
    /// - Draw the three calls; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which draws each marker with its own look and the style's values for the empty spans.
    /// </remarks>
    [Fact]
    public void PerMarkerLooks_DrawEachMarkersOwn()
    {
        Vector2[] centers = [new(5.5f, 5.2f), new(15.3f, 5.5f), new(25.5f, 5.5f), new(5.5f, 16.4f), new(15.5f, 16.5f), new(25.2f, 16.5f)];
        Color[] colors =
        [
            new(200, 30, 30, 255),
            new(30, 160, 30, 128),
            new(30, 30, 200, 255),
            new(0, 0, 0, 255),
            new(160, 30, 160, 128),
            new(20, 120, 140, 255),
        ];
        Check(
            new Call(centers, colors, new MarkerStyle(default, 1, MarkerShape.Circle, 1.5f), null, [4, 5, 6, 7, 8, 9], Enum.GetValues<MarkerShape>()),
            new Call([new(6.5f, 27), new(16, 27)], null, new MarkerStyle(new Color(0, 0, 0, 255), 1, MarkerShape.Diamond), null, [5, 8], null),
            new Call(
                [new(23, 27), new(28.5f, 27)],
                null,
                new MarkerStyle(new Color(90, 60, 0, 255), 5),
                null,
                null,
                [MarkerShape.X, MarkerShape.Triangle]
            )
        );
    }

    /// <summary>Half-transparent markers that overlap are each blended over what is below, in the order of the centers (design §5.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - One call of three discs of alpha 128 whose neighbors overlap, and one call of two squares with a color each.
    ///
    /// Steps:
    /// - Draw both calls; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which blends each marker in turn, the squares in their own colors.
    /// </remarks>
    [Fact]
    public void Overlap_HalfTransparent_BlendsEachInOrder() =>
        Check(
            ([new Vector2(8, 10), new Vector2(13, 10), new Vector2(18, 10)], null, new MarkerStyle(new Color(30, 120, 200, 128), 9), null),
            (
                [new Vector2(12, 22), new Vector2(16.5f, 23.5f)],
                [new Color(220, 40, 40, 128), new Color(20, 160, 60, 200)],
                new MarkerStyle(default, 8, MarkerShape.Square),
                null
            )
        );

    /// <summary>The clip of SetClip holds markers, and a center that is not finite draws nothing (design §5.1, §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Discs under a clip that cuts them, with a NaN and an infinite center among them, and a disc after it under the whole target.
    ///
    /// Steps:
    /// - Draw them; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference: no pixel of the first call outside the clip, nothing for the two centers that are not
    ///   finite, the last disc whole.
    /// </remarks>
    [Fact]
    public void Clip_CutsMarkers_AndNonFiniteCentersDrawNothing() =>
        Check(
            (
                [new Vector2(8, 8), new Vector2(float.NaN, 8), new Vector2(14, float.PositiveInfinity), new Vector2(15, 15)],
                null,
                new MarkerStyle(new Color(20, 120, 40, 220), 10),
                (6, 4, 8, 20)
            ),
            ([new Vector2(24, 24)], null, new MarkerStyle(new Color(10, 10, 10, 255), 7), null)
        );

    /// <summary>Recording and drawing markers again allocates nothing once the buffers have grown (design §5.1, §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 64 target; one frame of 300 markers in three calls to grow the buffers.
    ///
    /// Steps:
    /// - Five times: clear the list, record the 300 markers, draw.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void RecordAndDraw_SteadyState_AllocatesNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
        var list = new RenderList();
        var centers = new Vector2[100];
        var colors = new Color[100];
        var sizes = new float[100];
        var shapes = new MarkerShape[100];
        for (var i = 0; i < centers.Length; i++)
        {
            centers[i] = new Vector2(i % 60, i / 2);
            colors[i] = new Color((byte)i, 0, 0, 64);
            (sizes[i], shapes[i]) = (2 + (i % 5), (MarkerShape)(i % 6));
        }

        Frame();

        // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            Frame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(backend.Markers.InstanceCapacity >= 400);

        void Frame()
        {
            list.Clear();
            list.DrawMarkers(centers, new MarkerStyle(new Color(0, 0, 0, 64), 3));
            list.DrawMarkers(centers, colors, new MarkerStyle(default, 2.5f, MarkerShape.Square));
            list.DrawMarkers(centers, new MarkerStyle(new Color(0, 0, 255, 64), 5, MarkerShape.Cross));
            list.DrawMarkers(centers, colors, sizes, shapes, new MarkerStyle(default, 1));
            backend.Draw(target, list);
        }
    }

    /// <summary>After a device loss, the next frame makes the marker pipeline again and draws the same pixels (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 32 × 32 target; a translucent disc and a cross at fractional centers.
    ///
    /// Steps:
    /// - Clear and draw; read. Lose the device through Map; clear and draw again; read.
    ///
    /// Expected:
    /// - The loss throws GpuDeviceLostException; both reads equal the reference.
    /// </remarks>
    [Fact]
    public void DeviceLost_NextFrame_DrawsTheSamePixels()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        Call[] calls =
        [
            ([new Vector2(10.5f, 11.25f)], null, new MarkerStyle(new Color(30, 60, 200, 180), 12), null),
            ([new Vector2(22.5f, 20.5f)], null, new MarkerStyle(new Color(200, 30, 60, 255), 10, MarkerShape.Cross, 3), null),
        ];
        var list = new RenderList();
        Record(list, calls);
        var expected = Reference(calls);

        AssertClose(expected, DrawAndRead());
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[Size * Size * 4]));
        backend.CheckResult = null;

        AssertClose(expected, DrawAndRead());

        byte[] DrawAndRead()
        {
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[Size * Size * 4];
            target.ReadPixels(pixels);
            return pixels;
        }
    }

    private static void Record(RenderList list, Call[] calls)
    {
        foreach (var call in calls)
        {
            if (call.Clip is { } c)
            {
                list.SetClip(c.X, c.Y, c.W, c.H);
            }
            else
            {
                list.ResetClip();
            }

            if (call.Sizes is not null || call.Shapes is not null)
            {
                list.DrawMarkers(call.Centers, call.Colors, call.Sizes, call.Shapes, call.Style);
            }
            else if (call.Colors is { } colors)
            {
                list.DrawMarkers(call.Centers, colors, call.Style);
            }
            else
            {
                list.DrawMarkers(call.Centers, call.Style);
            }
        }
    }

    private static void Check(params Call[] calls)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        Record(list, calls);
        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference(calls), actual);
    }

    /// <summary>
    /// The CPU reference, written apart from the shader: the coverage of each pixel by the marker's shape, and the
    /// premultiplied color over the image with that coverage, marker by marker.
    /// </summary>
    private static byte[] Reference(params Call[] calls)
    {
        var image = new byte[Size * Size * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            image[i] = Background.R;
            image[i + 1] = Background.G;
            image[i + 2] = Background.B;
            image[i + 3] = Background.A;
        }

        foreach (var call in calls)
        {
            for (var m = 0; m < call.Centers.Length; m++)
            {
                var center = call.Centers[m];
                if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
                {
                    continue;
                }

                var color = call.Colors is { } colors ? colors[m] : call.Style.Color;
                var style = call.Style with
                {
                    Size = call.Sizes is { } sizes ? sizes[m] : call.Style.Size,
                    Shape = call.Shapes is { } shapes ? shapes[m] : call.Style.Shape,
                };
                var a = color.A;
                double[] source = [Multiply(color.R, a), Multiply(color.G, a), Multiply(color.B, a), a];
                for (var row = 0; row < Size; row++)
                {
                    for (var column = 0; column < Size; column++)
                    {
                        if (call.Clip is { } c && (column < c.X || row < c.Y || column >= c.X + c.W || row >= c.Y + c.H))
                        {
                            continue;
                        }

                        var coverage = Coverage(style, center, column, row);
                        if (coverage <= 0)
                        {
                            continue;
                        }

                        var index = ((row * Size) + column) * 4;
                        var alpha = source[3] / 255 * coverage;
                        for (var channel = 0; channel < 4; channel++)
                        {
                            var value = (source[channel] * coverage) + (image[index + channel] * (1 - alpha));
                            image[index + channel] = (byte)Math.Round(value);
                        }
                    }
                }
            }
        }

        return image;
    }

    private static double Coverage(MarkerStyle style, Vector2 center, int column, int row)
    {
        var radius = style.Size / 2.0;
        var half = style.Stroke / 2.0;
        var dx = column + 0.5 - center.X;
        var dy = row + 0.5 - center.Y;
        switch (style.Shape)
        {
            case MarkerShape.Circle:
                return Math.Clamp(radius + 0.5 - Math.Sqrt((dx * dx) + (dy * dy)), 0, 1);
            case MarkerShape.Square:
                return Box(center, radius, radius, column, row);
            case MarkerShape.Cross:
                return Box(center, radius, half, column, row) + Box(center, half, radius, column, row) - Box(center, half, half, column, row);
            case MarkerShape.Triangle:
                // The distance past the box, and past a slanted side: the line from the apex (0, −r) to a base corner (±r, r).
                var past = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)) - radius, ((2 * Math.Abs(dx)) - (dy + radius)) / Math.Sqrt(5));
                return Math.Clamp(0.5 - past, 0, 1);
            case MarkerShape.Diamond:
                var outside = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)) - radius, (Math.Abs(dx) + Math.Abs(dy) - radius) / Math.Sqrt(2));
                return Math.Clamp(0.5 - outside, 0, 1);
            default:
                var down = Math.Clamp(0.5 - ((Math.Abs(dx - dy) / Math.Sqrt(2)) - half), 0, 1);
                var up = Math.Clamp(0.5 - ((Math.Abs(dx + dy) / Math.Sqrt(2)) - half), 0, 1);
                return Math.Max(down, up) * Box(center, radius, radius, column, row);
        }
    }

    /// <summary>Returns whether a point, as its offset from the center, lies in a shape of the half size r with the half stroke.</summary>
    private static bool Inside(MarkerShape shape, double x, double y, double r, double half) =>
        shape switch
        {
            MarkerShape.Circle => (x * x) + (y * y) <= r * r,
            MarkerShape.Square => Math.Abs(x) <= r && Math.Abs(y) <= r,
            MarkerShape.Cross => (Math.Abs(x) <= r && Math.Abs(y) <= half) || (Math.Abs(x) <= half && Math.Abs(y) <= r),
            MarkerShape.Triangle => y <= r && y >= -r && Math.Abs(x) <= (y + r) / 2,
            MarkerShape.Diamond => Math.Abs(x) + Math.Abs(y) <= r,
            _ => Math.Abs(x) <= r && Math.Abs(y) <= r && (Math.Abs(x - y) <= half * Math.Sqrt(2) || Math.Abs(x + y) <= half * Math.Sqrt(2)),
        };

    private static double Box(Vector2 center, double halfWidth, double halfHeight, int column, int row)
    {
        var ox = Math.Clamp(Math.Min(column + 1, center.X + halfWidth) - Math.Max(column, center.X - halfWidth), 0, 1);
        var oy = Math.Clamp(Math.Min(row + 1, center.Y + halfHeight) - Math.Max(row, center.Y - halfHeight), 0, 1);
        return ox * oy;
    }

    private static double Multiply(byte c, byte a) => ((c * a) + 127) / 255;

    private static void AssertClose(byte[] expected, byte[] actual)
    {
        var worst = 0;
        var at = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            var difference = Math.Abs(expected[i] - actual[i]);
            if (difference > worst)
            {
                worst = difference;
                at = i;
            }
        }

        Assert.True(
            worst <= 1,
            $"Channel {at % 4} of pixel ({(at / 4) % Size}, {(at / 4) / Size}) differs by {worst}: {(at < 0 ? 0 : expected[at])} expected, {(at < 0 ? 0 : actual[at])} drawn."
        );
    }

    private readonly record struct Call(
        Vector2[] Centers,
        Color[]? Colors,
        MarkerStyle Style,
        (int X, int Y, int W, int H)? Clip,
        float[]? Sizes = null,
        MarkerShape[]? Shapes = null
    )
    {
        public static implicit operator Call((Vector2[] Centers, Color[]? Colors, MarkerStyle Style, (int X, int Y, int W, int H)? Clip) value) =>
            new(value.Centers, value.Colors, value.Style, value.Clip);
    }
}
