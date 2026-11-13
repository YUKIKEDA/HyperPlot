using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The pages of points a backend keeps on the GPU (design §5.1 "生の点のページ", §5.5.2, row R3a): a page is a run of 65,536
/// points of a spatial snapshot's copy, or the rest of it, 12 bytes a point. Core decides which pages stay, by the byte
/// budget it shares with the tiles and least-recently-used order, and reads the pages a frame sends; the backend makes a
/// buffer for each and draws from it. A page holds its points in steps of <see cref="Stride"/> through the copy's order,
/// so a page is drawn whole.
/// </summary>
/// <remarks>One thread at a time. After the staging has grown to a frame's size, a request allocates nothing.</remarks>
internal sealed class PointPages
{
    /// <summary>The points of a page: the last page of a copy holds the rest.</summary>
    internal const int PagePoints = 65_536;

    /// <summary>The bytes of a point on the GPU.</summary>
    internal const int PointBytes = 12;

    /// <summary>
    /// The step through a page's points in the copy's order: point k of the page on the GPU is point k × 104,729 modulo the
    /// page's count. The step is a prime above the points of a page, so every point is taken once.
    /// </summary>
    /// <remarks>
    /// Measured, not explained: 1e7 discs of 6 pixels at alpha 0.6 took 15.9 ms of GPU time a frame in the copy's order,
    /// where a point's neighbors lie within a pixel of it, and 11.3 to 12.0 ms in steps through the page (RTX 3060 Ti,
    /// 1,920 × 1,080; Issue #438). Steps within less than a page and across pages were slower, and what in the GPU makes
    /// the difference was not found (docs/benchmarks/r3a-points.md).
    /// </remarks>
    internal const int Stride = 104_729;

    private readonly ResidencyCache _owner;
    private readonly Dictionary<Key, int> _slots = [];
    private readonly List<PointUpload> _uploads = [];
    private readonly List<int> _dropped = [];
    private readonly ResidentPoint[] _ordered = new ResidentPoint[PagePoints];
    private Key?[] _keys = [];
    private long[] _lastUsed = [];
    private int[] _counts = [];
    private ResidentPoint[] _staging = new ResidentPoint[ResidencyCache.FrameUpload / PointBytes];
    private int _staged;

    internal PointPages(ResidencyCache owner) => _owner = owner;

    /// <summary>Gets the bytes of the pages that are on the GPU or staged for it.</summary>
    internal long BytesInUse { get; private set; }

    /// <summary>Gets the bytes the current frame sends.</summary>
    internal long BytesThisFrame => (long)_staged * PointBytes;

    /// <summary>Gets the pages to send before the frame draws: the backend makes a buffer for each slot.</summary>
    internal ReadOnlySpan<PointUpload> Uploads => CollectionsMarshal.AsSpan(_uploads);

    /// <summary>Gets the slots whose pages were let go of since the last <see cref="Sent"/>: the backend releases their buffers before it sends.</summary>
    internal ReadOnlySpan<int> Dropped => CollectionsMarshal.AsSpan(_dropped);

    /// <summary>Gets the number of pages on the GPU or staged for it. Tests only.</summary>
    internal int PagesInUse => _slots.Count;

    /// <summary>Returns the number of points of a page of a snapshot.</summary>
    internal static int PointsOf(SpatialSnapshot snapshot, int page) => (int)Math.Min(PagePoints, snapshot.PointCount - ((long)page * PagePoints));

    /// <summary>Returns the staged points of an upload.</summary>
    internal ReadOnlySpan<ResidentPoint> Staged(in PointUpload upload) => _staging.AsSpan(upload.Offset, upload.Count);

    /// <summary>
    /// Returns the slot of a page, reading it from the snapshot's copy when it is not there and the frame's budget allows:
    /// the slot, or −1 when it is not there and cannot be sent this frame. A page used this frame is never let go of for
    /// another this frame.
    /// </summary>
    /// <param name="snapshot">The full snapshot, which the caller borrows.</param>
    /// <param name="page">The page: the points from page × 65,536.</param>
    internal int Request(SpatialSnapshot snapshot, int page)
    {
        var key = new Key(snapshot, page);
        var frame = _owner.Frame;
        if (_slots.TryGetValue(key, out var slot))
        {
            _lastUsed[slot] = frame;
            return slot;
        }

        var count = PointsOf(snapshot, page);
        var bytes = (long)count * PointBytes;
        if (!_owner.Unbounded && _owner.BytesThisFrame + bytes > ResidencyCache.FrameUpload)
        {
            return -1;
        }

        // Room in the budget: the pages used longest ago before this frame go first.
        while (_owner.BytesInUse + bytes > ResidencyCache.DefaultBudget)
        {
            var (oldest, when) = (-1, frame);
            for (var s = 0; s < _keys.Length; s++)
            {
                if (_keys[s] is not null && _lastUsed[s] < when)
                {
                    (oldest, when) = (s, _lastUsed[s]);
                }
            }

            if (oldest < 0)
            {
                return -1;
            }

            Drop(oldest);
        }

        slot = Array.IndexOf(_keys, null);
        if (slot < 0)
        {
            slot = _keys.Length;
            var length = Math.Max(16, slot * 2);
            Array.Resize(ref _keys, length);
            Array.Resize(ref _lastUsed, length);
            Array.Resize(ref _counts, length);
        }

        if (_staged + count > _staging.Length)
        {
            Array.Resize(ref _staging, Math.Max(_staged + count, _staging.Length * 2));
        }

        var ordered = _ordered.AsSpan(0, count);
        snapshot.ReadBlock((long)page * PagePoints, ordered);
        var staged = _staging.AsSpan(_staged, count);

        // Spike R3b2: the page in runs of the copy's order, each in steps alone: runs of SplitProbe.BlockPoints points when
        // set, otherwise SplitProbe.ShuffleBlocks equal runs; one run is main's order.
        var size = SplitProbe.BlockPoints;
        var blocks = size > 0 ? (count + size - 1) / size : Math.Min(SplitProbe.ShuffleBlocks, count);
        for (var b = 0; b < blocks; b++)
        {
            var (start, end) =
                size > 0 ? (b * size, Math.Min(count, (b + 1) * size)) : ((int)((long)count * b / blocks), (int)((long)count * (b + 1) / blocks));
            var length = end - start;
            var from = 0;
            for (var k = 0; k < length; k++)
            {
                staged[start + k] = ordered[start + from];
                from = (int)((from + (long)Stride) % length);
            }
        }

        _uploads.Add(new PointUpload(slot, _staged, count));
        _staged += count;
        (_keys[slot], _lastUsed[slot], _counts[slot]) = (key, frame, count);
        _slots.Add(key, slot);
        BytesInUse += bytes;

        // A slot let go of and taken again in one frame is not released after its new buffer is made.
        _ = _dropped.Remove(slot);
        return slot;
    }

    /// <summary>Lets go of every page of a snapshot the caller gives back: its copy is no longer read.</summary>
    internal void Forget(SpatialSnapshot snapshot)
    {
        for (var s = 0; s < _keys.Length; s++)
        {
            if (_keys[s] is { } key && ReferenceEquals(key.Snapshot, snapshot))
            {
                Drop(s);
            }
        }
    }

    /// <summary>Says that the backend made the buffers of the uploads and released the dropped ones: the staging is free again.</summary>
    internal void Sent()
    {
        _uploads.Clear();
        _dropped.Clear();
        _staged = 0;
    }

    /// <summary>Forgets every page after the device was lost: they are read again from Core (invariant 10).</summary>
    internal void Reset()
    {
        _slots.Clear();
        Array.Clear(_keys);
        BytesInUse = 0;
        Sent();
    }

    private void Drop(int slot)
    {
        // A page staged this frame and dropped again is not sent.
        for (var u = _uploads.Count - 1; u >= 0; u--)
        {
            if (_uploads[u].Slot == slot)
            {
                _uploads.RemoveAt(u);
            }
        }

        _ = _slots.Remove(_keys[slot]!.Value);
        _keys[slot] = null;
        BytesInUse -= (long)_counts[slot] * PointBytes;
        _dropped.Add(slot);
    }

    /// <summary>A page to send: its slot, where its points begin in the staging, and how many they are.</summary>
    internal readonly record struct PointUpload(int Slot, int Offset, int Count);

    private readonly record struct Key(SpatialSnapshot Snapshot, int Page);
}
