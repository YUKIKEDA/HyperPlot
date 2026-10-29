using System.Numerics;

namespace HyperPlot.Tests;

[Collection("Large data")]
public sealed class M4Tests
{
    public static TheoryData<string, string, M4Gaps> Cases
    {
        get
        {
            var data = new TheoryData<string, string, M4Gaps>();
            foreach (var pattern in PyramidData.Patterns)
            {
                data.Add("float", pattern, M4Gaps.Break);
                data.Add("float", pattern, M4Gaps.Connect);
            }

            data.Add("double", "Sparse", M4Gaps.Break);
            data.Add("int", "None", M4Gaps.Break);
            data.Add("long", "None", M4Gaps.Connect);
            data.Add("byte", "None", M4Gaps.Break);
            return data;
        }
    }

    /// <summary>
    /// M4 extraction equals a loop over every element, for every kind of X, NaN pattern, and pixel-column width.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks and 70,001 elements of seeded values with ties and the named NaN pattern; a pyramid built to completion.
    /// - X evenly spaced (start 1000, step 0.1), and stored as double, long, and float, non-decreasing with repeats.
    ///
    /// Steps:
    /// - For each X, extract 8 views: boundaries evenly spaced (some past both ends of the data), at element values exactly, or
    ///   one double step above element values, with 1 to 1,920 pixel columns, so that a column holds from 0 to every element.
    /// - Compute the same points with a loop over every element (Reference.M4).
    ///
    /// Expected:
    /// - The same points: index, X, Y, roles, and breaks; none provisional. Points come in index order without repeats.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Extract_RandomViews_EqualALoopOverEveryElement(string type, string pattern, M4Gaps gaps)
    {
        switch (type)
        {
            case "float":
                CheckViews<float>(pattern, gaps);
                break;
            case "double":
                CheckViews<double>(pattern, gaps);
                break;
            case "int":
                CheckViews<int>(pattern, gaps);
                break;
            case "long":
                CheckViews<long>(pattern, gaps);
                break;
            default:
                CheckViews<byte>(pattern, gaps);
                break;
        }
    }

    /// <summary>
    /// Each role and break of design §5.5.1 on a series small enough to write the expected points by hand.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - X 0..19 evenly spaced; Y 5, 1, 9, 2, 7, then NaN, 3, 3, 8, 0, 4, then 6, 6, 6, 6, 6, then 2, 2, NaN, 1.
    ///
    /// Steps:
    /// - Extract with boundaries 1, 5, 11, 16, 19 (Break), and with 1, 11 (Connect).
    ///
    /// Expected:
    /// - Break: Before at 0; column [1, 5) is raw; column [5, 11) drops the NaN first and breaks before its min; column
    ///   [11, 16) merges first and min (all equal) into one point and last and max into another; After at 19.
    /// - Connect: the NaN is skipped, and the first of [5, 11) is 6.
    /// </remarks>
    [Fact]
    public void Extract_HandWrittenSeries_GivesTheRolesAndBreaksOfTheDesign()
    {
        float[] y = [5, 1, 9, 2, 7, float.NaN, 3, 3, 8, 0, 4, 6, 6, 6, 6, 6, 2, 2, float.NaN, 1];
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(y);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        var x = Column.Uniform(0, 1, y.Length);
        var points = new M4Point[M4.MaxPointCount(4)];

        var result = M4.Extract(x, pyramid, default, [1, 5, 11, 16, 19], points);

        Assert.False(result.IsProvisional);
        Assert.Equal(
            [
                (0L, M4Roles.Before, false),
                (1L, M4Roles.Raw, false),
                (2L, M4Roles.Raw, false),
                (3L, M4Roles.Raw, false),
                (4L, M4Roles.Raw, false),
                (8L, M4Roles.Max, true),
                (9L, M4Roles.Min, false),
                (10L, M4Roles.Last, false),
                (11L, M4Roles.First | M4Roles.Min | M4Roles.Max, false),
                (15L, M4Roles.Last, false),
                (16L, M4Roles.Raw, false),
                (17L, M4Roles.Raw, false),
                (19L, M4Roles.After, true),
            ],
            points[..result.Count].Select(p => (p.Index, p.Roles, p.BreakBefore))
        );

        var connected = M4.Extract(x, pyramid, default, [1, 11], points, new M4Options(M4Gaps.Connect));
        Assert.Equal(
            [(0L, M4Roles.Before), (1L, M4Roles.First), (2L, M4Roles.Max), (9L, M4Roles.Min), (10L, M4Roles.Last), (11L, M4Roles.After)],
            points[..connected.Count].Select(p => (p.Index, p.Roles))
        );
    }

    /// <summary>
    /// Bad arguments throw the exceptions design §5.5.1 names.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pyramid over 8 floats; an evenly spaced X of 8 and of 7; a stored X that decreases.
    ///
    /// Steps:
    /// - Extract with 1 boundary, a NaN boundary, an infinite one, equal ones, a destination 1 short, an X of another length.
    /// - Extract with the decreasing X before and after its statistics are computed; extract after disposing the pyramid.
    ///
    /// Expected:
    /// - ArgumentException for the boundaries, the destination, and the length.
    /// - Before its statistics, the decreasing X gives a provisional result; after, InvalidOperationException.
    /// - ObjectDisposedException after Dispose.
    /// </remarks>
    [Fact]
    public void Extract_BadArguments_Throw()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[8]);
        var pyramid = MinMaxPyramidTests.Build(pool, column);
        var x = Column.Uniform(0, 1, 8);
        using var decreasing = Column.Wrap(new double[] { 0, 1, 2, 3, 4, 5, 7, 6 });
        var points = new M4Point[M4.MaxPointCount(2)];

        Assert.Throws<ArgumentException>(() => M4.Extract(x, pyramid, default, [1], points));
        Assert.Throws<ArgumentException>(() => M4.Extract(x, pyramid, default, [0, double.NaN, 4], points));
        Assert.Throws<ArgumentException>(() => M4.Extract(x, pyramid, default, [0, double.PositiveInfinity], points));
        Assert.Throws<ArgumentException>(() => M4.Extract(x, pyramid, default, [0, 2, 2], points));
        Assert.Throws<ArgumentException>(() => M4.Extract(x, pyramid, default, [0, 2, 4], points.AsSpan(0, points.Length - 1)));
        Assert.Throws<ArgumentException>(() => M4.Extract(Column.Uniform(0, 1, 7), pyramid, default, [0, 2, 4], points));
        Assert.True(M4.Extract(decreasing, pyramid, default, [0, 2, 4], points).IsProvisional);
        Eventually.Completes(pool.PrecomputeStats(decreasing));
        Assert.Throws<InvalidOperationException>(() => M4.Extract(decreasing, pyramid, default, [0, 2, 4], points));
        pyramid.Dispose();
        Assert.Throws<ObjectDisposedException>(() => M4.Extract(x, pyramid, default, [0, 2, 4], points));
    }

    private static void CheckViews<T>(string pattern, M4Gaps gaps)
        where T : unmanaged, INumber<T>
    {
        var integral = typeof(T) != typeof(float) && typeof(T) != typeof(double);
        var values = PyramidData.Convert<T>(PyramidData.Values(PyramidData.Length, pattern, 31, integral));
        var y = PyramidData.AsStored(values);
        using var pool = new PlotWorkerPool(4);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        var random = new Random(37);
        foreach (var kind in new[] { "Uniform", "Double", "Long", "Float" })
        {
            var (x, xValues) = PyramidData.X(kind, values.Length, 41);
            var stats = x switch
            {
                Column<double> doubles => pool.PrecomputeStats(doubles),
                Column<long> longs => pool.PrecomputeStats(longs),
                Column<float> floats => pool.PrecomputeStats(floats),
                _ => Task.CompletedTask,
            };
            Eventually.Completes(stats);
            for (var view = 0; view < 8; view++)
            {
                var boundaries = PyramidData.Boundaries(xValues, random);
                if (boundaries is null)
                {
                    continue;
                }

                var points = new M4Point[M4.MaxPointCount(boundaries.Length - 1)];
                var result = x switch
                {
                    UniformColumn uniform => M4.Extract(uniform, pyramid, default, boundaries, points, new M4Options(gaps)),
                    Column<double> doubles => M4.Extract(doubles, pyramid, default, boundaries, points, new M4Options(gaps)),
                    Column<long> longs => M4.Extract(longs, pyramid, default, boundaries, points, new M4Options(gaps)),
                    Column<float> floats => M4.Extract(floats, pyramid, default, boundaries, points, new M4Options(gaps)),
                    _ => throw new InvalidOperationException(),
                };

                var expected = Reference.M4(xValues, y, boundaries, gaps);
                Assert.False(result.IsProvisional);
                Assert.Equal(expected, points[..result.Count].Select(p => new Reference.Point(p.Index, p.X, p.Y, p.Roles, p.BreakBefore)));
            }

            (x as IDisposable)?.Dispose();
        }
    }
}
