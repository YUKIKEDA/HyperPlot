using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A compiled plot of row G1 (design §4.8): the pyramid of each Y column, the M4 series of each layer, the learned domains,
/// and the recorded lines, for a target of one size. Each <see cref="Render(PlotSpec)"/> compares the spec with the last one
/// drawn and does again only the stages that changed.
/// </summary>
/// <remarks>One thread at a time uses a plan. It owns its pyramids; the columns stay with their owner, who disposes them after the plan.</remarks>
internal sealed class PlotPlan : IDisposable
{
    private readonly PlotWorkerPool _pool;
    private readonly int _width;
    private readonly int _height;
    private readonly Dictionary<Column, PlotPyramid> _pyramids = [];
    private readonly M4Batch _batch = new();
    private readonly RenderList _list = new();
    private readonly Vector2[] _points;
    private readonly double[] _boundaries;
    private LayerPlan[] _layers = [];
    private PlotSpec? _spec;

    // ScaleRange.None until boundaries are made for a domain: no domain equals it.
    private ScaleRange _x = ScaleRange.None;
    private ScaleRange _y;

    /// <summary>Initializes a new instance of the <see cref="PlotPlan"/> class for a target of a size, in physical pixels.</summary>
    internal PlotPlan(PlotWorkerPool pool, int width, int height)
    {
        _pool = pool;
        _width = width;
        _height = height;

        // Each M4 point may bring a break before it, which is a point of NaN (design §5.1).
        _points = new Vector2[2 * M4.MaxPointCount(width)];
        _boundaries = new double[width + 1];
    }

    /// <summary>Gets the number of pyramids built, for the tests of the diff (design §4.8).</summary>
    internal int PyramidsBuilt { get; private set; }

    /// <summary>Gets the number of M4 extractions, for the tests of the diff.</summary>
    internal int Extractions { get; private set; }

    /// <summary>Gets the number of times the lines were recorded, for the tests of the diff.</summary>
    internal int Recordings { get; private set; }

    /// <summary>Gets the pyramids the plan holds, one a Y column. Tests only.</summary>
    internal IReadOnlyCollection<PlotPyramid> Pyramids => _pyramids.Values;

    /// <summary>Gets the x domain of the last extraction, as an origin and differences (design §3.6). Tests only.</summary>
    internal ScaleRange XDomain => _x;

    /// <summary>Gets the y domain of the last extraction. Tests only.</summary>
    internal ScaleRange YDomain => _y;

    /// <summary>
    /// Compiles a spec against the last one drawn, waits until its pyramids and X statistics are built for the columns'
    /// current values, and returns the recorded lines over the whole target (design §4.8). Allocates nothing for the spec
    /// drawn last once warm.
    /// </summary>
    /// <exception cref="ArgumentNullException">The spec has a null member or layer.</exception>
    /// <exception cref="ArgumentException">The spec does not check (design §4.8), or the x domain is too narrow for the width.</exception>
    /// <exception cref="InvalidOperationException">A learned domain has an infinite end (design §4.2), or an X column decreases.</exception>
    internal RenderList Render(PlotSpec spec)
    {
        Validate(spec);
        var previous = _spec;

        // Until this spec is drawn, the next one is compiled from the start: a stage that threw leaves no state to trust.
        _spec = null;
        var data = previous is null || DataChanged(previous, spec);
        var scales = previous is null || !SameScale(previous.ScaleX, spec.ScaleX) || !SameScale(previous.ScaleY, spec.ScaleY);
        var looks = data || StyleChanged(previous!, spec);
        if (data)
        {
            BuildLayers(spec);
        }
        else
        {
            // The layers name the same columns; take the new nodes for their strokes.
            for (var i = 0; i < _layers.Length; i++)
            {
                _layers[i].Spec = (LineLayerSpec)spec.Layers[i];
            }
        }

        // The versions are read before the wait and the extraction. An Invalidate that comes after this read leaves the
        // recorded versions behind the column's, so the next Render extracts again.
        foreach (var layer in _layers)
        {
            (layer.XSeen, layer.YSeen) = (layer.X.Version, layer.Y.Column.Version);
        }

        WaitForIndexes();
        var extract = data || scales || ValuesChanged();
        if (extract)
        {
            var x = Learn(spec, spec.ScaleX, ofX: true);
            _y = Learn(spec, spec.ScaleY, ofX: false);
            if (x != _x)
            {
                MakeBoundaries(x);
            }

            _pool.Extract(_batch, new ScaleOrigin(_x.Origin, _y.Origin), _boundaries);
            Extractions++;
            foreach (var layer in _layers)
            {
                (layer.XVersion, layer.YVersion) = (layer.XSeen, layer.YSeen);
            }
        }

        if (extract || looks)
        {
            Record();
            Recordings++;
        }

        _spec = spec;
        return _list;
    }

    /// <summary>Releases the pyramids.</summary>
    public void Dispose()
    {
        foreach (var pyramid in _pyramids.Values)
        {
            pyramid.Dispose();
        }

        _pyramids.Clear();
        _layers = [];
        _batch.Clear();
        _spec = null;
    }

    /// <summary>Throws unless the spec is one that row G1 compiles (design §4.8). Reads no data.</summary>
    private static void Validate(PlotSpec plot)
    {
        if (plot.Data is null || plot.Layers is null || plot.ScaleX is null || plot.ScaleY is null)
        {
            throw new ArgumentNullException(nameof(plot), "The spec's Data, Layers, ScaleX, and ScaleY must not be null.");
        }

        for (var i = 0; i < plot.Layers.Count; i++)
        {
            var layer = plot.Layers[i];
            if (layer is null)
            {
                throw new ArgumentNullException(nameof(plot), $"Layer {i} of the spec is null.");
            }

            if (layer is not LineLayerSpec line)
            {
                throw new ArgumentException($"Layer {i} is a {layer.GetType().Name}; row G1 compiles line layers only.", nameof(plot));
            }

            CheckReference(plot.Data, line.X, i, "X");
            CheckReference(plot.Data, line.Y, i, "Y");
            if (line.Y.Column is not IPyramidColumn)
            {
                throw new ArgumentException(
                    $"The Y column \"{plot.Data.GetName(line.Y.Index)}\" of layer {i} is a {line.Y.Column.KindName}; a line's Y is a stored column.",
                    nameof(plot)
                );
            }
        }

        CheckScale(plot.ScaleX, "ScaleX");
        CheckScale(plot.ScaleY, "ScaleY");
    }

    private static void CheckReference(DataTable data, ColumnRef reference, int layer, string channel)
    {
        if (reference.IsDefault)
        {
            throw new ArgumentException($"The {channel} of layer {layer} is the default reference, which names no column.");
        }

        if (!ReferenceEquals(reference.TableOrNull, data))
        {
            throw new ArgumentException($"The {channel} column of layer {layer} belongs to another table than the spec's Data.");
        }
    }

    private static void CheckScale(ScaleSpec scale, string name)
    {
        if (scale is not LinearScaleSpec linear)
        {
            throw new ArgumentException($"The {name} is a {scale.GetType().Name}; row G1 compiles linear scales only.");
        }

        if (linear.Domain is { } domain)
        {
            LinearScaleSpec.CheckDomain(domain.Min, domain.Max);
        }
    }

    /// <summary>
    /// Returns whether a layer's columns changed, or the layers were added, removed, or moved (design §4.8). A new table
    /// changes every reference, since a reference names its table and the check refuses another table's.
    /// </summary>
    private static bool DataChanged(PlotSpec previous, PlotSpec spec)
    {
        if (previous.Layers.Count != spec.Layers.Count)
        {
            return true;
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var before = (LineLayerSpec)previous.Layers[i];
            var after = (LineLayerSpec)spec.Layers[i];
            if (before.Id != after.Id && (before.X != after.X || before.Y != after.Y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a layer's stroke changed; called only when the layers name the same columns.</summary>
    private static bool StyleChanged(PlotSpec previous, PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var before = (LineLayerSpec)previous.Layers[i];
            var after = (LineLayerSpec)spec.Layers[i];
            if (before.Id != after.Id && before.Style != after.Style)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameScale(ScaleSpec before, ScaleSpec after) =>
        before.Id == after.Id || ((LinearScaleSpec)before).Domain == ((LinearScaleSpec)after).Domain;

    /// <summary>
    /// Learns a linear domain (design §4.2) as an origin and differences (§3.6): the one given, or the union of the ranges of
    /// every layer's column, from statistics that the wait for the indexes had the workers compute. Integer ends are compared
    /// as <see cref="long"/> while every column is an integer column, and as <see cref="double"/> once a floating-point one
    /// comes in.
    /// </summary>
    private static ScaleRange Learn(PlotSpec spec, ScaleSpec scale, bool ofX)
    {
        if (((LinearScaleSpec)scale).Domain is { } domain)
        {
            return ScaleRange.FromDouble(domain.Min, domain.Max);
        }

        var (integerMin, integerMax) = (long.MaxValue, long.MinValue);
        var (min, max) = (double.PositiveInfinity, double.NegativeInfinity);
        var (found, integer) = (false, true);
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var line = (LineLayerSpec)spec.Layers[i];
            var reference = ofX ? line.X : line.Y;
            if (!reference.Column.TryGetRange(out var low, out var high))
            {
                continue;
            }

            if (!double.IsFinite(low.Value) || !double.IsFinite(high.Value))
            {
                throw new InvalidOperationException(
                    $"The column \"{spec.Data.GetName(reference.Index)}\" holds {(double.IsFinite(low.Value) ? high.Value : low.Value)}, so a learned domain would have an infinite end; give the scale a domain with WithDomain."
                );
            }

            integer &= low.IsInteger;
            if (low.IsInteger)
            {
                (integerMin, integerMax) = (Math.Min(integerMin, low.Integer), Math.Max(integerMax, high.Integer));
            }

            (min, max, found) = (Math.Min(min, low.Value), Math.Max(max, high.Value), true);
        }

        if (!found)
        {
            return ScaleRange.FromDouble(0, 1);
        }

        if (!integer)
        {
            return min == max ? ScaleRange.FromDouble(min - 0.5, max + 0.5) : ScaleRange.FromDouble(min, max);
        }

        // One value v gives [v - 0.5, v + 0.5], taken as differences from v so that a v past 2^53 stays exact.
        return integerMin == integerMax ? new ScaleRange(integerMin, -0.5, 0.5).Anchor() : ScaleRange.FromInteger(integerMin, integerMax);
    }

    /// <summary>
    /// Writes the W + 1 boundaries of the pixel columns over an x domain into the plan's buffer, as differences from its
    /// origin, or throws when they do not increase in double (design §4.8). Allocates nothing, so that a new x domain costs no
    /// garbage.
    /// </summary>
    private void MakeBoundaries(ScaleRange domain)
    {
        // Until the buffer holds this domain's boundaries, it holds no domain's.
        _x = ScaleRange.None;
        for (var k = 0; k <= _width; k++)
        {
            _boundaries[k] = domain.Lower + ((domain.Upper - domain.Lower) * k / _width);
            if (k > 0 && !(_boundaries[k - 1] < _boundaries[k]))
            {
                throw new ArgumentException(
                    $"The x domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}] is too narrow for {_width} pixel columns: their boundaries do not increase in double."
                );
            }
        }

        _x = domain;
    }

    /// <summary>Makes the layers again: a pyramid for each Y column not held yet, one M4 series a layer, and frees the pyramids no layer uses.</summary>
    private void BuildLayers(PlotSpec spec)
    {
        _batch.Clear();
        var layers = new LayerPlan[spec.Layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            var line = (LineLayerSpec)spec.Layers[i];
            var y = line.Y.Column;
            if (!_pyramids.TryGetValue(y, out var pyramid))
            {
                pyramid = ((IPyramidColumn)y).BuildPlotPyramid(_pool);
                _pyramids.Add(y, pyramid);
                PyramidsBuilt++;
            }

            var output = new M4Point[M4.MaxPointCount(_width)];
            layers[i] = new LayerPlan(line, line.X.Column, pyramid, output, pyramid.AddTo(_batch, line.X.Column, output));
        }

        foreach (var column in _pyramids.Keys.ToArray())
        {
            if (!Array.Exists(layers, layer => ReferenceEquals(layer.Y.Column, column)))
            {
                _pyramids[column].Dispose();
                _ = _pyramids.Remove(column);
            }
        }

        _layers = layers;
    }

    /// <summary>
    /// Waits until every pyramid and every stored X column's statistics are there for the columns' current values (design
    /// §4.8): a headless image is the same whenever it is drawn. A failure is thrown again as it was.
    /// </summary>
    private void WaitForIndexes()
    {
        foreach (var layer in _layers)
        {
            layer.Y.WhenComplete().GetAwaiter().GetResult();
            layer.X.PrecomputeStatsIfMissing(_pool)?.GetAwaiter().GetResult();
        }
    }

    /// <summary>Returns whether a column's values changed between the last extraction and this Render's read of the versions (an Invalidate).</summary>
    private bool ValuesChanged()
    {
        foreach (var layer in _layers)
        {
            if (layer.XSeen != layer.XVersion || layer.YSeen != layer.YVersion)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps each layer's M4 points to pixels in double and records them as a polyline, in layer order (design §4.8).</summary>
    private void Record()
    {
        _list.Clear();
        foreach (var layer in _layers)
        {
            var count = 0;
            foreach (var point in layer.Output.AsSpan(0, _batch.GetResult(layer.Series).Count))
            {
                if (point.BreakBefore)
                {
                    _points[count++] = new Vector2(float.NaN);
                }

                // (d(v) − lower) / (upper − lower) maps to [0, 1], in differences from the domain's origin; x runs left to right and y
                // bottom to top (design §3.6, §4.2).
                var x = (point.X - _x.Lower) / (_x.Upper - _x.Lower) * _width;
                var y = _height - ((point.Y - _y.Lower) / (_y.Upper - _y.Lower) * _height);
                _points[count++] = new Vector2((float)x, (float)y);
            }

            _list.DrawPolyline(_points.AsSpan(0, count), layer.Spec.Style);
        }
    }

    /// <summary>
    /// One layer of the plan: its node, its columns, its M4 series, the column versions it was extracted at, and the versions
    /// this Render read before its wait.
    /// </summary>
    private sealed class LayerPlan(LineLayerSpec spec, Column x, PlotPyramid y, M4Point[] output, int series)
    {
        public LineLayerSpec Spec { get; set; } = spec;

        public Column X { get; } = x;

        public PlotPyramid Y { get; } = y;

        public M4Point[] Output { get; } = output;

        public int Series { get; } = series;

        public long XVersion { get; set; } = -1;

        public long YVersion { get; set; } = -1;

        public long XSeen { get; set; }

        public long YSeen { get; set; }
    }
}
