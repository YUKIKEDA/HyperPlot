using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A row-major two-dimensional grid over a column (design §3.7, row G9): numbers, or an RGBA image of 4 bytes a pixel. The
/// column keeps its storage (managed, native, or memory-mapped), its wrapping without a copy, and its Invalidate.
/// </summary>
/// <remarks>
/// A raster only wraps its column and is not <see cref="IDisposable"/>: the user owns the column, as with
/// <see cref="DataTable"/>. A raster with a side above 16,384 (Direct3D 11's texture limit) is drawn too, a tile at a time.
/// </remarks>
public abstract class Raster
{
    private protected Raster(Column values, int width, int height, bool rgba) => (Values, Width, Height, IsRgba) = (values, width, height, rgba);

    /// <summary>Gets the number of columns of pixels, at least 1.</summary>
    public int Width { get; }

    /// <summary>Gets the number of rows of pixels, at least 1.</summary>
    public int Height { get; }

    /// <summary>Gets a value indicating whether the raster is an RGBA image of 4 bytes a pixel (<see cref="Rgba"/>).</summary>
    public bool IsRgba { get; }

    /// <summary>Gets the column the raster wraps, row 0 first.</summary>
    public Column Values { get; }

    /// <summary>Gets the number of elements a pixel takes: 4 for an RGBA image, 1 for numbers.</summary>
    internal int Channels => IsRgba ? 4 : 1;

    /// <summary>Returns a raster of numbers over a column of width × height elements, row 0 first.</summary>
    /// <typeparam name="T">The element type: float, double, int, long, or byte. A float's or a double's NaN is missing and transparent.</typeparam>
    /// <param name="values">The column.</param>
    /// <param name="width">The number of columns of pixels.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is below 1.</exception>
    /// <exception cref="ArgumentException">The column's length is not a multiple of the width, or the height passes <see cref="int.MaxValue"/>.</exception>
    public static Raster<T> Of<T>(Column<T> values, int width)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(values);
        return new Raster<T>(values, width, HeightOf(values, width, 1), rgba: false);
    }

    /// <summary>
    /// Returns an RGBA image over a column of 4 × width × height bytes: red, green, blue, and alpha a pixel, row 0 first, the
    /// alpha not premultiplied (as <see cref="Color"/>).
    /// </summary>
    /// <param name="values">The column of bytes.</param>
    /// <param name="width">The number of columns of pixels.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is below 1.</exception>
    /// <exception cref="ArgumentException">The column's length is not a multiple of 4 × the width, or the height passes <see cref="int.MaxValue"/>.</exception>
    public static Raster<byte> Rgba(Column<byte> values, int width)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new Raster<byte>(values, width, HeightOf(values, width, 4), rgba: true);
    }

    /// <summary>
    /// Says that the user rewrote rows of the column: passes their elements to the column's Invalidate. The mipmap learns
    /// the rows from the column's chunks, so calling the column's Invalidate does the same.
    /// </summary>
    /// <param name="firstRow">The first row rewritten.</param>
    /// <param name="rowCount">The number of rows rewritten.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rows are not inside the raster.</exception>
    public void Invalidate(int firstRow, int rowCount)
    {
        if (firstRow < 0 || rowCount < 0 || (long)firstRow + rowCount > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstRow),
                $"Rows {firstRow} to {(long)firstRow + rowCount} are not inside the raster's {Height}."
            );
        }

        var row = (long)Width * Channels;
        InvalidateElements(firstRow * row, rowCount * row);
    }

    /// <summary>Passes a range of elements to the column's Invalidate.</summary>
    private protected abstract void InvalidateElements(long start, long length);

    /// <summary>
    /// Makes the raster's mipmap (design §3.7): the raster makes it, a virtual method rather than a switch over the element
    /// types, so that NativeAOT compiles the averages of the types an application builds only.
    /// </summary>
    internal abstract RasterPyramid MakePyramid();

    private static int HeightOf(Column values, int width, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        var length = values.Length;
        var row = (long)width * channels;
        if (length == 0 || length % row != 0 || length / row > int.MaxValue)
        {
            throw new ArgumentException(
                $"The column has {length} elements; a raster {width} wide of {channels} element{(channels == 1 ? string.Empty : "s")} a pixel needs a positive multiple of {row}, of at most {int.MaxValue} rows.",
                nameof(values)
            );
        }

        return (int)(length / row);
    }
}

/// <summary>A raster over a <see cref="Column{T}"/> (design §3.7, row G9).</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class Raster<T> : Raster
    where T : unmanaged, INumber<T>
{
    internal Raster(Column<T> values, int width, int height, bool rgba)
        : base(values, width, height, rgba) => Values = values;

    /// <summary>Gets the column the raster wraps, row 0 first.</summary>
    public new Column<T> Values { get; }

    private protected override void InvalidateElements(long start, long length) => Values.Invalidate(start, length);

    internal override RasterPyramid MakePyramid() => new RasterPyramid<T>(this);
}
