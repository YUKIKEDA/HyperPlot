using System.Numerics;
using System.Runtime.InteropServices;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The drawing of one point layer on the index path (design §5.5.2 "`Auto` の切り替え"): raw points or density, chosen once
/// a frame for all its parts, those of every facet panel included, from what each part's scan before the recording found
/// (<see cref="IndexedPointPlan.Scan"/>): the upper counts of the points in the view, summed, the largest bin, the greatest
/// of the parts', and the bytes the parts need as raw points, summed. <see cref="PointBudget"/> sets the counts and the
/// predicted times against the thresholds summed over the <see cref="PointDraw.Auto"/> layers of each panel, and the bytes
/// against the budget with every other layer's.
/// </summary>
/// <remarks>One thread at a time: the frame's. After the parts' list has grown, a frame allocates nothing.</remarks>
/// <param name="layer">The layer's number in the spec of the plans' making: a tie of the budget is broken by it.</param>
internal sealed class DensitySwitch(int layer)
{
    /// <summary>
    /// The share of each threshold under which a layer drawn as a density goes back to raw points (design §5.5.2
    /// "入れ替わりを抑える"): of <see cref="PointLayerSpec.DensityThreshold"/>, of <see cref="PredictedTime.Threshold"/>, and
    /// of the budget.
    /// </summary>
    internal const double Back = 0.8;

    private readonly List<IndexedPointPlan> _parts = [];
    private long _frame = -1;
    private long _entered = -1;
    private PointDraw _draw;
    private long _threshold = -1;
    private bool _density;
    private PointLayerSpec? _point;
    private float _scale;

    // What the panels' sums read of the layer this frame (design §5.5.2 "重なる層の和"): its two terms of the predicted
    // time, with the largest bin and with four times it, whether it drew a density in the last frame, and whether every
    // panel of the layer lets it go back.
    private double _terms;
    private double _termsAtFour;
    private bool _wasDensity;
    private bool _mayReturn;

    /// <summary>How a layer takes part in the frame's choice (design §5.5.2 "予算", "重なる層の和").</summary>
    internal enum Share
    {
        /// <summary>The layer reads no condition and draws no page: a density, or no full snapshot scanned.</summary>
        None,

        /// <summary>A <see cref="PointDraw.Points"/> layer: its bytes are in the budget's sum, and nothing changes its drawing.</summary>
        Counted,

        /// <summary>
        /// An <see cref="PointDraw.Auto"/> layer that scanned a full snapshot: in the sums of its panels, which choose its
        /// drawing from the last one, and then in the budget's sum while it draws raw points.
        /// </summary>
        Summed,
    }

    /// <summary>Gets the layer's number in the spec of the plans' making.</summary>
    public int Layer => layer;

    /// <summary>Gets the number of the layer's plot in its figure, 0 for a plot alone, as the frame's scan entered it.</summary>
    public int Plot { get; private set; }

    /// <summary>Gets a value indicating whether the layer draws a density this frame. Valid after <see cref="Choose"/>.</summary>
    public bool Density => _density;

    /// <summary>
    /// Gets the bytes the layer needs as raw points this frame (design §5.5.2 "予算"): the parts' that scanned a full
    /// snapshot, summed, whatever the layer draws; 0 for a <see cref="PointDraw.Density"/> layer, whose scan walks no cell.
    /// </summary>
    public long Needed { get; private set; }

    /// <summary>Gets how the layer takes part in the choice of the frame it last read its conditions for.</summary>
    internal Share Role { get; private set; }

    /// <summary>Gets the upper count of the points in the view the last choice read, summed over the parts.</summary>
    internal long LastCount { get; private set; }

    /// <summary>Gets the <see cref="PointLayerSpec.DensityThreshold"/> the last choice read.</summary>
    internal long Threshold => _threshold;

    /// <summary>Gets the largest bin the last choice read, the greatest of the parts', in points a physical pixel. Tests only.</summary>
    internal double LastLargestBin { get; private set; }

    /// <summary>Gets the predicted time of drawing the layer as raw points the last choice read, in ms; 0 when it read none.</summary>
    internal double LastTime { get; private set; }

    /// <summary>Gets the predicted time the last choice read with four times the largest bin, in ms. Tests only.</summary>
    internal double LastTimeAtFourBins { get; private set; }

    /// <summary>Gets the terms of <see cref="LastTime"/> past the fixed one, which a panel's sum adds (<see cref="PredictedTime.Terms"/>).</summary>
    internal double Terms => _terms;

    /// <summary>Gets the terms of <see cref="LastTimeAtFourBins"/> past the fixed one.</summary>
    internal double TermsAtFour => _termsAtFour;

    /// <summary>
    /// Gets a value indicating whether a <see cref="Share.Summed"/> layer drew a density in the last frame and so may go
    /// back this frame (design §5.5.2 "重なる層の和" "戻り"), rather than be made a density.
    /// </summary>
    internal bool WasDensity => _wasDensity;

    /// <summary>Gets a value indicating whether every panel of the layer has let it go back so far this frame.</summary>
    internal bool MayReturn => _mayReturn;

    /// <summary>Adds a part of the layer, of any panel.</summary>
    public void Add(IndexedPointPlan part) => _parts.Add(part);

    /// <summary>
    /// Enters the layer into its frame's budget (<see cref="RecordContext.Budget"/>) when a part is scanned: once a frame,
    /// with the spec and the scale its drawing is chosen by and its plot's number, and once a panel, the panel being scanned
    /// (<see cref="RecordContext.PanelNumber"/>), whose sums it takes part in.
    /// </summary>
    public void Enter(RecordContext context, PointLayerSpec point)
    {
        var frame = context.Images.Frame;
        if (_entered != frame)
        {
            (_entered, _point, _scale, Plot) = (frame, point, context.Scale, context.Plot);
            context.Budget.Enter(this, frame);
        }

        context.Budget.EnterPanel(this, context.Plot, context.PanelNumber);
    }

    /// <summary>
    /// Returns the drawing of the layer for a frame (design §5.5.2 "`Auto` の切り替え", "予算"): the one the frame's budget
    /// chose for every layer before any recorded, or, for a frame whose budget did not choose, the one the time and the
    /// count give alone (<see cref="Next"/>).
    /// </summary>
    /// <returns>Whether the layer draws a density this frame.</returns>
    public bool Choose(RecordContext context, PointLayerSpec point)
    {
        var frame = context.Images.Frame;
        if (frame != _frame && Gather(frame, point, context.Scale) == Share.Summed)
        {
            _density = Next(_wasDensity, LastCount, _threshold, LastTime, LastTimeAtFourBins);
        }

        return _density;
    }

    /// <summary>
    /// Reads the layer's conditions for the frame it was entered in, from the spec and the scale entered with it; a
    /// <see cref="Share.Summed"/> layer keeps the last frame's drawing until its panels and the budget choose.
    /// </summary>
    internal Share Gather(long frame) => Gather(frame, _point!, _scale);

    /// <summary>Makes the layer a density this frame: a panel's sum, or the budget's, is over its threshold, or leaves no room to go back.</summary>
    internal void Hold() => _density = true;

    /// <summary>Makes a layer that drew a density in the last frame draw raw points this frame: its panels' sums and the budget let it go back.</summary>
    internal void Release() => _density = false;

    /// <summary>Keeps the layer from going back this frame: one of its panels' sums with it as raw points does not let it.</summary>
    internal void Bar() => _mayReturn = false;

    /// <summary>
    /// Returns the drawing of an <see cref="PointDraw.Auto"/> layer after a frame by the time and the count (design §5.5.2
    /// "`Auto` の切り替え", "入れ替わりを抑える", "水準が替わるところ"): from raw points, a density when the upper count passes
    /// the threshold or the predicted time reaches <see cref="PredictedTime.Threshold"/> (<see cref="Raises"/>); from a
    /// density, raw points only when the count is under 0.8 times the threshold, the time under 0.8 times its threshold, and
    /// the time with four times the largest bin under its threshold (<see cref="Lowers"/>). A layer with no decision yet
    /// comes from raw points. The budget's condition comes after (<see cref="PointBudget"/>).
    /// </summary>
    /// <param name="density">Whether the layer drew a density in the last frame.</param>
    /// <param name="count">The upper count of the points in the view, summed over the parts.</param>
    /// <param name="threshold">The layer's <see cref="PointLayerSpec.DensityThreshold"/>.</param>
    /// <param name="time">The predicted time, in ms (<see cref="PredictedTime.Of"/>).</param>
    /// <param name="timeAtFourBins">The predicted time with four times the largest bin, in ms.</param>
    internal static bool Next(bool density, long count, long threshold, double time, double timeAtFourBins) =>
        density ? !Lowers(count, threshold, time, timeAtFourBins) : Raises(count, threshold, time);

    /// <summary>
    /// Returns whether a count and a predicted time, a layer's or a panel's sums (design §5.5.2 "重なる層の和"), make raw
    /// points a density: the count passes the threshold, or the time reaches <see cref="PredictedTime.Threshold"/>.
    /// </summary>
    internal static bool Raises(long count, long threshold, double time) => count > threshold || PredictedTime.Reaches(time);

    /// <summary>
    /// Returns whether a count and the predicted times, a layer's or a panel's sums with the layer that goes back, let a
    /// density go back to raw points: the count under 0.8 times the threshold and the times under their bounds
    /// (<see cref="PredictedTime.Clears"/>).
    /// </summary>
    internal static bool Lowers(long count, long threshold, double time, double timeAtFourBins) =>
        count < threshold * Back && PredictedTime.Clears(time, timeAtFourBins);

    /// <summary>
    /// Reads the layer's conditions for a frame, once a frame (design §5.5.2 "`Auto` の切り替え", "入れ替わりを抑える"): for
    /// <see cref="PointDraw.Auto"/>, the summed upper count and the predicted times of the parts the frame scanned on a full
    /// snapshot, with the last drawing kept for the panels' sums to choose from (<see cref="PointBudget"/>, or
    /// <see cref="Next"/> alone in <see cref="Choose"/>); <see cref="PointDraw.Points"/> and <see cref="PointDraw.Density"/>
    /// read nothing. A frame in which no part scanned a full snapshot predicts no time and keeps the last drawing: raw points
    /// for a layer with no decision yet. Returns how the layer takes part in the frame's choice, which <see cref="Role"/>
    /// keeps.
    /// </summary>
    private Share Gather(long frame, PointLayerSpec point, float scale) => Role = Read(frame, point, scale);

    /// <summary>Reads the layer's conditions for a frame: the work of <see cref="Gather(long, PointLayerSpec, float)"/>.</summary>
    private Share Read(long frame, PointLayerSpec point, float scale)
    {
        _frame = frame;
        Needed = 0;

        // A new drawing or threshold forgets the last frame's choice: the layer is counted again from raw points.
        if (point.Draw != _draw || point.DensityThreshold != _threshold)
        {
            (_draw, _threshold, _density) = (point.Draw, point.DensityThreshold, false);
        }

        var (count, largest, needed, full) = (0L, 0.0, 0L, false);
        foreach (var part in CollectionsMarshal.AsSpan(_parts))
        {
            if (part.ScannedFull(frame))
            {
                (count, largest, needed, full) = (
                    count + part.ScannedCount,
                    Math.Max(largest, part.ScannedLargestBin),
                    needed + part.NeededBytes,
                    true
                );
            }
        }

        if (point.Draw != PointDraw.Auto)
        {
            (_density, Needed) = (point.Draw == PointDraw.Density, needed);
            return _density || !full ? Share.None : Share.Counted;
        }

        // A frame in which no part scanned a full snapshot reads no condition and keeps the drawing: a layer with no
        // decision yet stays on raw points, and one that drew a density stays one (design §5.5.2 "入れ替わりを抑える").
        if (!full)
        {
            (LastCount, LastLargestBin, LastTime, LastTimeAtFourBins) = (0, 0, 0, 0);
            return Share.None;
        }

        // The markers' diameter in physical pixels, as the recording scales it.
        var diameter = point.Size * scale;
        (_terms, _termsAtFour) = (
            PredictedTime.Terms(count, diameter, largest),
            PredictedTime.Terms(count, diameter, PredictedTime.LevelJump * largest)
        );
        (LastCount, LastLargestBin, LastTime, LastTimeAtFourBins, Needed) = (
            count,
            largest,
            PredictedTime.Fixed + _terms,
            PredictedTime.Fixed + _termsAtFour,
            needed
        );
        (_wasDensity, _mayReturn) = (_density, true);
        return Share.Summed;
    }
}

/// <summary>
/// The choice over the point layers that draw with one residency cache in a frame (design §5.5.2 "重なる層の和", "予算", "替
/// える順", "入れ替わりを抑える"), made once before any layer records. First each panel's sums: the upper counts and the
/// predicted times of the <see cref="PointDraw.Auto"/> layers that lie over one another in it, the fixed term counted once,
/// against <see cref="PointLayerSpec.DensityThreshold"/> and <see cref="PredictedTime.Threshold"/>; over them, raw layers
/// become densities, the most points and then the longest time first. Then the budget: the sum of the bytes of the density
/// and raster tiles the last frame used and the bytes every layer that draws raw points needs, those of a figure's other
/// plots too, against the cache's budget; over it, the Auto layers on raw points become densities, the one that needs the
/// most bytes first, until the sum is within it. Last the way back: a panel lets back at most one density a frame, the
/// shortest time first whose panels' sums with it as raw points let it and whose bytes leave the budget's sum under 0.8
/// times the budget; the panels' candidates go back the fewest bytes first. One a plot alone, one a figure.
/// </summary>
/// <remarks>One thread at a time: the frame's. After its lists have grown, a frame allocates nothing.</remarks>
internal sealed class PointBudget
{
    private readonly List<DensitySwitch> _entered = [];
    private readonly List<PanelLayer> _panels = [];
    private readonly List<DensitySwitch> _members = [];
    private readonly List<DensitySwitch> _raw = [];
    private readonly List<DensitySwitch> _returning = [];
    private readonly List<bool> _returned = [];
    private long _frame = -1;

    /// <summary>Gets the sum of the bytes the last choice left against the budget. Tests only.</summary>
    internal long LastSum { get; private set; }

    /// <summary>Gets the number of panels whose sums the last choice read. Tests only.</summary>
    internal int LastPanels { get; private set; }

    /// <summary>Enters a layer scanned in a frame; the layers of an earlier frame are let go of.</summary>
    public void Enter(DensitySwitch layer, long frame)
    {
        if (frame != _frame)
        {
            _entered.Clear();
            _panels.Clear();
            _frame = frame;
        }

        _entered.Add(layer);
    }

    /// <summary>
    /// Enters a layer entered this frame (<see cref="Enter"/>) into the sums of a panel of its plot, once for the parts of
    /// one panel scanned one after another.
    /// </summary>
    public void EnterPanel(DensitySwitch layer, int plot, int panel)
    {
        if (_panels.Count > 0 && _panels[^1] is var last && last.Plot == plot && last.Panel == panel && ReferenceEquals(last.Layer, layer))
        {
            return;
        }

        _panels.Add(new PanelLayer(plot, panel, layer));
    }

    /// <summary>
    /// Chooses the drawing of every layer entered in the cache's current frame, before any of them records: by the panels'
    /// sums of the times and the counts, then by the budget over all of them, then the way back. A frame with no layer
    /// entered chooses nothing.
    /// </summary>
    public void Decide(ResidencyCache cache)
    {
        var frame = cache.Frame;
        if (frame != _frame)
        {
            return;
        }

        _raw.Clear();
        _returning.Clear();
        var sum = cache.TileBytesLastFrame;
        foreach (var layer in CollectionsMarshal.AsSpan(_entered))
        {
            if (layer.Gather(frame) == DensitySwitch.Share.Counted)
            {
                sum += layer.Needed;
            }
        }

        // The panels of the frame in the order of their plot and number, a panel's layers in their order and each once.
        var panels = CollectionsMarshal.AsSpan(_panels);
        panels.Sort(
            static (a, b) =>
                a.Plot != b.Plot ? a.Plot.CompareTo(b.Plot)
                : a.Panel != b.Panel ? a.Panel.CompareTo(b.Panel)
                : a.Layer.Layer.CompareTo(b.Layer.Layer)
        );
        _returned.Clear();
        for (var start = 0; start < panels.Length; )
        {
            start = Members(panels, start, _returned.Count);
            _returned.Add(false);
            Raise(CollectionsMarshal.AsSpan(_members));
        }

        LastPanels = _returned.Count;
        foreach (var layer in CollectionsMarshal.AsSpan(_entered))
        {
            if (layer.Role == DensitySwitch.Share.Summed && !layer.Density)
            {
                sum += layer.Needed;
                _raw.Add(layer);
            }
        }

        // Over the budget: the Auto layers on raw points become densities, the most bytes first, ties by the plot's and the
        // layer's numbers, until the sum is within it.
        if (sum > cache.Budget)
        {
            var raw = CollectionsMarshal.AsSpan(_raw);
            raw.Sort(static (a, b) => a.Needed != b.Needed ? b.Needed.CompareTo(a.Needed) : First(a, b));
            foreach (var layer in raw)
            {
                sum -= layer.Needed;
                layer.Hold();
                if (sum <= cache.Budget)
                {
                    break;
                }
            }
        }

        // The way back: each panel's sums with a density as raw points, read for every density of the panel; then each
        // panel's first candidate, the shortest time, whose panels all let it and whose bytes fit; then the candidates
        // the fewest bytes first, one a panel, while the sum with their bytes stays under 0.8 times the budget.
        var back = cache.Budget * DensitySwitch.Back;
        for (var start = 0; start < panels.Length; )
        {
            start = Members(panels, start, panels[start].Group);
            Qualify(CollectionsMarshal.AsSpan(_members));
        }

        for (var start = 0; start < panels.Length; )
        {
            start = Members(panels, start, panels[start].Group);
            if (Nominate(CollectionsMarshal.AsSpan(_members), back - sum) is { } candidate && !_returning.Contains(candidate))
            {
                _returning.Add(candidate);
            }
        }

        var returning = CollectionsMarshal.AsSpan(_returning);
        returning.Sort(static (a, b) => a.Needed != b.Needed ? a.Needed.CompareTo(b.Needed) : First(a, b));
        foreach (var layer in returning)
        {
            if (!Returned(panels, layer) && sum + layer.Needed < back)
            {
                sum += layer.Needed;
                layer.Release();
                MarkReturned(panels, layer);
            }
        }

        LastSum = sum;

        static int First(DensitySwitch a, DensitySwitch b) => a.Plot != b.Plot ? a.Plot.CompareTo(b.Plot) : a.Layer.CompareTo(b.Layer);
    }

    /// <summary>
    /// Makes a panel's raw layers densities while its sums pass their thresholds (design §5.5.2 "重なる層の和"): the counts'
    /// sum first, against the least <see cref="PointLayerSpec.DensityThreshold"/> of the layers left in it, the most points
    /// first; then the times' sum of the layers left, the fixed term once, the longest time first; ties in the layers'
    /// order. A layer alone in its panel compares its own count and time.
    /// </summary>
    private static void Raise(ReadOnlySpan<DensitySwitch> members)
    {
        while (true)
        {
            var (count, limit, most) = (0L, long.MaxValue, (DensitySwitch?)null);
            foreach (var layer in members)
            {
                if (!layer.Density)
                {
                    (count, limit) = (count + layer.LastCount, Math.Min(limit, layer.Threshold));
                    most = most is null || layer.LastCount > most.LastCount ? layer : most;
                }
            }

            if (most is null || count <= limit)
            {
                break;
            }

            most.Hold();
        }

        while (true)
        {
            var (time, longest) = (PredictedTime.Fixed, (DensitySwitch?)null);
            foreach (var layer in members)
            {
                if (!layer.Density)
                {
                    time += layer.Terms;
                    longest = longest is null || layer.LastTime > longest.LastTime ? layer : longest;
                }
            }

            if (longest is null || !PredictedTime.Reaches(time))
            {
                break;
            }

            longest.Hold();
        }
    }

    /// <summary>
    /// Bars from going back each density of a panel whose sums with it as raw points do not let it (design §5.5.2 "重なる層の
    /// 和" "戻り"): the counts of the raw layers and its own against 0.8 times the least threshold among them, and the times
    /// with the largest bins and with four times every layer's, the fixed term once.
    /// </summary>
    private static void Qualify(ReadOnlySpan<DensitySwitch> members)
    {
        var (count, limit, terms, termsAtFour) = (0L, long.MaxValue, 0.0, 0.0);
        foreach (var layer in members)
        {
            if (!layer.Density)
            {
                (count, limit, terms, termsAtFour) = (
                    count + layer.LastCount,
                    Math.Min(limit, layer.Threshold),
                    terms + layer.Terms,
                    termsAtFour + layer.TermsAtFour
                );
            }
        }

        foreach (var layer in members)
        {
            if (
                layer.Density
                && layer.WasDensity
                && !DensitySwitch.Lowers(
                    count + layer.LastCount,
                    Math.Min(limit, layer.Threshold),
                    PredictedTime.Fixed + terms + layer.Terms,
                    PredictedTime.Fixed + termsAtFour + layer.TermsAtFour
                )
            )
            {
                layer.Bar();
            }
        }
    }

    /// <summary>
    /// Returns a panel's candidate to go back: of its densities that drew one in the last frame and that every panel lets,
    /// the shortest predicted time whose bytes fit in the room under 0.8 times the budget, ties in the layers' order; or null.
    /// </summary>
    private static DensitySwitch? Nominate(ReadOnlySpan<DensitySwitch> members, double room)
    {
        DensitySwitch? shortest = null;
        foreach (var layer in members)
        {
            if (
                layer.Density
                && layer.WasDensity
                && layer.MayReturn
                && layer.Needed < room
                && (shortest is null || layer.LastTime < shortest.LastTime)
            )
            {
                shortest = layer;
            }
        }

        return shortest;
    }

    /// <summary>
    /// Fills <see cref="_members"/> with the <see cref="DensitySwitch.Share.Summed"/> layers of the panel whose entries begin
    /// at a position of the sorted entries, each once, marks the entries with the panel's number, and returns the position
    /// after them.
    /// </summary>
    private int Members(Span<PanelLayer> panels, int start, int group)
    {
        _members.Clear();
        var end = start;
        for (; end < panels.Length && panels[end].Plot == panels[start].Plot && panels[end].Panel == panels[start].Panel; end++)
        {
            panels[end].Group = group;
            var layer = panels[end].Layer;
            if (layer.Role == DensitySwitch.Share.Summed && (_members.Count == 0 || !ReferenceEquals(_members[^1], layer)))
            {
                _members.Add(layer);
            }
        }

        return end;
    }

    /// <summary>Returns whether a panel of a layer let a density go back this frame already.</summary>
    private bool Returned(ReadOnlySpan<PanelLayer> panels, DensitySwitch layer)
    {
        foreach (var entry in panels)
        {
            if (ReferenceEquals(entry.Layer, layer) && _returned[entry.Group])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks every panel of a layer that goes back as having let one back this frame.</summary>
    private void MarkReturned(ReadOnlySpan<PanelLayer> panels, DensitySwitch layer)
    {
        foreach (var entry in panels)
        {
            if (ReferenceEquals(entry.Layer, layer))
            {
                _returned[entry.Group] = true;
            }
        }
    }

    /// <summary>A layer's entry into the sums of a panel of a plot, and the number of that panel among the frame's.</summary>
    private struct PanelLayer(int plot, int panel, DensitySwitch layer)
    {
        public readonly int Plot = plot;
        public readonly int Panel = panel;
        public readonly DensitySwitch Layer = layer;
        public int Group;
    }
}

/// <summary>
/// What the parts of one point layer on the index path share in a panel (design §5.5.2 "ブロック", "濃さの上端 m"): their
/// number, which sets the points of a block of their pages, and the top of the density's scale, the largest normalized bin
/// of every part's drawn pieces. Another panel's parts are not among them.
/// </summary>
/// <remarks>One thread at a time: the frame's. After the parts' list has grown, a frame allocates nothing.</remarks>
internal sealed class DensityPanel
{
    private readonly List<IndexedPointPlan> _parts = [];
    private long _frame = -1;

    /// <summary>Gets the top of the density's scale this frame, in points a square physical pixel; 0 when nothing is drawn.</summary>
    public double Top { get; private set; }

    /// <summary>
    /// Gets the number of the layer's parts in the panel, a part of no point too (design §5.5.2 "ブロック"): the parts that
    /// lie over one another, which set the points of a block of their pages.
    /// </summary>
    public int PartCount => _parts.Count;

    /// <summary>Adds a part of the layer in the panel.</summary>
    public void Add(IndexedPointPlan part) => _parts.Add(part);

    /// <summary>
    /// Sets each part's drawing for the frame, once a frame, and takes the top: the largest of the parts' that draw a
    /// density, a part still on its coarse snapshot too.
    /// </summary>
    public void Prepare(RecordContext context, bool density)
    {
        var frame = context.Images.Frame;
        if (frame == _frame)
        {
            return;
        }

        _frame = frame;
        var top = 0.0;
        foreach (var part in CollectionsMarshal.AsSpan(_parts))
        {
            top = Math.Max(top, part.PlanDensity(density));
        }

        Top = top;
    }
}

/// <summary>
/// The predicted GPU time of drawing a point layer as raw points (design §5.5.2 "予測時間"): T̂ = 1.56 + 6.11 n + 0.0690 n
/// A ρ^0.37 ms, with n the points in the view over 1e7, A the area of a disc of the markers' diameter in square physical
/// pixels, and ρ the largest bin in points a physical pixel. The coefficients and the threshold are the reference machine's
/// (an RTX 3060 Ti, the product's NativeAOT build, Issue #624); another machine is not promised to fit 16.7 ms under the
/// threshold.
/// </summary>
internal static class PredictedTime
{
    /// <summary>The fixed term, in ms.</summary>
    internal const double Fixed = 1.56;

    /// <summary>The term of the points, in ms a unit of <see cref="PointsUnit"/> points.</summary>
    internal const double PerPoints = 6.11;

    /// <summary>The coefficient of the overlap term: n · A · ρ^<see cref="Exponent"/>, A in square physical pixels, ρ in points a physical pixel.</summary>
    internal const double Overlap = 0.0690;

    /// <summary>The exponent of the largest bin in the overlap term.</summary>
    internal const double Exponent = 0.37;

    /// <summary>The points of one unit of n.</summary>
    internal const double PointsUnit = 1e7;

    /// <summary>The predicted time, in ms, from which an <see cref="PointDraw.Auto"/> layer draws a density (design §5.5.2 "境目").</summary>
    internal const double Threshold = 14.1;

    /// <summary>
    /// The most the largest bin grows when the chosen tile level gets one deeper (design §5.5.2 "水準が替わるところ"): a bin's
    /// area on the screen is a quarter, so its points a pixel are 1 to 4 times the shallower level's.
    /// </summary>
    internal const double LevelJump = 4;

    /// <summary>Returns the predicted time, in ms, in doubles, not rounded: <see cref="Fixed"/> plus <see cref="Terms"/>. Allocates nothing.</summary>
    /// <param name="points">The upper count of the points in the view, summed over the layer's parts.</param>
    /// <param name="diameter">The markers' diameter in physical pixels, whatever their shape.</param>
    /// <param name="largestBin">The largest bin, in points a physical pixel.</param>
    public static double Of(long points, double diameter, double largestBin) => Fixed + Terms(points, diameter, largestBin);

    /// <summary>
    /// Returns the terms of the predicted time past the fixed one, in ms: those of the points and of the overlap, which a
    /// panel's sum adds layer by layer with the fixed term counted once (design §5.5.2 "重なる層の和").
    /// </summary>
    /// <param name="points">The upper count of the points in the view, summed over the layer's parts.</param>
    /// <param name="diameter">The markers' diameter in physical pixels, whatever their shape.</param>
    /// <param name="largestBin">The largest bin, in points a physical pixel.</param>
    public static double Terms(long points, double diameter, double largestBin)
    {
        var n = points / PointsUnit;
        var area = Math.PI * diameter * diameter / 4;
        var overlap = n > 0 && largestBin > 0 ? Overlap * n * area * Math.Pow(largestBin, Exponent) : 0;
        return (PerPoints * n) + overlap;
    }

    /// <summary>
    /// Returns whether a predicted time, a layer's or a panel's sum, draws raw points as a density (design §5.5.2 "境目"):
    /// it is <see cref="Threshold"/> or more. Every comparison of a time with the threshold that makes a density goes
    /// through here.
    /// </summary>
    public static bool Reaches(double time) => time >= Threshold;

    /// <summary>
    /// Returns whether predicted times, a layer's or a panel's sums, let a density go back to raw points (design §5.5.2
    /// "入れ替わりを抑える", "水準が替わるところ"): the time under 0.8 times <see cref="Threshold"/> and the time with four times
    /// the largest bin under it. Every comparison of a time with the bounds of the way back goes through here.
    /// </summary>
    public static bool Clears(double time, double timeAtFourBins) => time < Threshold * DensitySwitch.Back && timeAtFourBins < Threshold;
}

/// <summary>
/// One piece of a density a part draws (design §5.5.2 "水準の選び方"): a cell of the snapshot's table, drawn from the tile
/// of the cell itself or of its deepest ancestor that has one, or as one fill of its count when no ancestor has a tile.
/// </summary>
/// <param name="CellCode">The cell's code at its level.</param>
/// <param name="CellLevel">The cell's level.</param>
/// <param name="TileCode">The code of the cell whose tile is read, at <paramref name="TileLevel"/>; or the coarse grid's 0.</param>
/// <param name="TileLevel">The level of the tile read: the cell's, an ancestor's, or −1 for a fill.</param>
/// <param name="Count">A fill's points a square physical pixel: its count over the cell's area on the screen.</param>
internal readonly record struct DensityPiece(ulong CellCode, int CellLevel, ulong TileCode, int TileLevel, double Count);

/// <summary>The density's arithmetic of design §5.5.2, apart from the plan so that tests read it.</summary>
internal static class DensityMath
{
    /// <summary>The deepest level a density tile is drawn at.</summary>
    internal const int DeepestLevel = DensityTiles.DeepestTileLevel;

    /// <summary>
    /// Returns the level of the tiles to draw (design §5.5.2 "水準の選び方"): the deepest whose bins, cells of 8 levels
    /// deeper, are at least a physical pixel wide on both axes, from 0 to 24.
    /// </summary>
    /// <param name="pixelsX">The pixels the whole range of x spans: the range's width times the pixels of one unit.</param>
    /// <param name="pixelsY">The pixels the whole range of y spans.</param>
    public static int TileLevel(double pixelsX, double pixelsY)
    {
        // A bin of level L is the range over 2^(L + 8): at least a pixel while 2^(L + 8) is at most the range's pixels.
        var pixels = Math.Min(Math.Abs(pixelsX), Math.Abs(pixelsY));
        if (!(pixels >= 1 << DensityTiles.BinLevels))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(Math.Log2(pixels)) - DensityTiles.BinLevels, 0, DeepestLevel);
    }

    /// <summary>Returns the level whose cells are about a physical pixel wide on both axes, for counting the view (design §5.5.2).</summary>
    public static int PixelLevel(double pixelsX, double pixelsY)
    {
        var pixels = Math.Max(Math.Abs(pixelsX), Math.Abs(pixelsY));
        return !(pixels > 1) ? 0 : Math.Clamp((int)Math.Ceiling(Math.Log2(pixels)), 0, 32);
    }

    /// <summary>
    /// Returns the area on the screen, in square physical pixels, of a bin of a cell's tile: the cell's extent over 256 on
    /// each axis times the physical pixels of a unit (design §5.5.2 "広さで正規化する").
    /// </summary>
    /// <param name="extentX">The cell's extent along x, in units of the snapshot's scale space.</param>
    /// <param name="extentY">The cell's extent along y.</param>
    /// <param name="perUnitX">The physical pixels of a unit along x, signed.</param>
    /// <param name="perUnitY">The physical pixels of a unit along y, signed.</param>
    public static double BinArea(double extentX, double extentY, double perUnitX, double perUnitY) =>
        Math.Abs(extentX * perUnitX * (extentY * perUnitY)) / ((double)DensityTiles.Side * DensityTiles.Side);

    /// <summary>
    /// Returns a resident cell's estimate of the largest bin (design §5.5.2 "密度タイルが付く前"): its count over its area on
    /// the screen, or over a bin's area when the cell is smaller than a bin; 0 when neither area is positive, so that the
    /// estimate is never NaN or infinite.
    /// </summary>
    /// <param name="count">The cell's points.</param>
    /// <param name="extentX">The cell's extent along x, in units of the snapshot's scale space.</param>
    /// <param name="extentY">The cell's extent along y.</param>
    /// <param name="perUnitX">The physical pixels of a unit along x, signed.</param>
    /// <param name="perUnitY">The physical pixels of a unit along y, signed.</param>
    /// <param name="binArea">The area of a bin of the chosen level on the screen (<see cref="BinArea"/>).</param>
    public static double CellEstimate(long count, double extentX, double extentY, double perUnitX, double perUnitY, double binArea) =>
        Normalized(count, Math.Max(Math.Abs(extentX * perUnitX * (extentY * perUnitY)), binArea));

    /// <summary>
    /// Returns a count normalized to the points of a square physical pixel: the count over the area of its bin or cell on
    /// the screen (design §5.5.2 "広さで正規化する"), so that the same density has the same value whatever the bin's level
    /// and whatever the part's range.
    /// </summary>
    public static double Normalized(double count, double area) => area > 0 ? count / area : 0;

    /// <summary>
    /// Returns where a count lies from 0 to 1 under the top (design §5.5.2 "描き方"): log(1 + c) / log(1 + m), c / m, or
    /// ∛c / ∛m; 0 for a count of 0 or a top of 0.
    /// </summary>
    public static double Position(DensityNorm norm, double count, double top)
    {
        if (!(count > 0) || !(top > 0))
        {
            return 0;
        }

        var t = norm switch
        {
            DensityNorm.Linear => count / top,
            DensityNorm.Cbrt => Math.Cbrt(count) / Math.Cbrt(top),
            _ => Math.Log(1 + count) / Math.Log(1 + top),
        };
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Returns how the image command paints a tile whose bins cover an area on the screen (design §5.5.2 "広さで正規化する"):
    /// the transform's kind as the image's flags read it, its constant, and the domain's high end, so that the shader's
    /// value of a bin's count c equals the value of c / area under the top.
    /// </summary>
    /// <param name="norm">The layer's norm.</param>
    /// <param name="top">The top of the scale, in points a square physical pixel.</param>
    /// <param name="area">The area of a bin of the tile on the screen, in square physical pixels (<see cref="BinArea"/>).</param>
    public static (uint Kind, float Constant, float High) Paint(DensityNorm norm, double top, double area) =>
        norm switch
        {
            // Linear: c / (m · area).
            DensityNorm.Linear => (0u, 0f, (float)(top * area)),

            // Pow 1/3: ∛c / (∛m · ∛area).
            DensityNorm.Cbrt => (3u, 1f / 3, (float)(Math.Cbrt(top) * Math.Cbrt(area))),

            // Symlog of constant area: log10(1 + c / area) over log10(1 + m).
            _ => (2u, (float)area, (float)Math.Log10(1 + top)),
        };

    /// <summary>Returns the density's color table of a layer drawn in its own color: from transparent to the color.</summary>
    public static ColorMap Ramp(Color color) => new(color with { A = 0 }, color);

    /// <summary>Returns a vector's components in screen order: swapped when the plot is flipped.</summary>
    public static Vector2 Screen(double along, double across, bool flipped) =>
        flipped ? new Vector2((float)across, (float)along) : new Vector2((float)along, (float)across);
}
