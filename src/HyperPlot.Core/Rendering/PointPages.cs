using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The pages of points a backend keeps on the GPU (design §5.1 "生の点のページ", §5.5.2, row R3a): a page is a run of 65,536
/// points of a spatial snapshot's copy, or the rest of it, 12 bytes a point. Core decides which pages stay, by the byte
/// budget it shares with the tiles and one least-recently-used order over the tiles and the pages (design §5.5.2 "1 つの
/// LRU"), and the frame sends the pages the workers have read; the
/// backend makes a buffer for each and draws from it. A page is cut into blocks of a size its layer's parts set (row R3b2,
/// design §5.5.2 "ブロック"), and holds each block's points in steps of <see cref="Stride"/> through the copy's order, so a
/// page is drawn by whole blocks.
/// </summary>
/// <remarks>
/// The frame's thread never reads a copy (design §5.5.2 "ページの読み出し", row R3b2): a page not on the GPU takes one of
/// <see cref="ReadSlots"/> reads, a worker of the snapshot's index's pool reads it into the staging, and a later frame sends
/// it. The members other than the readers' are called by one thread at a time. Allocates nothing after the first request,
/// but for the first request of a pool, which registers the pool's reader.
/// </remarks>
internal sealed class PointPages
{
    /// <summary>The points of a page: the last page of a copy holds the rest.</summary>
    internal const int PagePoints = 65_536;

    /// <summary>The bytes of a point on the GPU.</summary>
    internal const int PointBytes = 12;

    /// <summary>The most points of a block: half a page, the block of a layer of one part.</summary>
    internal const int MaxBlockPoints = 32_768;

    /// <summary>The fewest points of a block that is not a page's last: the block of a layer of 64 parts or more.</summary>
    internal const int MinBlockPoints = 512;

    /// <summary>
    /// The reads asked for, being read, and read but not yet sent, at once (design §5.5.2 "同時に読む数とステージング"): two
    /// frames of <see cref="ResidencyCache.FrameUpload"/>, so that a copy not in the page cache still sends five pages a frame.
    /// One set a cache, shared by every plot and layer that draws with it, whatever their pools.
    /// </summary>
    internal const int ReadSlots = 10;

    /// <summary>The points of the staging: a page to read into and a page to send from, for each read.</summary>
    internal const int StagingPoints = 2 * ReadSlots * PagePoints;

    /// <summary>
    /// The step through a block's points in the copy's order: point k of the block on the GPU is point k × 104,729 modulo
    /// the block's count. The step is a prime above the points of a block, so every point is taken once.
    /// </summary>
    /// <remarks>
    /// Measured, not explained: 1e7 discs of 6 pixels at alpha 0.6 took 15.9 ms of GPU time a frame in the copy's order,
    /// where a point's neighbors lie within a pixel of it, and 11.3 to 12.0 ms in steps through the page (RTX 3060 Ti,
    /// 1,920 × 1,080; Issue #438, docs/benchmarks/r3a-points.md). A layer of 8 parts took 15.3 to 16.1 ms in steps through
    /// the page and 9.9 ms in steps through blocks of 4,096 points (Issue #472, sweep 1). What in the GPU makes the
    /// differences was not found.
    /// </remarks>
    internal const int Stride = 104_729;

    // The states of a read (design §5.5.2 "読みの場所の状態"): the frame's thread moves Free to Asked and back, and Read to
    // Free; a worker moves Asked to Reading, winning the exchange, and Reading to Read.
    private const int Free = 0;
    private const int Asked = 1;
    private const int Reading = 2;
    private const int Read = 3;

    private readonly ResidencyCache _owner;
    private readonly Dictionary<Key, int> _slots = [];
    private readonly List<PointUpload> _uploads = [];
    private readonly List<int> _dropped = [];
    private readonly ReadSlot[] _reads = new ReadSlot[ReadSlots];
    private readonly List<PageReader> _readers = [];

    // The headless render waits on it for the reads in flight; a worker pulses it when one ends and someone waits.
    private readonly object _readGate = new();
    private Key?[] _keys = [];
    private long[] _lastUsed = [];

    // The release (ResidencyCache.Releases) at which each slot was last let go of, which a kept list checks (design §5.5.2
    // "予算").
    private long[] _lastFreed = [];
    private int[] _counts = [];
    private ResidentPoint[]? _staging;
    private int _staged;
    private int _waiters;

    // Written by the frame's thread, read by the workers: volatile reads and writes.
    private bool _detached;

    internal PointPages(ResidencyCache owner)
    {
        _owner = owner;
        for (var r = 0; r < _reads.Length; r++)
        {
            _reads[r] = new ReadSlot();
        }
    }

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

    /// <summary>Gets the bytes of the staging, 0 before the first request (design §5.5.2 "同時に読む数とステージング"). Tests only.</summary>
    internal long StagingBytes => (_staging?.LongLength ?? 0) * PointBytes;

    /// <summary>Gets the number of pools a reader is registered with. Tests only.</summary>
    internal int ReaderCount => _readers.Count;

    /// <summary>Gets the number of reads asked for, being read, or read and not yet sent. Tests only.</summary>
    internal int ReadsInUse
    {
        get
        {
            var count = 0;
            foreach (var read in _reads)
            {
                count += Volatile.Read(ref read.State) != Free ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Gets the number of requests of the current frame that found their page not on the GPU.</summary>
    internal int MissedThisFrame { get; private set; }

    /// <summary>Gets the number of pages the current frame staged to send.</summary>
    internal int ScheduledThisFrame { get; private set; }

    /// <summary>Gets the number of pages on the GPU or staged for it that the current frame used.</summary>
    internal int UsedThisFrame { get; private set; }

    /// <summary>
    /// Gets the number of requests of the current frame that searched the reads or the slots for a page not on the GPU
    /// (design §5.5.2 "予算が満ちたフレーム"): none once the frame is known full. Tests only.
    /// </summary>
    internal int SearchesThisFrame { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether a recording draws every block of each page it draws a block of, in view or
    /// not: the form the blocks in view are compared with (design §9 "重なりと予算", §10 R3b2 row). Tests and benchmarks
    /// only.
    /// </summary>
    internal bool DrawsWholePages { get; set; }

    /// <summary>
    /// Gets or sets a hook a worker calls before it reads a page, with the snapshot and the page: a test holds the reads in
    /// it, or makes one throw. Tests only.
    /// </summary>
    internal Action<SpatialSnapshot, int>? TestRead { get; set; }

    /// <summary>
    /// Returns the points of a block of a layer's pages (design §5.5.2 "ブロック"): 32,768 over the parts, rounded up to a
    /// power of two, from 512 to 32,768. A block's size divides a page's, so no block crosses a page.
    /// </summary>
    /// <param name="parts">The parts of the layer that lie over one another in a panel, 1 or more.</param>
    internal static int BlockPoints(int parts)
    {
        var block = MinBlockPoints;
        while (block < MaxBlockPoints && (long)block * parts < MaxBlockPoints)
        {
            block *= 2;
        }

        return block;
    }

    /// <summary>Returns the number of points of a page of a snapshot.</summary>
    internal static int PointsOf(SpatialSnapshot snapshot, int page) => (int)Math.Min(PagePoints, snapshot.PointCount - ((long)page * PagePoints));

    /// <summary>Returns the staged points of an upload.</summary>
    internal ReadOnlySpan<ResidentPoint> Staged(in PointUpload upload) => _staging.AsSpan(upload.Offset, upload.Count);

    /// <summary>
    /// Returns the slot of a page, or −1 when it is not on the GPU and cannot be sent this frame (design §5.5.2 "ページの
    /// 読み出し"). A page a worker has read is sent when the frame's upload and the budget allow; a page not read yet takes a
    /// free read, which a worker of the snapshot's index's pool reads, and is sent in a later frame. A page used this frame
    /// is never let go of for another this frame. Once the frame is known full, a page not on the GPU is −1 at once, with no
    /// search (design §5.5.2 "予算が満ちたフレーム").
    /// </summary>
    /// <param name="snapshot">The full snapshot, which the caller borrows.</param>
    /// <param name="page">The page: the points from page × 65,536.</param>
    /// <param name="block">
    /// The points of a block, from <see cref="BlockPoints"/>: the page is put in steps block by block, and the page's last
    /// block holds the rest. A page asked for with another block is another page, and the first is let go of as any page
    /// not used.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="block"/> is 0 or less.</exception>
    internal int Request(SpatialSnapshot snapshot, int page, int block)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(block);
        var key = new Key(snapshot, page, block);
        var frame = _owner.Frame;
        if (_slots.TryGetValue(key, out var slot))
        {
            if (_lastUsed[slot] != frame)
            {
                _lastUsed[slot] = frame;
                UsedThisFrame++;
            }

            return slot;
        }

        // A frame known full sends no page more, so the rest of its requests search for none (design §5.5.2 "予算が満ちた
        // フレーム"): every tile and page is this frame's, and the budget has no room for one.
        MissedThisFrame++;
        if (_owner.IsFull((long)PointsOf(snapshot, page) * PointBytes))
        {
            return -1;
        }

        SearchesThisFrame++;
        var r = FindRead(snapshot, page, block);
        if (r < 0)
        {
            Ask(key, frame);
            return -1;
        }

        var read = _reads[r];
        read.Wanted = frame;
        if (Volatile.Read(ref read.State) != Read)
        {
            return -1;
        }

        if (read.Failure is not null)
        {
            // The index records the failure (design §5.5.2 "読みの例外"): the layer fails at its next poll.
            read.Release();
            return -1;
        }

        if (read.Lost || read.Generation != read.AskedGeneration)
        {
            // Read for a snapshot given back, or before the device was lost: asked again.
            read.Release();
            Ask(key, frame);
            return -1;
        }

        var bytes = (long)read.Count * PointBytes;
        if (!_owner.Unbounded && _owner.BytesThisFrame + bytes > ResidencyCache.FrameUpload)
        {
            read.Refused = frame;
            return -1;
        }

        // Room in the budget: the tiles and the pages used longest ago before this frame go first, of any kind (design
        // §5.5.2 "1 つの LRU").
        if (!_owner.MakeRoom(bytes))
        {
            read.Refused = frame;
            return -1;
        }

        slot = Array.IndexOf(_keys, null);
        if (slot < 0)
        {
            slot = _keys.Length;
            var length = Math.Max(16, slot * 2);
            Array.Resize(ref _keys, length);
            Array.Resize(ref _lastUsed, length);
            Array.Resize(ref _lastFreed, length);
            Array.Resize(ref _counts, length);
        }

        // The read stays taken until the backend has sent the page from its staging.
        read.Scheduled = true;
        ScheduledThisFrame++;
        MissedThisFrame--;
        _uploads.Add(new PointUpload(slot, DestinationOf(r), read.Count));
        _staged += read.Count;
        (_keys[slot], _lastUsed[slot], _counts[slot]) = (key, frame, read.Count);
        _slots.Add(key, slot);
        BytesInUse += bytes;
        UsedThisFrame++;

        // A slot let go of and taken again in one frame is not released after its new buffer is made.
        _ = _dropped.Remove(slot);
        return slot;
    }

    /// <summary>
    /// Starts a frame (design §5.5.2 "要らなくなった読み"): a read asked for that the last frame did not ask for again is
    /// withdrawn, and a page read that the last frame did not ask for, or that is stale or failed, is let go of unsent.
    /// </summary>
    internal void BeginFrame()
    {
        var last = _owner.Frame - 1;
        (MissedThisFrame, ScheduledThisFrame, UsedThisFrame, SearchesThisFrame) = (0, 0, 0, 0);
        foreach (var read in _reads)
        {
            var state = Volatile.Read(ref read.State);
            if (state == Asked && read.Wanted < last)
            {
                _ = read.Withdraw();
            }
            else if (
                state == Read
                && !read.Scheduled
                && (read.Wanted < last || read.Failure is not null || read.Lost || read.Generation != read.AskedGeneration)
            )
            {
                read.Release();
            }
        }
    }

    /// <summary>
    /// Lets go of every page of a snapshot the caller gives back: its copy is no longer read. A read of it asked for is
    /// withdrawn, one read is let go of unsent, and one being read is sent nowhere when it ends (design §5.5.2 "要らなくなった
    /// 読み"). Waits for no read.
    /// </summary>
    internal void Forget(SpatialSnapshot snapshot)
    {
        for (var s = 0; s < _keys.Length; s++)
        {
            if (_keys[s] is { } key && ReferenceEquals(key.Snapshot, snapshot))
            {
                Drop(s);
            }
        }

        foreach (var read in _reads)
        {
            if (Volatile.Read(ref read.State) != Free && ReferenceEquals(read.Key.Snapshot, snapshot))
            {
                Forget(read);
            }
        }
    }

    /// <summary>Says that the backend made the buffers of the uploads and released the dropped ones: the staging is free again.</summary>
    internal void Sent()
    {
        foreach (var read in _reads)
        {
            if (read.Scheduled)
            {
                read.Release();
            }
        }

        _uploads.Clear();
        _dropped.Clear();
        _staged = 0;
    }

    /// <summary>
    /// Forgets every page after the device was lost: they are read again from Core (invariant 10). A read asked for is
    /// withdrawn, and a read being read is sent nowhere when it ends.
    /// </summary>
    internal void Reset()
    {
        _slots.Clear();
        Array.Clear(_keys);
        (BytesInUse, UsedThisFrame) = (0, 0);
        foreach (var read in _reads)
        {
            Forget(read);
        }

        Sent();
    }

    /// <summary>
    /// Waits until no read is asked for or being read, and returns whether a render that waits draws again to send more
    /// (design §5.5.2 "ヘッドレス"): reads were in flight, a page read is there that this frame did not refuse, or this frame
    /// sent pages and missed others. A frame that sent none, refused every page read, and has none in flight gets no more,
    /// as when the budget holds no more pages.
    /// </summary>
    internal bool AwaitReads()
    {
        var waited = false;
        Interlocked.Increment(ref _waiters);
        try
        {
            lock (_readGate)
            {
                while (InFlight())
                {
                    waited = true;
                    _ = Monitor.Wait(_readGate);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }

        return waited || Ready() || (ScheduledThisFrame > 0 && MissedThisFrame > 0);
    }

    /// <summary>
    /// Takes the readers off their pools once the cache is let go of (design §5.5.2 "ページの読み出し"): no worker reads for
    /// it again. A read asked for and not taken is withdrawn, so that a wait for the reads does not wait for it; a read in
    /// flight ends into the staging, which nothing sends. Waits for no read.
    /// </summary>
    internal void Detach()
    {
        Volatile.Write(ref _detached, true);
        foreach (var reader in _readers)
        {
            reader.Pool.Unregister(reader);
        }

        _readers.Clear();
        foreach (var read in _reads)
        {
            if (read.Withdraw())
            {
                Woken();
            }
        }
    }

    /// <summary>Returns the slot of the page used longest ago before a frame, and its frame; −1 when every page was used since.</summary>
    internal int Oldest(long before, out long when)
    {
        var oldest = -1;
        when = before;
        for (var s = 0; s < _keys.Length; s++)
        {
            if (_keys[s] is not null && _lastUsed[s] < when)
            {
                (oldest, when) = (s, _lastUsed[s]);
            }
        }

        return oldest;
    }

    /// <summary>Lets go of the page in a slot for the budget (design §5.5.2 "1 つの LRU"); the backend releases its buffer.</summary>
    internal void Evict(int slot) => Drop(slot);

    /// <summary>
    /// Returns whether a slot still holds the page it held when <see cref="ResidencyCache.Releases"/> was a count (design
    /// §5.5.2 "予算", Issue #636): a page is there, and the slot was not let go of after that release. Searches nothing.
    /// </summary>
    internal bool Holds(int slot, long since) => slot >= 0 && slot < _keys.Length && _keys[slot] is not null && _lastFreed[slot] <= since;

    /// <summary>Marks a slot that holds a page as used in this frame, so that the page is not let go of for another in it.</summary>
    internal void Keep(int slot)
    {
        var frame = _owner.Frame;
        if (_lastUsed[slot] != frame)
        {
            _lastUsed[slot] = frame;
            UsedThisFrame++;
        }
    }

    // Where a read's points go: the page it reads into first, and the page it sends from after it.
    private static int SourceOf(int read) => 2 * read * PagePoints;

    private static int DestinationOf(int read) => ((2 * read) + 1) * PagePoints;

    private static void Forget(ReadSlot read)
    {
        read.Generation++;
        var state = Volatile.Read(ref read.State);
        if (state == Asked)
        {
            _ = read.Withdraw();
        }
        else if (state == Read)
        {
            read.Release();
        }
    }

    // A frame's points of a page, block by block in steps through the copy's order: the last block holds the rest.
    private static void Place(ReadOnlySpan<ResidentPoint> ordered, Span<ResidentPoint> staged, int block)
    {
        for (var start = 0; start < ordered.Length; start += block)
        {
            var length = Math.Min(block, ordered.Length - start);
            var from = 0;
            for (var k = 0; k < length; k++)
            {
                staged[start + k] = ordered[start + from];
                from = (int)((from + (long)Stride) % length);
            }
        }
    }

    private int FindRead(SpatialSnapshot snapshot, int page, int block)
    {
        for (var r = 0; r < _reads.Length; r++)
        {
            var read = _reads[r];
            if (
                Volatile.Read(ref read.State) != Free
                && ReferenceEquals(read.Key.Snapshot, snapshot)
                && read.Key.Page == page
                && read.Key.Block == block
            )
            {
                return r;
            }
        }

        return -1;
    }

    // Takes a free read for a page, if one is, for a worker of the snapshot's index's pool: the request goes to the next
    // frame otherwise (design §5.5.2 "同時に読む数とステージング").
    private void Ask(in Key key, long frame)
    {
        ReadSlot? free = null;
        foreach (var read in _reads)
        {
            if (Volatile.Read(ref read.State) == Free)
            {
                free = read;
                break;
            }
        }

        var pool = key.Snapshot.Index.Pool;
        if (free is null || !Readies(pool))
        {
            return;
        }

        // The staging, made once (design §5.5.2): a page to read into and a page to send from, for each read.
        _staging ??= new ResidentPoint[StagingPoints];
        (free.Key, free.Pool, free.Count, free.Wanted) = (key, pool, PointsOf(key.Snapshot, key.Page), frame);
        (free.AskedGeneration, free.Failure, free.Lost) = (free.Generation, null, false);

        // The fields are written before the state: a worker that sees the request sees them. The exchange is a full barrier
        // before the pool's flag is read, as the pool's disposal writes the flag before it abandons the reads: if the
        // disposal began meanwhile, either it sees the request, or this sees the flag and abandons the request itself.
        _ = Interlocked.Exchange(ref free.State, Asked);
        if (pool.IsDisposed)
        {
            Abandon(pool);
            return;
        }

        pool.Wake();
    }

    // The pool's reader, registered the first time a page of its snapshots is asked for (design §5.5.2 "プール"): once a
    // pool, since a registration makes the pool's array of works again. False for a pool disposed, whose reader is
    // registered or not, and for a cache let go of: neither reads anything.
    private bool Readies(PlotWorkerPool pool)
    {
        if (Volatile.Read(ref _detached) || pool.IsDisposed)
        {
            return false;
        }

        foreach (var reader in _readers)
        {
            if (reader.Pool == pool)
            {
                return true;
            }
        }

        var made = new PageReader(this, pool);
        if (!pool.Register(made))
        {
            return false;
        }

        _readers.Add(made);
        return true;
    }

    // A page read that the next recording can send: one this frame did not try and refuse for the frame's upload or the budget.
    private bool Ready()
    {
        foreach (var read in _reads)
        {
            if (Volatile.Read(ref read.State) == Read && !read.Scheduled && read.Refused != _owner.Frame)
            {
                return true;
            }
        }

        return false;
    }

    private bool InFlight()
    {
        foreach (var read in _reads)
        {
            var state = Volatile.Read(ref read.State);
            if (state is Asked or Reading)
            {
                return true;
            }
        }

        return false;
    }

    // A worker's turn: the first read asked for of a snapshot of its pool, taken by the exchange from Asked to Reading.
    private int Take(PlotWorkerPool pool)
    {
        if (Volatile.Read(ref _detached))
        {
            return -1;
        }

        for (var r = 0; r < _reads.Length; r++)
        {
            var read = _reads[r];
            if (Volatile.Read(ref read.State) != Asked || read.Pool != pool)
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref read.State, Reading, Asked) != Asked)
            {
                continue;
            }

            // The frame may have withdrawn the request and asked for another of another pool before the exchange: the
            // fields are read again now that they cannot change. Put back, the request's pool is woken, since its workers
            // may have passed the read over while this one held it.
            var owner = read.Pool;
            if (owner == pool)
            {
                return r;
            }

            Volatile.Write(ref read.State, Asked);
            owner?.Wake();
        }

        return -1;
    }

    // Reads a page on a worker (design §5.5.2 "ページの読み出し"): with a reference of its own to the snapshot, which stays
    // readable until it is given back, into the read's first page, then in steps into its second.
    private void ReadPage(int r)
    {
        var read = _reads[r];
        var snapshot = read.Key.Snapshot;
        try
        {
            if (!snapshot.TryAddReference())
            {
                // Every reference went back before the read took one: the copy is freed, and nothing asks for it.
                read.Lost = true;
            }
            else
            {
                try
                {
                    TestRead?.Invoke(snapshot, read.Key.Page);
                    var staging = _staging!;
                    var ordered = staging.AsSpan(SourceOf(r), read.Count);
                    snapshot.ReadBlock((long)read.Key.Page * PagePoints, ordered);
                    Place(ordered, staging.AsSpan(DestinationOf(r), read.Count), read.Key.Block);
                }
                finally
                {
                    snapshot.Release();
                }
            }
        }
        catch (Exception exception)
        {
            // The read's failure is the layer's index's (design §5.5.2 "読みの例外"): the frame lets the read go.
            read.Failure = exception;
            snapshot.Index.FailRead(exception);
        }

        Ended(read);
    }

    // The pool was disposed: a read asked for of it is read by no worker, and is freed (design §5.5.2 "読みの例外", Issue
    // #603's decision of 2026-11-27). It is no read's failure: the layer does not fail, and its page is not asked for again,
    // since a disposed pool takes no request. Called by the pool's disposal, and by a request that saw the disposal begin.
    private void Abandon(PlotWorkerPool pool)
    {
        foreach (var read in _reads)
        {
            if (read.Pool == pool && Interlocked.CompareExchange(ref read.State, Free, Asked) == Asked)
            {
                Woken();
            }
        }
    }

    private void Ended(ReadSlot read)
    {
        _ = Interlocked.Exchange(ref read.State, Read);
        Woken();
    }

    // A read left flight: a wait for the reads is woken. The state was written with a full barrier before the count of
    // waiters is read, as the waiter writes the count before it reads the states: one of the two sees the other.
    private void Woken()
    {
        if (Volatile.Read(ref _waiters) > 0)
        {
            lock (_readGate)
            {
                Monitor.PulseAll(_readGate);
            }
        }
    }

    private void Drop(int slot)
    {
        // A page staged this frame and dropped again is not sent.
        for (var u = _uploads.Count - 1; u >= 0; u--)
        {
            if (_uploads[u].Slot == slot)
            {
                _staged -= _uploads[u].Count;
                _uploads.RemoveAt(u);
            }
        }

        _ = _slots.Remove(_keys[slot]!.Value);
        _keys[slot] = null;
        _lastFreed[slot] = _owner.Release();
        if (_lastUsed[slot] == _owner.Frame)
        {
            UsedThisFrame--;
        }

        BytesInUse -= (long)_counts[slot] * PointBytes;
        _dropped.Add(slot);
    }

    /// <summary>A page to send: its slot, where its points begin in the staging, and how many they are.</summary>
    internal readonly record struct PointUpload(int Slot, int Offset, int Count);

    private readonly record struct Key(SpatialSnapshot Snapshot, int Page, int Block);

    /// <summary>
    /// A read of a page (design §5.5.2 "読みの場所"): its state, which the frame's thread and the workers move, and what the
    /// frame wrote before it asked. A worker writes only the state, the failure, and whether the copy was gone; the frame's
    /// thread writes the rest, the generation also while a worker reads.
    /// </summary>
    private sealed class ReadSlot
    {
        public int State;
        public Key Key;
        public PlotWorkerPool? Pool;
        public int Count;

        // The frame that last asked for the page.
        public long Wanted;

        // Moved on when the page's snapshot is given back or the device is lost; the request remembers the one it was asked at.
        public int Generation;
        public int AskedGeneration;

        // The read's page is staged to send: the read stays taken until the backend has sent it.
        public bool Scheduled;

        // The frame that found the page read and had no room to send it: the frame's upload or the budget was full.
        public long Refused;
        public bool Lost;
        public Exception? Failure;

        // The frame's thread withdraws a request a worker has not taken; one taken is read to the end. True when withdrawn:
        // the read is the frame's again, and holds neither the snapshot nor the pool.
        public bool Withdraw()
        {
            if (Interlocked.CompareExchange(ref State, Free, Asked) != Asked)
            {
                return false;
            }

            (Key, Pool) = (default, null);
            return true;
        }

        // The frame's thread lets a read go once no worker writes it: it holds neither the snapshot nor the pool.
        public void Release()
        {
            (Scheduled, Failure, Key, Pool) = (false, null, default, null);
            Volatile.Write(ref State, Free);
        }
    }

    /// <summary>The pool's work of reading pages (design §8.1 "ページの読み出しの仕事"): a request a turn, before the builds' turns.</summary>
    private sealed class PageReader(PointPages pages, PlotWorkerPool pool) : PoolWork
    {
        public PlotWorkerPool Pool => pool;

        internal override void OnPoolDisposed() => pages.Abandon(pool);

        // Reads are requested work only: they are taken in the pool's turn of requested chunks (design §8.1).
        private protected override int TryTake(bool requestedOnly) => requestedOnly ? pages.Take(pool) : -1;

        private protected override void Build(int chunk) => pages.ReadPage(chunk);
    }
}
