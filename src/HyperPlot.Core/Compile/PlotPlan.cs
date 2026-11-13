using System.Globalization;
using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A compiled plot (design §4.8): the pyramid of each Y column, the M4 series of each layer, the learned domains, the layout
/// of the guides (row G3, §4.6), and the recorded lines and guides, for a target of one size and scale. Each
/// <see cref="Render(PlotSpec)"/> compares the spec with the last one drawn and does again only the stages that changed.
/// </summary>
/// <remarks>One thread at a time uses a plan. It owns its pyramids; the columns stay with their owner, who disposes them after the plan.</remarks>
internal sealed partial class PlotPlan : IDisposable
{
    /// <summary>The name a raster's range goes by in messages: it has no name in the table.</summary>
    private const string RasterName = "an image's raster";

    /// <summary>The name of the color column of 2D bins in messages and learning (row G5c): the cells' values.</summary>
    private const string CellsName = "the cells of 2D bins";

    private readonly Dictionary<Raster, RasterPyramid> _rasters = [];
    private readonly bool _ownImages;
    private readonly PlotWorkerPool _pool;
    private readonly PlotGuides _guides;
    private readonly TextEngine _text;

    // The pyramids the plan reads (design §5.5.3): its own store, or its figure's, which the plots share (row G6c); and
    // the columns whose pyramid this plan holds in the store.
    private readonly PyramidStore _store;
    private readonly bool _ownStore;
    private readonly HashSet<Column> _held = [];

    // In a figure, the figure starts and sends the residency cache's frame once for all its plots (row G6c).
    private readonly bool _inFigure;

    private readonly RenderList _list = new();
    private readonly RecordContext _context;
    private readonly RangeSink _sink = new();
    private readonly ColorScale _color = new();
    private readonly SizeScale _size = new();
    private readonly ShapeScale _shape = new();

    // How the layers reach their indexes: waited for (a headless render) or read as they are (the engine's frame, row W1b1).
    private readonly LayerIndexes _indexes;

    // The failures of the layers at their positions in the spec (row W1b1), each with the sum of its columns' versions when
    // it failed: a layer that failed is not drawn until a version moves; then it is made again.
    private readonly List<(int Layer, Exception Error)> _newFailures = [];
    private (Exception Error, long Versions)?[] _failures = [];

    // Whether the last extraction read a pyramid not built, for a layer that draws (row W1b1).
    private bool _extractProvisional;

    // The default color scales (design §4.2, row G7c), made once: a spec without one draws without allocating.
    private static readonly OrdinalScaleSpec DefaultOrdinal = Scale.Ordinal();
    private static readonly SequentialScaleSpec DefaultSequential = Scale.Sequential();
    private static readonly SizeScaleSpec DefaultSize = Scale.Size();
    private static readonly ShapeScaleSpec DefaultShape = Scale.Shape();

    // The channels of a layer's node and of the one before it, for the checks and the diff (design §4.8).
    private readonly ColumnRef[] _channels = new ColumnRef[4];
    private readonly ColumnRef[] _previousChannels = new ColumnRef[4];
    private float _scale;
    private int _capacity;

    // The target grew past the capacity of the layers' M4 output (row W1a): the layers are made again, their pyramids kept.
    private bool _grown;
    private IScale? _xTransform;

    // A Render's stages (row G6c): what Compile found, for Prepare and Finish, so that a figure extracts all its plots at once.
    private PlotSpec? _pendingSpec;
    private bool _pendingScales;
    private bool _pendingRanges;
    private bool _pendingLooks;
    private bool _pendingFlipped;
    private bool _pendingAnnotations;
    private bool _pendingExtract;
    private bool _pendingRecord;
    private PanelDomains[] _views = [];
    private PanelDomains[] _full = [];
    private bool _viewChanged;
    private bool _placed;
    private IScale? _yTransform;
    private IScale? _xTransform2;
    private IScale? _yTransform2;
    private PlotSpec? _spec;

    // The panels (design §4.5, row G6b): one without a facet. Each holds its layers, batches, boundaries, and domains.
    private PlotPanel[] _panels = [new PlotPanel()];
    private PanelDomains[] _learned = new PanelDomains[1];
    private FacetData? _facet;

    // The batches, origins, and boundaries of every panel's pairs of scales, at panel · 4 + pair: M4 extracts them together.
    private M4Batch[] _allBatches = [];
    private ScaleOrigin[] _allOrigins = [];
    private ArraySegment<double>[] _allBoundaries = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PlotPlan"/> class for a target of a size in physical pixels, measuring
    /// its text with a backend's engine, at a scale from DIPs to pixels (design §4.6).
    /// </summary>
    /// <remarks>
    /// The residency cache is the backend's (row G9); without one, the plan keeps its own, which sends nothing and holds a
    /// frame's tiles as if they were sent (the tests of Core).
    /// </remarks>
    internal PlotPlan(PlotWorkerPool pool, int width, int height, TextEngine text, float scale, ResidencyCache? images = null)
        : this(pool, width, height, text, scale, images, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlotPlan"/> class for a plot of a figure (row G6c): the target's size, a
    /// store of pyramids the figure's plots share, and the figure's residency cache, whose frame the figure starts. The plot
    /// is laid out in the rectangle <see cref="Place"/> gives.
    /// </summary>
    internal PlotPlan(PlotWorkerPool pool, int width, int height, TextEngine text, float scale, ResidencyCache? images, PyramidStore? store)
    {
        (_store, _ownStore, _inFigure) = (store ?? new PyramidStore(), store is null, store is not null);
        _pool = pool;
        _indexes = new LayerIndexes(pool, column => _held.Contains(column) ? _store.Find(column) : null) { Makings = _store.Makings };
        _scale = scale;
        _guides = new PlotGuides(text, scale, width, height);
        _text = text;
        _context = new RecordContext(_guides, new M4Batch(), _list, new GeomFrame()) { Images = images ?? new ResidencyCache() };
        _ownImages = images is null;
        GatherBatches();

        // The panel is never wider or taller than the target; flipped, M4 runs over its height (row G10), and polar over the
        // outer circle, ⌈2πR⌉ with R at most half the smaller side (row G11).
        _capacity = Capacity(width, height);
    }

    /// <summary>Returns the most points one series' M4 output holds on a target: the panel is never wider or taller.</summary>
    private static int Capacity(int width, int height) =>
        M4.MaxPointCount(Math.Max(Math.Max(width, height), PolarTransform.Turn((Math.Min(width, height) / 2) + 1)));

    /// <summary>Gets the number of pyramids built, for the tests of the diff (design §4.8).</summary>
    internal int PyramidsBuilt { get; private set; }

    /// <summary>Gets the residency cache the image layers ask for their tiles (row G9). Tests only.</summary>
    internal ResidencyCache Images => _context.Images;

    /// <summary>Gets the number of M4 extractions, for the tests of the diff.</summary>
    internal int Extractions { get; private set; }

    /// <summary>Gets the number of times the lines were recorded, for the tests of the diff.</summary>
    internal int Recordings { get; private set; }

    /// <summary>Gets the number of times the guides were laid out, for the tests of the diff (design §4.8).</summary>
    internal int Layouts { get; private set; }

    /// <summary>Gets the number of layers the last recording drew, over every panel (row W1b1). Tests only.</summary>
    internal int LayersRecorded { get; private set; }

    /// <summary>Gets the number of pyramids the plan's store holds or its makings reserved (row W1b2). Tests only.</summary>
    internal int PyramidsInStore => _store.Count;

    /// <summary>Gets the columns whose statistics the frames asked the workers for (row W1b1). Tests only.</summary>
    internal Column[] StatsRequested => _indexes.Requested;

    /// <summary>Gets a value indicating whether no making of the plan's store runs or waits to (row W1b2). Tests only.</summary>
    internal bool MakingsIdle => _store.Makings.Idle;

    /// <summary>Gets the color scale of the last spec drawn, or null when no layer has a color channel. Tests only.</summary>
    internal ColorScale? Color { get; private set; }

    /// <summary>Gets the size scale of the last spec drawn, or null when no layer has a SizeBy (row R3f). Tests only.</summary>
    internal SizeScale? Size { get; private set; }

    /// <summary>Gets the shape scale of the last spec drawn, or null when no layer has a ShapeBy (row R3f). Tests only.</summary>
    internal ShapeScale? Shape { get; private set; }

    /// <summary>Gets the layers' plans. Tests only.</summary>
    internal IReadOnlyList<LayerPlan> Layers => _panels[0].Layers;

    /// <summary>Gets the facet's panels and their rows, or null without a facet (row G6b). Tests only.</summary>
    internal FacetData? Facet => _facet;

    /// <summary>Gets the number of rasters' mipmaps the plan holds, one a raster however many panels draw it (row G6b). Tests only.</summary>
    internal int RasterCount => _rasters.Count;

    /// <summary>Gets the panels (row G6b). Tests only.</summary>
    internal IReadOnlyList<PlotPanel> Panels => _panels;

    /// <summary>Gets the layout of the guides. Tests only.</summary>
    internal PlotGuides Guides => _guides;

    /// <summary>Gets the pyramids the plan holds, one a Y column. Tests only.</summary>
    internal IReadOnlyCollection<PlotPyramid> Pyramids => [.. _held.Select(c => _store.Find(c)!)];

    /// <summary>Gets the number of pixel columns (rows when flipped) M4 last ran over: W, or H when flipped. Tests only.</summary>
    internal int PixelColumns => _panels[0].Boundaries.Count - 1;

    /// <summary>Gets the x domain of the last extraction, as an origin and differences (design §3.6). Tests only.</summary>
    internal ScaleRange XDomain => _panels[0].X;

    /// <summary>Gets the y domain of the last extraction. Tests only.</summary>
    internal ScaleRange YDomain => _panels[0].Y;

    /// <summary>
    /// Gets or sets a value indicating whether a Render waits for the indexes and the workers' results (a headless render,
    /// design §4.8) or draws what is there and reports failures instead of throwing them (the engine's frame, row W1b1).
    /// </summary>
    internal bool Waits
    {
        get => _indexes.Waits;
        set => _indexes.Waits = value;
    }

    /// <summary>
    /// Gets a value indicating whether the last Render was provisional (row W1b1): a layer drew results older than its
    /// columns or nothing yet, a domain learned without every chunk's statistics, or M4 read a pyramid not built. A layer
    /// that failed does not make it provisional. Always false for a render that waits.
    /// </summary>
    internal bool IsProvisional { get; private set; }

    /// <summary>Gets the failures the last Render found (row W1b1): a layer's position and the exception, once each.</summary>
    internal IReadOnlyList<(int Layer, Exception Error)> Failures => _newFailures;

    /// <summary>
    /// Compiles a spec against the last one drawn, waits until its pyramids and X statistics are built for the columns'
    /// current values, lays out the guides, and returns the recorded panel, grid, lines, axes, titles, and legend (design
    /// §4.6, §4.8); an empty list when the panel is too small. Allocates nothing for the spec drawn last once warm.
    /// </summary>
    /// <exception cref="ArgumentNullException">The spec has a null member (its theme, its theme's palette, and its coordinate system included) or layer.</exception>
    /// <exception cref="ArgumentException">
    /// The spec or its theme does not check (design §4.7, §4.8), or the x domain is too narrow for the panel's width, in
    /// the scale space or once mapped back to data units.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A learned domain has an end that is not finite, in data units or once the scale maps it (design §4.2), an X column or
    /// a rule's column decreases, or a segment or a rect layer has more than 131,072 elements (design §5.5.3). While ticks are chosen: a time domain outside the years 1 to 9999, a tick format that returns false or a
    /// length outside 0 to 64, or a default label past 64 characters (design §4.6). Shaping a text failed. An exception
    /// that a user's <see cref="IScale"/>, tick format, or <see cref="IGeom"/> throws comes out as it is.
    /// </exception>
    internal RenderList Render(PlotSpec spec)
    {
        Compile(spec);
        if (Prepare())
        {
            _pool.ExtractTogether(_allBatches, _allOrigins, _allBoundaries, _allBatches.Length, rethrow: Waits);
        }

        Finish();
        return _list;
    }

    /// <summary>
    /// Places the plot in a rectangle of the target (design §4.6, row G6c): a figure's cell. A new rectangle lays the plot out
    /// again at the next Render.
    /// </summary>
    internal void Place(int left, int top, int width, int height)
    {
        _guides.Place(left, top, width, height);
        _placed = true;
    }

    /// <summary>
    /// Takes a new target size and scale (design §7.1, row W1a): the plot fills the target until a figure places it, and the
    /// next Render lays out and records again, as after <see cref="Place"/>. The pyramids stay. A target larger than the
    /// layers' M4 output holds makes the layers again at the next Render, with room for a side up to the next power of two,
    /// so that a window dragged larger does not make them at every pixel.
    /// </summary>
    internal void SetTarget(int width, int height, float scale)
    {
        _scale = scale;
        _guides.SetTarget(scale, width, height);
        _placed = true;
        var needed = Capacity(width, height);
        if (needed > _capacity)
        {
            var side = (int)Math.Min(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(width, height)), RenderTargetDescription.MaxSide);
            _capacity = Math.Max(needed, Capacity(side, side));
            _grown = true;
        }
    }

    /// <summary>
    /// Gets the M4 batches, origins, and boundaries that <see cref="Prepare"/> set for the extraction (row G6c): a figure
    /// extracts its plots' together.
    /// </summary>
    internal (M4Batch[] Batches, ScaleOrigin[] Origins, ArraySegment<double>[] Boundaries) Extraction => (_allBatches, _allOrigins, _allBoundaries);

    /// <summary>Gets the list the plan records into: the one <see cref="Render(PlotSpec)"/> returns, or a figure's plot's (row G6c).</summary>
    internal RenderList List => _list;

    /// <summary>Gets the number of panels the last compile made (row G6c): a figure's plots have at most 256 together.</summary>
    internal int PanelCount => _panels.Length;

    /// <summary>Gets a value indicating whether the last <see cref="Prepare"/> dropped the views, the mapping having changed (row I1a).</summary>
    internal bool ViewsDropped { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the last render changed what the scale space means, views or not (row I3a): the
    /// condition under which views are dropped, so that a selection, a predicate in that space, goes with them.
    /// </summary>
    internal bool MappingChanged { get; private set; }

    /// <summary>
    /// Gets each panel's domains with no view, after nice (rows I1a, I1b): the full view of design §6.2. An axis's is updated
    /// by each <see cref="Prepare"/> that draws it with no view; <see cref="FullStale"/> tells when the learned domains moved
    /// since.
    /// </summary>
    internal ReadOnlySpan<PanelDomains> Full => _full;

    /// <summary>Gets a value indicating whether the plot learned again while an axis had a view, so that <see cref="Full"/> may be out of date.</summary>
    internal bool FullStale { get; private set; }

    /// <summary>Gets the number of times the plot learned its domains, for the tests of the view stage (row I1a).</summary>
    internal int Learnings { get; private set; }

    /// <summary>
    /// Sets the views the next <see cref="Prepare"/> draws (rows I1a, I1b, design §6.2, §4.8): a panel each, an axis with no
    /// view <see cref="ScaleRange.None"/>; panels past the span have none. A view that changes only lays out, extracts, and
    /// records again: the plot does not learn. Allocates only when the number of panels changes.
    /// </summary>
    internal void SetViews(ReadOnlySpan<PanelDomains> views)
    {
        var same = views.Length == _views.Length;
        for (var p = 0; same && p < views.Length; p++)
        {
            same =
                Same(views[p].X, _views[p].X) && Same(views[p].Y, _views[p].Y) && Same(views[p].X2, _views[p].X2) && Same(views[p].Y2, _views[p].Y2);
        }

        if (same)
        {
            return;
        }

        if (_views.Length != views.Length)
        {
            _views = new PanelDomains[views.Length];
        }

        views.CopyTo(_views);
        _viewChanged = true;

        static bool Same(in ScaleRange a, in ScaleRange b) => (IsNone(a) && IsNone(b)) || a == b;
    }

    /// <summary>
    /// Returns the y range of the rows a view of x shows (row I1a, design §6.2: the y of the visible x): the union, over the
    /// lines, steps, and areas of the first panel with an x of no transform, of each layer's least and greatest y between the
    /// view's ends, read from the layer's pyramid in O(log n), an area's baseline included, mapped into the y scale's space.
    /// False when no such layer has a row there.
    /// </summary>
    internal bool TryVisibleY(in ScaleRange x, out ScaleRange y)
    {
        y = ScaleRange.None;
        if (_spec is null || _xTransform is not null || IsNone(x))
        {
            return false;
        }

        var (min, max, found) = (double.PositiveInfinity, double.NegativeInfinity, false);
        foreach (var layer in _panels[0].Layers)
        {
            // By the plan, not the spec: a split layer and a stat layer have the same specs, and keep the y (design §6.2).
            var (columnX, columnY, baseline) = (layer, layer.Spec) switch
            {
                (LinePlan, LineLayerSpec line) => (line.X.Column, line.Y.Column, double.NaN),
                (StepPlan, StepLayerSpec step) => (step.X.Column, step.Y.Column, double.NaN),
                (AreaPlan, AreaLayerSpec area) => (area.X.Column, area.Y.Column, area.Baseline),
                _ => (null, null, double.NaN),
            };
            if (columnX is null || columnY is null || _store.Find(columnY) is not { } pyramid)
            {
                continue;
            }

            var first = columnX.LowerBound(x.Lower, x.Origin, layer.Rows);
            var end = columnX.LowerBound(Math.BitIncrement(x.Upper), x.Origin, layer.Rows);
            if (end > first && pyramid.TryRange(first, end - first, out var low, out var high))
            {
                (min, max, found) = (Math.Min(min, low), Math.Max(max, high), true);
                if (double.IsFinite(baseline))
                {
                    (min, max) = (Math.Min(min, baseline), Math.Max(max, baseline));
                }
            }
        }

        if (!found || !(min < max))
        {
            return false;
        }

        if (_yTransform is { } transform)
        {
            Span<double> values = [min, max];
            transform.Forward(values, values);
            (min, max) = (Math.Min(values[0], values[1]), Math.Max(values[0], values[1]));
            if (!double.IsFinite(min) || !double.IsFinite(max) || !(min < max))
            {
                return false;
            }
        }

        y = ScaleRange.FromDouble(min, max);
        return true;
    }

    /// <summary>Returns the flags of the axes a view gives a range (<see cref="PanelDomains.Viewed"/>).</summary>
    private static byte Flags(in PanelDomains view) =>
        (byte)((IsNone(view.X) ? 0 : 1) | (IsNone(view.Y) ? 0 : 2) | (IsNone(view.X2) ? 0 : 4) | (IsNone(view.Y2) ? 0 : 8));

    /// <summary>Returns whether any panel has a view on any axis.</summary>
    private bool AnyView()
    {
        foreach (var view in _views)
        {
            if (Flags(view) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a range is <see cref="ScaleRange.None"/>.</summary>
    internal static bool IsNone(in ScaleRange range) => double.IsNaN(range.Lower);

    /// <summary>
    /// The first stage of <see cref="Render(PlotSpec)"/> (row G6c): checks the spec, and makes the layers and the panels again
    /// when the data stage asks. A figure compiles all its plots before any waits, so that its checks come first.
    /// </summary>
    internal void Compile(PlotSpec spec)
    {
        Check(spec);
        _newFailures.Clear();
        var previous = _spec;

        // Until this spec is drawn, the next one is compiled from the start: a stage that threw leaves no state to trust.
        _spec = null;
        CompileChecked(spec, previous);
    }

    /// <summary>
    /// Throws unless a spec checks (design §4.7, §4.8): what <see cref="Compile"/> checks before it compiles. An engine checks a
    /// spec when it is set, before its frame (row I1a, design §7.1).
    /// </summary>
    internal void Check(PlotSpec spec)
    {
        Validate(spec);
        if (spec.Theme is null)
        {
            throw new ArgumentNullException(nameof(spec), "The spec's Theme must not be null.");
        }

        if (spec.Coord is null)
        {
            throw new ArgumentNullException(nameof(spec), "The spec's Coord must not be null.");
        }

        if (spec.Theme.Palette is null)
        {
            throw new ArgumentNullException(nameof(spec), "The theme's Palette must not be null.");
        }

        spec.Theme.Validate(_scale);
        CheckTextSizes(spec);
        CheckAnnotations(spec);
        (spec.Coord as PolarCoordSpec)?.Validate();
    }

    private void CompileChecked(PlotSpec spec, PlotSpec? previous)
    {
        var data = previous is null || DataChanged(previous, spec) || FacetDataChanged(previous.Facet, spec.Facet);
        var placed = _placed;
        _placed = false;
        var flipped = Flipped(spec);

        // A flip, a polar system, its theta, and its hole change the axes' lengths, which nice and the boundaries depend on:
        // the "scale" stage (design §4.8). Its start and direction only lay out and record again.
        var scales =
            previous is null
            || !SameMapping(previous.ScaleX, spec.ScaleX)
            || !SameMapping(previous.ScaleY, spec.ScaleY)
            || !SameMapping(previous.ScaleX2, spec.ScaleX2)
            || !SameMapping(previous.ScaleY2, spec.ScaleY2)
            || CoordMappingChanged(previous.Coord, spec.Coord)
            || previous.Facet?.Scales != spec.Facet?.Scales;
        var ranges = data || RangesChanged(previous!, spec);
        var looks =
            data
            || NodesChanged(previous!, spec)
            || OptionsChanged(previous!.ScaleX, spec.ScaleX)
            || OptionsChanged(previous!.ScaleY, spec.ScaleY)
            || !string.Equals(previous.Title, spec.Title, StringComparison.Ordinal)
            || !string.Equals(previous.Subtitle, spec.Subtitle, StringComparison.Ordinal)
            || !string.Equals(previous.Caption, spec.Caption, StringComparison.Ordinal)
            || !previous.Theme.Equals(spec.Theme)
            || CoordLooksChanged(previous.Coord, spec.Coord)
            || previous.ScaleColor?.Id != spec.ScaleColor?.Id
            || previous.ScaleSize?.Id != spec.ScaleSize?.Id
            || previous.ScaleShape?.Id != spec.ScaleShape?.Id
            || OptionsChanged(previous.ScaleX2, spec.ScaleX2)
            || OptionsChanged(previous.ScaleY2, spec.ScaleY2)
            || AxesChanged(previous.ScaleX, spec.ScaleX)
            || AxesChanged(previous.ScaleY, spec.ScaleY)
            || AxesChanged(previous.ScaleX2, spec.ScaleX2)
            || AxesChanged(previous.ScaleY2, spec.ScaleY2)
            || (previous.Facet as FacetWrapSpec)?.ColumnCount != (spec.Facet as FacetWrapSpec)?.ColumnCount
            || placed;
        if (scales)
        {
            // A new transform makes new boundaries even over the same range of the scale space.
            // A band scale has no transform: its axis is the space of category positions (design §4.2, row G7b).
            (_xTransform, _yTransform) = (
                (spec.ScaleX as ContinuousScaleSpec)?.CreateTransform(),
                ((ContinuousScaleSpec)spec.ScaleY).CreateTransform()
            );
            (_xTransform2, _yTransform2) = (spec.ScaleX2?.CreateTransform(), spec.ScaleY2?.CreateTransform());
            foreach (var panel in _panels)
            {
                (panel.X, panel.X2) = (ScaleRange.None, ScaleRange.None);
            }
        }

        // A stack over a numeric x whose columns changed is made again (row G5a): its union of x values may change length.
        // Bins, densities, and moving averages are made in the x scale's space, 2D bins in the y scale's too (rows G5b and
        // G5c): another space (not another domain) makes the layers again.
        // A gathered panel's rows follow their columns' new versions before the layers check whether they are stale (row G6b).
        if (!data && Waits)
        {
            _ = _facet?.Refresh(_pool);
        }
        else if (!data && _facet is { } facet)
        {
            RefreshOffFrame(facet);
        }

        // A facet's new arrangement (its columns' count or its scales) keeps its panels' rows; new scales change where a stat's
        // edges come from, a shared or a free direction (design §4.5).
        if (
            !data
            && previous!.Facet is { } before
            && spec.Facet is { } after
            && (before.Scales != after.Scales || (before as FacetWrapSpec)?.ColumnCount != (after as FacetWrapSpec)?.ColumnCount)
        )
        {
            _facet!.Rearrange(after);
        }

        var grown = _grown;
        _grown = false;
        var retry = Retry(spec);
        var offFrame = MakesOffFrame(spec);
        SizeRemakes(spec);
        var whole =
            data
            || grown
            || retry
            || (!offFrame && StaleLayers())
            || (previous is not null && previous.Facet?.Scales != spec.Facet?.Scales && HasStat(spec))
            || (previous is not null && !SameSpace(previous.ScaleX, spec.ScaleX) && SpaceLayers(spec, ofX: true, secondary: false))
            || (previous is not null && !SameSpace(previous.ScaleY, spec.ScaleY) && SpaceLayers(spec, ofX: false, secondary: false))
            || (previous is not null && !SameSpace(previous.ScaleX2, spec.ScaleX2) && SpaceLayers(spec, ofX: true, secondary: true))
            || (previous is not null && !SameSpace(previous.ScaleY2, spec.ScaleY2) && SpaceLayers(spec, ofX: false, secondary: true));
        if (whole && offFrame)
        {
            // A layer whose making waits for the workers is made off the frame (row W1b2); the frames draw the background and
            // the title until every layer is made.
            StartWhole(spec, data);
        }
        else if (whole)
        {
            _whole?.Abandon();
            _whole = null;
            AbandonRemakes();
            BuildLayers(spec, data);
        }

        // What the makings made comes in at the start of a frame: a whole making's layers, and a single layer that stood by and
        // is ready (row W1b2).
        var made = TakeMade(spec);
        if (_whole is null && (!whole || made))
        {
            TakeNodes(spec, previous);
        }

        // A layer of an off-frame spec whose columns moved is made again alone, beside the one drawn (Issue #297 Decision 3).
        if (offFrame && _whole is null)
        {
            for (var i = 0; i < spec.Layers.Count; i++)
            {
                if (LayerStale(i))
                {
                    StartRemake(spec, i);
                }
            }
        }

        // The versions are read before the wait and the extraction. An Invalidate that comes after this read leaves the
        // recorded versions behind the column's, so the next Render extracts again.
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                layer.ReadVersions();
            }
        }

        (_pendingSpec, _pendingScales, _pendingRanges, _pendingLooks, _pendingFlipped) = (spec, scales, ranges, looks, flipped);

        // Annotations of other nodes, or moved by the engine, are recorded again alone (row I4a): no scale, no layout.
        _pendingAnnotations = previous is null || !SameAnnotations(previous, spec) || _shiftsChanged;
        _shiftsChanged = false;
        (_pendingExtract, _pendingRecord) = (false, false);
    }

    /// <summary>Returns whether the layer at a position is stale in any panel (row W1b2).</summary>
    private bool LayerStale(int layer)
    {
        foreach (var panel in _panels)
        {
            if (layer < panel.Layers.Length && panel.Layers[layer].Stale)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The layers name the same columns; takes the new nodes for their looks, baselines, and widths. A gathered panel's layers
    /// read its own table: a new node of other looks is remapped onto it (row G6b).
    /// </summary>
    private void TakeNodes(PlotSpec spec, PlotSpec? previous)
    {
        {
            foreach (var panel in _panels)
            {
                for (var i = 0; i < panel.Layers.Length; i++)
                {
                    var layer = spec.Layers[i];
                    if (panel.Data is not { Map: { } map } || FacetData.Rowless(layer))
                    {
                        panel.Layers[i].Take(layer);
                    }
                    else if (previous is null || !(ReferenceEquals(previous.Layers[i], layer) || SplitPlan.SameLooks(previous.Layers[i], layer)))
                    {
                        panel.Layers[i].Take(FacetData.Remap(layer, map));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The second stage (row G6c): waits for the indexes, learns, lays the guides out, and makes the boundaries. Returns
    /// whether the batches of <see cref="Extraction"/> are to be extracted before <see cref="Finish"/>.
    /// </summary>
    internal bool Prepare()
    {
        var (spec, scales, ranges, looks, flipped) = (_pendingSpec!, _pendingScales, _pendingRanges, _pendingLooks, _pendingFlipped);
        if (_whole is not null)
        {
            // Every layer is being made (row W1b2): the background and the title only (Issue #291 Decision 5).
            _list.Clear();
            _guides.RecordTitle(_list, spec);
            LayersRecorded = 0;
            return false;
        }

        PollIndexes();
        ViewsDropped = false;
        MappingChanged = scales || _views.Length != _panels.Length;
        if (MappingChanged && AnyView())
        {
            // A view over the old mapping, or of other panels, no longer means the same values: the full view (design §6.2).
            (_views, ViewsDropped) = ([], true);
        }

        // While a frame is provisional, the next one learns and extracts again (row W1b1): the statistics, the pyramids, and
        // the workers' results fill in between frames.
        var learn = ranges || scales || ValuesChanged() || IsProvisional;
        var extract = learn || _viewChanged;
        _viewChanged = false;
        if (learn)
        {
            Learnings++;
            _indexes.Incomplete = false;

            // A user's geom learns only when an axis learns: with both domains given, nothing would read its ranges.
            if (
                (spec.ScaleX is ContinuousScaleSpec x && Learns(x))
                || Learns((ContinuousScaleSpec)spec.ScaleY)
                || (spec.ScaleX2 is { } x2 && Learns(x2))
                || (spec.ScaleY2 is { } y2 && Learns(y2))
            )
            {
                foreach (var panel in _panels)
                {
                    foreach (var layer in panel.Layers)
                    {
                        layer.BeginLearning();
                    }
                }
            }

            // A shared direction learns once from every panel; a free one a panel each (design §4.5, row G6b). A band scale
            // keeps all its categories.
            var (freeX, freeY) = (_facet?.Arrangement.FreeXOf(spec.ScaleX) == true, _facet?.Arrangement is { FreeY: true });
            for (var p = 0; p < _panels.Length; p++)
            {
                var panel = _panels[p];
                var first = _panels[0];
                if (spec.ScaleX is BandScaleSpec band)
                {
                    panel.LearnedX = BandAxis.Domain(band, BandColumn(spec)?.CategoryCount ?? 0);
                }
                else
                {
                    panel.LearnedX =
                        freeX || p == 0
                            ? Learn(
                                spec,
                                (ContinuousScaleSpec)spec.ScaleX,
                                _xTransform,
                                ofX: true,
                                secondary: false,
                                freeX ? _panels.AsSpan(p, 1) : _panels
                            )
                            : first.LearnedX;
                }

                panel.LearnedY =
                    freeY || p == 0
                        ? Learn(
                            spec,
                            (ContinuousScaleSpec)spec.ScaleY,
                            _yTransform,
                            ofX: false,
                            secondary: false,
                            freeY ? _panels.AsSpan(p, 1) : _panels
                        )
                        : first.LearnedY;

                // A secondary scale learns from the layers on it only, by the same rules (design §4.2, row G6a).
                if (spec.ScaleX2 is { } secondaryX)
                {
                    panel.LearnedX2 =
                        freeX || p == 0
                            ? Learn(spec, secondaryX, _xTransform2, ofX: true, secondary: true, freeX ? _panels.AsSpan(p, 1) : _panels)
                            : first.LearnedX2;
                }

                if (spec.ScaleY2 is { } secondaryY)
                {
                    panel.LearnedY2 =
                        freeY || p == 0
                            ? Learn(spec, secondaryY, _yTransform2, ofX: false, secondary: true, freeY ? _panels.AsSpan(p, 1) : _panels)
                            : first.LearnedY2;
                }
            }
        }

        if (extract || looks)
        {
            // The color domain: its columns' ranges, or the one given (design §4.2, row G7c). The color bar's ticks widen it
            // with nice, and a diverging middle is checked against the final one.
            if (Color is { Inner: not null })
            {
                LearnColor(spec);
            }

            // The size domain, learned as the color domain is (design §4.2, row R3f).
            if (Size is not null)
            {
                LearnSize(spec);
            }

            // The layout chooses the ticks, which nice may widen the domains to (design §4.6, §4.8).
            for (var p = 0; p < _panels.Length; p++)
            {
                var panel = _panels[p];
                var view = p < _views.Length ? _views[p] : PanelDomains.None;
                _learned[p] = new PanelDomains(
                    IsNone(view.X) ? panel.LearnedX : view.X,
                    IsNone(view.Y) ? panel.LearnedY : view.Y,
                    IsNone(view.X2) ? panel.LearnedX2 : view.X2,
                    IsNone(view.Y2) ? panel.LearnedY2 : view.Y2,
                    Flags(view)
                );
            }

            _guides.Layout(
                spec,
                _xTransform,
                _yTransform,
                BandColumn(spec),
                Color,
                Size,
                Shape,
                _xTransform2,
                _yTransform2,
                _facet?.Arrangement,
                _learned
            );

            // The full view: what the plot draws with no view (row I1a). An axis with a view keeps the last one it drew, which
            // a learning since may have moved.
            if (!_guides.Empty)
            {
                if (_full.Length != _panels.Length)
                {
                    _full = new PanelDomains[_panels.Length];
                    _full.AsSpan().Fill(PanelDomains.None);
                }

                for (var p = 0; p < _panels.Length; p++)
                {
                    var (guide, flags) = (_guides.PanelAt(p), _learned[p].Viewed);
                    var old = _full[p];
                    _full[p] = new PanelDomains(
                        (flags & 1) == 0 ? guide.X.FinalDomain : old.X,
                        (flags & 2) == 0 ? guide.Y.FinalDomain : old.Y,
                        (flags & 4) == 0 && spec.ScaleX2 is not null ? guide.X2.FinalDomain : old.X2,
                        (flags & 8) == 0 && spec.ScaleY2 is not null ? guide.Y2.FinalDomain : old.Y2
                    );
                }

                FullStale = AnyView() && (FullStale || learn);
            }

            Color?.CheckMiddle();
            Layouts++;
            if (_guides.Empty)
            {
                // Only the background, which the renderer clears to (design §4.6).
                _list.Clear();
                foreach (var panel in _panels)
                {
                    panel.X = ScaleRange.None;
                }

                return false;
            }

            var boundaries = false;
            for (var p = 0; p < _panels.Length; p++)
            {
                var (panel, guide) = (_panels[p], _guides.PanelAt(p));
                var x = guide.X.FinalDomain;
                panel.Y = guide.Y.FinalDomain;
                var along =
                    guide.Polar is { } polar ? polar.AlongSize
                    : flipped ? guide.Height
                    : guide.Width;
                if (x != panel.X || panel.Boundaries.Count != along + 1)
                {
                    panel.MakeBoundaries(x, along, _xTransform);
                    boundaries = true;
                }

                // The secondary scales (row G6a): their own boundaries, made the same way over the secondary x domain.
                panel.Y2 = spec.ScaleY2 is null ? default : guide.Y2.FinalDomain;
                if (spec.ScaleX2 is not null && (guide.X2.FinalDomain != panel.X2 || panel.Boundaries2.Count != along + 1))
                {
                    panel.MakeSecondaryBoundaries(guide.X2.FinalDomain, along, _xTransform2);
                    boundaries = true;
                }
            }

            if (extract || boundaries)
            {
                // A transformed axis reads its data in the origin 0; its origin is one of the scale space (design §4.2). A layer
                // on a secondary scale runs over its pair's x boundaries and origins (row G6a). Every panel's layers run
                // together (row G6b), each in a batch of its own (row W1b2).
                var at = 0;
                foreach (var panel in _panels)
                {
                    foreach (var layer in panel.Layers)
                    {
                        var pair = Pair(layer.Spec);
                        (_allOrigins[at], _allBoundaries[at]) = (OriginOf(panel, pair), (pair & 1) != 0 ? panel.Boundaries2 : panel.Boundaries);
                        at++;
                    }
                }

                _pendingExtract = true;
            }

            _pendingRecord = true;
            if (_pendingExtract && !Waits)
            {
                SkipSeries();
            }
        }
        else if (_pendingAnnotations && !_guides.Empty)
        {
            _pendingRecord = true;
        }

        return _pendingExtract;
    }

    /// <summary>
    /// The last stage (row G6c): after the extraction <see cref="Prepare"/> asked for, marks the layers extracted and records
    /// the list; the spec is then the one drawn.
    /// </summary>
    internal void Finish()
    {
        if (_pendingExtract)
        {
            Extractions++;
            foreach (var panel in _panels)
            {
                foreach (var layer in panel.Layers)
                {
                    layer.MarkExtracted();
                }
            }

            if (!Waits)
            {
                ReadSeries();
            }
        }

        if (!Waits)
        {
            IsProvisional = Provisional();
        }

        if (_pendingRecord)
        {
            Record(_pendingSpec!, _pendingFlipped);
            Recordings++;

            // An image that drew a tile from an ancestor waits for tiles the frame's budget left out (design §5.5.3, Issue
            // #319): the frame is provisional, so that the next one sends more.
            if (!Waits && RecordedProvisional())
            {
                IsProvisional = true;
            }
        }

        _spec = _pendingSpec;
        (_pendingSpec, _pendingExtract, _pendingRecord) = (null, false, false);
    }

    /// <summary>Releases the pyramids, and gives up the makings without waiting for them (Issue #297 Decision 8).</summary>
    public void Dispose()
    {
        AbandonMakings();
        foreach (var column in _held)
        {
            _store.Release(column);
        }

        _held.Clear();
        if (_ownStore)
        {
            _store.Dispose();
        }

        foreach (var mipmap in _rasters.Values)
        {
            mipmap.Dispose();
        }

        _rasters.Clear();

        // The mipmaps the makings made and the plan never took: an abandoned making leaves its own for the next (row W1b2).
        lock (_mipmapGate)
        {
            foreach (var mipmap in _madeMipmaps.Values)
            {
                mipmap.Dispose();
            }

            _madeMipmaps.Clear();
        }

        foreach (var panel in _panels)
        {
            panel.Release();
        }

        _facet?.Dispose();
        _facet = null;
        _spec = null;
    }

    /// <summary>
    /// Throws when the text layers of a plot have more than 4,096 rows together (design §5.5.3, row G8): the frame's glyph
    /// records and the shaping cache, which the plot holds one of, bound them all. Reads the columns' lengths only.
    /// </summary>
    private static void CheckTextRows(PlotSpec plot)
    {
        var total = 0L;
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            total += plot.Layers[i] is TextLayerSpec text ? text.X.Column.Length : 0;
        }

        if (total <= LayerPlan.MaxTextRows)
        {
            return;
        }

        var counts = new System.Text.StringBuilder();
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (plot.Layers[i] is TextLayerSpec text)
            {
                _ = counts.Append(counts.Length > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"layer {i} {text.X.Column.Length}");
            }
        }

        throw new InvalidOperationException(
            $"The text layers have {total} rows together ({counts}); a plot draws at most {LayerPlan.MaxTextRows} labels (design §5.5.3). Many points are drawn as a scatter (a point layer)."
        );
    }

    /// <summary>
    /// Throws when a plot draws an image on axes it cannot be put on as a parallelogram (design §5.5.3, row G9): a polar
    /// coordinate system, or an x or a y scale other than linear and time.
    /// </summary>
    private static void CheckImages(PlotSpec plot)
    {
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (plot.Layers[i] is not ImageLayerSpec)
            {
                continue;
            }

            if (plot.Coord is PolarCoordSpec)
            {
                throw new ArgumentException(
                    $"Layer {i} is an image, and the coordinate system is polar; an image is drawn on cartesian axes only (design §5.5.3)."
                );
            }

            var image = plot.Layers[i];
            ReadOnlySpan<(ScaleSpec?, string)> scales =
            [
                image.SecondaryX ? (plot.ScaleX2, "ScaleX2") : (plot.ScaleX, "ScaleX"),
                image.SecondaryY ? (plot.ScaleY2, "ScaleY2") : (plot.ScaleY, "ScaleY"),
            ];
            foreach (var (scale, name) in scales)
            {
                if (scale is not (null or LinearScaleSpec or TimeScaleSpec))
                {
                    throw new ArgumentException(
                        $"Layer {i} is an image, and the {name} is a {scale.GetType().Name}; an image is drawn on linear and time scales only, where its rectangle stays a parallelogram (design §5.5.3)."
                    );
                }
            }

            return;
        }
    }

    /// <summary>
    /// Throws unless the axes' sides and the secondary scales and axes go together (design §4.1, §4.8, row G6a): a layer on a
    /// secondary scale has one, a secondary scale has a layer on it, a direction has one second axis, a side fits its
    /// direction and a secondary scale lies opposite its primary, a transform's axis belongs to a primary, a color scale's
    /// inner scale has no axis, a polar plot has none of these, and a primary that no layer lies on has a domain given.
    /// </summary>
    private static void CheckAxes(PlotSpec plot)
    {
        foreach (var ofX in (ReadOnlySpan<bool>)[true, false])
        {
            var (primary, secondary, name, secondName) = ofX
                ? (plot.ScaleX, plot.ScaleX2, "ScaleX", "ScaleX2")
                : (plot.ScaleY, plot.ScaleY2, "ScaleY", "ScaleY2");
            var (on, off) = (-1, -1);
            for (var i = 0; i < plot.Layers.Count; i++)
            {
                (on, off) = On(plot.Layers[i], ofX) ? (on < 0 ? i : on, off) : (on, off < 0 ? i : off);
            }

            if (on >= 0 && secondary is null)
            {
                throw new ArgumentException(
                    $"Layer {on} lies on the secondary {(ofX ? "x" : "y")} scale, and the {secondName} is null (design §4.1)."
                );
            }

            if (secondary is not null && on < 0)
            {
                throw new ArgumentException(
                    $"The {secondName} has no layer on it to learn from; put one there with Secondary{(ofX ? "X" : "Y")} (design §4.1)."
                );
            }

            if (secondary is not null && primary is ContinuousScaleSpec { SecondaryAxis: not null })
            {
                throw new ArgumentException(
                    $"The {name} has a secondary axis, and the {secondName} is given too; the side opposite the primary holds one axis (design §4.1)."
                );
            }

            if (primary is TimeScaleSpec { SecondaryAxis: not null })
            {
                throw new ArgumentException(
                    $"The {name} is a time scale with a secondary axis; a time scale takes none, its values are dates, not doubles (design §4.1)."
                );
            }

            if (secondary is { SecondaryAxis: not null })
            {
                throw new ArgumentException($"The {secondName} has a secondary axis; a transform's axis belongs to a primary scale (design §4.1).");
            }

            if (on >= 0 && off < 0 && !Given(primary))
            {
                throw new ArgumentException(
                    primary is BandScaleSpec
                        ? $"Every layer lies on the {secondName}, and the {name} is a band scale with no layer; put a layer on it (design §4.1)."
                        : $"Every layer lies on the {secondName}, and the {name} has no domain given; give it one with WithDomain, or put a layer on it (design §4.1)."
                );
            }

            var side = SideOf(primary);
            CheckSide(side, ofX, name);
            CheckSide(secondary?.Side, ofX, secondName);
            var primaryFar = side is { } given && Far(given);
            if (secondary?.Side is { } secondSide && Far(secondSide) == primaryFar)
            {
                throw new ArgumentException(
                    $"The {secondName}'s axis is on the {secondary!.Side}, the side of the {name}'s; a secondary scale lies opposite its primary (design §4.1)."
                );
            }

            if (
                plot.Coord is PolarCoordSpec
                && (side is not null || secondary is not null || primary is ContinuousScaleSpec { SecondaryAxis: not null })
            )
            {
                throw new ArgumentException(
                    $"The coordinate system is polar, and the {name} has a side or a secondary axis, or the {secondName} is given; the angle's and the radius's axes have no sides (design §4.1)."
                );
            }
        }

        var inner = plot.ScaleColor switch
        {
            SequentialScaleSpec sequential => sequential.Scale,
            DivergingScaleSpec diverging => diverging.Scale,
            _ => null,
        };
        if (inner is not null && (inner.Side is not null || inner.SecondaryAxis is not null))
        {
            throw new ArgumentException(
                "The color scale's inner scale has a side or a secondary axis; the color bar is a legend, not an axis (design §4.1)."
            );
        }

        static bool Far(AxisSide side) => side is AxisSide.Top or AxisSide.Right;

        static bool Given(ScaleSpec scale) => scale is TimeScaleSpec { Domain: not null } || scale is ContinuousScaleSpec { GivenDomain: not null };

        static void CheckSide(AxisSide? side, bool ofX, string name)
        {
            if (side is { } value && (ofX ? value is not (AxisSide.Bottom or AxisSide.Top) : value is not (AxisSide.Left or AxisSide.Right)))
            {
                throw new ArgumentException(
                    $"The {name}'s axis is on the {value}; {(ofX ? "x goes on the Bottom or the Top" : "y goes on the Left or the Right")} (design §4.1)."
                );
            }
        }
    }

    /// <summary>Returns whether a layer lies on an axis's secondary scale (row G6a).</summary>
    private static bool On(LayerSpec layer, bool ofX) => ofX ? layer.SecondaryX : layer.SecondaryY;

    /// <summary>
    /// Throws when a text layer's size at the target's scale passes 512 pixels (design §4.1, row G8), as the theme's text
    /// sizes are checked: before the learning.
    /// </summary>
    private void CheckTextSizes(PlotSpec plot)
    {
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (plot.Layers[i] is TextLayerSpec text && text.Size * _scale > TextEngine.MaxSize)
            {
                throw new ArgumentException(
                    $"The Size of layer {i} is {text.Size}; a text size is at most {TextEngine.MaxSize} pixels at the scale {_scale}."
                );
            }
        }
    }

    /// <summary>Throws unless the spec is one that rows G1 to G4 compile (design §4.8). Reads no data but column lengths.</summary>
    private void Validate(PlotSpec plot)
    {
        if (plot.Data is null || plot.Layers is null || plot.ScaleX is null || plot.ScaleY is null)
        {
            throw new ArgumentNullException(nameof(plot), "The spec's Data, Layers, ScaleX, and ScaleY must not be null.");
        }

        for (var i = 0; i < plot.Layers.Count; i++)
        {
            var layer = plot.Layers[i] ?? throw new ArgumentNullException(nameof(plot), $"Layer {i} of the spec is null.");
            LayerPlan.Validate(plot.Data, layer, i, _channels);
            if (plot.Coord is PolarCoordSpec && layer is PointLayerSpec point && point.X.Column.Length > LayerPlan.MaxElements)
            {
                throw new InvalidOperationException(
                    $"Layer {i} has {point.X.Column.Length} points in polar coordinates; at most {LayerPlan.MaxElements} are drawn so (design §5.5.2). Row R3e2 draws more."
                );
            }
        }

        CheckTextRows(plot);
        CheckImages(plot);
        CheckAxes(plot);
        FacetData.Check(plot);
        CheckScale(plot, plot.ScaleX, _spec?.ScaleX, "ScaleX", ofX: true, secondary: false);
        CheckScale(plot, plot.ScaleY, _spec?.ScaleY, "ScaleY", ofX: false, secondary: false);
        if (plot.ScaleX2 is { } x2)
        {
            CheckScale(plot, x2, _spec?.ScaleX2, "ScaleX2", ofX: true, secondary: true);
        }

        if (plot.ScaleY2 is { } y2)
        {
            CheckScale(plot, y2, _spec?.ScaleY2, "ScaleY2", ofX: false, secondary: true);
        }

        CheckColor(plot);
        CheckMarkerScales(plot);
    }

    /// <summary>
    /// Throws unless the point layers' size and shape columns and their scales go together (design §4.2, §4.8 "検査（行
    /// R3f）"), and takes the scales. A scale no layer uses is neither taken nor checked.
    /// </summary>
    private void CheckMarkerScales(PlotSpec plot)
    {
        (Size, Shape) = (null, null);
        var sized = false;
        CategoricalColumn? categories = null;
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (plot.Layers[i] is not PointLayerSpec point)
            {
                continue;
            }

            sized |= point.SizeBy is not null;
            if (point.ShapeBy is { Column: CategoricalColumn column } reference)
            {
                categories ??= column;
                if (!categories.SameCategories(column))
                {
                    throw new ArgumentException(
                        $"The shape column \"{plot.Data.GetName(reference.Index)}\" of layer {i} has other categories than the first shape column; the layers share one order of categories (design §4.2)."
                    );
                }
            }
        }

        if (sized)
        {
            var scale = plot.ScaleSize ?? DefaultSize;
            if (scale.Scale is null)
            {
                throw new ArgumentNullException(nameof(plot), "The size scale's Scale must not be null.");
            }

            if (!SizeScaleSpec.IsRange(scale.RangeMin, scale.RangeMax))
            {
                throw new ArgumentException(
                    $"The size scale's range is {scale.RangeMin} to {scale.RangeMax}; its diameters are 0 < RangeMin ≤ RangeMax ≤ 512 DIPs (design §4.2)."
                );
            }

            if (scale.Scale.Side is not null || scale.Scale.SecondaryAxis is not null)
            {
                throw new ArgumentException(
                    "The size scale's inner scale has a side or a secondary axis; the size legend is a legend, not an axis (design §4.1)."
                );
            }

            if (_spec?.ScaleSize is not { } before || before.Id != scale.Id)
            {
                scale.Scale.CheckGivenDomain();
            }

            _size.Set(scale);
            Size = _size;
        }

        if (categories is not null)
        {
            var scale = plot.ScaleShape ?? DefaultShape;
            var shapes = scale.Shapes ?? throw new ArgumentNullException(nameof(plot), "The shape scale's Shapes must not be null.");
            if (shapes.Count == 0)
            {
                throw new ArgumentException("The shape scale has no shape; it has one or more (design §4.2).");
            }

            for (var i = 0; i < shapes.Count; i++)
            {
                if (!MarkerStyle.IsShape(shapes[i]))
                {
                    throw new ArgumentException($"The shape scale's shape {i} is {(int)shapes[i]}; a shape is a named MarkerShape value.");
                }

                for (var j = 0; j < i; j++)
                {
                    if (shapes[j] == shapes[i])
                    {
                        throw new ArgumentException($"The shape scale holds {shapes[i]} twice; it holds each shape once (design §4.2).");
                    }
                }
            }

            if (categories.CategoryCount > shapes.Count)
            {
                throw new ArgumentException(
                    $"The shape columns have {categories.CategoryCount} categories, and the shape scale {shapes.Count} shapes; a shape is not used twice (design §4.2)."
                );
            }

            _shape.Set(scale, categories);
            Shape = _shape;
        }
    }

    /// <summary>
    /// Learns the size domain (design §4.2, row R3f): the inner scale's given one, or the union of the ranges of the point
    /// layers' size columns, by the rules of a position scale.
    /// </summary>
    private void LearnSize(PlotSpec spec)
    {
        var inner = _size.Inner;
        if (inner is TimeScaleSpec { Domain: { } time })
        {
            _size.Domain = ScaleRange.FromInteger(time.Min, time.Max);
            return;
        }

        if (inner.GivenDomain is { } domain)
        {
            _size.Domain = _size.Transform is null
                ? ScaleRange.FromDouble(domain.Min, domain.Max)
                : Mapped(_size.Transform, domain.Min, domain.Max, string.Empty, string.Empty);
            return;
        }

        _sink.Reset();
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (Failed(i) || spec.Layers[i] is not PointLayerSpec { SizeBy: { } reference })
            {
                continue;
            }

            try
            {
                _sink.AddColumn(reference.Column, spec.Data.GetName(reference.Index));
            }
            catch (Exception exception) when (!Waits)
            {
                Fail(i, exception);
            }
        }

        _size.Domain = FromSink(inner, _size.Transform);
    }

    /// <summary>
    /// Throws unless the layers' color columns and the color scale go together (design §4.2, row G7c): all categories of one
    /// order on an ordinal scale, or all numbers on a sequential or diverging one; and takes the scale for the drawing.
    /// </summary>
    private void CheckColor(PlotSpec plot)
    {
        Color = null;
        CategoricalColumn? categories = null;
        string? number = null;
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            foreach (var channel in (ReadOnlySpan<ColumnRef?>)[plot.Layers[i].ColorBy, plot.Layers[i].FillBy])
            {
                if (channel is not { } reference)
                {
                    continue;
                }

                if (reference.Column is CategoricalColumn column)
                {
                    categories ??= column;
                    if (!categories.SameCategories(column))
                    {
                        throw new ArgumentException(
                            $"The color column \"{plot.Data.GetName(reference.Index)}\" of layer {i} has other categories than the first color column; the layers share one order of categories (design §4.2)."
                        );
                    }
                }
                else
                {
                    number ??= plot.Data.GetName(reference.Index);
                }
            }
        }

        // 2D bins fill their cells by their values (row G5c): numbers.
        for (var i = 0; i < plot.Layers.Count && number is null; i++)
        {
            number = plot.Layers[i].Stat is Bin2DStatSpec ? CellsName : null;
        }

        var raster = -1;
        for (var i = 0; i < plot.Layers.Count && raster < 0; i++)
        {
            raster = plot.Layers[i] is ImageLayerSpec { Raster.IsRgba: false } ? i : -1;
        }

        if (raster >= 0)
        {
            number ??= RasterName;
        }

        if (categories is null && number is null)
        {
            return;
        }

        var scale = plot.ScaleColor ?? (categories is not null ? DefaultOrdinal : DefaultSequential);
        if (raster >= 0 && scale is OrdinalScaleSpec)
        {
            throw new ArgumentException(
                $"Layer {raster} draws a raster of numbers, and the ScaleColor is an ordinal scale; numbers take a sequential or a diverging scale (design §4.2)."
            );
        }

        if (raster >= 0 && scale is SequentialScaleSpec { Scale: TransformScaleSpec } or DivergingScaleSpec { Scale: TransformScaleSpec })
        {
            throw new ArgumentException(
                $"Layer {raster} draws a raster of numbers through a color scale of a user's IScale; a raster's colors are mapped on the GPU, which runs the linear, log, symlog, and pow transforms only (design §4.2)."
            );
        }

        switch (scale)
        {
            case OrdinalScaleSpec when number is not null:
                throw new ArgumentException(
                    $"The ScaleColor is an ordinal scale, and the color column \"{number}\" holds numbers; numbers take a sequential or a diverging scale."
                );
            case SequentialScaleSpec or DivergingScaleSpec when categories is not null:
                throw new ArgumentException(
                    $"The ScaleColor is a {scale.GetType().Name}, and the color columns hold categories; categories take an ordinal scale."
                );
            case SequentialScaleSpec { Map: null } or DivergingScaleSpec { Map: null }:
                throw new ArgumentNullException(nameof(plot), "The color scale's Map must not be null.");
            case SequentialScaleSpec { Scale: null } or DivergingScaleSpec { Scale: null }:
                throw new ArgumentNullException(nameof(plot), "The color scale's Scale must not be null.");
            case DivergingScaleSpec { Mid: { } mid } when !double.IsFinite(mid):
                throw new ArgumentException($"The diverging color scale's Mid is {mid}; it is finite.");
            default:
                break;
        }

        _color.Set(scale, plot.Theme?.Palette ?? Palettes.Tab10, categories);
        if (_color.Inner is { } inner)
        {
            if (_spec?.ScaleColor is not { } before || before.Id != scale.Id)
            {
                inner.CheckGivenDomain();
            }

            if (inner is TimeScaleSpec)
            {
                for (var i = 0; i < plot.Layers.Count; i++)
                {
                    var layer = plot.Layers[i];
                    foreach (var channel in (ReadOnlySpan<ColumnRef?>)[layer.ColorBy, layer.FillBy])
                    {
                        if (channel is { Column: not (Column<long> or UniformColumn { IsInteger: true }) } reference)
                        {
                            throw new ArgumentException(
                                $"The color scale's inner scale is a time scale, and the color column \"{plot.Data.GetName(reference.Index)}\" is a {reference.Column.KindName}, not a long date-time column."
                            );
                        }
                    }

                    // A raster of numbers is a color column too (row G9, PR #201's review).
                    if (layer is ImageLayerSpec { Raster: { IsRgba: false, Values: not Column<long> } timed })
                    {
                        throw new ArgumentException(
                            $"The color scale's inner scale is a time scale, and the raster of layer {i} is a {timed.Values.KindName}, not a long date-time column."
                        );
                    }
                }
            }
        }

        Color = _color;
    }

    /// <summary>
    /// Learns the color domain (design §4.2, row G7c): the inner scale's given one, or the union of the ranges of the layers'
    /// numeric color columns, by the rules of a position scale.
    /// </summary>
    private void LearnColor(PlotSpec spec)
    {
        var inner = _color.Inner!;
        if (inner is TimeScaleSpec { Domain: { } time })
        {
            _color.Domain = ScaleRange.FromInteger(time.Min, time.Max);
            return;
        }

        if (inner.GivenDomain is { } domain)
        {
            _color.Domain = _color.Transform is null
                ? ScaleRange.FromDouble(domain.Min, domain.Max)
                : Mapped(_color.Transform, domain.Min, domain.Max, string.Empty, string.Empty);
            return;
        }

        _sink.Reset();

        // An index, not foreach: the interface's enumerator would be boxed on every layout.
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (Failed(i))
            {
                continue;
            }

            try
            {
                LearnColor(spec, i);
            }
            catch (Exception exception) when (!Waits)
            {
                Fail(i, exception);
            }
        }

        _color.Domain = FromSink(inner, _color.Transform);
    }

    /// <summary>Adds a layer's ranges to the color domain: its color channels, its raster of numbers, its 2D bins' cells.</summary>
    private void LearnColor(PlotSpec spec, int i)
    {
        {
            var layer = spec.Layers[i];
            foreach (var channel in (ReadOnlySpan<ColumnRef?>)[layer.ColorBy, layer.FillBy])
            {
                if (channel is { } reference)
                {
                    _sink.AddColumn(reference.Column, spec.Data.GetName(reference.Index));
                }
            }

            // A raster of numbers adds its column's range (design §4.2, row G9).
            if (layer is ImageLayerSpec { Raster: { IsRgba: false } raster })
            {
                _sink.AddColumn(raster.Values, RasterName);
            }

            // 2D bins add their cells' values (row G5c), every panel's (row G6b).
            foreach (var panel in _panels)
            {
                if (panel.Layers[i] is StatPlan { CellValues: { } cells })
                {
                    _sink.AddColumn(cells, CellsName);
                }
            }
        }
    }

    /// <summary>
    /// Throws unless a scale is one of row G2's (design §4.8), its domain set with <c>with</c> meets its kind's conditions,
    /// and a time scale reads long date-time columns. A user cannot derive a new continuous kind: its base has internal
    /// abstract members.
    /// </summary>
    private void CheckScale(PlotSpec plot, ScaleSpec scale, ScaleSpec? previous, string name, bool ofX, bool secondary)
    {
        if (ofX && scale is BandScaleSpec band)
        {
            CheckBand(plot, band, previous);
            return;
        }

        if (scale is not ContinuousScaleSpec continuous)
        {
            throw new ArgumentException(
                $"The {name} is a {scale.GetType().Name}; the plot compiles row G2's position scales, and a band scale on x only."
            );
        }

        // A continuous scale reads numbers: a categorical column needs a band scale (design §4.2, row G7b). A scale reads the
        // layers it maps (row G6a).
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (On(plot.Layers[i], ofX) != secondary)
            {
                continue;
            }

            var count = LayerPlan.AxisChannels(plot.Layers[i], ofX, _channels);
            for (var c = 0; c < count; c++)
            {
                if (_channels[c].Column is CategoricalColumn)
                {
                    throw new ArgumentException(
                        $"The {(ofX ? "x" : "y")} column \"{plot.Data.GetName(_channels[c].Index)}\" of layer {i} is a CategoricalColumn, and the {name} is a {scale.GetType().Name}; categories go on a band scale on x (Scale.Band)."
                    );
                }
            }
        }

        // A node drawn last time was checked then: it is immutable, and a user's Forward runs once a node (design §4.2).
        if (previous is null || previous.Id != scale.Id)
        {
            continuous.CheckGivenDomain();
        }

        if (continuous is not TimeScaleSpec)
        {
            return;
        }

        // An index, not foreach: the interface's enumerator would be boxed on every Render.
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (On(plot.Layers[i], ofX) != secondary)
            {
                continue;
            }

            var count = LayerPlan.AxisChannels(plot.Layers[i], ofX, _channels);
            for (var c = 0; c < count; c++)
            {
                var reference = _channels[c];
                if (reference.Column is not (Column<long> or UniformColumn { IsInteger: true }))
                {
                    throw new ArgumentException(
                        $"The {name} is a time scale, and the column \"{plot.Data.GetName(reference.Index)}\" is a {reference.Column.KindName}, not a long date-time column."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Returns whether a layer's columns or kind changed, or the layers were added, removed, or moved (design §4.8): the
    /// "data" stage. A new table changes every reference, since a reference names its table and the check refuses another
    /// table's.
    /// </summary>
    private bool DataChanged(PlotSpec previous, PlotSpec spec)
    {
        if (previous.Layers.Count != spec.Layers.Count)
        {
            return true;
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var (before, after) = (previous.Layers[i], spec.Layers[i]);
            if (before.Id != after.Id && LayerPlan.ChannelsDiffer(before, after, _previousChannels, _channels))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a layer's Baseline or bar Width changed (the "scale" stage); called when the layers name the same columns.</summary>
    private static bool RangesChanged(PlotSpec previous, PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var (before, after) = (previous.Layers[i], spec.Layers[i]);
            if (before.Id != after.Id && LayerPlan.RangesDiffer(before, after))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a layer is a new node (design §4.8): over the same columns and ranges, a new node changes only its
    /// looks (a stroke, a fill, a label, a mode), which records again.
    /// </summary>
    private static bool NodesChanged(PlotSpec previous, PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (previous.Layers[i].Id != spec.Layers[i].Id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a layer's stat is made in an axis's scale space (rows G5b and G5c): bins, densities, moving averages,
    /// and 2D bins on x; 2D bins on y.
    /// </summary>
    private static bool SpaceLayers(PlotSpec spec, bool ofX, bool secondary)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var layer = spec.Layers[i];
            var on = (ofX ? layer.SecondaryX : layer.SecondaryY) == secondary;
            if (on && (layer.Stat is Bin2DStatSpec || (ofX && layer.Stat is BinStatSpec or DensityStatSpec or SmoothStatSpec)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a layer's workers' results are older than its columns (row G5a).</summary>
    private bool StaleLayers()
    {
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                if (layer.Stale)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether a spec's coordinate system swaps x and y (row G10).</summary>
    private static bool Flipped(PlotSpec spec) => spec.Coord is CartesianCoordSpec { Flip: true };

    /// <summary>
    /// Returns whether a change of coordinate system changes the axes' lengths (design §4.8): its kind, a flip, a polar
    /// theta, or a polar hole. The same node, or equal values, change nothing.
    /// </summary>
    private static bool CoordMappingChanged(CoordSpec before, CoordSpec after) =>
        before.Id != after.Id
        && (before, after) switch
        {
            (CartesianCoordSpec b, CartesianCoordSpec a) => b.Flip != a.Flip,
            (PolarCoordSpec b, PolarCoordSpec a) => b.Theta != a.Theta || !b.InnerRadius.Equals(a.InnerRadius),
            _ => true,
        };

    /// <summary>Returns whether a polar system's start or direction changed, which only lays out and records again (design §4.8).</summary>
    private static bool CoordLooksChanged(CoordSpec before, CoordSpec after) =>
        before.Id != after.Id && before is PolarCoordSpec b && after is PolarCoordSpec a && (!b.Start.Equals(a.Start) || b.Clockwise != a.Clockwise);

    /// <summary>
    /// Throws unless a band scale's values are in range and every layer's x is a categorical column with the same categories
    /// (design §4.2, row G7b). A layer without an x channel (a horizontal rule, a user's geom) may be there. A layer on the
    /// secondary x is mapped by ScaleX2, not by the band (row G6a), and is left out.
    /// </summary>
    private void CheckBand(PlotSpec plot, BandScaleSpec band, ScaleSpec? previous)
    {
        if (previous is null || previous.Id != band.Id)
        {
            band.Validate();
        }

        CategoricalColumn? first = null;
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            if (plot.Layers[i].SecondaryX)
            {
                continue;
            }

            var count = LayerPlan.AxisChannels(plot.Layers[i], ofX: true, _channels);
            for (var c = 0; c < count; c++)
            {
                var reference = _channels[c];
                if (reference.Column is not CategoricalColumn categories)
                {
                    throw new ArgumentException(
                        $"The x column \"{plot.Data.GetName(reference.Index)}\" of layer {i} is a {reference.Column.KindName}, and the ScaleX is a band scale, which reads a CategoricalColumn."
                    );
                }

                first ??= categories;
                if (!first.SameCategories(categories))
                {
                    throw new ArgumentException(
                        $"The x column \"{plot.Data.GetName(reference.Index)}\" of layer {i} has other categories than the first layer's; the layers of a band scale share one order of categories."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Returns the categories of the band x scale of the spec compiled last (row I3b): what a link compares, so that a band's
    /// view, cursor, and selection go only to a band of the same categories in the same order. Null without a band x.
    /// </summary>
    internal CategoricalColumn? BandCategories() => _spec is { ScaleX: BandScaleSpec } spec ? BandColumn(spec) : null;

    /// <summary>
    /// Returns the categorical column of the plot's x, the first layer's on the primary x that has one; null when no such layer
    /// reads x. A layer on ScaleX2 is not the band's (row G6a).
    /// </summary>
    private CategoricalColumn? BandColumn(PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (spec.Layers[i].SecondaryX)
            {
                continue;
            }

            var count = LayerPlan.AxisChannels(spec.Layers[i], ofX: true, _channels);
            if (count > 0 && _channels[0].Column is CategoricalColumn categories)
            {
                return categories;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns whether two scales map data the same way (design §4.8): the same node, or the same kind with the same domain,
    /// zero, nice, and constants. Reverse, clamp, the title, and the format are left out: they change the layout and the
    /// recording only (design §4.8). Allocates nothing.
    /// </summary>
    private static bool SameMapping(ScaleSpec? before, ScaleSpec? after) =>
        before is null || after is null
            ? before is null && after is null
            : before.Id == after.Id
                || (before, after) switch
                {
                    // A band's paddings and alignment move its bands: the "scale" stage (design §4.8, row G7b).
                    (BandScaleSpec b, BandScaleSpec a) => b.PaddingInner.Equals(a.PaddingInner)
                        && b.PaddingOuter.Equals(a.PaddingOuter)
                        && b.Align.Equals(a.Align),
                    (ContinuousScaleSpec b, ContinuousScaleSpec a) => b.Nice == a.Nice && SameKind(before, after),
                    _ => false,
                };

    /// <summary>
    /// Returns whether two scales map to the same space, whatever their domains (row G5b): the same kind and transform. Bins
    /// are chosen in that space, so a zoom or a domain given anew keeps them.
    /// </summary>
    internal static bool SameSpace(ScaleSpec? before, ScaleSpec? after) =>
        before is null || after is null
            ? before is null && after is null
            : before.Id == after.Id
                || (before, after) switch
                {
                    (LinearScaleSpec, LinearScaleSpec) or (LogScaleSpec, LogScaleSpec) or (BandScaleSpec, BandScaleSpec) => true,
                    (SymlogScaleSpec b, SymlogScaleSpec a) => b.Constant.Equals(a.Constant),
                    (PowScaleSpec b, PowScaleSpec a) => b.Exponent.Equals(a.Exponent),
                    (TimeScaleSpec b, TimeScaleSpec a) => b.Unit == a.Unit && b.Epoch == a.Epoch,
                    (TransformScaleSpec b, TransformScaleSpec a) => ReferenceEquals(b.Transform, a.Transform),
                    _ => false,
                };

    private static bool SameKind(ScaleSpec before, ScaleSpec after) =>
        (before, after) switch
        {
            (LinearScaleSpec b, LinearScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero,
            (LogScaleSpec b, LogScaleSpec a) => b.Domain == a.Domain,
            (SymlogScaleSpec b, SymlogScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero && b.Constant.Equals(a.Constant),
            (PowScaleSpec b, PowScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero && b.Exponent.Equals(a.Exponent),
            (TimeScaleSpec b, TimeScaleSpec a) => b.Domain == a.Domain && b.Unit == a.Unit && b.Epoch == a.Epoch,
            (TransformScaleSpec b, TransformScaleSpec a) => b.Domain == a.Domain && ReferenceEquals(b.Transform, a.Transform),
            _ => false,
        };

    /// <summary>
    /// Returns whether a scale's reverse, clamp, title, or tick format changed, which only lays out and records again (design
    /// §4.8). Formats are compared as delegates: the same method and target are equal.
    /// </summary>
    private static bool OptionsChanged(ScaleSpec? before, ScaleSpec? after)
    {
        if (before is null || after is null)
        {
            return before is not null || after is not null;
        }

        if (before is BandScaleSpec band)
        {
            return after is not BandScaleSpec other
                || band.Reverse != other.Reverse
                || !string.Equals(band.Title, other.Title, StringComparison.Ordinal);
        }

        if (before is not ContinuousScaleSpec b || after is not ContinuousScaleSpec a)
        {
            return true;
        }

        return b.Reverse != a.Reverse
            || b.Clamp != a.Clamp
            || !string.Equals(b.Title, a.Title, StringComparison.Ordinal)
            || !Equals(b.NumericFormat, a.NumericFormat)
            || (b is TimeScaleSpec bt && a is TimeScaleSpec at && !Equals(bt.TickFormat, at.TickFormat));
    }

    /// <summary>
    /// Returns whether a scale's axis moved or its secondary axis changed (design §4.8, row G6a): the "looks" stage. A secondary
    /// axis compares its transform by reference and its title and format by value, so that a new node of the same values does
    /// nothing.
    /// </summary>
    private static bool AxesChanged(ScaleSpec? before, ScaleSpec? after)
    {
        var (side, nextSide) = (SideOf(before), SideOf(after));
        if (side != nextSide)
        {
            return true;
        }

        var (axis, next) = ((before as ContinuousScaleSpec)?.SecondaryAxis, (after as ContinuousScaleSpec)?.SecondaryAxis);
        if (axis is null || next is null)
        {
            return axis is not null || next is not null;
        }

        return axis.Id != next.Id
            && (
                !ReferenceEquals(axis.Transform, next.Transform)
                || !string.Equals(axis.Title, next.Title, StringComparison.Ordinal)
                || !Equals(axis.TickFormat, next.TickFormat)
            );
    }

    /// <summary>Returns the side a scale's axis is given (row G6a), or null for the default and a scale without one.</summary>
    private static AxisSide? SideOf(ScaleSpec? scale) =>
        scale switch
        {
            ContinuousScaleSpec c => c.Side,
            BandScaleSpec b => b.Side,
            _ => null,
        };

    /// <summary>
    /// Learns a domain (design §4.2) as an origin and differences (§3.6): the one given, or the union of the ranges of every
    /// layer's column, from statistics that the wait for the indexes had the workers compute. Without a transform (linear,
    /// time) the domain is in data units; integer ends are compared as <see cref="long"/> while every column is an integer
    /// column, and as <see cref="double"/> once a floating-point one comes in. With a transform it is in the scale space: the
    /// ends are mapped, and the cases of no value and one value are taken there.
    /// </summary>
    private ScaleRange Learn(PlotSpec spec, ContinuousScaleSpec scale, IScale? transform, bool ofX, bool secondary, ReadOnlySpan<PlotPanel> panels)
    {
        if (scale is TimeScaleSpec { Domain: { } time })
        {
            return ScaleRange.FromInteger(time.Min, time.Max);
        }

        if (scale.GivenDomain is { } domain)
        {
            return transform is null
                ? ScaleRange.FromDouble(domain.Min, domain.Max)
                : Mapped(transform, domain.Min, domain.Max, string.Empty, string.Empty);
        }

        _sink.Reset();
        foreach (var panel in panels)
        {
            for (var i = 0; i < panel.Layers.Length; i++)
            {
                // A scale learns from the layers it maps (row G6a): the primary from those not on the secondary.
                var layer = panel.Layers[i];
                if ((ofX ? layer.Spec.SecondaryX : layer.Spec.SecondaryY) != secondary || Failed(i))
                {
                    continue;
                }

                try
                {
                    layer.AddRanges(_sink, ofX, panel.Table ?? spec.Data);
                }
                catch (Exception exception) when (!Waits)
                {
                    Fail(i, exception);
                }
            }
        }

        if (_sink.BaselineMin <= _sink.BaselineMax)
        {
            AddBaseline(transform, _sink.BaselineMin);
            AddBaseline(transform, _sink.BaselineMax);
        }

        return FromSink(scale, transform);
    }

    /// <summary>Returns the domain of the ranges in the sink (design §4.2): none, one value, or a range, mapped or in data units.</summary>
    private ScaleRange FromSink(ContinuousScaleSpec scale, IScale? transform)
    {
        var (integerMin, integerMax, min, max) = (_sink.IntegerMin, _sink.IntegerMax, _sink.Min, _sink.Max);
        var (minName, maxName, found, integer) = (_sink.MinName, _sink.MaxName, _sink.Found, _sink.Integer);
        if (!found)
        {
            // No value: [0, 1], in the scale space when there is a transform (log: 1 to 10).
            return ScaleRange.FromDouble(0, 1);
        }

        if (scale.ZeroOption)
        {
            // 0 first; one value that is not 0 then has a width (design §4.2).
            (integerMin, integerMax) = (Math.Min(integerMin, 0), Math.Max(integerMax, 0));
            (min, max) = (Math.Min(min, 0), Math.Max(max, 0));
        }

        if (transform is not null)
        {
            return min == max ? Single(transform, min, minName) : Mapped(transform, min, max, minName, maxName);
        }

        if (!integer)
        {
            return min == max ? ScaleRange.FromDouble(min - 0.5, max + 0.5) : ScaleRange.FromDouble(min, max);
        }

        // One value v gives [v - 0.5, v + 0.5], taken as differences from v so that a v past 2^53 stays exact.
        return integerMin == integerMax ? new ScaleRange(integerMin, -0.5, 0.5).Anchor() : ScaleRange.FromInteger(integerMin, integerMax);
    }

    /// <summary>Adds a baseline to the range when the scale maps it to a finite value (design §4.2).</summary>
    private void AddBaseline(IScale? transform, double value)
    {
        if (transform is not null)
        {
            Span<double> end = [value];
            Span<double> mapped = stackalloc double[1];
            transform.Forward(end, mapped);
            if (!double.IsFinite(mapped[0]))
            {
                return;
            }
        }

        _sink.Add(value, value, "the Baseline");
    }

    /// <summary>Returns whether a scale learns its domain from the data, that is, has no domain given (design §4.2).</summary>
    private static bool Learns(ContinuousScaleSpec scale) => scale is not TimeScaleSpec { Domain: not null } && scale.GivenDomain is null;

    /// <summary>Returns the range of the scale space between the mapped ends, or throws when an end does not map to a finite value.</summary>
    private static ScaleRange Mapped(IScale transform, double min, double max, string minName, string maxName)
    {
        Span<double> ends = [min, max];
        Span<double> mapped = stackalloc double[2];
        transform.Forward(ends, mapped);
        CheckMapped(min, mapped[0], minName);
        CheckMapped(max, mapped[1], maxName);
        return ScaleRange.FromDouble(mapped[0], mapped[1]);
    }

    /// <summary>Returns [f(v) − 0.5, f(v) + 0.5] in the scale space, for a learned domain of one value (design §4.2).</summary>
    private static ScaleRange Single(IScale transform, double value, string name)
    {
        Span<double> end = [value];
        Span<double> mapped = stackalloc double[1];
        transform.Forward(end, mapped);
        CheckMapped(value, mapped[0], name);
        return ScaleRange.FromDouble(mapped[0] - 0.5, mapped[0] + 0.5);
    }

    private static void CheckMapped(double value, double mapped, string name)
    {
        if (!double.IsFinite(mapped))
        {
            throw new InvalidOperationException(
                $"The column \"{name}\" holds {value}, which the scale maps to {mapped}, so a learned domain would have an end that is not finite; give the scale a domain with WithDomain."
            );
        }
    }

    /// <summary>Returns the number of a layer's pair of scales (row G6a): (secondary x ? 1 : 0) + (secondary y ? 2 : 0).</summary>
    private static int Pair(LayerSpec layer) => (layer.SecondaryX ? 1 : 0) + (layer.SecondaryY ? 2 : 0);

    /// <summary>Returns the origins a panel's pair of scales reads its columns in (row G6a): of its x domain and its y domain, 0 with a transform.</summary>
    private ScaleOrigin OriginOf(PlotPanel panel, int pair)
    {
        var (secondaryX, secondaryY) = ((pair & 1) != 0, (pair & 2) != 0);
        var x = secondaryX ? (_xTransform2 is null ? panel.X2.Origin : 0) : (_xTransform is null ? panel.X.Origin : 0);
        var y = secondaryY ? (_yTransform2 is null ? panel.Y2.Origin : 0) : (_yTransform is null ? panel.Y.Origin : 0);
        return new ScaleOrigin(x, y);
    }

    /// <summary>
    /// Makes a layer's plan in a panel (design §4.5, row G6b): over the panel's run, or remapped onto its gathered table; an
    /// image or a user's geom as it is in every panel; nothing in an empty cell. A stat's edges and ranges come from the whole
    /// layer along a shared direction and from the panel's rows along a free one.
    /// </summary>
    private static LayerPlan PanelLayer(LayerSpec layer, PanelData? data, PlanBuild build, (StatRange? X, StatRange? Y) shared)
    {
        if (data is null || FacetData.Rowless(layer))
        {
            return LayerPlan.Create(layer, build);
        }

        if (data.Empty)
        {
            return new EmptyPlan(layer);
        }

        // The path of a point layer is chosen by the layer's rows, not by the panel's gathered ones (design §5.5.2).
        var mapped = data.Map is { } map ? FacetData.Remap(LayerPlan.Marked(layer), map) : layer;
        StatSource? source = null;
        if (layer.Stat is not null)
        {
            var (ownX, ownY) = StatPlan.StatColumns(mapped);
            source = new StatSource(
                shared.X ?? new StatRange([(ownX, data.Rows)]),
                ownY is null ? null : shared.Y ?? new StatRange([(ownY, data.Rows)])
            );
        }

        return LayerPlan.Create(mapped, build, data.Rows, source);
    }

    /// <summary>
    /// Returns each stat layer's ranges along a facet's shared directions (design §4.5, row G6b): the union of every panel's
    /// rows, a run or a gathered table each, made once a layer so that its panels read it once. Rows in no panel are left
    /// out. Null along a free direction, and for a layer without a stat or a plot without a facet.
    /// </summary>
    private static (StatRange? X, StatRange? Y)[] SharedSources(PlotSpec spec, FacetData? facet)
    {
        var shared = new (StatRange?, StatRange?)[spec.Layers.Count];
        if (facet is null)
        {
            return shared;
        }

        for (var i = 0; i < shared.Length; i++)
        {
            var layer = spec.Layers[i];
            if (layer.Stat is null || FacetData.Rowless(layer))
            {
                continue;
            }

            var xs = new List<(Column, RowRange)>();
            var ys = new List<(Column, RowRange)>();
            foreach (var data in facet.Panels)
            {
                if (!data.Empty)
                {
                    var (x, y) = StatPlan.StatColumns(data.Map is { } map ? FacetData.Remap(layer, map) : layer);
                    xs.Add((x, data.Rows));
                    if (y is not null)
                    {
                        ys.Add((y, data.Rows));
                    }
                }
            }

            shared[i] = (
                facet.Arrangement.FreeXOf(spec.ScaleX) ? null : new StatRange(xs),
                facet.Arrangement.FreeY || ys.Count == 0 ? null : new StatRange(ys)
            );
        }

        return shared;
    }

    /// <summary>Returns whether a layer has a stat, whose edges a facet's new scales move (row G6b).</summary>
    private static bool HasStat(PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (spec.Layers[i].Stat is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a facet's kind or columns changed (design §4.8, row G6b): the "data" stage, which finds the panels and
    /// their rows again. Its scales and its columns' count only arrange them again.
    /// </summary>
    private static bool FacetDataChanged(FacetSpec? before, FacetSpec? after) =>
        before is null || after is null
            ? before is not null || after is not null
            : before.Id != after.Id
                && (before, after) switch
                {
                    (FacetWrapSpec b, FacetWrapSpec a) => b.By != a.By,
                    (FacetGridSpec b, FacetGridSpec a) => b.Rows != a.Rows || b.Columns != a.Columns,
                    _ => true,
                };

    /// <summary>
    /// Gathers every panel's layers' batches into the arrays M4 extracts together (row W1b2: a batch a layer, so that a layer
    /// is replaced alone), made again when the layers change.
    /// </summary>
    private void GatherBatches()
    {
        var count = 0;
        foreach (var panel in _panels)
        {
            count += panel.Layers.Length;
        }

        if (_allBatches.Length != count)
        {
            (_allBatches, _allOrigins, _allBoundaries) = (new M4Batch[count], new ScaleOrigin[count], new ArraySegment<double>[count]);
        }

        var at = 0;
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                _allBatches[at++] = layer.Batch;
            }
        }
    }

    /// <summary>
    /// Makes the layers again (design §4.8): a pyramid for each Y column not held yet, the layers' M4 series, and frees the
    /// pyramids no layer uses.
    /// </summary>
    private void BuildLayers(PlotSpec spec, bool data)
    {
        List<Column> columns = [];
        List<Raster> rasters = [];
        var olds = new List<LayerPlan>();

        // The facet's panels and their rows (row G6b): found again when the facet or the layers' columns changed.
        var oldFacet = _facet;
        if (spec.Facet is null)
        {
            _facet = null;
        }
        else if (data || _facet is null)
        {
            _facet = FacetData.Build(spec, _pool);
        }

        var count = _facet?.Panels.Length ?? 1;
        if (_panels.Length != count)
        {
            olds.AddRange(_panels.SelectMany(p => p.Layers));
            _panels = [.. Enumerable.Range(0, count).Select(_ => new PlotPanel())];
            _learned = new PanelDomains[count];
        }

        // Layers made again try again (row W1b1): the passes of statistics a failed one started are forgotten too.
        _indexes.Forget();
        var shared = SharedSources(spec, _facet);
        var indexing = new SpatialIndexing();
        for (var p = 0; p < count; p++)
        {
            var panel = _panels[p];
            (panel.Data, panel.Table) = _facet is null ? (null, spec.Data) : (_facet.Panels[p], _facet.Panels[p].Data);
            var build = new PlanBuild(
                Pyramid,
                _store.Find,
                new M4Batch(),
                _capacity,
                _pool,
                Mipmap,
                spec.ScaleX,
                _xTransform,
                spec.ScaleY,
                _yTransform
            )
            {
                Indexing = indexing,
            };
            var layers = new LayerPlan[spec.Layers.Count];
            for (var i = 0; i < layers.Length; i++)
            {
                // A layer made again tries again (row W1b1).
                if (i < _failures.Length)
                {
                    _failures[i] = null;
                }

                // A layer's series go into a batch of its own (row W1b2), extracted over its pair of scales, and its stat
                // reads those scales (row G6a).
                var layer = spec.Layers[i];
                var batch = new M4Batch();
                build.Layer = i;
                build.Use(
                    batch,
                    layer.SecondaryX ? spec.ScaleX2 : spec.ScaleX,
                    layer.SecondaryX ? _xTransform2 : _xTransform,
                    layer.SecondaryY ? spec.ScaleY2 : spec.ScaleY,
                    layer.SecondaryY ? _yTransform2 : _yTransform
                );
                (columns, rasters) = ([], []);
                layers[i] = PanelLayer(layer, panel.Data, build, shared[i]);
                (layers[i].Batch, layers[i].PyramidColumns, layers[i].Rasters) = (batch, columns, rasters);
            }

            olds.AddRange(panel.Layers);
            panel.Layers = layers;
        }

        // The pyramids over the old layers' columns go first, waiting for their chunks in flight; then the columns, gathered
        // ones in native memory among them (PR #311 review).
        ReleaseUnused();
        foreach (var old in olds)
        {
            old.Release();
        }

        if (!ReferenceEquals(oldFacet, _facet))
        {
            oldFacet?.Dispose();
        }

        GatherBatches();

        RasterPyramid Mipmap(Raster raster)
        {
            rasters.Add(raster);
            if (!_rasters.TryGetValue(raster, out var mipmap))
            {
                mipmap = raster.MakePyramid();
                _rasters.Add(raster, mipmap);
            }

            return mipmap;
        }

        PlotPyramid Pyramid(Column y)
        {
            columns.Add(y);
            if (_held.Add(y))
            {
                _ = _store.Acquire(y, _pool, out var built);
                PyramidsBuilt += built ? 1 : 0;
            }

            return _store.Find(y)!;
        }
    }

    /// <summary>
    /// Reaches every pyramid and the statistics, the orders, and the workers' results the layers read, for the columns'
    /// current values (design §4.8). A render that waits waits for them, so that a headless image is the same whenever it
    /// is drawn, and throws a failure as it was. The engine's frame reads what is there (row W1b1): it requests the stale
    /// chunks of the pyramids, takes each layer's readiness, and fails a layer alone.
    /// </summary>
    private void PollIndexes()
    {
        _sink.Indexes = Waits ? null : _indexes;
        foreach (var column in _held)
        {
            // A fault fails the layers that read the pyramid, below; the others go on.
            var pyramid = _store.Find(column)!;
            if (Waits)
            {
                pyramid.WhenComplete().GetAwaiter().GetResult();
            }
            else
            {
                _ = pyramid.RequestStale();
            }
        }

        // The rasters' mipmaps: the rows of the chunks that changed, on the workers (design §3.7, row G9). A frame that does
        // not wait starts the update and looks at it; its image layers draw what is on the GPU meanwhile (row W1b3).
        foreach (var mipmap in _rasters.Values)
        {
            mipmap.Update(_pool, Waits);
        }

        foreach (var panel in _panels)
        {
            for (var i = 0; i < panel.Layers.Length; i++)
            {
                var layer = panel.Layers[i];
                if (Failed(i))
                {
                    continue;
                }

                try
                {
                    layer.Readiness = LayerReadiness.Ready;
                    ThrowFault(layer);
                    layer.Readiness = layer.Poll(_indexes);
                }
                catch (Exception exception) when (!Waits)
                {
                    Fail(i, exception);
                }
            }
        }
    }

    /// <summary>Throws the fault of a pyramid a layer reads (row W1b1): its build failed, and the layer fails with it.</summary>
    private void ThrowFault(LayerPlan layer)
    {
        if (Waits)
        {
            return;
        }

        foreach (var column in layer.Columns)
        {
            if (_held.Contains(column) && _store.Find(column)!.Fault is { } fault)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault);
            }
        }
    }

    /// <summary>Returns whether the layer at a position in the spec failed and waits for a version to move (row W1b1).</summary>
    private bool Failed(int layer) => layer < _failures.Length && _failures[layer] is not null;

    /// <summary>Returns whether a layer is drawn: it has not failed, and its results, current or older, are there (row W1b1).</summary>
    private bool Drawn(int position, LayerPlan layer) => !Failed(position) && layer.Readiness != LayerReadiness.Pending;

    /// <summary>
    /// Fails the layer at a position in the spec (row W1b1): it is not drawn and learns nothing until one of its columns'
    /// versions moves, and the failure is reported once.
    /// </summary>
    private void Fail(int layer, Exception error)
    {
        if (Failed(layer))
        {
            return;
        }

        _failures[layer] = (error, VersionsOf(layer));
        _newFailures.Add((layer, error));
    }

    /// <summary>Returns the sum of the versions of the columns the layer at a position reads, in every panel: it grows when one moves.</summary>
    private long VersionsOf(int layer)
    {
        var sum = 0L;
        foreach (var panel in _panels)
        {
            if (layer < panel.Layers.Length)
            {
                foreach (var column in panel.Layers[layer].Columns)
                {
                    sum += column.Version;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Returns whether a failed layer's columns moved, so that the layers are made again and try once more (row W1b1); a
    /// pyramid with a fault among them is built again. Forgets the failures of a spec of another number of layers.
    /// </summary>
    private bool Retry(PlotSpec spec)
    {
        if (_failures.Length != spec.Layers.Count)
        {
            _failures = new (Exception, long)?[spec.Layers.Count];
            return false;
        }

        var retry = false;
        for (var i = 0; i < _failures.Length; i++)
        {
            if (_failures[i] is not { } failure || VersionsOf(i) == failure.Versions)
            {
                continue;
            }

            // The failure stands for the versions it is tried again for: a making off the frame (row W1b2) that the next
            // frame does not start again; the new layers, made or failed again, replace it.
            retry = true;
            _failures[i] = failure with { Versions = VersionsOf(i) };
            foreach (var panel in _panels)
            {
                foreach (var column in panel.Layers[i].Columns)
                {
                    _store.Rebuild(column, _pool);
                }
            }
        }

        return retry;
    }

    /// <summary>Leaves out of the extraction the series of the layers that draw nothing this frame (row W1b1).</summary>
    private void SkipSeries()
    {
        foreach (var panel in _panels)
        {
            for (var i = 0; i < panel.Layers.Length; i++)
            {
                var batch = panel.Layers[i].Batch;
                var skipped = !Drawn(i, panel.Layers[i]);
                for (var s = 0; s < batch.Count; s++)
                {
                    batch.SetSkipped(s, skipped);
                }
            }
        }
    }

    /// <summary>
    /// Reads the series' errors and results after an extraction that did not throw (row W1b1): a failed series fails its
    /// layer, and a provisional result of a layer that draws makes the frame provisional.
    /// </summary>
    private void ReadSeries()
    {
        _extractProvisional = false;
        foreach (var panel in _panels)
        {
            for (var layer = 0; layer < panel.Layers.Length; layer++)
            {
                var batch = panel.Layers[layer].Batch;
                for (var s = 0; s < batch.Count; s++)
                {
                    if (Failed(layer))
                    {
                        break;
                    }

                    if (batch.GetError(s) is { } error)
                    {
                        Fail(layer, error);
                    }
                    else
                    {
                        _extractProvisional |= batch.GetResult(s).IsProvisional && Drawn(layer, panel.Layers[layer]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns whether the frame is provisional (row W1b1): a layer that has not failed draws older results or none yet, a
    /// domain learned without every chunk's statistics, or M4 read a pyramid not built.
    /// </summary>
    private bool Provisional()
    {
        if (_indexes.Incomplete || _extractProvisional || Making)
        {
            return true;
        }

        foreach (var panel in _panels)
        {
            for (var i = 0; i < panel.Layers.Length; i++)
            {
                if (!Failed(i) && panel.Layers[i].Readiness != LayerReadiness.Ready)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether a layer of the last recording drew a stand-in, an image's tile from an ancestor (Issue #319).</summary>
    private bool RecordedProvisional()
    {
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                if (layer.RecordedProvisional)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether a column's values changed between the last extraction and this Render's read of the versions (an Invalidate).</summary>
    private bool ValuesChanged()
    {
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                if (layer.ValuesChanged())
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Records the guides behind, each panel's layers in order inside its clip, and the guides over (design §4.6, §5.5.3). A
    /// layer maps its values to pixels in double, through the transforms a span at a time. A horizontal axis runs left to
    /// right and a vertical one bottom to top, or the other way when reversed; flipped, x is the vertical one (row G10).
    /// </summary>
    private void Record(PlotSpec spec, bool flipped)
    {
        _list.Clear();
        _context.Theme = spec.Theme;
        if (_ownImages && !_inFigure)
        {
            // No backend sends the plan's own cache: what the last frame staged counts as sent.
            _context.Images.Sent();
        }

        if (!_inFigure)
        {
            _context.Images.BeginFrame();
        }

        _guides.RecordBehind(_list, spec);
        LayersRecorded = 0;
        _annotationHits.Clear();
        for (var p = 0; p < _panels.Length; p++)
        {
            RecordPanel(spec, p, _panels[p], _guides.PanelAt(p), flipped);
        }

        _guides.RecordOver(_list, spec);
    }

    /// <summary>Records a panel's layers inside its clip, mapped by its axes (row G6b).</summary>
    private void RecordPanel(PlotSpec spec, int index, PlotPanel panel, PanelGuide guide, bool flipped)
    {
        var yScale = (ContinuousScaleSpec)spec.ScaleY;

        // A band axis increases with the positions of its categories, which hold its reverse (design §4.2, row G7b).
        var (xReverse, xClamp, band) = spec.ScaleX switch
        {
            BandScaleSpec b => (false, false, (BandAxis?)new BandAxis(BandColumn(spec)?.CategoryCount ?? 0, b.PaddingInner, b.Reverse)),
            ContinuousScaleSpec c => (c.Reverse, c.Clamp, null),
            _ => (false, false, null),
        };
        var (left, top, width, height) = (guide.Left, guide.Top, guide.Width, guide.Height);
        if (guide.Polar is { } polar)
        {
            // The stretched space (design §4.4): each axis from 0 over its pixels, increasing with its data unless reversed.
            var polarX = new GeomFrame.Axis(_xTransform, panel.X, xReverse, xClamp, 0, polar.AlongSize, band);
            var polarY = new GeomFrame.Axis(_yTransform, panel.Y, yScale.Reverse, yScale.Clamp, 0, polar.AcrossSize);
            _context.Begin(polarX, polarY, panel.Boundaries, _scale, flipped: false, (left, top, width, height), polar);
            RecordLayers(panel, spec.Theme.Palette, flipped: false);
            RecordAnnotations(spec, index, guide, new AnnotationAxes(polarX, polarY, polarX, polarY, Flipped: false));
            return;
        }

        var x = flipped
            ? new GeomFrame.Axis(_xTransform, panel.X, !xReverse, xClamp, top, height, band)
            : new GeomFrame.Axis(_xTransform, panel.X, xReverse, xClamp, left, width, band);
        var y = flipped
            ? new GeomFrame.Axis(_yTransform, panel.Y, yScale.Reverse, yScale.Clamp, left, width)
            : new GeomFrame.Axis(_yTransform, panel.Y, !yScale.Reverse, yScale.Clamp, top, height);

        // The secondary scales' axes over the same panel (row G6a), each with its own domain, transform, reverse, and clamp.
        var (x2, y2) = (x, y);
        if (spec.ScaleX2 is { } secondX)
        {
            x2 = flipped
                ? new GeomFrame.Axis(_xTransform2, panel.X2, !secondX.Reverse, secondX.Clamp, top, height)
                : new GeomFrame.Axis(_xTransform2, panel.X2, secondX.Reverse, secondX.Clamp, left, width);
        }

        if (spec.ScaleY2 is { } secondY)
        {
            y2 = flipped
                ? new GeomFrame.Axis(_yTransform2, panel.Y2, secondY.Reverse, secondY.Clamp, left, width)
                : new GeomFrame.Axis(_yTransform2, panel.Y2, !secondY.Reverse, secondY.Clamp, top, height);
        }

        panel.Frames = (x, y, x2, y2);
        _context.Begin(x, y, panel.Boundaries, _scale, flipped, (left, top, width, height), null);
        RecordLayers(panel, spec.Theme.Palette, flipped);
        RecordAnnotations(spec, index, guide, new AnnotationAxes(x, y, x2, y2, flipped));
    }

    /// <summary>
    /// Records a panel's layers in order inside its clip, in its series color where its spec gives none; a polar layer's fills
    /// go out as one shape after it.
    /// </summary>
    private void RecordLayers(PlotPanel panel, Palette palette, bool flipped)
    {
        _context.ClipToPanel();
        var current = 0;
        var layers = panel.Layers;
        for (var i = 0; i < layers.Length; i++)
        {
            // The series color of the layer's position (design §4.1, row G7a).
            var layer = layers[i];

            // A layer on a secondary scale is mapped by its pair's axes (row G6a); polar has none. A layer reads its own batch
            // (row W1b2).
            var pair = Pair(layer.Spec);
            if (pair != current)
            {
                var frames = panel.Frames;
                _context.Begin(
                    (pair & 1) != 0 ? frames.X2 : frames.X,
                    (pair & 2) != 0 ? frames.Y2 : frames.Y,
                    (pair & 1) != 0 ? panel.Boundaries2 : panel.Boundaries,
                    _scale,
                    flipped,
                    _context.Panel,
                    null
                );
                current = pair;
            }

            _context.Use(layer.Batch);

            layer.SeriesColor = palette.Series(i);
            layer.Index = i;
            layer.Colors = Color;
            (layer.Sizes, layer.Shapes) = (Size, Shape);
            if (Drawn(i, layer))
            {
                layer.Record(_context);
                LayersRecorded++;
            }

            _context.Flush();
        }
    }
}
