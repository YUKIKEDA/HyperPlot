namespace HyperPlot;

/// <summary>
/// Where each level of one chunk's pyramid block lives (design §3.3). Level <c>l</c> (0 to 4 here, levels 1 to 5 in the
/// design) has buckets of <c>64 · 8^l</c> elements; each bucket has a minimum, a maximum, and the count of its values that
/// are not NaN, in the narrowest width that holds the bucket's size (1, 2, 2, 2, and 4 bytes).
/// </summary>
internal sealed class PyramidLayout
{
    internal const int LevelCount = 5;

    internal const int FirstShift = 6;

    internal const int ShiftStep = 3;

    private readonly int[] _counts = new int[LevelCount];
    private readonly int[] _minOffsets = new int[LevelCount];
    private readonly int[] _maxOffsets = new int[LevelCount];
    private readonly int[] _numberOffsets = new int[LevelCount];

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

        // The counts come after the minima and maxima; the 4-byte counts of the top level come last and are aligned to 4.
        for (var level = 0; level < LevelCount; level++)
        {
            var width = NumberWidth(level);
            offset = (offset + width - 1) & -width;
            _numberOffsets[level] = offset;
            offset += _counts[level] * width;
        }

        ByteCount = offset;
    }

    /// <summary>Gets the number of elements of the chunk the layout is for.</summary>
    internal int ChunkLength { get; }

    /// <summary>Gets the size of the block in bytes.</summary>
    internal int ByteCount { get; }

    internal static int Shift(int level) => FirstShift + (ShiftStep * level);

    internal static int BucketSize(int level) => 1 << Shift(level);

    /// <summary>Returns the bytes of one bucket's count: the narrowest width that holds the bucket's size.</summary>
    internal static int NumberWidth(int level) =>
        level switch
        {
            0 => 1,
            4 => 4,
            _ => 2,
        };

    internal int Count(int level) => _counts[level];

    internal int MinOffset(int level) => _minOffsets[level];

    internal int MaxOffset(int level) => _maxOffsets[level];

    internal int NumberOffset(int level) => _numberOffsets[level];

    /// <summary>Returns the number of elements of a bucket; the last bucket of a level may be shorter.</summary>
    internal int BucketLength(int level, int bucket)
    {
        var start = bucket << Shift(level);
        return Math.Min(BucketSize(level), ChunkLength - start);
    }
}

/// <summary>The NaN state of a bucket (design §3.3), read from its count of values that are not NaN.</summary>
internal enum BucketNaN : byte
{
    /// <summary>No value is NaN: the count is the bucket's length.</summary>
    None = 0,

    /// <summary>Some values are NaN, and some are not.</summary>
    Some = 1,

    /// <summary>Every value is NaN: the count is 0, and the bucket's minimum and maximum are not used.</summary>
    All = 2,
}
