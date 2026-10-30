using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// One axis while its ticks are chosen, and the ticks chosen (design §4.6): the scale's mapping over the axis length, the
/// labels' style and format, and, once chosen, each tick's pixel along the axis from its low end, its label, and the
/// domain that nice widened. The plan keeps one for x and one for y and reuses their buffers.
/// </summary>
/// <remarks>One thread at a time uses an axis, the plan's.</remarks>
internal sealed class TickAxis
{
    private readonly double[] _one = new double[1];
    private readonly double[] _oneMapped = new double[1];
    private readonly char[] _buffer = new char[TickFormat.BufferLength];
    private double[] _pixels = new double[16];
    private string[] _labels = new string[16];
    private float[] _extents = new float[16];
    private TickNumber[] _numbers = new TickNumber[16];
    private Int128[] _times = new Int128[16];
    private TimeInterval[] _boundaries = new TimeInterval[16];
    private int _count;

    /// <summary>Gets or sets the scale.</summary>
    public ContinuousScaleSpec Scale { get; set; } = null!;

    /// <summary>Gets or sets the scale's transform, or null for linear and time.</summary>
    public IScale? Transform { get; set; }

    /// <summary>Gets or sets the domain in the scale space, as learned (design §4.2).</summary>
    public ScaleRange Domain { get; set; }

    /// <summary>Gets or sets the axis length in pixels.</summary>
    public double Length { get; set; }

    /// <summary>Gets or sets the target spacing of the ticks in pixels: 1 / ρt.</summary>
    public double Spacing { get; set; }

    /// <summary>Gets or sets a value indicating whether a label's extent along the axis is its width (x) or the line height (y).</summary>
    public bool Horizontal { get; set; }

    /// <summary>Gets or sets the line height of the labels, their extent along a vertical axis.</summary>
    public float LineHeight { get; set; }

    /// <summary>Gets or sets the em of the labels: their size in pixels.</summary>
    public double Em { get; set; }

    /// <summary>Gets or sets the style the labels are measured and drawn with.</summary>
    public TextStyle Style { get; set; }

    /// <summary>Gets or sets the engine that measures the labels.</summary>
    public TextEngine Text { get; set; } = null!;

    /// <summary>Gets or sets the table of the labels' strings.</summary>
    public LabelTable Labels { get; set; } = null!;

    /// <summary>Gets the domain after nice: the learned one when nice is off or the scale's domain was given.</summary>
    public ScaleRange FinalDomain { get; private set; }

    /// <summary>Gets the number of ticks chosen; 0 when none fits.</summary>
    public int Count => _count;

    /// <summary>Gets the pixels of the ticks along the axis, from its low end, over <see cref="FinalDomain"/>.</summary>
    public ReadOnlySpan<double> Pixels => _pixels.AsSpan(0, _count);

    /// <summary>Gets the labels of the ticks; an empty string is not drawn.</summary>
    public ReadOnlySpan<string> TickLabels => _labels.AsSpan(0, _count);

    /// <summary>Gets each label's extent along the axis.</summary>
    public ReadOnlySpan<float> Extents => _extents.AsSpan(0, _count);

    /// <summary>
    /// Chooses the ticks of the scale over the axis (design §4.6), and widens a learned domain to the outer ticks when the
    /// scale asks for nice. Allocates only for labels the table has not seen.
    /// </summary>
    /// <param name="nice">Whether nice applies: the scale asks for it and its domain is learned.</param>
    public void Choose(bool nice)
    {
        _count = 0;
        FinalDomain = Domain;
        switch (Scale)
        {
            case TimeScaleSpec time:
                TimeTicks.Choose(this, time, nice);
                break;
            case LogScaleSpec:
                LogTicks.Choose(this, nice, constant: 0);
                break;
            case SymlogScaleSpec symlog:
                LogTicks.Choose(this, nice, symlog.Constant);
                break;
            default:
                ExtendedTicks.Choose(this, nice);
                break;
        }
    }

    /// <summary>Returns the data value of a point of the scale space: the inverse of the transform, or the point itself.</summary>
    internal double Inverse(double scaleValue)
    {
        if (Transform is null)
        {
            return scaleValue;
        }

        _one[0] = scaleValue;
        Transform.Inverse(_one, _oneMapped);
        return _oneMapped[0];
    }

    /// <summary>Returns the scale-space value of a data value: the transform's, or the value itself.</summary>
    internal double Forward(double value)
    {
        if (Transform is null)
        {
            return value;
        }

        _one[0] = value;
        Transform.Forward(_one, _oneMapped);
        return _oneMapped[0];
    }

    /// <summary>Returns a tick's pixel along the axis over a domain of the scale space.</summary>
    internal double Pixel(TickNumber tick, in ScaleRange domain)
    {
        var d = Transform is null ? tick.Difference(domain.Origin) : Forward(tick.Value) - domain.Origin;
        return (d - domain.Lower) / (domain.Upper - domain.Lower) * Length;
    }

    /// <summary>Returns a difference from the domain's origin's pixel along the axis.</summary>
    internal double PixelOfDifference(double d, in ScaleRange domain) => (d - domain.Lower) / (domain.Upper - domain.Lower) * Length;

    /// <summary>Starts a candidate set of ticks: the next adds go into the scratch slots.</summary>
    internal void ClearCandidate() => _count = 0;

    /// <summary>Adds a numeric tick to the candidate set.</summary>
    internal void AddNumber(TickNumber tick, double pixel)
    {
        Grow();
        _numbers[_count] = tick;
        _pixels[_count++] = pixel;
    }

    /// <summary>Adds a time tick to the candidate set: its instant in ns from 0001-01-01 and its boundary.</summary>
    internal void AddTime(Int128 instant, TimeInterval boundary, double pixel)
    {
        Grow();
        _times[_count] = instant;
        _boundaries[_count] = boundary;
        _pixels[_count++] = pixel;
    }

    /// <summary>Returns the numeric tick in a slot.</summary>
    internal TickNumber NumberAt(int index) => _numbers[index];

    /// <summary>Returns the instant of the time tick in a slot, in ns from 0001-01-01.</summary>
    internal Int128 InstantAt(int index) => _times[index];

    /// <summary>Sets the pixel of the tick in a slot.</summary>
    internal void SetPixel(int index, double pixel) => _pixels[index] = pixel;

    /// <summary>Sets the domain after nice.</summary>
    internal void SetFinalDomain(in ScaleRange domain) => FinalDomain = domain;

    /// <summary>
    /// Writes the candidate numeric ticks' labels (design §4.6) and returns the overlap value of Talbot et al. 2010 §4.4.4:
    /// −∞ when two labels overlap. <paramref name="sameDecimals"/> keeps the decimals alike over the axis (the extended
    /// search); otherwise each label takes its own (log, symlog).
    /// </summary>
    internal double LabelNumbers(bool sameDecimals)
    {
        var format = Scale.NumericFormat;
        if (format is null)
        {
            WriteDefaultNumbers(sameDecimals);
        }
        else
        {
            for (var i = 0; i < _count; i++)
            {
                _labels[i] = Call(format, _numbers[i].Value);
            }
        }

        return Measure();
    }

    /// <summary>Writes the candidate time ticks' labels and returns the overlap value.</summary>
    internal double LabelTimes(TimeScaleSpec scale, long epochTicks, long unitNanoseconds)
    {
        for (var i = 0; i < _count; i++)
        {
            var instant = _times[i];
            if (scale.TickFormat is { } format)
            {
                // The value in the column's unit: ⌊(instant − epoch) / unit⌋.
                var value = Int128.Clamp(FloorDivide(instant - ((Int128)epochTicks * 100), unitNanoseconds), long.MinValue, long.MaxValue);
                _labels[i] = CallTime(format, (long)value, _boundaries[i]);
            }
            else
            {
                var time = new DateTime((long)(instant / 100));
                var nanoseconds = (int)(instant % 100);
                if (!TickFormat.TryWriteTime(time, nanoseconds, _boundaries[i], _buffer, out var written))
                {
                    throw new InvalidOperationException($"The label of the time tick {time:O} does not fit {TickFormat.BufferLength} characters.");
                }

                _labels[i] = Labels.Get(_buffer.AsSpan(0, written));
            }
        }

        return Measure();
    }

    /// <summary>Returns the overlap value of the labels in the slots (Talbot et al. 2010 §4.4.4).</summary>
    internal double Measure()
    {
        for (var i = 0; i < _count; i++)
        {
            _extents[i] =
                _labels[i].Length == 0 ? 0
                : Horizontal ? Text.Measure(_labels[i], Style).Width
                : LineHeight;
        }

        var worst = 1.0;
        for (var i = 1; i < _count; i++)
        {
            var gap = Math.Abs(_pixels[i] - _pixels[i - 1]) - ((_extents[i] + _extents[i - 1]) / 2.0);
            var value =
                gap >= 1.5 * Em ? 1
                : gap > 0 ? 2 - (1.5 * Em / gap)
                : double.NegativeInfinity;
            worst = Math.Min(worst, value);
        }

        return worst;
    }

    private static Int128 FloorDivide(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private void WriteDefaultNumbers(bool sameDecimals)
    {
        var decimals = 0;
        var fixedLength = 0;
        for (var i = 0; i < _count; i++)
        {
            decimals = Math.Max(decimals, TickFormat.Decimals(_numbers[i]));
        }

        for (var i = 0; i < _count; i++)
        {
            var own = sameDecimals ? decimals : TickFormat.Decimals(_numbers[i]);
            fixedLength = Math.Max(fixedLength, TickFormat.FixedLength(_numbers[i], own));
        }

        if (fixedLength <= TickFormat.MaxFixedLength)
        {
            for (var i = 0; i < _count; i++)
            {
                var own = sameDecimals ? decimals : TickFormat.Decimals(_numbers[i]);
                _ = TickFormat.TryWriteFixed(_numbers[i], own, _buffer, out var written);
                _labels[i] = Labels.Get(_buffer.AsSpan(0, written));
            }

            return;
        }

        // The exponent form: the largest magnitude of the axis, and the mantissa's decimals the axis needs.
        var exponent = int.MinValue;
        for (var i = 0; i < _count; i++)
        {
            if (_numbers[i].Mantissa != 0)
            {
                exponent = Math.Max(exponent, TickFormat.Magnitude(_numbers[i]));
            }
        }

        var mantissaDecimals = 0;
        for (var i = 0; i < _count; i++)
        {
            var shifted = new TickNumber(_numbers[i].Mantissa, _numbers[i].Exponent - exponent);
            mantissaDecimals = Math.Max(mantissaDecimals, TickFormat.Decimals(shifted));
        }

        for (var i = 0; i < _count; i++)
        {
            // An axis of alike decimals shares the largest magnitude; a log axis writes each label with its own (1e-30, 1e30).
            var shared = sameDecimals || _numbers[i].Mantissa == 0;
            var own = shared ? exponent : TickFormat.Magnitude(_numbers[i]);
            var places = sameDecimals ? mantissaDecimals : TickFormat.Decimals(new TickNumber(_numbers[i].Mantissa, _numbers[i].Exponent - own));
            if (!TickFormat.TryWriteExponent(_numbers[i], own, places, _buffer, out var written))
            {
                throw new InvalidOperationException($"The label of the tick {_numbers[i].Value} does not fit {TickFormat.BufferLength} characters.");
            }

            _labels[i] = Labels.Get(_buffer.AsSpan(0, written));
        }
    }

    private string Call(TickFormatter format, double value)
    {
        if (!format(value, _buffer, out var written) || written < 0 || written > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"The TickFormat of the {Scale.GetType().Name} wrote no label for {value}: it returned false or a length outside 0 to {TickFormat.BufferLength}."
            );
        }

        return Labels.Get(_buffer.AsSpan(0, written));
    }

    private string CallTime(TimeTickFormatter format, long value, TimeInterval boundary)
    {
        if (!format(value, boundary, _buffer, out var written) || written < 0 || written > _buffer.Length)
        {
            throw new InvalidOperationException(
                $"The TickFormat of the {Scale.GetType().Name} wrote no label for {value}: it returned false or a length outside 0 to {TickFormat.BufferLength}."
            );
        }

        return Labels.Get(_buffer.AsSpan(0, written));
    }

    private void Grow()
    {
        if (_count < _pixels.Length)
        {
            return;
        }

        var size = _pixels.Length * 2;
        Array.Resize(ref _pixels, size);
        Array.Resize(ref _labels, size);
        Array.Resize(ref _extents, size);
        Array.Resize(ref _numbers, size);
        Array.Resize(ref _times, size);
        Array.Resize(ref _boundaries, size);
    }
}
