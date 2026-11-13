using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <content>
/// The measurement (design §6.5 "計測", requirement I-6, row I4b, Issue #390): its value, the statistics it reads, its notice,
/// and what it draws.
/// </content>
public sealed partial class PlotEngine
{
    // The measurement, the tool that made it (whose format writes its box), where its box goes, and the buffers its
    // statistics and its text are found in again without allocating.
    private readonly char[] _measureText = new char[HoverTool.TooltipLength];

    // The readout shaped outside the shaping cache: it is new in each frame of a drag (Issue #378).
    private readonly OwnedText _measureShaped = new();
    private Measurement2D? _measurement;
    private MeasureTool? _measureTool;
    private SeriesStatistics[] _statisticsBuffer = new SeriesStatistics[16];

    /// <summary>
    /// Occurs when the measurement changes (design §6.5, row I4b): a new one, cleared, a range's statistics found again with
    /// other values, or dropped for a spec whose mapping changed.
    /// </summary>
    public event Action? MeasurementChanged;

    /// <summary>
    /// Gets the measurement (design §6.5, row I4b, Issue #390 Decision 8): one an engine, as a tool or <see cref="Measure"/>
    /// made it, with its differences or its statistics; null when there is none. An unchanging value: a new one replaces it
    /// when the statistics change (Decision 16).
    /// </summary>
    public Measurement? Measurement
    {
        get
        {
            CheckThread();
            return _measurement;
        }
    }

    /// <summary>
    /// Measures from code (design §6.5, row I4b, requirement I-9): as <see cref="MeasureTool"/> does, the engine computes the
    /// differences or the statistics, replaces the measurement, and tells <see cref="MeasurementChanged"/>.
    /// </summary>
    /// <param name="measurement">A <see cref="Measurement2D"/> of a drawn panel that is not polar.</param>
    /// <exception cref="ArgumentNullException"><paramref name="measurement"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The measurement's panel is not drawn.</exception>
    /// <exception cref="ArgumentException">The measurement is not a <see cref="Measurement2D"/>, its panel is polar, or its points or range are not finite or reversed.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    public void Measure(Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        CheckPanel(measurement.Panel);
        if (measurement is not Measurement2D flat)
        {
            throw new ArgumentException("A 2D panel takes a Measurement2D.", nameof(measurement));
        }

        if (PanelOf(measurement.Panel).Polar is not null)
        {
            throw new ArgumentException(
                "A polar panel takes no measurement: its pixels do not run along its axes (design §6.5).",
                nameof(measurement)
            );
        }

        var good = flat.Kind switch
        {
            MeasureKind.Points => Measurement2D.Finite(flat.From.X)
                && Measurement2D.Finite(flat.From.Y)
                && Measurement2D.Finite(flat.To.X)
                && Measurement2D.Finite(flat.To.Y),
            MeasureKind.Range => Measurement2D.Finite(flat.X),
            _ => false,
        };
        if (!good)
        {
            throw new ArgumentException(
                $"The {flat.Kind} measurement's points or range are not finite, or the range is reversed (design §6.5).",
                nameof(measurement)
            );
        }

        Take(Computed(flat), null);
    }

    /// <summary>Clears the measurement (design §6.5, row I4b): tells <see cref="MeasurementChanged"/> when there was one.</summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public void ClearMeasurement()
    {
        CheckThread();
        _ = ClearMeasured();
    }

    /// <summary>
    /// Answers the statistics of a panel's series over an x range (design §6.5 "計測", row I4b, Issue #390 Decisions 2, 3, 4):
    /// each series' count, mean, least, and greatest y, in the order of the layers. On the UI thread in O(log N + 8,192); waits
    /// for no worker, and a part not indexed yet is answered provisionally. Allocates nothing.
    /// </summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x range, in the scale space.</param>
    /// <param name="results">Where the statistics go: as many as fit.</param>
    /// <returns>The number of series found: more than <paramref name="results"/> holds when it was too short.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="ArgumentException">The range is not finite, or reversed.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    public int Statistics(PanelId panel, ViewRange x, Span<SeriesStatistics> results)
    {
        CheckPanel(panel);
        if (!Measurement2D.Finite(x))
        {
            throw new ArgumentException("The x range is finite and not reversed.", nameof(x));
        }

        return PlanOf(panel.Plot).Statistics(panel.Plot, panel.Panel, x.ToScale(), results);
    }

    /// <summary>Measures between two pixels of a panel for a tool (row I4b): points, or the x range they span.</summary>
    internal void MeasureAt(MeasureTool tool, PanelId panel, Vector2 from, Vector2 to)
    {
        var (a, b) = (PixelToView(panel, from), PixelToView(panel, to));
        var measurement = tool.Kind == MeasureKind.Points ? Measurement2D.OfPoints(panel, a, b) : Measurement2D.OfRange(panel, Span(a.X, b.X));
        Take(Computed(measurement), tool);

        static ViewRange Span(in ViewRange one, in ViewRange other)
        {
            var (low, high) = one.LowValue <= other.LowValue ? (one, other) : (other, one);
            return new ViewRange(low.Origin, low.Lower, ScaleSpace.Difference(high.Origin, low.Origin) + high.Lower);
        }
    }

    /// <summary>Returns a measurement with its differences or its statistics (Issue #390 Decisions 3, 5).</summary>
    private Measurement2D Computed(Measurement2D measurement)
    {
        var plan = PlanOf(measurement.Panel.Plot);
        if (measurement.Kind == MeasureKind.Points)
        {
            var (from, to) = (measurement.From, measurement.To);
            var dx = plan.DataValue(ofX: true, to.X.Origin, to.X.Lower) - plan.DataValue(ofX: true, from.X.Origin, from.X.Lower);
            var dy = plan.DataValue(ofX: false, to.Y.Origin, to.Y.Lower) - plan.DataValue(ofX: false, from.Y.Origin, from.Y.Lower);
            if (plan.SecondsPerUnit(ofX: false) != 1 || plan.SecondsPerUnit(ofX: true) == 1)
            {
                return measurement.WithDeltas(dx, dy);
            }

            // A time axis's slope is a y unit a second (Decision 5): Δx stays in the axis's unit.
            var deltas = measurement.WithDeltas(dx * plan.SecondsPerUnit(ofX: true), dy);
            return measurement.WithDeltas(dx, dy).WithSlope(deltas.Slope);
        }

        var count = FindStatistics(measurement);
        return measurement.WithStatistics(_statisticsBuffer.AsSpan(0, count).ToArray());
    }

    /// <summary>Finds a range's statistics into the engine's buffer, grown once when the series outnumber it; returns their number.</summary>
    private int FindStatistics(Measurement2D measurement)
    {
        var plan = PlanOf(measurement.Panel.Plot);
        var count = plan.Statistics(measurement.Panel.Plot, measurement.Panel.Panel, measurement.X.ToScale(), _statisticsBuffer);
        if (count > _statisticsBuffer.Length)
        {
            _statisticsBuffer = new SeriesStatistics[count];
            count = plan.Statistics(measurement.Panel.Plot, measurement.Panel.Panel, measurement.X.ToScale(), _statisticsBuffer);
        }

        return count;
    }

    /// <summary>Takes a measurement and the tool that made it; tells it and asks for a frame.</summary>
    private void Take(Measurement2D measurement, MeasureTool? tool)
    {
        (_measurement, _measureTool) = (measurement, tool);
        RequestFrame();
        MeasurementChanged?.Invoke();
    }

    /// <summary>Clears the measurement; returns whether there was one.</summary>
    private bool ClearMeasured()
    {
        if (_measurement is null)
        {
            return false;
        }

        (_measurement, _measureTool) = (null, null);
        RequestFrame();
        MeasurementChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Finds a range's statistics again in a frame drawn again (Issue #390 Decisions 9, 10, 16): a new measurement and a notice
    /// only when the values changed.
    /// </summary>
    private void MeasureAgain()
    {
        if (
            _measurement is not { Kind: MeasureKind.Range } measured
            || measured.Panel.Plot >= PlotCount
            || measured.Panel.Panel >= _states[measured.Panel.Plot].Panels
        )
        {
            return;
        }

        var count = FindStatistics(measured);
        if (Measurement2D.Same(_statisticsBuffer.AsSpan(0, count), measured.Statistics.Span))
        {
            return;
        }

        _measurement = measured.WithStatistics(_statisticsBuffer.AsSpan(0, count).ToArray());
        MeasurementChanged?.Invoke();
    }

    /// <summary>Returns the middle of a panel's y range as a range of one point: where a range's box is placed.</summary>
    private ViewRange MiddleY(PanelId panel)
    {
        // The view drawn, read in place: CurrentView makes a new object, and a frame of the mouse alone allocates nothing.
        var y = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Shown[panel.Panel]).Y;
        var middle = (y.Lower + y.Upper) / 2;
        return new ViewRange(y.Origin, middle, middle);
    }

    /// <summary>
    /// Records the measurement (design §6.5, Issue #390 Decisions 6, 11): points as a line of 1 DIP between markers, a range
    /// as its x range in 15 % of the foreground across the panel, and its box at its end. Allocates nothing once warm.
    /// </summary>
    private void DrawMeasurement(RenderList list)
    {
        if (_measurement is not { } measured || measured.Panel.Plot >= PlotCount || measured.Panel.Panel >= _states[measured.Panel.Plot].Panels)
        {
            return;
        }

        var (guide, theme, plan) = (PanelOf(measured.Panel), SpecOf(measured.Panel.Plot).Theme, PlanOf(measured.Panel.Plot));
        var (left, top, width, height) = (guide.Left, guide.Top, guide.Width, guide.Height);
        var line = MathF.Max(1, MathF.Round(Scale));

        // The box goes at the end as the view shows it now (PR #395's review): the second point, or the range's end.
        Vector2 end;
        if (measured.Kind == MeasureKind.Points)
        {
            var (a, b) = (ViewToPixel(measured.Panel, measured.From.X, measured.From.Y), ViewToPixel(measured.Panel, measured.To.X, measured.To.Y));
            end = b;
            list.SetClip(left, top, width, height);
            list.DrawPolyline([a, b], new LineStyle(theme.Foreground, line));
            list.ResetClip();
            Marker(list, a, theme.Foreground, theme);
            Marker(list, b, theme.Foreground, theme);
        }
        else
        {
            var y = MiddleY(measured.Panel);
            var low = ViewToPixel(measured.Panel, measured.X with { Upper = measured.X.Lower }, y);
            var high = ViewToPixel(measured.Panel, measured.X with { Lower = measured.X.Upper }, y);
            end = high;
            var faint = theme.Foreground with { A = (byte)Math.Round(theme.Foreground.A * 0.15) };
            if (plan.Guides.Flipped)
            {
                var (y0, y1) = (Math.Clamp(MathF.Min(low.Y, high.Y), top, top + height), Math.Clamp(MathF.Max(low.Y, high.Y), top, top + height));
                list.DrawRect(left, y0, width, y1 - y0, faint);
            }
            else
            {
                var (x0, x1) = (Math.Clamp(MathF.Min(low.X, high.X), left, left + width), Math.Clamp(MathF.Max(low.X, high.X), left, left + width));
                list.DrawRect(x0, top, x1 - x0, height, faint);
            }
        }

        var length = _measureTool?.Format is null ? plan.WriteMeasurement(measured, _measureText) : 0;
        if (_measureTool?.Format is { } format)
        {
            if (!format(measured, _measureText, out length))
            {
                return;
            }

            if (length < 0 || length > _measureText.Length)
            {
                throw new InvalidOperationException($"The MeasureTool's Format wrote {length} characters: a length from 0 to {_measureText.Length}.");
            }
        }

        if (length > 0)
        {
            DrawBox(list, _measureText.AsSpan(0, length), _measureShaped, end, theme, (left, top, width, height), line);
        }
    }
}
