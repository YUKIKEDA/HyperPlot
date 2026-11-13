using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HyperPlot.Volume;

/// <summary>The fields the generator makes.</summary>
internal enum Field
{
    /// <summary>A sum of Gaussian blobs: wide regions near the minimum, which a transfer function leaves empty.</summary>
    Smooth,

    /// <summary>The blobs plus value noise down to two voxels: few regions stay empty.</summary>
    Noisy,
}

/// <summary>
/// N³ voxels of R16_UNORM codes in native memory, x fastest, then y, then z. Codes 0 to 65,534 are the values normalized by
/// the minimum and maximum of the build; 65,535 is kept for NaN (Issue #484, design §13.6).
/// </summary>
internal sealed unsafe class NativeVolume : IDisposable
{
    public const ushort NaNCode = 65535;
    public const ushort MaxCode = 65534;

    private ushort* _data;

    public NativeVolume(int size)
    {
        Size = size;
        Length = (long)size * size * size;
        _data = (ushort*)NativeMemory.Alloc((nuint)Length, sizeof(ushort));
    }

    public int Size { get; }

    public long Length { get; }

    /// <summary>The voxels' address, for code that reads them by index after checking it against <see cref="Length"/>.</summary>
    public nint Address => (nint)_data;

    /// <summary>The voxels of slice z (Size² of them).</summary>
    public Span<ushort> Slice(int z)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, Size);
        var plane = Size * Size;

        // The slice lies inside the Length elements allocated: z < Size.
        return new Span<ushort>(_data + ((long)z * plane), plane);
    }

    public void Dispose()
    {
        if (_data != null)
        {
            NativeMemory.Free(_data);
            _data = null;
        }
    }
}

/// <summary>
/// The seeded generator of the prototype's fields and the file that keeps one: a 64-byte header (magic, size, field, seed,
/// the minimum and maximum of the build) and the codes.
/// </summary>
internal static class VolumeFile
{
    private const int HeaderBytes = 64;
    private static readonly byte[] Magic = "HPVOL001"u8.ToArray();

    /// <summary>Generates the field and writes it; prints the minimum, the maximum, and the time.</summary>
    public static void Generate(string path, int size, Field field, ulong seed)
    {
        CheckSize(size);
        var started = Stopwatch.GetTimestamp();
        var function = new FieldFunction(field, size, seed);

        // Pass 1: the minimum and the maximum of the build, a slice a task.
        var minima = new float[size];
        var maxima = new float[size];
        _ = Parallel.For(
            0,
            size,
            z =>
            {
                var (low, high) = (float.PositiveInfinity, float.NegativeInfinity);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var v = function.Value(x, y, z);
                        low = MathF.Min(low, v);
                        high = MathF.Max(high, v);
                    }
                }

                (minima[z], maxima[z]) = (low, high);
            }
        );
        var min = minima.Min();
        var max = maxima.Max();

        // Pass 2: the codes, computed again rather than kept as float (4 GiB at 1024³).
        using var volume = new NativeVolume(size);
        var range = max - min;
        _ = Parallel.For(
            0,
            size,
            z =>
            {
                var slice = volume.Slice(z);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var v = function.Value(x, y, z);
                        slice[(y * size) + x] = range > 0 ? (ushort)MathF.Round((v - min) / range * (float)NativeVolume.MaxCode) : (ushort)0;
                    }
                }
            }
        );

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(size);
                writer.Write((int)field);
                writer.Write(seed);
                writer.Write((double)min);
                writer.Write((double)max);
                writer.Write(new byte[HeaderBytes - 8 - 4 - 4 - 8 - 8 - 8]);
            }

            for (var z = 0; z < size; z++)
            {
                file.Write(MemoryMarshal.AsBytes(volume.Slice(z)));
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"generate size={size} field={field} seed={seed} min={min:G9} max={max:G9} seconds={Stopwatch.GetElapsedTime(started).TotalSeconds:F1} file={path}"
            )
        );
    }

    /// <summary>Reads a file the generator wrote; checks its length against the size in its header first.</summary>
    public static NativeVolume Read(string path, out Field field, out ulong seed)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a volume of this prototype.");
        }

        var size = reader.ReadInt32();
        CheckSize(size);
        field = (Field)reader.ReadInt32();
        seed = reader.ReadUInt64();
        if (file.Length != HeaderBytes + ((long)size * size * size * sizeof(ushort)))
        {
            throw new InvalidDataException($"{path} does not hold {size}³ voxels.");
        }

        file.Position = HeaderBytes;
        var volume = new NativeVolume(size);
        try
        {
            for (var z = 0; z < size; z++)
            {
                file.ReadExactly(MemoryMarshal.AsBytes(volume.Slice(z)));
            }
        }
        catch
        {
            volume.Dispose();
            throw;
        }

        return volume;
    }

    private static void CheckSize(int size)
    {
        if (size < 16 || size > 1024 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException($"The size {size} is not a power of 2 from 16 to 1024.");
        }
    }

    /// <summary>The field at the voxels' centers, (i + 0.5) / N on each axis, from a seed: the same seed, the same values.</summary>
    private sealed class FieldFunction
    {
        private const int Blobs = 12;
        private const float NoiseAmplitude = 0.5f;

        private readonly int _size;
        private readonly bool _noisy;
        private readonly uint _noiseSeed;
        private readonly float[] _centers = new float[Blobs * 3];
        private readonly float[] _inverseTwoSigmaSquared = new float[Blobs];
        private readonly float[] _cutoffSquared = new float[Blobs];
        private readonly float[] _amplitudes = new float[Blobs];
        private readonly int[] _frequencies;
        private readonly float[] _weights;

        public FieldFunction(Field field, int size, ulong seed)
        {
            _size = size;
            _noisy = field == Field.Noisy;
            var state = seed;
            for (var b = 0; b < Blobs; b++)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    _centers[(b * 3) + axis] = 0.15f + (0.7f * Next(ref state));
                }

                var sigma = 0.03f + (0.09f * Next(ref state));
                _inverseTwoSigmaSquared[b] = 1 / (2 * sigma * sigma);
                _cutoffSquared[b] = 16 * sigma * sigma;
                _amplitudes[b] = 0.4f + (0.6f * Next(ref state));
            }

            _noiseSeed = (uint)SplitMix(ref state);

            // Octaves from 8 cells across to 2 voxels a cell, each 0.7 of the one before; the weights sum to 1.
            var frequencies = new List<int>();
            for (var f = 8; f <= size / 2; f *= 2)
            {
                frequencies.Add(f);
            }

            _frequencies = [.. frequencies];
            _weights = new float[_frequencies.Length];
            var total = 0f;
            for (var o = 0; o < _weights.Length; o++)
            {
                _weights[o] = MathF.Pow(0.7f, o);
                total += _weights[o];
            }

            for (var o = 0; o < _weights.Length; o++)
            {
                _weights[o] /= total;
            }
        }

        public float Value(int x, int y, int z)
        {
            var inverse = 1f / _size;
            var px = (x + 0.5f) * inverse;
            var py = (y + 0.5f) * inverse;
            var pz = (z + 0.5f) * inverse;
            var v = 0f;
            for (var b = 0; b < Blobs; b++)
            {
                var dx = px - _centers[b * 3];
                var dy = py - _centers[(b * 3) + 1];
                var dz = pz - _centers[(b * 3) + 2];
                var r2 = (dx * dx) + (dy * dy) + (dz * dz);
                if (r2 < _cutoffSquared[b])
                {
                    v += _amplitudes[b] * MathF.Exp(-r2 * _inverseTwoSigmaSquared[b]);
                }
            }

            if (_noisy)
            {
                var noise = 0f;
                for (var o = 0; o < _frequencies.Length; o++)
                {
                    noise += _weights[o] * ValueNoise(px * _frequencies[o], py * _frequencies[o], pz * _frequencies[o], (uint)o);
                }

                v += NoiseAmplitude * noise;
            }

            return v;
        }

        // A float in [0, 1) from SplitMix64.
        private static float Next(ref ulong state) => (SplitMix(ref state) >> 40) * (1f / (1 << 24));

        private static ulong SplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        private static float Smooth(float t) => t * t * (3 - (2 * t));

        // Value noise in [-1, 1]: a hash at each lattice point, interpolated with smoothstep weights.
        private float ValueNoise(float x, float y, float z, uint octave)
        {
            var (ix, iy, iz) = ((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));
            var (fx, fy, fz) = (Smooth(x - ix), Smooth(y - iy), Smooth(z - iz));
            var c00 = Lerp(Lattice(ix, iy, iz, octave), Lattice(ix + 1, iy, iz, octave), fx);
            var c10 = Lerp(Lattice(ix, iy + 1, iz, octave), Lattice(ix + 1, iy + 1, iz, octave), fx);
            var c01 = Lerp(Lattice(ix, iy, iz + 1, octave), Lattice(ix + 1, iy, iz + 1, octave), fx);
            var c11 = Lerp(Lattice(ix, iy + 1, iz + 1, octave), Lattice(ix + 1, iy + 1, iz + 1, octave), fx);
            return Lerp(Lerp(c00, c10, fy), Lerp(c01, c11, fy), fz);
        }

        private float Lattice(int x, int y, int z, uint octave)
        {
            var h = _noiseSeed ^ (octave * 0x9E3779B9u) ^ ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u) ^ ((uint)z * 0xCB1AB31Fu);
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return ((h >> 8) * (2f / (1 << 24))) - 1;
        }

        private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
    }
}
