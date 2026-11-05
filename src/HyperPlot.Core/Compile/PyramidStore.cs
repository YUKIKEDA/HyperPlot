namespace HyperPlot;

/// <summary>
/// The pyramids of the Y columns that one plot, or the plots of one figure, read (design §5.5.3, row G6c): one a column,
/// built the first time a plan asks for it, counted by the plans that hold it, and released when none does.
/// </summary>
/// <remarks>One thread at a time uses a store, its plans'.</remarks>
internal sealed class PyramidStore : IDisposable
{
    private readonly Dictionary<Column, Entry> _entries = [];

    /// <summary>Gets the number of pyramids held. Tests only.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Returns a column's pyramid for a plan that does not hold it yet, building it the first time any plan asks; the plan
    /// holds it until it releases it.
    /// </summary>
    public PlotPyramid Acquire(Column y, PlotWorkerPool pool, out bool built)
    {
        built = !_entries.TryGetValue(y, out var entry);
        if (built)
        {
            entry = new Entry(((IPyramidColumn)y).BuildPlotPyramid(pool));
            _entries.Add(y, entry);
        }

        entry!.Users++;
        return entry.Pyramid;
    }

    /// <summary>Returns a column's pyramid, or null when no plan holds one.</summary>
    public PlotPyramid? Find(Column y) => _entries.TryGetValue(y, out var entry) ? entry.Pyramid : null;

    /// <summary>Releases a plan's hold of a column's pyramid, and disposes it when no plan holds it.</summary>
    public void Release(Column y)
    {
        var entry = _entries[y];
        if (--entry.Users == 0)
        {
            entry.Pyramid.Dispose();
            _ = _entries.Remove(y);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Pyramid.Dispose();
        }

        _entries.Clear();
    }

    private sealed class Entry(PlotPyramid pyramid)
    {
        public PlotPyramid Pyramid { get; } = pyramid;

        public int Users { get; set; }
    }
}
