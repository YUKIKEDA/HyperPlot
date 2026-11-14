using System.Numerics;

namespace HyperPlot.Depth3D;

/// <summary>
/// The CPU reference image: R, G, B in 0..255 for each pixel, and the pixels near a line or a marker (those whose 3 × 3
/// neighborhood has a sample of one), where the errors are counted.
/// </summary>
internal sealed class ReferenceImage(float[] rgb, bool[] touched)
{
    public float[] Rgb { get; } = rgb;

    public bool[] Touched { get; } = touched;
}

/// <summary>One traced pixel of postpass (Reference.TracePostpass).</summary>
internal sealed class TracedPixel
{
    /// <summary>The pixel's index, y × width + x.</summary>
    public int Pixel { get; init; }

    /// <summary>Whether the surface covers the pixel's center, as the GPU's rasterizer decides.</summary>
    public bool PlaneCovers { get; init; }

    /// <summary>The surface's depth at the pixel's center; NaN where it does not cover it.</summary>
    public float PlaneDepth { get; init; }

    /// <summary>The share of the reference's samples the surface wins.</summary>
    public float ReferencePlaneShare { get; set; }

    /// <summary>The share of the reference's samples nothing covers.</summary>
    public float ReferenceBackgroundShare { get; set; }

    /// <summary>postpass replayed on the CPU at the pixel's center: R, G, B in 0..255.</summary>
    public Vector3 Replayed { get; set; }

    /// <summary>The polylines with a segment whose quad reaches the pixel, in the order they are drawn.</summary>
    public TracedPolyline[] Polylines { get; set; } = [];
}

/// <summary>One polyline at a traced pixel.</summary>
internal sealed class TracedPolyline
{
    public int Polyline { get; init; }

    /// <summary>Its segments whose quad reaches the pixel, in the order they are drawn.</summary>
    public List<int> Segments { get; } = [];

    /// <summary>Each segment's coverage at the pixel's center (Stroke in Depth3D.hlsl).</summary>
    public List<float> Coverages { get; } = [];

    /// <summary>Each segment's depth at the pixel's center, never below the nearer end's (Stroke in Depth3D.hlsl).</summary>
    public List<float> Depths { get; } = [];

    /// <summary>The share of the reference's samples this polyline wins.</summary>
    public float ReferenceShare { get; set; }

    /// <summary>The share of the reference's samples inside this polyline's stroke, whatever is in front.</summary>
    public float ReferenceCoverage { get; set; }

    /// <summary>The largest coverage of its segments, untested.</summary>
    public float CoverageMax { get; set; }

    /// <summary>The largest coverage of its segments that pass the depth test (the coverage pass's result).</summary>
    public float CoveragePassed { get; set; }

    /// <summary>The least depth of its segments with coverage.</summary>
    public float DepthMin { get; set; }

    /// <summary>The depth buffer at the pixel before the polyline's passes.</summary>
    public float DepthBefore { get; set; }

    /// <summary>The depth buffer at the pixel after its depth pass.</summary>
    public float DepthAfter { get; set; }

    /// <summary>Whether its depth pass wrote the pixel.</summary>
    public bool WroteDepth { get; set; }
}

/// <summary>
/// SPIKE (row K1, Issue #509): the exact area with depth order, by supersampling. Each pixel holds samples × samples
/// samples on a regular grid ((i + 0.5) / samples); each sample keeps the nearest shape that covers it (a z-buffer of
/// samples, the first of equal depths), and the pixel is the mean of its samples' colors over a white background. A line
/// is the stroke of half the width around each projected segment, a sample's depth the depth of the segment's point
/// nearest it (lerp(z0, z1, t), affine on the screen as on the GPU); a marker is a disc of the radius around its projected
/// center, at the center's depth. The surface covers a pixel when the GPU's rasterizer does (its center inside the
/// projected square: no anti-aliasing on either side, so its edge is not counted as an error of the methods), and each
/// of the pixel's samples takes the surface's depth at the sample. Positions are projected in float32 by the same
/// transform as on the GPU.
/// </summary>
internal static class Reference
{
    /// <summary>The surface's color, RGBA8 with R in the low byte: 140, 153, 166 (PlaneColor in Depth3D.hlsl).</summary>
    public const uint PlaneColor = 0xFFA6998C;

    private const int Tile = 32;

    public static ReferenceImage Lines(SceneData scene, in Matrix4x4 transform, int width, int height, float halfWidth, bool plane, int samples)
    {
        var vertices = scene.Vertices;
        var (x, y, z) = (new float[vertices.Length], new float[vertices.Length], new float[vertices.Length]);
        var visible = new bool[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            visible[i] = Project(vertices[i].Position, transform, width, height, out x[i], out y[i], out z[i]);
        }

        var count = scene.Segments.Length;
        var shapes = new Shapes(count) { IsLine = true, Size = halfWidth };
        for (var s = 0; s < count; s++)
        {
            var a = (int)scene.Segments[s];
            var b = a + 1;
            shapes.Visible[s] = visible[a] && visible[b];
            (shapes.Ax[s], shapes.Ay[s], shapes.Az[s]) = (x[a], y[a], z[a]);
            (shapes.Bx[s], shapes.By[s], shapes.Bz[s]) = (x[b], y[b], z[b]);
            shapes.Color[s] = vertices[a].Color;
        }

        return Render(shapes, transform, width, height, plane, samples);
    }

    public static ReferenceImage Markers(SceneData scene, in Matrix4x4 transform, int width, int height, float radius, bool plane, int samples)
    {
        var vertices = scene.Vertices;
        var shapes = new Shapes(vertices.Length) { IsLine = false, Size = radius };
        for (var i = 0; i < vertices.Length; i++)
        {
            shapes.Visible[i] = Project(vertices[i].Position, transform, width, height, out var x, out var y, out var z);
            (shapes.Ax[i], shapes.Ay[i], shapes.Az[i]) = (x, y, z);
            (shapes.Bx[i], shapes.By[i], shapes.Bz[i]) = (x, y, z);
            shapes.Color[i] = vertices[i].Color;
        }

        return Render(shapes, transform, width, height, plane, samples);
    }

    /// <summary>
    /// Traces pixels of the postpass method (Issue #513, decision 1): for each pixel, the segments whose stroke reaches it,
    /// grouped by polyline; the share of the pixel's samples each polyline wins in the reference's z-buffer and covers
    /// at all; and postpass replayed on the CPU at the pixel's center as Depth3D.hlsl draws it (the surface, then for each
    /// polyline in order the coverage pass tested LESS_EQUAL against the depth so far, MAX into an R8 coverage, the color
    /// over the 8-bit target, then the depth pass where a segment's coverage reaches the threshold), with the depth before
    /// and after each polyline. The replayed color is checked against the GPU's by the caller.
    /// </summary>
    public static TracedPixel[] TracePostpass(
        SceneData scene,
        in Matrix4x4 transform,
        int width,
        int height,
        float halfWidth,
        float threshold,
        PlaneOrder plane,
        int samples,
        IReadOnlyList<int> pixels
    )
    {
        var vertices = scene.Vertices;
        var (x, y, z) = (new float[vertices.Length], new float[vertices.Length], new float[vertices.Length]);
        var visible = new bool[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            visible[i] = Project(vertices[i].Position, transform, width, height, out x[i], out y[i], out z[i]);
        }

        var polylineOf = new int[scene.Segments.Length];
        for (var p = 0; p < scene.Polylines.Length; p++)
        {
            Array.Fill(polylineOf, p, scene.Polylines[p].SegmentStart, scene.Polylines[p].SegmentCount);
        }

        // The segments whose quad on the GPU (half the width and 1 pixel around the segment) reaches each traced pixel, in
        // the order they are drawn.
        var index = new Dictionary<int, int>(pixels.Count);
        for (var i = 0; i < pixels.Count; i++)
        {
            index[pixels[i]] = i;
        }

        var hits = new List<int>[pixels.Count];
        for (var i = 0; i < hits.Length; i++)
        {
            hits[i] = [];
        }

        var reach = halfWidth + 1;
        for (var s = 0; s < scene.Segments.Length; s++)
        {
            var a = (int)scene.Segments[s];
            if (!visible[a] || !visible[a + 1])
            {
                continue;
            }

            var fromX = Math.Max(0, (int)MathF.Floor(MathF.Min(x[a], x[a + 1]) - reach));
            var toX = Math.Min(width - 1, (int)MathF.Floor(MathF.Max(x[a], x[a + 1]) + reach));
            var fromY = Math.Max(0, (int)MathF.Floor(MathF.Min(y[a], y[a + 1]) - reach));
            var toY = Math.Min(height - 1, (int)MathF.Floor(MathF.Max(y[a], y[a + 1]) + reach));
            for (var py = fromY; py <= toY; py++)
            {
                for (var px = fromX; px <= toX; px++)
                {
                    if (index.TryGetValue((py * width) + px, out var at))
                    {
                        hits[at].Add(s);
                    }
                }
            }
        }

        var surface = plane != PlaneOrder.None ? new Surface(transform, width, height) : null;
        var planeRgb = new Vector3(PlaneColor & 0xFF, (PlaneColor >> 8) & 0xFF, (PlaneColor >> 16) & 0xFF) / 255;
        var traced = new TracedPixel[pixels.Count];
        var n = samples;
        var depths = new float[n * n];
        var owners = new int[n * n];
        for (var i = 0; i < pixels.Count; i++)
        {
            var (px, py) = (pixels[i] % width, pixels[i] / width);
            var (cx, cy) = (px + 0.5f, py + 0.5f);
            var covers = surface is not null && surface.Covers(cx, cy);
            var planeDepth = covers ? surface!.Depth(cx, cy) : float.NaN;
            var pixel = new TracedPixel
            {
                Pixel = pixels[i],
                PlaneCovers = covers,
                PlaneDepth = planeDepth,
            };

            // The reference's samples: the nearest of the surface and the strokes, the first of equal depths (owner -2 the
            // surface, -1 the background, else the polyline).
            Array.Fill(depths, float.PositiveInfinity);
            Array.Fill(owners, -1);
            if (covers)
            {
                for (var k = 0; k < n * n; k++)
                {
                    depths[k] = surface!.Depth(px + (((k % n) + 0.5f) / n), py + (((k / n) + 0.5f) / n));
                    owners[k] = -2;
                }
            }

            var byPolyline = new SortedDictionary<int, TracedPolyline>();
            var coveredBy = new Dictionary<int, bool[]>();
            foreach (var s in hits[i])
            {
                var a = (int)scene.Segments[s];
                var (dx, dy) = (x[a + 1] - x[a], y[a + 1] - y[a]);
                var lengthSquared = (dx * dx) + (dy * dy);
                var inverse = lengthSquared > 1e-12f ? 1 / lengthSquared : 0;
                var polyline = polylineOf[s];
                if (!byPolyline.TryGetValue(polyline, out var entry))
                {
                    entry = new TracedPolyline { Polyline = polyline, DepthMin = float.PositiveInfinity };
                    byPolyline[polyline] = entry;
                    coveredBy[polyline] = new bool[n * n];
                }

                entry.Segments.Add(s);
                var covered = coveredBy[polyline];
                for (var k = 0; k < n * n; k++)
                {
                    var (sx, sy) = (px + (((k % n) + 0.5f) / n), py + (((k / n) + 0.5f) / n));
                    if (Distance2(sx, sy, x[a], y[a], dx, dy, inverse, out var t) > halfWidth * halfWidth)
                    {
                        continue;
                    }

                    covered[k] = true;
                    var depth = z[a] + ((z[a + 1] - z[a]) * t);
                    if (depth < depths[k])
                    {
                        depths[k] = depth;
                        owners[k] = polyline;
                    }
                }

                // As Stroke in Depth3D.hlsl, at the pixel's center.
                var d = MathF.Sqrt(Distance2(cx, cy, x[a], y[a], dx, dy, inverse, out var centerT));
                var coverage = Math.Clamp(0.5f + halfWidth - d, 0, 1) - Math.Clamp(0.5f - halfWidth - d, 0, 1);
                var centerDepth = MathF.Max(z[a] + ((z[a + 1] - z[a]) * centerT), MathF.Min(z[a], z[a + 1]));
                entry.Coverages.Add(coverage);
                entry.Depths.Add(centerDepth);
                entry.CoverageMax = MathF.Max(entry.CoverageMax, coverage);
                if (coverage > 0 && centerDepth < entry.DepthMin)
                {
                    entry.DepthMin = centerDepth;
                }
            }

            for (var k = 0; k < n * n; k++)
            {
                if (owners[k] == -2)
                {
                    pixel.ReferencePlaneShare += 1f / (n * n);
                }
                else if (owners[k] == -1)
                {
                    pixel.ReferenceBackgroundShare += 1f / (n * n);
                }
                else
                {
                    byPolyline[owners[k]].ReferenceShare += 1f / (n * n);
                }
            }

            foreach (var (polyline, covered) in coveredBy)
            {
                byPolyline[polyline].ReferenceCoverage = covered.AsSpan().Count(true) / (float)(n * n);
            }

            // postpass replayed at the center: a cleared depth of 1 and a white target, 8 bits a channel after each blend.
            var target = Vector3.One;
            var depthBuffer = 1f;
            if (plane == PlaneOrder.Before && covers && planeDepth < depthBuffer)
            {
                (target, depthBuffer) = (planeRgb, planeDepth);
            }

            foreach (var entry in byPolyline.Values)
            {
                entry.DepthBefore = depthBuffer;
                var passed = 0f;
                for (var k = 0; k < entry.Segments.Count; k++)
                {
                    if (entry.Coverages[k] > 0 && entry.Depths[k] <= depthBuffer)
                    {
                        passed = MathF.Max(passed, entry.Coverages[k]);
                    }
                }

                entry.CoveragePassed = passed;
                var stored = MathF.Round(passed * 255) / 255;
                var color = scene.Polylines[entry.Polyline].Color;
                var rgb = new Vector3(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF) / 255;
                target = Quantize((rgb * stored) + (target * (1 - stored)));
                for (var k = 0; k < entry.Segments.Count; k++)
                {
                    if (entry.Coverages[k] > 0 && entry.Coverages[k] >= threshold && entry.Depths[k] <= depthBuffer)
                    {
                        depthBuffer = entry.Depths[k];
                        entry.WroteDepth = true;
                    }
                }

                entry.DepthAfter = depthBuffer;
            }

            if (plane == PlaneOrder.After && covers && planeDepth < depthBuffer)
            {
                target = planeRgb;
            }

            pixel.Replayed = target * 255;
            pixel.Polylines = [.. byPolyline.Values];
            traced[i] = pixel;
        }

        return traced;

        static Vector3 Quantize(Vector3 value) =>
            new(MathF.Round(value.X * 255) / 255, MathF.Round(value.Y * 255) / 255, MathF.Round(value.Z * 255) / 255);
    }

    /// <summary>The pixel position and depth of a point, as Project in Depth3D.hlsl; false outside the frustum.</summary>
    public static bool Project(Vector3 position, in Matrix4x4 transform, int width, int height, out float x, out float y, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), transform);
        var (ndcX, ndcY, ndcZ) = (clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        x = ((ndcX * 0.5f) + 0.5f) * width;
        y = (0.5f - (ndcY * 0.5f)) * height;
        depth = ndcZ;
        return clip.W > 0 && ndcZ >= 0 && ndcZ <= 1;
    }

    private static ReferenceImage Render(Shapes shapes, in Matrix4x4 transform, int width, int height, bool plane, int samples)
    {
        var tilesX = (width + Tile - 1) / Tile;
        var tilesY = (height + Tile - 1) / Tile;

        // The shapes of each tile, in the order they are drawn: a count, the starts, then the lists.
        var starts = new int[(tilesX * tilesY) + 1];
        var reach = shapes.Size + 1;
        for (var pass = 0; pass < 2; pass++)
        {
            var lists = pass == 0 ? null : new int[starts[^1]];
            var cursor = pass == 0 ? null : (int[])starts.Clone();
            for (var s = 0; s < shapes.Count; s++)
            {
                if (!shapes.Visible[s])
                {
                    continue;
                }

                var tx0 = Math.Clamp((int)MathF.Floor((MathF.Min(shapes.Ax[s], shapes.Bx[s]) - reach) / Tile), 0, tilesX - 1);
                var tx1 = Math.Clamp((int)MathF.Floor((MathF.Max(shapes.Ax[s], shapes.Bx[s]) + reach) / Tile), 0, tilesX - 1);
                var ty0 = Math.Clamp((int)MathF.Floor((MathF.Min(shapes.Ay[s], shapes.By[s]) - reach) / Tile), 0, tilesY - 1);
                var ty1 = Math.Clamp((int)MathF.Floor((MathF.Max(shapes.Ay[s], shapes.By[s]) + reach) / Tile), 0, tilesY - 1);
                for (var ty = ty0; ty <= ty1; ty++)
                {
                    for (var tx = tx0; tx <= tx1; tx++)
                    {
                        var tile = (ty * tilesX) + tx;
                        if (lists is null)
                        {
                            starts[tile + 1]++;
                        }
                        else
                        {
                            lists[cursor![tile]++] = s;
                        }
                    }
                }
            }

            if (lists is null)
            {
                for (var t = 0; t < tilesX * tilesY; t++)
                {
                    starts[t + 1] += starts[t];
                }
            }
            else
            {
                shapes.Lists = lists;
            }
        }

        var surface = plane ? new Surface(transform, width, height) : null;
        var rgb = new float[width * height * 3];
        var near = new bool[width * height];
        _ = Parallel.For(
            0,
            tilesX * tilesY,
            () => new TileBuffer(samples),
            (tile, _, buffer) =>
            {
                DrawTile(shapes, surface, starts, tile % tilesX, tile / tilesX, width, height, buffer, rgb, near);
                return buffer;
            },
            static _ => { }
        );

        // The pixels whose 3 × 3 neighborhood holds a sample of a shape: the GPU's anti-aliased edge reaches half a pixel past
        // the reference's.
        var touched = new bool[width * height];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var any = false;
                for (var dy = -1; dy <= 1 && !any; dy++)
                {
                    for (var dx = -1; dx <= 1 && !any; dx++)
                    {
                        var (nx, ny) = (px + dx, py + dy);
                        any = nx >= 0 && ny >= 0 && nx < width && ny < height && near[(ny * width) + nx];
                    }
                }

                touched[(py * width) + px] = any;
            }
        }

        return new ReferenceImage(rgb, touched);
    }

    private static void DrawTile(
        Shapes shapes,
        Surface? surface,
        int[] starts,
        int tileX,
        int tileY,
        int width,
        int height,
        TileBuffer buffer,
        float[] rgb,
        bool[] near
    )
    {
        var (x0, y0) = (tileX * Tile, tileY * Tile);
        var (x1, y1) = (Math.Min(x0 + Tile, width), Math.Min(y0 + Tile, height));
        var n = buffer.Samples;
        var perPixel = n * n;
        Array.Fill(buffer.Depth, float.PositiveInfinity);
        Array.Clear(buffer.Color);

        if (surface is not null)
        {
            for (var py = y0; py < y1; py++)
            {
                for (var px = x0; px < x1; px++)
                {
                    if (!surface.Covers(px + 0.5f, py + 0.5f))
                    {
                        continue;
                    }

                    var at = (((py - y0) * Tile) + (px - x0)) * perPixel;
                    for (var j = 0; j < n; j++)
                    {
                        for (var i = 0; i < n; i++)
                        {
                            buffer.Depth[at + (j * n) + i] = surface.Depth(px + ((i + 0.5f) / n), py + ((j + 0.5f) / n));
                            buffer.Color[at + (j * n) + i] = PlaneColor;
                        }
                    }
                }
            }
        }

        var tile = (tileY * ((width + Tile - 1) / Tile)) + tileX;
        for (var k = starts[tile]; k < starts[tile + 1]; k++)
        {
            var s = shapes.Lists[k];
            if (shapes.IsLine)
            {
                DrawSegment(shapes, s, x0, y0, x1, y1, buffer);
            }
            else
            {
                DrawDisc(shapes, s, x0, y0, x1, y1, buffer);
            }
        }

        // The mean of each pixel's samples; white where a sample holds nothing.
        for (var py = y0; py < y1; py++)
        {
            for (var px = x0; px < x1; px++)
            {
                var at = (((py - y0) * Tile) + (px - x0)) * perPixel;
                var (r, g, b) = (0f, 0f, 0f);
                var shape = false;
                for (var k = 0; k < perPixel; k++)
                {
                    var color = buffer.Color[at + k];
                    if (color == 0)
                    {
                        (r, g, b) = (r + 255, g + 255, b + 255);
                        continue;
                    }

                    shape |= color != PlaneColor;
                    (r, g, b) = (r + (color & 0xFF), g + ((color >> 8) & 0xFF), b + ((color >> 16) & 0xFF));
                }

                var pixel = (py * width) + px;
                rgb[pixel * 3] = r / perPixel;
                rgb[(pixel * 3) + 1] = g / perPixel;
                rgb[(pixel * 3) + 2] = b / perPixel;
                near[pixel] = shape;
            }
        }
    }

    private static void DrawSegment(Shapes shapes, int s, int x0, int y0, int x1, int y1, TileBuffer buffer)
    {
        var r = shapes.Size;
        var reach = r + 0.70711f;
        var (ax, ay, az) = (shapes.Ax[s], shapes.Ay[s], shapes.Az[s]);
        var (bx, by, bz) = (shapes.Bx[s], shapes.By[s], shapes.Bz[s]);
        var color = shapes.Color[s];
        var (dx, dy) = (bx - ax, by - ay);
        var lengthSquared = (dx * dx) + (dy * dy);
        var inverse = lengthSquared > 1e-12f ? 1 / lengthSquared : 0;
        var n = buffer.Samples;
        var perPixel = n * n;
        var fromX = Math.Max(x0, (int)MathF.Floor(MathF.Min(ax, bx) - r));
        var toX = Math.Min(x1 - 1, (int)MathF.Floor(MathF.Max(ax, bx) + r));
        var fromY = Math.Max(y0, (int)MathF.Floor(MathF.Min(ay, by) - r));
        var toY = Math.Min(y1 - 1, (int)MathF.Floor(MathF.Max(ay, by) + r));
        for (var py = fromY; py <= toY; py++)
        {
            for (var px = fromX; px <= toX; px++)
            {
                // A pixel whose center is farther than the radius and half the pixel's diagonal holds no sample of the stroke.
                if (Distance2(px + 0.5f, py + 0.5f, ax, ay, dx, dy, inverse, out _) > reach * reach)
                {
                    continue;
                }

                var at = (((py - y0) * Tile) + (px - x0)) * perPixel;
                for (var j = 0; j < n; j++)
                {
                    var sy = py + ((j + 0.5f) / n);
                    for (var i = 0; i < n; i++)
                    {
                        var sx = px + ((i + 0.5f) / n);
                        if (Distance2(sx, sy, ax, ay, dx, dy, inverse, out var t) > r * r)
                        {
                            continue;
                        }

                        var depth = az + ((bz - az) * t);
                        var k = at + (j * n) + i;
                        if (depth < buffer.Depth[k])
                        {
                            buffer.Depth[k] = depth;
                            buffer.Color[k] = color;
                        }
                    }
                }
            }
        }
    }

    private static void DrawDisc(Shapes shapes, int s, int x0, int y0, int x1, int y1, TileBuffer buffer)
    {
        var r = shapes.Size;
        var (cx, cy, depth) = (shapes.Ax[s], shapes.Ay[s], shapes.Az[s]);
        var color = shapes.Color[s];
        var n = buffer.Samples;
        var perPixel = n * n;
        var outer = r + 0.70711f;
        var inner = r - 0.70711f;
        var fromX = Math.Max(x0, (int)MathF.Floor(cx - r));
        var toX = Math.Min(x1 - 1, (int)MathF.Floor(cx + r));
        var fromY = Math.Max(y0, (int)MathF.Floor(cy - r));
        var toY = Math.Min(y1 - 1, (int)MathF.Floor(cy + r));
        for (var py = fromY; py <= toY; py++)
        {
            for (var px = fromX; px <= toX; px++)
            {
                var (ex, ey) = (px + 0.5f - cx, py + 0.5f - cy);
                var centerDistance2 = (ex * ex) + (ey * ey);
                if (centerDistance2 > outer * outer)
                {
                    continue;
                }

                // Every sample of a pixel whose center is the half diagonal inside the radius is in the disc.
                var whole = inner > 0 && centerDistance2 <= inner * inner;
                var at = (((py - y0) * Tile) + (px - x0)) * perPixel;
                for (var j = 0; j < n; j++)
                {
                    var sy = py + ((j + 0.5f) / n) - cy;
                    for (var i = 0; i < n; i++)
                    {
                        var sx = px + ((i + 0.5f) / n) - cx;
                        var k = at + (j * n) + i;
                        if ((whole || (sx * sx) + (sy * sy) <= r * r) && depth < buffer.Depth[k])
                        {
                            buffer.Depth[k] = depth;
                            buffer.Color[k] = color;
                        }
                    }
                }
            }
        }
    }

    // The squared distance from (x, y) to the segment from (ax, ay) along (dx, dy), and the parameter of its nearest point.
    private static float Distance2(float x, float y, float ax, float ay, float dx, float dy, float inverse, out float t)
    {
        t = Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) * inverse, 0, 1);
        var (ex, ey) = (ax + (dx * t) - x, ay + (dy * t) - y);
        return (ex * ex) + (ey * ey);
    }

    /// <summary>The shapes on the screen: a line's segment from A to B, or a marker's center in A (and B).</summary>
    private sealed class Shapes(int count)
    {
        public int Count { get; } = count;

        public bool IsLine { get; init; }

        /// <summary>Half the width of a line, or the radius of a marker, in pixels.</summary>
        public float Size { get; init; }

        public bool[] Visible { get; } = new bool[count];

        public float[] Ax { get; } = new float[count];

        public float[] Ay { get; } = new float[count];

        public float[] Az { get; } = new float[count];

        public float[] Bx { get; } = new float[count];

        public float[] By { get; } = new float[count];

        public float[] Bz { get; } = new float[count];

        public uint[] Color { get; } = new uint[count];

        /// <summary>The shapes of each tile, tile after tile.</summary>
        public int[] Lists { get; set; } = [];
    }

    /// <summary>The samples of one tile of one thread: the nearest depth and its color (0 for none).</summary>
    private sealed class TileBuffer(int samples)
    {
        public int Samples { get; } = samples;

        public float[] Depth { get; } = new float[Tile * Tile * samples * samples];

        public uint[] Color { get; } = new uint[Tile * Tile * samples * samples];
    }

    /// <summary>
    /// The surface on the screen: the square [-1, 1]² at z = 0 as the two triangles of PlaneVS, and its depth, affine on the
    /// screen (one plane in 3D).
    /// </summary>
    private sealed class Surface
    {
        private readonly float[] _x = new float[4];
        private readonly float[] _y = new float[4];
        private readonly float _depthX;
        private readonly float _depthY;
        private readonly float _depthAt;

        public Surface(in Matrix4x4 transform, int width, int height)
        {
            var z = new float[4];
            for (var corner = 0; corner < 4; corner++)
            {
                var position = new Vector3((corner & 1) != 0 ? 1 : -1, (corner & 2) != 0 ? 1 : -1, 0);
                _ = Project(position, transform, width, height, out _x[corner], out _y[corner], out z[corner]);
            }

            // depth = depthX x + depthY y + depthAt through corners 0, 1, 2.
            var (ux, uy, uz) = (_x[1] - _x[0], _y[1] - _y[0], z[1] - z[0]);
            var (vx, vy, vz) = (_x[2] - _x[0], _y[2] - _y[0], z[2] - z[0]);
            var determinant = (ux * vy) - (uy * vx);
            _depthX = ((uz * vy) - (uy * vz)) / determinant;
            _depthY = ((ux * vz) - (uz * vx)) / determinant;
            _depthAt = z[0] - (_depthX * _x[0]) - (_depthY * _y[0]);
        }

        public float Depth(float x, float y) => (_depthX * x) + (_depthY * y) + _depthAt;

        // Inside either triangle (0, 1, 2) or (2, 1, 3), edges included, whatever their winding.
        public bool Covers(float x, float y) => Inside(0, 1, 2, x, y) || Inside(2, 1, 3, x, y);

        private bool Inside(int a, int b, int c, float x, float y)
        {
            var e0 = Edge(a, b, x, y);
            var e1 = Edge(b, c, x, y);
            var e2 = Edge(c, a, x, y);
            return (e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0);
        }

        private float Edge(int a, int b, float x, float y) => ((_x[b] - _x[a]) * (y - _y[a])) - ((_y[b] - _y[a]) * (x - _x[a]));
    }
}
