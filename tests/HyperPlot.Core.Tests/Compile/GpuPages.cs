using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The pages and the faces a GPU would hold, slot by slot, from what the draws of a null backend send: a dropped slot's
/// buffer goes, and a sent page's points or a sent face's texels take their slot's.
/// </summary>
internal sealed class GpuPages
{
    private readonly Dictionary<int, ResidentPoint[]> _pages = [];
    private readonly Dictionary<(ResidencyCache.Format, int), uint[]> _faces = [];

    public void Send(ResidencyCache cache)
    {
        foreach (var slot in cache.Points.Dropped)
        {
            _ = _pages.Remove(slot);
        }

        foreach (var upload in cache.Points.Uploads)
        {
            _pages[upload.Slot] = cache.Points.Staged(upload).ToArray();
        }

        foreach (var upload in cache.Uploads)
        {
            _faces[(upload.Format, upload.Slot)] = cache.Face(upload).ToArray();
        }
    }

    /// <summary>Returns the faces a list's image records read, in the records' order: null for a slot that holds none.</summary>
    public uint[]?[] Faces(RenderList list)
    {
        var images = list.Images;
        var calls = list.ImageCalls;
        var faces = new uint[]?[images.Length];
        for (var i = 0; i < images.Length; i++)
        {
            // A call's page is the format a 1,024 and the texture array of the slot (ImagePlan.PageOf).
            var page = calls[i].Page;
            var format = page >= 1024 ? ResidencyCache.Format.Rgba : ResidencyCache.Format.Float;
            faces[i] = _faces.GetValueOrDefault((format, ((page % 1024) * ResidencyCache.PageSlots) + (int)images[i].Slice));
        }

        return faces;
    }

    /// <summary>
    /// Returns the centers a list's resident runs draw, sorted, each computed from the kept page as Markers.hlsl computes
    /// it (the cell's pixel plus the point's units from the cell's base times its scale, clamped); NaN for a point of a
    /// slot that holds no page or whose cell is not in the list's table.
    /// </summary>
    public (float X, float Y)[] Centers(RenderList list)
    {
        var centers = new List<(float X, float Y)>();
        var cells = list.ResidentCells;
        foreach (var run in list.ResidentRuns)
        {
            if (!_pages.TryGetValue(run.Slot, out var page) || run.First + run.Count > page.Length)
            {
                centers.AddRange(Enumerable.Repeat((float.NaN, float.NaN), run.Count));
                continue;
            }

            for (var i = run.First; i < run.First + run.Count; i++)
            {
                var point = page[i];
                var at = run.Cells + (long)point.Cell;
                if (at >= cells.Length)
                {
                    centers.Add((float.NaN, float.NaN));
                    continue;
                }

                var cell = cells[(int)at];
                var x = Math.Clamp(cell.PixelX + (Units(point.X, cell.BaseX) * cell.ScaleX), run.ClampLow.X, run.ClampHigh.X);
                var y = Math.Clamp(cell.PixelY + (Units(point.Y, cell.BaseY) * cell.ScaleY), run.ClampLow.Y, run.ClampHigh.Y);
                centers.Add(run.Swap ? (y, x) : (x, y));
            }
        }

        return [.. centers.Order()];

        static float Units(uint position, uint origin) => position >= origin ? position - origin : -(float)(origin - position);
    }
}
