namespace HyperPlot;

/// <summary>
/// One panel of a compiled plot (design §4.5, §4.8, row G6b): its layers' plans, the M4 batches of its pairs of scales, the
/// boundaries of its pixel columns, and its learned and drawn domains. A plot without a facet has one panel; a facet has one
/// a category, or a pair of categories.
/// </summary>
internal sealed class PlotPanel
{
    // The buffers of the boundaries, in data units and in the scale space, primary and secondary: as long as the widest W + 1
    // seen, so that a width seen before, or a narrower one, allocates nothing (§4.6).
    private double[] _data = [];
    private double[] _scale = [];
    private double[] _data2 = [];
    private double[] _scale2 = [];

    /// <summary>
    /// Gets or sets the panel's rows (row G6b): a run of the plot's table, or a table of its gathered rows; null without a
    /// facet.
    /// </summary>
    public PanelData? Data { get; set; }

    /// <summary>Gets or sets the table the panel's layers read, whose names the messages use: the plot's, or the panel's own.</summary>
    public DataTable? Table { get; set; }

    /// <summary>Gets or sets the layers' plans, one a layer of the spec, in its order.</summary>
    public LayerPlan[] Layers { get; set; } = [];

    /// <summary>
    /// Gets the boundaries of the pixel columns along x, W + 1 of them for the panel's width W (H + 1 for its height when
    /// flipped), in data units (design §4.8): the start of a buffer that holds the widest panel's.
    /// </summary>
    public ArraySegment<double> Boundaries { get; private set; } = ArraySegment<double>.Empty;

    /// <summary>Gets the boundaries over the secondary x domain (row G6a): M4 runs a layer on ScaleX2 over them.</summary>
    public ArraySegment<double> Boundaries2 { get; private set; } = ArraySegment<double>.Empty;

    /// <summary>Gets or sets the learned x domain (design §4.2), before nice widens it in the layout (§4.6).</summary>
    public ScaleRange LearnedX { get; set; }

    /// <summary>Gets or sets the learned y domain.</summary>
    public ScaleRange LearnedY { get; set; }

    /// <summary>Gets or sets the learned secondary x domain (row G6a).</summary>
    public ScaleRange LearnedX2 { get; set; }

    /// <summary>Gets or sets the learned secondary y domain.</summary>
    public ScaleRange LearnedY2 { get; set; }

    /// <summary>Gets or sets the x domain of the boundaries: <see cref="ScaleRange.None"/> until they are made, which no domain equals.</summary>
    public ScaleRange X { get; set; } = ScaleRange.None;

    /// <summary>Gets or sets the drawn y domain.</summary>
    public ScaleRange Y { get; set; }

    /// <summary>Gets or sets the secondary x domain of the secondary boundaries.</summary>
    public ScaleRange X2 { get; set; } = ScaleRange.None;

    /// <summary>Gets or sets the drawn secondary y domain.</summary>
    public ScaleRange Y2 { get; set; }

    /// <summary>Gets or sets the axes of the last recording (row G6a): a layer on a secondary scale is mapped by its pair's.</summary>
    public (GeomFrame.Axis X, GeomFrame.Axis Y, GeomFrame.Axis X2, GeomFrame.Axis Y2) Frames { get; set; }

    /// <summary>
    /// Writes the W + 1 boundaries of the pixel columns over the primary x domain (design §4.8), in buffers that grow to the
    /// widest panel's and never shrink: the y labels' width sets W, so a domain that changes W writes into the same buffers.
    /// </summary>
    public void MakeBoundaries(ScaleRange domain, int width, IScale? transform)
    {
        // Until the buffer holds this domain's boundaries, it holds no domain's.
        X = ScaleRange.None;
        Boundaries = Write(domain, width, ref _data, ref _scale, transform);
        X = domain;
    }

    /// <summary>
    /// Writes the boundaries of the pixel columns over the secondary x domain (row G6a), as <see cref="MakeBoundaries"/> does
    /// over the primary, in buffers of their own.
    /// </summary>
    public void MakeSecondaryBoundaries(ScaleRange domain, int width, IScale? transform)
    {
        X2 = ScaleRange.None;
        Boundaries2 = Write(domain, width, ref _data2, ref _scale2, transform);
        X2 = domain;
    }

    /// <summary>Releases the layers' plans, and their batches' series with them.</summary>
    public void Release()
    {
        foreach (var layer in Layers)
        {
            layer.Release();
        }

        Layers = [];
    }

    /// <summary>
    /// Writes the W + 1 boundaries of the pixel columns over an x domain, or throws when they do not increase in double (design
    /// §4.8). Without a transform they are differences from the domain's origin; with one they are made in the scale space and
    /// mapped back to data units with <see cref="IScale.Inverse"/>, and must still increase (design §4.2). Allocates only to
    /// grow the buffers past the widest W seen, so that a new x domain, or a new width, costs no garbage.
    /// </summary>
    private static ArraySegment<double> Write(ScaleRange domain, int width, ref double[] data, ref double[] scale, IScale? transform)
    {
        if (data.Length < width + 1)
        {
            (data, scale) = (new double[width + 1], new double[width + 1]);
        }

        var boundaries = data.AsSpan(0, width + 1);
        var scaleBoundaries = scale.AsSpan(0, width + 1);
        var target = transform is null ? boundaries : scaleBoundaries;
        for (var k = 0; k <= width; k++)
        {
            target[k] = domain.Lower + ((domain.Upper - domain.Lower) * k / width);
            if (k > 0 && !(target[k - 1] < target[k]))
            {
                throw new ArgumentException(
                    $"The x domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}] is too narrow for {width} pixel columns: their boundaries do not increase in double."
                );
            }
        }

        if (transform is null)
        {
            return new ArraySegment<double>(data, 0, width + 1);
        }

        for (var k = 0; k <= width; k++)
        {
            scaleBoundaries[k] += domain.Origin;
        }

        transform.Inverse(scaleBoundaries, boundaries);
        for (var k = 0; k <= width; k++)
        {
            if (!double.IsFinite(boundaries[k]) || (k > 0 && !(boundaries[k - 1] < boundaries[k])))
            {
                throw new ArgumentException(
                    $"The x domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}] of the scale space is too narrow for {width} pixel columns: its boundaries, mapped back to data units, do not increase in double."
                );
            }
        }

        return new ArraySegment<double>(data, 0, width + 1);
    }
}
