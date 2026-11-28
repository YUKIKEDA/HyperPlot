using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>Pages of points made resident in a test, as frames make them (design §5.5.2 "ページの読み出し", row R3b2).</summary>
internal static class PageTesting
{
    /// <summary>
    /// Returns the slot of a page once a worker has read it, staged in this frame: asked for, waited for, and asked for
    /// again, as the frame after the one that asked sends it, twice when the first read was let go of as stale. The read
    /// stays taken until the cache is told it was sent.
    /// </summary>
    public static int Read(this PointPages pages, SpatialSnapshot snapshot, int page, int block)
    {
        // A read that ended for a snapshot the cache was told to forget is let go of and asked for again: a second wait.
        var slot = pages.Request(snapshot, page, block);
        for (var tries = 0; slot < 0 && tries < 2; tries++)
        {
            _ = pages.AwaitReads();
            slot = pages.Request(snapshot, page, block);
        }

        Assert.True(slot >= 0, $"Page {page} was not staged once its read had ended.");
        return slot;
    }

    /// <summary>
    /// Returns the slot of a page as a backend holds it: read by a worker, staged, and sent, so that its read is free again.
    /// The cache is told that the frame's uploads were sent. The page is staged past the frame's upload, as a headless
    /// render sends all it needs: a send does not take the frame's count back (design §5.5.2 "1 tick に送る量"), and a test
    /// makes more pages resident in one frame than 4 MB.
    /// </summary>
    public static int Resident(this ResidencyCache cache, SpatialSnapshot snapshot, int page, int block)
    {
        var unbounded = cache.Unbounded;
        cache.Unbounded = true;
        try
        {
            var slot = cache.Points.Read(snapshot, page, block);
            cache.Sent();
            return slot;
        }
        finally
        {
            cache.Unbounded = unbounded;
        }
    }
}
