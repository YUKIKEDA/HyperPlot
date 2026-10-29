using HyperPlot.Rendering;

namespace HyperPlot.Tests;

public sealed class PlotBuilderTests
{
    /// <summary>The builder makes the spec it was told: layers in order, their strokes, the scales, and defaults.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of an evenly spaced "t", a Column&lt;float&gt; "a", and a Column&lt;double&gt; "b".
    ///
    /// Steps:
    /// - Build a plot of two lines, the first configured with a color, a width, and a dash, with a y scale given; count the
    ///   calls of configure; build a plot of no layers.
    ///
    /// Expected:
    /// - Two LineLayerSpec in order with their columns; the first has the stroke set, the second the default (black, 1, solid);
    ///   configure was called once; ScaleY is the scale given and ScaleX a linear scale with no domain; the empty plot has no
    ///   layer.
    /// </remarks>
    [Fact]
    public void Build_Lines_MakesTheSpec()
    {
        var table = Table();
        var y = Scale.Linear().WithDomain(-1, 1);
        var calls = 0;
        var dash = new LineDash(4, 2);

        var plot = Plot.Create(table)
            .Line(
                table.Col<double>("t"),
                table.Col<float>("a"),
                l =>
                {
                    calls++;
                    _ = l.Color(new Color(1, 2, 3, 4)).Width(2.5f).Dash(dash);
                }
            )
            .Line(table.Col<double>("t"), table.Col<double>("b"))
            .ScaleY(y)
            .Build();
        var empty = Plot.Create(table).Build();

        Assert.Equal(1, calls);
        Assert.Same(table, plot.Data);
        var first = Assert.IsType<LineLayerSpec>(plot.Layers[0]);
        var second = Assert.IsType<LineLayerSpec>(plot.Layers[1]);
        Assert.Equal(2, plot.Layers.Count);
        Assert.Equal((table.Col<double>("t").ToColumnRef(), table.Col<float>("a").ToColumnRef()), (first.X, first.Y));
        Assert.Equal(table.Col<double>("b").ToColumnRef(), second.Y);
        Assert.Equal(new LineStyle(new Color(1, 2, 3, 4), 2.5f, dash), first.Style);
        Assert.Equal(new LineStyle(new Color(0, 0, 0, 255), 1), second.Style);
        Assert.Same(y, plot.ScaleY);
        Assert.Null(Assert.IsType<LinearScaleSpec>(plot.ScaleX).Domain);
        Assert.Empty(empty.Layers);
    }

    /// <summary>Line refuses references that are default, of another table, or whose Y holds no values (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two tables of the same shape: "t" evenly spaced and "a" a Column&lt;float&gt;.
    ///
    /// Steps:
    /// - Add a line with a default X, a default Y, an X of the other table, a Y of the other table, and "t" as Y.
    ///
    /// Expected:
    /// - ArgumentException each time, with ParamName x or y; the last names "t" and its kind.
    /// </remarks>
    [Fact]
    public void Line_BadReference_Throws()
    {
        var table = Table();
        var other = Table();
        var t = table.Col<double>("t");
        var a = table.Col<float>("a");

        Assert.Equal("x", Assert.Throws<ArgumentException>(() => Plot.Create(table).Line(default(ColumnRef<double>), a)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentException>(() => Plot.Create(table).Line(t, default(ColumnRef<float>))).ParamName);
        Assert.Equal("x", Assert.Throws<ArgumentException>(() => Plot.Create(table).Line(other.Col<double>("t"), a)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentException>(() => Plot.Create(table).Line(t, other.Col<float>("a"))).ParamName);
        var uniform = Assert.Throws<ArgumentException>(() => Plot.Create(table).Line(t, t));
        Assert.Equal("y", uniform.ParamName);
        Assert.Contains("\"t\" is a UniformColumn", uniform.Message, StringComparison.Ordinal);
    }

    /// <summary>The stroke set in configure is checked as RenderList checks it (design §5.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "t" and "a".
    ///
    /// Steps:
    /// - Add lines with a width of NaN, of -1, and a dash of On and Off 0.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException each time.
    /// </remarks>
    [Fact]
    public void Line_StrokeOutOfRange_Throws()
    {
        var table = Table();
        var t = table.Col<double>("t");
        var a = table.Col<float>("a");

        Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Line(t, a, l => l.Width(float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Line(t, a, l => l.Width(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Line(t, a, l => l.Dash(new LineDash(0, 0))));
    }

    /// <summary>After Build the builder refuses every call; null arguments are refused.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A builder over a table of "t" and "a", built once.
    ///
    /// Steps:
    /// - Call Line, ScaleX, ScaleY, and Build on it; call Plot.Create with null, and ScaleX and ScaleY with null on a new builder.
    ///
    /// Expected:
    /// - InvalidOperationException for the four calls; ArgumentNullException for the three nulls.
    /// </remarks>
    [Fact]
    public void Builder_AfterBuildOrNull_Throws()
    {
        var table = Table();
        var builder = Plot.Create(table);
        _ = builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Line(table.Col<double>("t"), table.Col<float>("a")));
        Assert.Throws<InvalidOperationException>(() => builder.ScaleX(Scale.Linear()));
        Assert.Throws<InvalidOperationException>(() => builder.ScaleY(Scale.Linear()));
        Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Throws<ArgumentNullException>(() => Plot.Create(null!));
        Assert.Throws<ArgumentNullException>(() => Plot.Create(table).ScaleX(null!));
        Assert.Throws<ArgumentNullException>(() => Plot.Create(table).ScaleY(null!));
    }

    private static DataTable Table() =>
        DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("a", Column.Wrap(new float[3])).Add("b", Column.Wrap(new double[3])).Build();
}
