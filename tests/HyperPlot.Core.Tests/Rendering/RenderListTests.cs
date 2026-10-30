using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

public sealed partial class RenderListTests
{
    private static readonly LineStyle Solid = new(new Color(0, 0, 0, 255), 1);

    /// <summary>
    /// A polyline becomes segments that carry the length along their run; a point that is not finite breaks the run and the
    /// length starts at 0 again (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (0,0) (3,4) (3,4) NaN (10,0) (10,5) +∞ (20,0) (20,0): a run of three with a repeated point, a run of two, and
    ///   a run of a repeated point.
    ///
    /// Steps:
    /// - Record the polyline.
    ///
    /// Expected:
    /// - One polyline with four segments: (0,0)-(3,4) at 0, (3,4)-(3,4) at 5, (10,0)-(10,5) at 0, (20,0)-(20,0) at 0; its
    ///   box spans (0,0) to (20,5).
    /// </remarks>
    [Fact]
    public void DrawPolyline_Breaks_StartTheLengthAgain()
    {
        var list = new RenderList();
        Vector2[] points =
        [
            new(0, 0),
            new(3, 4),
            new(3, 4),
            new(float.NaN, 1),
            new(10, 0),
            new(10, 5),
            new(float.PositiveInfinity, 0),
            new(20, 0),
            new(20, 0),
        ];

        list.DrawPolyline(points, Solid);

        var polyline = Assert.Single(list.Polylines.ToArray());
        Assert.Equal((0, 4), (polyline.SegmentStart, polyline.SegmentCount));
        Assert.Equal((0f, 0f, 20f, 5f), (polyline.MinX, polyline.MinY, polyline.MaxX, polyline.MaxY));
        Assert.Equal(
            [
                new SegmentRecord(new(0, 0), new(3, 4), 0),
                new SegmentRecord(new(3, 4), new(3, 4), 5),
                new SegmentRecord(new(10, 0), new(10, 5), 0),
                new SegmentRecord(new(20, 0), new(20, 0), 0),
            ],
            list.Segments.ToArray()
        );
    }

    /// <summary>
    /// What draws nothing records nothing: no point, one point, single points between breaks, and a width of 0 (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty list.
    ///
    /// Steps:
    /// - Record no point; one point; (1,1) NaN (2,2) NaN; and two points at width 0.
    ///
    /// Expected:
    /// - No polyline and no segment.
    /// </remarks>
    [Fact]
    public void DrawPolyline_NothingToStroke_RecordsNothing()
    {
        var list = new RenderList();

        list.DrawPolyline([], Solid);
        list.DrawPolyline([new(1, 1)], Solid);
        list.DrawPolyline([new(1, 1), new(float.NaN, 0), new(2, 2), new(0, float.NaN)], Solid);
        list.DrawPolyline([new(1, 1), new(5, 5)], Solid with { Width = 0 });

        Assert.Equal((0, 0), (list.Polylines.Length, list.Segments.Length));
    }

    /// <summary>
    /// Widths, dash patterns, and clip sizes out of range throw (design §5.1); Clear keeps nothing and resets the clip.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty list.
    ///
    /// Steps:
    /// - Record with widths −1, NaN, ∞; dashes with a negative, an infinite, or a NaN part, or On + Off of 0; clip with a
    ///   negative width and height. Clip, record, clear, record.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException each time; after Clear, the list holds only the last polyline, clipped to everything.
    /// </remarks>
    [Fact]
    public void Arguments_OutOfRange_ThrowAndClearResets()
    {
        var list = new RenderList();
        Vector2[] two = [new(0, 0), new(1, 1)];
        foreach (var width in new[] { -1f, float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawPolyline(two, Solid with { Width = width }));
        }

        LineDash[] dashes = [new(-1, 1), new(1, -1), new(float.PositiveInfinity, 1), new(1, float.NaN), new(1, 1, float.NaN), new(0, 0)];
        foreach (var dash in dashes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawPolyline(two, Solid with { Dash = dash }));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => list.SetClip(0, 0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.SetClip(0, 0, 1, -1));
        list.SetClip(1, 2, 3, 4);
        list.DrawPolyline(two, Solid);
        Assert.Equal(new ClipRect(1, 2, 4, 6), list.Polylines[0].Clip);

        list.Clear();
        list.DrawPolyline(two, Solid);

        Assert.Equal((1, 1, ClipRect.All), (list.Polylines.Length, list.Segments.Length, list.Polylines[0].Clip));
    }

    /// <summary>
    /// Recording a frame again after the buffers have grown allocates nothing (design §5.1, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 16 M4-like polylines of 7,680 points each; one frame recorded to grow the buffers.
    ///
    /// Steps:
    /// - Clear, clip, and record the 16 polylines 10 times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Record_SteadyState_AllocatesNothing()
    {
        var random = new Random(9);
        var points = Enumerable.Range(0, 7680).Select(i => new Vector2(i / 4f, random.NextSingle() * 500)).ToArray();
        var list = new RenderList();
        Record();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            Record();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Record()
        {
            list.Clear();
            list.SetClip(0, 0, 1920, 1080);
            for (var series = 0; series < 16; series++)
            {
                list.DrawPolyline(points, Solid);
            }
        }
    }

    /// <summary>
    /// The generated shaders were made from the HLSL in the repository: Shaders.g.cs carries its SHA-256 (design §5.7). Runs
    /// on every OS, so a shader changed without running tools/ShaderGen fails the gate anywhere.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The repository's src/HyperPlot.Rendering.D3D11/Shaders, found by walking up from the test's output directory.
    ///
    /// Steps:
    /// - Hash every *.hlsl in ordinal name order (name, 0, text with LF line ends, 0) and read HlslSha256 from Shaders.g.cs.
    ///
    /// Expected:
    /// - The two are equal.
    /// </remarks>
    [Fact]
    public void Shaders_Generated_MatchTheHlsl()
    {
        var shaders = Path.Combine(RepositoryRoot(), "src", "HyperPlot.Rendering.D3D11", "Shaders");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.GetFiles(shaders, "*.hlsl").Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(path).ReplaceLineEndings("\n")));
            hash.AppendData([0]);
        }

        var generated = File.ReadAllText(Path.Combine(shaders, "Shaders.g.cs"));
        var recorded = HashLine().Match(generated).Groups[1].Value;

        Assert.Equal(Convert.ToHexString(hash.GetHashAndReset()), recorded);
    }

    /// <summary>
    /// Walks up from the test's output directory to the one with HyperPlot.slnx. Not [CallerFilePath]: the CI build maps
    /// source paths to /_/ (ContinuousIntegrationBuild), where no file is.
    /// </summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyperPlot.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The test runs from outside the repository.");
    }

    [GeneratedRegex("HlslSha256 = \"([0-9A-F]{64})\"")]
    private static partial Regex HashLine();

    /// <summary>
    /// DrawRect checks its arguments (design §5.1): a position that is not finite, and a width or a height that is not
    /// finite or below 0, throw ArgumentOutOfRangeException naming it; 0 records nothing; the record is premultiplied.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty list.
    ///
    /// Steps:
    /// - Draw rectangles with NaN and ∞ positions, negative, NaN, and ∞ sizes, a size of 0, and a valid half-transparent one.
    ///
    /// Expected:
    /// - The parameter's name in each exception; no rectangle for the size 0; one record of the premultiplied color under
    ///   the clip set before it.
    /// </remarks>
    [Fact]
    public void DrawRect_Arguments()
    {
        var list = new RenderList();
        var black = new Color(0, 0, 0, 255);
        Assert.Equal("x", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(float.NaN, 0, 1, 1, black)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(0, float.PositiveInfinity, 1, 1, black)).ParamName);
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(0, 0, -1, 1, black)).ParamName);
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(0, 0, float.NaN, 1, black)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(0, 0, 1, float.PositiveInfinity, black)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawRect(0, 0, 1, -0.5f, black)).ParamName);
        list.DrawRect(0, 0, 0, 5, black);
        list.DrawRect(0, 0, 5, 0, black);
        Assert.Equal(0, list.Rects.Length);
        Assert.Equal(0, list.Commands.Length);

        list.SetClip(1, 2, 3, 4);
        list.DrawRect(1.5f, 2.5f, 3, 4, new Color(255, 128, 0, 128));
        Assert.Equal(new RectInstance(1.5f, 2.5f, 3, 4, new System.Numerics.Vector4(128 / 255f, 64 / 255f, 0, 128 / 255f)), list.Rects[0]);
        Assert.Equal(new ClipRect(1, 2, 4, 6), list.RectClips[0]);
        Assert.Equal(new RenderCommand(RenderCommandKind.Rect, 0), list.Commands[0]);
    }
}
