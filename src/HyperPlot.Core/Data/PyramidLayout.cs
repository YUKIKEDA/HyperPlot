namespace HyperPlot;

/// <summary>
/// Where each level of one chunk's pyramid block lives (design §3.3). Level <c>l</c> (0 to 4 here, levels 1 to 5 in the
/// design) has buckets of <c>64 · 8^l</c> elements; each bucket has a minimum, a maximum, and a 2-bit NaN state.
/// </summary>
internal sealed class PyramidLayout
{
    internal const int LevelCount = 5;

    internal const int FirstShift = 6;

    internal const int ShiftStep = 3;

    private readonly int[] _counts = new int[LevelCount];
    private readonly int[] _minOffsets = new int[LevelCount];
    private readonly int[] _maxOffsets = new int[LevelCount];
    private readonly int[] _stateOffsets = new int[LevelCount];

    internal PyramidLayout(int chunkLength, int elementSize)
    {
        ChunkLength = chunkLength;
        var offset = 0;
        for (var level = 0; level < LevelCount; level++)
        {
            var count = (int)(((long)chunkLength + BucketSize(level) - 1) >> Shift(level));
            _counts[level] = count;
            _minOffsets[level] = offset;
            offset += count * elementSize;
            _maxOffsets[level] = offset;
            offset += count * elementSize;
        }

        for (var level = 0; level < LevelCount; level++)
        {
            _stateOffsets[level] = offset;
            offset += (_counts[level] + 3) >> 2;
        }

        ByteCount = offset;
    }

    /// <summary>Gets the number of elements of the chunk the layout is for.</summary>
    internal int ChunkLength { get; }

    /// <summary>Gets the size of the block in bytes.</summary>
    internal int ByteCount { get; }

    internal static int Shift(int level) => FirstShift + (ShiftStep * level);

    internal static int BucketSize(int level) => 1 << Shift(level);

    internal int Count(int level) => _counts[level];

    internal int MinOffset(int level) => _minOffsets[level];

    internal int MaxOffset(int level) => _maxOffsets[level];

    internal int StateOffset(int level) => _stateOffsets[level];
}

/// <summary>The NaN state of a bucket (design §3.3), two bits per bucket.</summary>
internal enum BucketNaN : byte
{
    /// <summary>No value is NaN.</summary>
    None = 0,

    /// <summary>Some values are NaN, and some are not.</summary>
    Some = 1,

    /// <summary>Every value is NaN; the bucket's minimum and maximum are not used.</summary>
    All = 2,
}
