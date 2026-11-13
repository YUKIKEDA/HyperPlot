using System.Runtime.InteropServices;

namespace HyperPlot.Volume;

/// <summary>
/// The levels of a volume (each axis averaged by 2, design §13.6 decision 2) cut into bricks of Brick³ voxels with a ghost of
/// 1 voxel on each side, so a brick is Side³ = (Brick + 2)³ codes. A ghost outside the volume repeats the edge voxel. Each
/// brick keeps the minimum and maximum code of all it holds, ghosts included (what its trilinear samples can read), and the
/// minimum and maximum over itself and every finer brick under it (an average can hide a child's extreme).
/// Bricks are numbered level by level, finest first, x fastest inside a level.
/// </summary>
internal sealed unsafe class BrickStore : IDisposable
{
    private ushort* _data;

    private BrickStore(int size, int brick)
    {
        Size = size;
        Brick = brick;
        Side = brick + 2;
        Grid = Math.Max(1, size / brick);
        var levels = 1;
        for (var n = size; n > brick; n /= 2)
        {
            levels++;
        }

        Levels = levels;
        LevelSide = new int[levels];
        LevelBricks = new int[levels];
        LevelFirst = new int[levels];
        var count = 0;
        for (var level = 0; level < levels; level++)
        {
            LevelSide[level] = size >> level;
            LevelBricks[level] = Math.Max(1, LevelSide[level] / brick);
            LevelFirst[level] = count;
            count += LevelBricks[level] * LevelBricks[level] * LevelBricks[level];
        }

        Count = count;
        BrickBytes = (long)Side * Side * Side * sizeof(ushort);
        Min = new ushort[count];
        Max = new ushort[count];
        TreeMin = new ushort[count];
        TreeMax = new ushort[count];
        _data = (ushort*)NativeMemory.Alloc((nuint)(count * BrickBytes));
    }

    /// <summary>Voxels per axis at level 0.</summary>
    public int Size { get; }

    /// <summary>Voxels per brick per axis, without the ghosts.</summary>
    public int Brick { get; }

    /// <summary>Codes per brick per axis, with the ghosts.</summary>
    public int Side { get; }

    /// <summary>Bricks per axis at level 0.</summary>
    public int Grid { get; }

    public int Levels { get; }

    /// <summary>Voxels per axis of each level.</summary>
    public int[] LevelSide { get; }

    /// <summary>Bricks per axis of each level.</summary>
    public int[] LevelBricks { get; }

    /// <summary>The number of each level's first brick.</summary>
    public int[] LevelFirst { get; }

    public int Count { get; }

    public long BrickBytes { get; }

    public ushort[] Min { get; }

    public ushort[] Max { get; }

    public ushort[] TreeMin { get; }

    public ushort[] TreeMax { get; }

    /// <summary>The voxels of all levels without ghosts, in bytes: what the levels alone add to the data.</summary>
    public long LevelBytes
    {
        get
        {
            var bytes = 0L;
            foreach (var side in LevelSide)
            {
                bytes += (long)side * side * side * sizeof(ushort);
            }

            return bytes;
        }
    }

    /// <summary>All bricks of all levels with their ghosts, in bytes.</summary>
    public long BrickedBytes => Count * BrickBytes;

    /// <summary>Makes the levels from level 0 and cuts them into bricks; level 0 stays the caller's.</summary>
    public static BrickStore Build(NativeVolume level0, int brick)
    {
        var store = new BrickStore(level0.Size, brick);
        try
        {
            store.Fill(level0);
        }
        catch
        {
            store.Dispose();
            throw;
        }

        return store;
    }

    /// <summary>The codes of brick id, Side³ of them, x fastest.</summary>
    public ReadOnlySpan<ushort> Codes(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Count);

        // The brick lies inside the Count · BrickBytes bytes allocated: id < Count.
        return new ReadOnlySpan<ushort>(_data + (id * (BrickBytes / sizeof(ushort))), (int)(BrickBytes / sizeof(ushort)));
    }

    /// <summary>The address of brick id's codes, for the upload; checked like <see cref="Codes"/>.</summary>
    public void* Address(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Count);
        return _data + (id * (BrickBytes / sizeof(ushort)));
    }

    /// <summary>The level of brick id.</summary>
    public int LevelOf(int id)
    {
        var level = Levels - 1;
        while (LevelFirst[level] > id)
        {
            level--;
        }

        return level;
    }

    /// <summary>The number of the brick at (x, y, z) of a level.</summary>
    public int Id(int level, int x, int y, int z)
    {
        var n = LevelBricks[level];
        return LevelFirst[level] + (((z * n) + y) * n) + x;
    }

    /// <summary>The position of brick id inside its level.</summary>
    public (int X, int Y, int Z) Position(int id, int level)
    {
        var n = LevelBricks[level];
        var i = id - LevelFirst[level];
        return (i % n, (i / n) % n, i / (n * n));
    }

    /// <summary>The brick of the next coarser level that covers brick id, or -1 at the coarsest level.</summary>
    public int Parent(int id)
    {
        var level = LevelOf(id);
        if (level == Levels - 1)
        {
            return -1;
        }

        var (x, y, z) = Position(id, level);
        return Id(level + 1, x / 2, y / 2, z / 2);
    }

    public void Dispose()
    {
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }

    private void Fill(NativeVolume level0)
    {
        // The finer level of each pass is read through its address, checked against its length by Read.
        var source = level0.Address;
        var made = new List<nint>();
        try
        {
            for (var level = 0; level < Levels; level++)
            {
                if (level > 0)
                {
                    var finer = source;
                    source = (nint)NativeMemory.Alloc((nuint)((long)LevelSide[level] * LevelSide[level] * LevelSide[level]), sizeof(ushort));
                    made.Add(source);
                    Average(finer, LevelSide[level - 1], source);
                }

                CutBricks(level, source);
            }
        }
        finally
        {
            foreach (var pointer in made)
            {
                NativeMemory.Free((void*)pointer);
            }
        }

        // The ranges over each brick and every finer brick under it, from the finest level up.
        for (var id = 0; id < Count; id++)
        {
            (TreeMin[id], TreeMax[id]) = (Min[id], Max[id]);
        }

        for (var level = 0; level < Levels - 1; level++)
        {
            var n = LevelBricks[level];
            for (var z = 0; z < n; z++)
            {
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var id = Id(level, x, y, z);
                        var parent = Id(level + 1, x / 2, y / 2, z / 2);
                        TreeMin[parent] = Math.Min(TreeMin[parent], TreeMin[id]);
                        TreeMax[parent] = Math.Max(TreeMax[parent], TreeMax[id]);
                    }
                }
            }
        }
    }

    // Level + 1 from level: the mean of each 2 × 2 × 2 block, leaving out the NaN code; a block of NaN only stays NaN.
    private static void Average(nint finer, int finerSide, nint coarser)
    {
        var side = finerSide / 2;
        var plane = (long)finerSide * finerSide;
        var length = plane * finerSide;
        _ = Parallel.For(
            0,
            side,
            z =>
            {
                for (var y = 0; y < side; y++)
                {
                    for (var x = 0; x < side; x++)
                    {
                        var (sum, n) = (0u, 0u);
                        for (var k = 0; k < 8; k++)
                        {
                            var at = (((2L * z) + (k >> 2)) * plane) + ((((2L * y) + ((k >> 1) & 1)) * finerSide) + (2L * x) + (k & 1));
                            var code = Read(finer, length, at);
                            if (code != NativeVolume.NaNCode)
                            {
                                sum += code;
                                n++;
                            }
                        }

                        var target = (((long)z * side) + y) * side;
                        Write(coarser, (long)side * side * side, target + x, n == 0 ? NativeVolume.NaNCode : (ushort)((sum + (n / 2)) / n));
                    }
                }
            }
        );
    }

    // Cuts a level into bricks with ghosts, in parallel, and takes each brick's range.
    private void CutBricks(int level, nint voxels)
    {
        var side = LevelSide[level];
        var bricks = LevelBricks[level];
        var length = (long)side * side * side;
        var brickSide = Side;
        var data = (nint)_data;
        var codes = BrickBytes / sizeof(ushort);
        _ = Parallel.For(
            0,
            bricks * bricks * bricks,
            i =>
            {
                var (bx, by, bz) = (i % bricks, (i / bricks) % bricks, i / (bricks * bricks));
                var id = LevelFirst[level] + i;
                var target = new Span<ushort>((ushort*)data + (id * codes), (int)codes);
                var (low, high) = (ushort.MaxValue, ushort.MinValue);
                var at = 0;
                for (var z = 0; z < brickSide; z++)
                {
                    var vz = Math.Clamp((bz * Brick) - 1 + z, 0, side - 1);
                    for (var y = 0; y < brickSide; y++)
                    {
                        var vy = Math.Clamp((by * Brick) - 1 + y, 0, side - 1);
                        for (var x = 0; x < brickSide; x++)
                        {
                            var vx = Math.Clamp((bx * Brick) - 1 + x, 0, side - 1);
                            var code = Read(voxels, length, ((((long)vz * side) + vy) * side) + vx);
                            target[at++] = code;
                            if (code != NativeVolume.NaNCode)
                            {
                                low = Math.Min(low, code);
                                high = Math.Max(high, code);
                            }
                        }
                    }
                }

                // A brick of NaN only keeps low > high: no value, so the transfer function finds it empty.
                (Min[id], Max[id]) = (low, high);
            }
        );
    }

    private static ushort Read(nint voxels, long length, long at)
    {
        if ((ulong)at >= (ulong)length)
        {
            throw new ArgumentOutOfRangeException(nameof(at), $"Voxel {at} is outside {length}.");
        }

        // at < length, the voxels allocated.
        return ((ushort*)voxels)[at];
    }

    private static void Write(nint voxels, long length, long at, ushort code)
    {
        if ((ulong)at >= (ulong)length)
        {
            throw new ArgumentOutOfRangeException(nameof(at), $"Voxel {at} is outside {length}.");
        }

        // at < length, the voxels allocated.
        ((ushort*)voxels)[at] = code;
    }
}
