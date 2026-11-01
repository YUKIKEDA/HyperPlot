namespace HyperPlot;

/// <summary>
/// One panel of a compiled plot (design §4.5, §4.8, row G6b): its layers' plans, the M4 batches of its pairs of scales, the
/// boundaries of its pixel columns, and its learned and drawn domains. A plot without a facet has one panel; a facet has one
/// a category, or a pair of categories.
/// </summary>
internal sealed class PlotPanel
{
    // The buffers of the boundaries of the last four lengths, so that drawing domains seen before allocates nothing (§4.6).
    private readonly (double[] Data, double[] Scale)[] _widths = new (double[], double[])[4];

    public PlotPanel()
    {
        Batches = [new M4Batch(), new M4Batch(), new M4Batch(), new M4Batch()];
    }

    /// <summary>
    /// Gets the M4 batches of the pairs of scales a layer may lie on (row G6a): at (secondary x ? 1 : 0) + (secondary y ? 2 :
    /// 0). The first is the primary pair's; the others hold series only when a layer lies on a secondary scale.
    /// </summary>
    public M4Batch[] Batches { get; }

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
    /// flipped), in data units (design §4.8).
    /// </summary>
    public double[] Boundaries { get; private set; } = [];

    /// <summary>Gets the boundaries in the scale space.</summary>
    public double[] ScaleBoundaries { get; private set; } = [];

    /// <summary>Gets the boundaries over the secondary x domain (row G6a): M4 runs a layer on ScaleX2 over them.</summary>
    public double[] Boundaries2 { get; private set; } = [];

    /// <summary>Gets the secondary boundaries in the scale space.</summary>
    public double[] ScaleBoundaries2 { get; private set; } = [];

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
    /// Writes the W + 1 boundaries of the pixel columns over the primary x domain (design §4.8): a new width takes kept buffers
    /// or new ones in the place of the least recent.
    /// </summary>
    public void MakeBoundaries(ScaleRange domain, int width, IScale? transform)
    {
        // Until the buffer holds this domain's boundaries, it holds no domain's. A new width makes new buffers: the layout
        // changed, which is not the steady state.
        X = ScaleRange.None;
        if (Boundaries.Length != width + 1)
        {
            (Boundaries, ScaleBoundaries) = Buffers(width + 1);
        }

        WriteBoundaries(domain, width, Boundaries, ScaleBoundaries, transform);
        X = domain;
    }

    /// <summary>
    /// Writes the boundaries of the pixel columns over the secondary x domain (row G6a), as <see cref="MakeBoundaries"/> does
    /// over the primary: a new width makes new buffers, which is not the steady state.
    /// </summary>
    public void MakeSecondaryBoundaries(ScaleRange domain, int width, IScale? transform)
    {
        X2 = ScaleRange.None;
        if (Boundaries2.Length != width + 1)
        {
            (Boundaries2, ScaleBoundaries2) = (new double[width + 1], new double[width + 1]);
        }

        WriteBoundaries(domain, width, Boundaries2, ScaleBoundaries2, transform);
        X2 = domain;
    }

    /// <summary>Releases the layers' plans and the batches' series.</summary>
    public void Release()
    {
        foreach (var layer in Layers)
        {
            layer.Release();
        }

        Layers = [];
        foreach (var batch in Batches)
        {
            batch.Clear();
        }
    }

    /// <summary>
    /// Writes the W + 1 boundaries of the pixel columns over an x domain, or throws when they do not increase in double (design
    /// §4.8). Without a transform they are differences from the domain's origin; with one they are made in the scale space and
    /// mapped back to data units with <see cref="IScale.Inverse"/>, and must still increase (design §4.2). Allocates nothing,
    /// so that a new x domain costs no garbage.
    /// </summary>
    private static void WriteBoundaries(ScaleRange domain, int width, double[] boundaries, double[] scaleBoundaries, IScale? transform)
    {
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
            return;
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
    }

    /// <summary>Returns the buffers of a length: kept ones, or new ones in the place of the least recent.</summary>
    private (double[] Data, double[] Scale) Buffers(int length)
    {
        for (var i = 0; i < _widths.Length; i++)
        {
            if (_widths[i].Data?.Length == length)
            {
                var found = _widths[i];
                Array.Copy(_widths, 0, _widths, 1, i);
                _widths[0] = found;
                return found;
            }
        }

        Array.Copy(_widths, 0, _widths, 1, _widths.Length - 1);
        _widths[0] = (new double[length], new double[length]);
        return _widths[0];
    }
}
