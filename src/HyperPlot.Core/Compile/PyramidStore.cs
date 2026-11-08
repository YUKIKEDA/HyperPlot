namespace HyperPlot;

/// <summary>
/// The pyramids of the Y columns that one plot, or the plots of one figure, read (design §5.5.3, row G6c): one a column,
/// built the first time a plan or a making asks for it, counted by the plans that hold it and the makings that reserved it
/// (row W1b2), and released when neither does.
/// </summary>
/// <remarks>
/// The plans' thread holds and releases; a making on its task reserves (design §4.8 "層を作る段"). Changes take a lock and
/// publish a new copy of the entries, so that a frame's reads (<see cref="Find"/>) take no lock: a lock that a frame waits on
/// allocates its wait event the first time (Issue #304).
/// </remarks>
internal sealed class PyramidStore : IDisposable
{
    private readonly Lock _gate = new();
    private Dictionary<Column, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the queue the makings of the store's plans run on, one after another (row W1b2).</summary>
    public MakingQueue Makings { get; } = new();

    /// <summary>Gets the number of pyramids held or reserved. Tests only.</summary>
    public int Count => Volatile.Read(ref _entries).Count;

    /// <summary>
    /// Returns a column's pyramid for a plan that does not hold it yet, building it the first time any plan or making asks;
    /// the plan holds it until it releases it.
    /// </summary>
    public PlotPyramid Acquire(Column y, PlotWorkerPool pool, out bool built)
    {
        lock (_gate)
        {
            var entry = EntryOf(y, pool, out built);
            entry.Users++;
            return entry.Pyramid;
        }
    }

    /// <summary>
    /// Returns a column's pyramid for a making (row W1b2), building it the first time; the making's reservation keeps it until
    /// its plan installs the made layers (<see cref="Install"/>) or the making is dropped (<see cref="Unreserve"/>). Called
    /// on the making's task.
    /// </summary>
    public PlotPyramid Reserve(Column y, PlotWorkerPool pool)
    {
        lock (_gate)
        {
            var entry = EntryOf(y, pool, out _);
            entry.Pending++;
            return entry.Pyramid;
        }
    }

    /// <summary>Turns a making's reservation of a column's pyramid into its plan's hold, when the plan does not hold it yet.</summary>
    public void Install(Column y, bool hold)
    {
        lock (_gate)
        {
            var entry = Volatile.Read(ref _entries)[y];
            entry.Pending--;
            entry.Users += hold ? 1 : 0;
            DropIfUnused(y, entry);
        }
    }

    /// <summary>Drops a making's reservation, and disposes the pyramid when no plan holds it and no making reserved it.</summary>
    public void Unreserve(Column y)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _entries).TryGetValue(y, out var entry))
            {
                entry.Pending--;
                DropIfUnused(y, entry);
            }
        }
    }

    /// <summary>
    /// Builds a column's pyramid again when it holds a fault (design §4.8, row W1b1): a failed chunk is not built again until
    /// its generation moves (§3.3, rule 3), so a retry makes a new pyramid. The plans keep their holds.
    /// </summary>
    public void Rebuild(Column y, PlotWorkerPool pool)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _entries).TryGetValue(y, out var entry) && entry.Pyramid.Fault is not null)
            {
                entry.Pyramid.Dispose();
                entry.Pyramid = ((IPyramidColumn)y).BuildPlotPyramid(pool);
            }
        }
    }

    /// <summary>Returns a column's pyramid, or null when no plan holds one and no making reserved one. Takes no lock.</summary>
    public PlotPyramid? Find(Column y) => Volatile.Read(ref _entries).TryGetValue(y, out var entry) ? entry.Pyramid : null;

    /// <summary>Releases a plan's hold of a column's pyramid, and disposes it when no plan holds it and no making reserved it.</summary>
    public void Release(Column y)
    {
        lock (_gate)
        {
            var entry = Volatile.Read(ref _entries)[y];
            entry.Users--;
            DropIfUnused(y, entry);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in Volatile.Read(ref _entries).Values)
            {
                entry.Pyramid.Dispose();
            }

            Volatile.Write(ref _entries, new Dictionary<Column, Entry>(ReferenceEqualityComparer.Instance));
        }
    }

    /// <summary>Returns a column's entry, building its pyramid and publishing a new copy of the entries the first time. Under the lock.</summary>
    private Entry EntryOf(Column y, PlotWorkerPool pool, out bool built)
    {
        var entries = Volatile.Read(ref _entries);
        built = !entries.TryGetValue(y, out var entry);
        if (built)
        {
            entry = new Entry(((IPyramidColumn)y).BuildPlotPyramid(pool));
            Volatile.Write(ref _entries, new Dictionary<Column, Entry>(entries, ReferenceEqualityComparer.Instance) { [y] = entry });
        }

        return entry!;
    }

    /// <summary>Disposes an entry's pyramid and publishes the entries without it when nothing holds or reserves it. Under the lock.</summary>
    private void DropIfUnused(Column y, Entry entry)
    {
        if (entry.Users > 0 || entry.Pending > 0)
        {
            return;
        }

        entry.Pyramid.Dispose();
        var entries = new Dictionary<Column, Entry>(Volatile.Read(ref _entries), ReferenceEqualityComparer.Instance);
        _ = entries.Remove(y);
        Volatile.Write(ref _entries, entries);
    }

    private sealed class Entry(PlotPyramid pyramid)
    {
        public PlotPyramid Pyramid { get; set; } = pyramid;

        public int Users { get; set; }

        public int Pending { get; set; }
    }
}
