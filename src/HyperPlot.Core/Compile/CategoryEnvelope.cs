namespace HyperPlot;

/// <summary>
/// The least and the greatest y of each category of a layer over categories (rows G7b, G5a): the workers' passes over the Y
/// columns, then a pyramid over the least and one over the greatest, so that bands under a pixel join a pixel column at a
/// time. Made in steps in a frame that does not wait (row W1b1).
/// </summary>
internal sealed class CategoryEnvelope(CategoryExtremes low, CategoryExtremes high)
{
    private Column<double>? _minColumn;
    private Column<double>? _maxColumn;
    private bool _started;

    /// <summary>Gets the pass whose least values are the categories' lows.</summary>
    public CategoryExtremes Low => low;

    /// <summary>Gets the pass whose greatest values are the categories' highs: the same pass as <see cref="Low"/> for bars.</summary>
    public CategoryExtremes High => high;

    /// <summary>Gets the pyramid over the lows, or null when there is no category.</summary>
    public MinMaxPyramid<double>? MinPyramid { get; private set; }

    /// <summary>Gets the pyramid over the highs, or null when there is no category.</summary>
    public MinMaxPyramid<double>? MaxPyramid { get; private set; }

    /// <summary>Returns whether the passes were made over these versions of the Y columns.</summary>
    public bool Of(long lowVersion, long highVersion) => low.Version == lowVersion && high.Version == highVersion;

    /// <summary>
    /// Moves on as far as the workers have: the passes, then the pyramids over their results, waited for when the indexes
    /// wait. Returns whether all are done; a failure is thrown as it was.
    /// </summary>
    public bool Advance(LayerIndexes indexes)
    {
        if (!_started)
        {
            // Both passes are asked first, so that a frame that does not wait sees either fail.
            var (lowDone, highDone) = (indexes.Done(low.Completion), ReferenceEquals(low, high) || indexes.Done(high.Completion));
            if (!lowDone || !highDone)
            {
                return false;
            }

            if (low.Min.Length > 0)
            {
                (_minColumn, _maxColumn) = (Column.Wrap(low.Min), Column.Wrap(high.Max));
                (MinPyramid, MaxPyramid) = (indexes.Pool.BuildPyramid(_minColumn), indexes.Pool.BuildPyramid(_maxColumn));
            }

            _started = true;
        }

        if (MinPyramid is null)
        {
            return true;
        }

        var minDone = indexes.Complete(MinPyramid);
        return indexes.Complete(MaxPyramid!) && minDone;
    }

    /// <summary>Frees the pyramids and their columns.</summary>
    public void Free()
    {
        MinPyramid?.Dispose();
        MaxPyramid?.Dispose();
        _minColumn?.Dispose();
        _maxColumn?.Dispose();
        (MinPyramid, MaxPyramid, _minColumn, _maxColumn) = (null, null, null, null);
    }

    /// <summary>
    /// Steps a layer's envelopes (row W1b1): the drawn one stays until the one of the columns' current versions is done.
    /// Returns the layer's readiness.
    /// </summary>
    public static LayerReadiness Step(
        ref CategoryEnvelope? done,
        ref CategoryEnvelope? making,
        Column lows,
        Column highs,
        CategoricalColumn categories,
        RowRange rows,
        LayerIndexes indexes
    )
    {
        var (lowVersion, highVersion) = (lows.Version, highs.Version);
        if (done is not null && done.Of(lowVersion, highVersion))
        {
            return LayerReadiness.Ready;
        }

        if (making is null || !making.Of(lowVersion, highVersion))
        {
            making?.Free();
            making = null;
            var low = lows.StartCategoryExtremes(categories, indexes.Pool, rows);
            making = new CategoryEnvelope(low, ReferenceEquals(lows, highs) ? low : highs.StartCategoryExtremes(categories, indexes.Pool, rows));
        }

        if (!making.Advance(indexes))
        {
            return done is null ? LayerReadiness.Pending : LayerReadiness.Previous;
        }

        done?.Free();
        (done, making) = (making, null);
        return LayerReadiness.Ready;
    }
}
