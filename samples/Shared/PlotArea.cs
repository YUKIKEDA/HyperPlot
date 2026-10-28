using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Samples;

/// <summary>
/// The rectangle of a plot in physical pixels, and the mapping from data to it, in double (design §3.6). The samples do
/// this themselves until the scales of row G2; the library takes pixels only (design §5.1).
/// </summary>
internal sealed class PlotArea(int left, int top, int width, int height)
{
    private Vector2[] _points = new Vector2[1024];

    public int Left => left;

    public int Top => top;

    public int Width => width;

    public int Height => height;

    /// <summary>Returns the W + 1 boundaries of the pixel columns of [from, to] along X (design §5.5.1).</summary>
    public double[] Boundaries(double from, double to)
    {
        var boundaries = new double[width + 1];
        for (var k = 0; k <= width; k++)
        {
            boundaries[k] = from + ((to - from) * k / width);
        }

        return boundaries;
    }

    /// <summary>Draws a light grid: lines every <paramref name="step"/> pixels, and the frame.</summary>
    public void DrawGrid(RenderList list, int step)
    {
        var grid = new LineStyle(new Color(220, 220, 225, 255), 1);
        for (var x = left; x <= left + width; x += step)
        {
            list.DrawPolyline([new(x + 0.5f, top), new(x + 0.5f, top + height)], grid);
        }

        for (var y = top; y <= top + height; y += step)
        {
            list.DrawPolyline([new(left, y + 0.5f), new(left + width, y + 0.5f)], grid);
        }
    }

    /// <summary>
    /// Maps M4 points into the area, X over [xFrom, xTo] and Y over [yFrom, yTo], computed in double, and records them as one
    /// polyline: a point that breaks the line becomes NaN (design §5.1).
    /// </summary>
    public void DrawSeries(RenderList list, ReadOnlySpan<M4Point> points, double xFrom, double xTo, double yFrom, double yTo, in LineStyle style)
    {
        var needed = points.Length * 2;
        if (_points.Length < needed)
        {
            _points = new Vector2[needed];
        }

        var count = 0;
        foreach (var point in points)
        {
            if (point.BreakBefore)
            {
                _points[count++] = new Vector2(float.NaN);
            }

            var x = left + ((point.X - xFrom) / (xTo - xFrom) * width);
            var y = top + height - ((point.Y - yFrom) / (yTo - yFrom) * height);
            _points[count++] = new Vector2((float)x, (float)y);
        }

        list.SetClip(left, top, width, height);
        list.DrawPolyline(_points.AsSpan(0, count), style);
        list.ResetClip();
    }
}
