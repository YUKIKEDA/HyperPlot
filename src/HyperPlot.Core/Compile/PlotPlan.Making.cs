namespace HyperPlot;

/// <content>The layers made off the plan's thread (design §4.8 "層を作る段", row W1b2, Issue #297).</content>
internal sealed partial class PlotPlan
{
    // The mipmaps the makings of this plan made and the plan has not taken yet, shared by its makings (row W1b2).
    private readonly Dictionary<Raster, RasterPyramid> _madeMipmaps = [];
    private readonly Lock _mipmapGate = new();

    // The making of every layer (and the facet) a frame waits for, and the makings of single layers, at their positions:
    // being made, or made and standing beside the layer they replace until they are ready.
    private LayersMaking? _whole;
    private LayersMaking?[] _remakes = [];
    private LayerPlan[]?[] _standby = [];

    /// <summary>Gets a value indicating whether a making runs or stands by: the frame is provisional.</summary>
    private bool Making
    {
        get
        {
            if (_whole is not null)
            {
                return true;
            }

            for (var i = 0; i < _remakes.Length; i++)
            {
                if (_remakes[i] is not null || _standby[i] is not null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns whether a spec's layers are made off the frame (row W1b2): a frame that does not wait, and a facet or a layer
    /// whose making waits for the workers (a stat, a stack over a numeric x, a split by a category).
    /// </summary>
    private bool MakesOffFrame(PlotSpec spec)
    {
        if (Waits)
        {
            return false;
        }

        if (spec.Facet is not null)
        {
            return true;
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            // In LayerPlan.Create's order: a stack of bars over categories sums at its frames' polls (row W1b1), split or not.
            var layer = spec.Layers[i];
            var made =
                layer.Stat is not null
                || (
                    LayerPlan.Stacks(LayerPlan.PositionOf(layer))
                        ? layer is not BarLayerSpec { X.Column: CategoricalColumn }
                        : SplitPlan.Splits(layer)
                );
            if (made)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts making every layer, and the facet when it is to be found again (row W1b2); the frames draw the background and the title until it is made.</summary>
    private void StartWhole(PlotSpec spec, bool data)
    {
        _whole?.Abandon();
        AbandonRemakes();
        _whole = new LayersMaking(this, spec, spec.Facet is not null && (data || _facet is null), -1);
        _store.Makings.Post(_whole);
    }

    /// <summary>Starts making again the layer at a position, beside the one drawn, unless it is made for these versions already (row W1b2).</summary>
    private void StartRemake(PlotSpec spec, int layer)
    {
        if (_remakes[layer] is { } running && running.Versions == VersionsOf(layer))
        {
            return;
        }

        _remakes[layer]?.Abandon();
        DropStandby(layer);
        _remakes[layer] = new LayersMaking(this, spec, buildFacet: false, layer) { Versions = VersionsOf(layer) };
        _store.Makings.Post(_remakes[layer]!);
    }

    /// <summary>
    /// Takes what the makings made (row W1b2): every layer of a whole making into the panels, a single layer's to stand by,
    /// and a layer that stood by and is now ready in place of the one drawn. Returns whether the layers changed.
    /// </summary>
    private bool TakeMade(PlotSpec spec)
    {
        var changed = false;
        if (_whole is { IsDone: true } whole)
        {
            _whole = null;
            InstallWhole(spec, whole);
            changed = true;
        }

        for (var i = 0; i < _remakes.Length; i++)
        {
            if (_remakes[i] is { IsDone: true } remake)
            {
                _remakes[i] = null;
                if ((remake.Error ?? remake.Failure(i)) is { } error)
                {
                    remake.Abandon();
                    Fail(i, error);
                    continue;
                }

                _standby[i] = remake.Take(i);
                Hold(remake);
            }

            if (_standby[i] is { } standby && Ready(standby))
            {
                Swap(i, standby);
                changed = true;
            }
        }

        if (changed)
        {
            ReleaseUnused();
            GatherBatches();
        }

        return changed;
    }

    /// <summary>Returns whether every panel's new layer is ready, polling them as the frame polls the drawn ones.</summary>
    private bool Ready(LayerPlan[] standby)
    {
        var ready = true;
        foreach (var layer in standby)
        {
            try
            {
                ThrowFault(layer);
                ready &= layer.Poll(_indexes) == LayerReadiness.Ready;
            }
            catch (Exception)
            {
                // A failure shows at the frame after the swap, when the drawn layer polls it.
                return true;
            }
        }

        return ready;
    }

    /// <summary>Puts a layer that stood by in place of the one drawn in every panel, and releases the old one (Issue #297 Decision 7).</summary>
    private void Swap(int layer, LayerPlan[] standby)
    {
        _standby[layer] = null;
        for (var p = 0; p < _panels.Length && p < standby.Length; p++)
        {
            _panels[p].Layers[layer].Release();
            standby[p].Readiness = LayerReadiness.Ready;
            _panels[p].Layers[layer] = standby[p];
        }

        if (layer < _failures.Length)
        {
            _failures[layer] = null;
        }
    }

    /// <summary>Takes a whole making's facet, panels, and layers in place of the ones drawn (row W1b2).</summary>
    private void InstallWhole(PlotSpec spec, LayersMaking whole)
    {
        if (_failures.Length != spec.Layers.Count)
        {
            _failures = new (Exception, long)?[spec.Layers.Count];
        }

        if (whole.Error is { } facetError)
        {
            // Without panels no layer draws (Issue #297 Decision 11): each layer fails with the facet.
            whole.Abandon();
            for (var i = 0; i < spec.Layers.Count; i++)
            {
                _failures[i] = null;
                Fail(i, facetError);
            }

            return;
        }

        var olds = new List<LayerPlan>();
        var oldFacet = _facet;
        _facet = whole.Facet;
        var made = whole.Panels!;
        if (_panels.Length != made.Length)
        {
            olds.AddRange(_panels.SelectMany(p => p.Layers));
            _panels = [.. Enumerable.Range(0, made.Length).Select(_ => new PlotPanel())];
            _learned = new PanelDomains[made.Length];
        }

        _indexes.Forget();
        for (var p = 0; p < made.Length; p++)
        {
            olds.AddRange(_panels[p].Layers);
            (_panels[p].Data, _panels[p].Table, _panels[p].Layers) = (made[p].Data, made[p].Table, made[p].Layers);
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            _failures[i] = null;
            if (whole.Failure(i) is { } failure)
            {
                Fail(i, failure);
            }
        }

        Hold(whole);
        foreach (var old in olds)
        {
            old.Release();
        }

        if (!ReferenceEquals(oldFacet, _facet))
        {
            oldFacet?.Dispose();
        }
    }

    /// <summary>Takes the pyramids and the mipmaps a making reserved and made into the plan's holds (row W1b2).</summary>
    private void Hold(LayersMaking making)
    {
        foreach (var column in making.Reserved)
        {
            _store.Install(column, hold: _held.Add(column));
        }

        making.Reserved.Clear();
        lock (_mipmapGate)
        {
            foreach (var raster in making.MadeMipmaps)
            {
                if (_madeMipmaps.Remove(raster, out var mipmap))
                {
                    _rasters[raster] = mipmap;
                }
            }
        }

        making.MadeMipmaps.Clear();
    }

    /// <summary>Releases the pyramids and the mipmaps no drawn or standing layer reads (row W1b2).</summary>
    private void ReleaseUnused()
    {
        var used = new HashSet<Column>(ReferenceEqualityComparer.Instance);
        var rasters = new HashSet<Raster>(ReferenceEqualityComparer.Instance);
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                used.UnionWith(layer.PyramidColumns);
                rasters.UnionWith(layer.Rasters);
            }
        }

        foreach (var standby in _standby)
        {
            foreach (var layer in standby ?? [])
            {
                used.UnionWith(layer.PyramidColumns);
                rasters.UnionWith(layer.Rasters);
            }
        }

        foreach (var column in _held.ToArray())
        {
            if (!used.Contains(column))
            {
                _store.Release(column);
                _ = _held.Remove(column);
            }
        }

        foreach (var raster in _rasters.Keys.ToArray())
        {
            if (!rasters.Contains(raster))
            {
                _rasters[raster].Dispose();
                _ = _rasters.Remove(raster);
            }
        }
    }

    /// <summary>Drops a layer's standing new plans, freeing what they made.</summary>
    private void DropStandby(int layer)
    {
        if (layer < _standby.Length && _standby[layer] is { } standby)
        {
            _standby[layer] = null;
            foreach (var plan in standby)
            {
                plan.Release();
            }

            ReleaseUnused();
        }
    }

    /// <summary>Gives up every single layer's making and standing plans (a whole making replaces them).</summary>
    private void AbandonRemakes()
    {
        for (var i = 0; i < _remakes.Length; i++)
        {
            _remakes[i]?.Abandon();
            _remakes[i] = null;
            DropStandby(i);
        }
    }

    /// <summary>Gives up every making (the plan is disposed, Issue #297 Decision 8): nothing waits for them.</summary>
    private void AbandonMakings()
    {
        _whole?.Abandon();
        _whole = null;
        AbandonRemakes();
    }

    /// <summary>Sizes the single layers' makings and standing plans to a spec's layers.</summary>
    private void SizeRemakes(PlotSpec spec)
    {
        if (_remakes.Length != spec.Layers.Count)
        {
            AbandonRemakes();
            (_remakes, _standby) = (new LayersMaking?[spec.Layers.Count], new LayerPlan[]?[spec.Layers.Count]);
        }
    }

    /// <summary>Returns a mipmap for a making: the plan's, one an earlier making of the plan made, or a new one (row W1b2).</summary>
    private RasterPyramid MipmapForMaking(Raster raster, IReadOnlyDictionary<Raster, RasterPyramid> taken, List<Raster> made)
    {
        if (taken.TryGetValue(raster, out var mipmap))
        {
            return mipmap;
        }

        lock (_mipmapGate)
        {
            if (!_madeMipmaps.TryGetValue(raster, out mipmap))
            {
                mipmap = raster.MakePyramid();
                _madeMipmaps.Add(raster, mipmap);
            }
        }

        made.Add(raster);
        return mipmap;
    }

    /// <summary>
    /// The making of a plot's layers on the store's queue (row W1b2): every layer and the facet, or one layer in every panel.
    /// It reads the spec, the plan's facet and panels' data as they were when it was made, and reserves the pyramids it needs;
    /// it touches no other state of the plan's.
    /// </summary>
    private sealed class LayersMaking : Making
    {
        private readonly PlotPlan _plan;
        private readonly PlotSpec _spec;
        private readonly bool _buildFacet;
        private readonly int _layer;
        private readonly FacetData? _facet;
        private readonly (PanelData? Data, DataTable Table)[] _panelData;
        private readonly (IScale? X, IScale? Y, IScale? X2, IScale? Y2) _transforms;
        private readonly int _capacity;
        private readonly Dictionary<Raster, RasterPyramid> _rasters;
        private Exception?[] _failures = [];

        public LayersMaking(PlotPlan plan, PlotSpec spec, bool buildFacet, int layer)
        {
            (_plan, _spec, _buildFacet, _layer) = (plan, spec, buildFacet, layer);
            _facet = buildFacet || spec.Facet is null ? null : plan._facet;
            _panelData = [.. plan._panels.Select(p => (p.Data, p.Table ?? spec.Data))];
            _transforms = (plan._xTransform, plan._yTransform, plan._xTransform2, plan._yTransform2);
            _capacity = plan._capacity;
            _rasters = new Dictionary<Raster, RasterPyramid>(plan._rasters);
        }

        /// <summary>Gets or initializes the sum of the layer's columns' versions it was started for (a single layer's making).</summary>
        public long Versions { get; init; }

        /// <summary>Gets the facet the making found, or the plan's when it found none.</summary>
        public FacetData? Facet { get; private set; }

        /// <summary>Gets each panel's data, table, and layers; a single layer's making leaves the others null.</summary>
        public (PanelData? Data, DataTable Table, LayerPlan[] Layers)[]? Panels { get; private set; }

        /// <summary>Gets the columns whose pyramids the making reserved, for its plan to hold.</summary>
        public List<Column> Reserved { get; } = [];

        /// <summary>Gets the rasters whose mipmaps the making made, for its plan to take.</summary>
        public List<Raster> MadeMipmaps { get; } = [];

        /// <summary>Returns the exception the making of the layer at a position threw, or null.</summary>
        public Exception? Failure(int layer) => layer < _failures.Length ? _failures[layer] : null;

        /// <summary>Returns a single layer's new plans, one a panel, and leaves them to the plan.</summary>
        public LayerPlan[] Take(int layer)
        {
            var taken = Panels!.Select(p => p.Layers[layer]).ToArray();
            Panels = null;
            return taken;
        }

        protected override void Work()
        {
            var spec = _spec;
            var pool = _plan._pool;
            var facet = _buildFacet ? FacetData.Build(spec, pool) : _facet;
            Facet = facet;
            var count = facet?.Panels.Length ?? (_layer < 0 ? 1 : _panelData.Length);
            var shared = SharedSources(spec, facet);
            var reserved = new HashSet<Column>(ReferenceEqualityComparer.Instance);
            var panels = new (PanelData?, DataTable, LayerPlan[])[count];
            _failures = new Exception?[spec.Layers.Count];
            List<Column> columns = [];
            List<Raster> rasters = [];
            for (var p = 0; p < count; p++)
            {
                var (data, table) = facet is null ? (_layer < 0 ? (null, spec.Data) : _panelData[p]) : (facet.Panels[p], facet.Panels[p].Data);
                var build = new PlanBuild(
                    Pyramid,
                    _plan._store.Find,
                    new M4Batch(),
                    _capacity,
                    pool,
                    Mipmap,
                    spec.ScaleX,
                    _transforms.X,
                    spec.ScaleY,
                    _transforms.Y
                );
                var layers = new LayerPlan[spec.Layers.Count];
                for (var i = 0; i < layers.Length; i++)
                {
                    if (_layer >= 0 && i != _layer)
                    {
                        continue;
                    }

                    var layer = spec.Layers[i];
                    var batch = new M4Batch();
                    (columns, rasters) = ([], []);
                    build.Layer = i;
                    build.Use(
                        batch,
                        layer.SecondaryX ? spec.ScaleX2 : spec.ScaleX,
                        layer.SecondaryX ? _transforms.X2 : _transforms.X,
                        layer.SecondaryY ? spec.ScaleY2 : spec.ScaleY,
                        layer.SecondaryY ? _transforms.Y2 : _transforms.Y
                    );
                    try
                    {
                        layers[i] = PanelLayer(layer, data, build, shared[i]);
                    }
                    catch (Exception exception)
                    {
                        // A layer whose making fails is not drawn, and fails once (Issue #297 Decision 5).
                        _failures[i] ??= exception;
                        layers[i] = new EmptyPlan(layer);
                    }

                    (layers[i].Batch, layers[i].PyramidColumns, layers[i].Rasters) = (batch, columns, rasters);
                }

                panels[p] = (data, table, layers);
            }

            Panels = panels;

            PlotPyramid Pyramid(Column y)
            {
                columns.Add(y);
                if (reserved.Add(y))
                {
                    Reserved.Add(y);
                    return _plan._store.Reserve(y, pool);
                }

                return _plan._store.Find(y)!;
            }

            RasterPyramid Mipmap(Raster raster)
            {
                rasters.Add(raster);
                return _plan.MipmapForMaking(raster, _rasters, MadeMipmaps);
            }
        }

        protected override void Free()
        {
            foreach (var panel in Panels ?? [])
            {
                foreach (var layer in panel.Layers)
                {
                    layer?.Release();
                }
            }

            Panels = null;
            foreach (var column in Reserved)
            {
                _plan._store.Unreserve(column);
            }

            Reserved.Clear();
            if (_buildFacet && Facet is not null && !ReferenceEquals(Facet, _plan._facet))
            {
                Facet.Dispose();
            }
        }
    }
}
