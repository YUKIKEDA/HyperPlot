namespace HyperPlot.Tests;

public sealed class SpecNodeTests
{
    /// <summary>Every node made and every copy that <c>with</c> makes gets an id of its own (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A plot spec built from a table of two columns, its layer, and its scales.
    ///
    /// Steps:
    /// - Copy the plot, the layer, and a scale with <c>with</c>, changing nothing and changing a member; make two scales with
    ///   Scale.Linear and one with WithDomain.
    ///
    /// Expected:
    /// - All ids differ and none is 0; a copy that changes nothing still differs from its original (the id is part of the
    ///   record's equality), and keeps the original's members.
    /// </remarks>
    [Fact]
    public void Id_MadeAndCopied_IsNewEachTime()
    {
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 4)).Add("y", Column.Wrap(new float[4])).Build();
        var plot = Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build();
        var layer = (LineLayerSpec)plot.Layers[0];

        var plotCopy = plot with { };
        var layerCopy = layer with { };
        var restyled = layer with { Style = layer.Style with { Width = 3 } };
        var scale = Scale.Linear();
        var other = Scale.Linear();
        var domained = scale.WithDomain(0, 1);

        SpecNode[] nodes = [plot, layer, plot.ScaleX, plot.ScaleY, plotCopy, layerCopy, restyled, scale, other, domained];
        var ids = nodes.Select(n => n.Id.Value).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(0L, ids);
        Assert.NotEqual(plot, plotCopy);
        Assert.NotEqual(layer, layerCopy);
        Assert.Equal((layer.X, layer.Y, layer.Style), (layerCopy.X, layerCopy.Y, layerCopy.Style));
        Assert.Same(plot.Data, plotCopy.Data);
        Assert.Null(scale.Domain);
        Assert.Equal((0d, 1d), domained.Domain);
    }

    /// <summary>WithDomain refuses a domain that is not finite or whose min is not below its max (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A linear scale.
    ///
    /// Steps:
    /// - Call WithDomain with NaN, ±∞ at either end, min equal to max, and min above max.
    ///
    /// Expected:
    /// - ArgumentException each time, naming the domain.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.NaN)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void WithDomain_OutOfRange_Throws(double min, double max)
    {
        var exception = Assert.Throws<ArgumentException>(() => Scale.Linear().WithDomain(min, max));

        Assert.Contains("domain", exception.Message, StringComparison.Ordinal);
    }
}
