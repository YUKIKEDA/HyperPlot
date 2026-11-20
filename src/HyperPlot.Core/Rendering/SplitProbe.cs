namespace HyperPlot.Rendering;

/// <summary>
/// Scratch switches of the spike for row R3b2 (Issue #472, decision 1): why a split point layer on the index path draws
/// slower than one part. Read once from the environment; not for merging.
/// </summary>
internal static class SplitProbe
{
    /// <summary>HP_PROBE_SHUFFLE_BLOCKS: a page is staged as this many runs of the copy's order, each shuffled in steps alone (1: the page whole, as on main).</summary>
    internal static readonly int ShuffleBlocks = Read("HP_PROBE_SHUFFLE_BLOCKS", 1);

    /// <summary>HP_PROBE_BLOCK_POINTS: a page is staged as runs of this many points of the copy's order (the last holds the rest), each shuffled in steps alone; overrides <see cref="ShuffleBlocks"/>. 0 (unset): off.</summary>
    internal static readonly int BlockPoints = Read("HP_PROBE_BLOCK_POINTS", 0);

    /// <summary>HP_PROBE_SPLIT: each page is drawn as this many calls over equal runs of its points (1: one call, as on main).</summary>
    internal static readonly int Split = Read("HP_PROBE_SPLIT", 1);

    /// <summary>HP_PROBE_ORDER=interleave: the calls of the parts go round the parts page by page (and split call by split call); otherwise part after part.</summary>
    internal static readonly bool Interleave = Environment.GetEnvironmentVariable("HP_PROBE_ORDER") == "interleave";

    /// <summary>HP_PROBE_COLOR: "one" draws every call with the first call's color; "cycle" gives call i of the frame the i-th of 8 colors (mod 8), with the run's alpha; otherwise the run's color.</summary>
    internal static readonly string Color = Environment.GetEnvironmentVariable("HP_PROBE_COLOR") ?? "keep";

    /// <summary>HP_PROBE_CB=changed: the run's constants are written only when they differ from the last written.</summary>
    internal static readonly bool ConstantsWhenChanged = Environment.GetEnvironmentVariable("HP_PROBE_CB") == "changed";

    /// <summary>
    /// HP_PROBE_VISIBLE_BLOCKS=1 (with <see cref="BlockPoints"/> set): a page draws only the blocks that hold points of the
    /// cells in view, consecutive blocks as one call, instead of the page whole. The cells (at most 32,768 points) decide
    /// what is in view; their ranges are widened to the blocks' ends.
    /// </summary>
    internal static readonly bool VisibleBlocks = Environment.GetEnvironmentVariable("HP_PROBE_VISIBLE_BLOCKS") == "1" && BlockPoints > 0;

    /// <summary>HP_PROBE_FRACTION=f (0 &lt; f &lt;= 1, with <see cref="VisibleBlocks"/>): each block in view is drawn in a call of its own, over the first f of its points. 0 (unset): off.</summary>
    internal static readonly double Fraction =
        VisibleBlocks
        && double.TryParse(Environment.GetEnvironmentVariable("HP_PROBE_FRACTION"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)
        && f > 0
        && f <= 1
            ? f
            : 0;

    /// <summary>
    /// HP_PROBE_BUDGET_FRACTION (Issue #567, with <see cref="VisibleBlocks"/>): each frame draws the first f of every block
    /// in view, f = the budget left by the tiles over the bytes of the pages that hold a block in view. 1 ("heads"): a page
    /// holds only the heads of its blocks, and is sent again when f changes. 2 ("whole"): a page holds every point and only
    /// the drawn range follows f; the pages may then pass the budget unless <see cref="PageCap"/>. 0 (unset): off.
    /// </summary>
    internal static readonly int BudgetFraction = !VisibleBlocks
        ? 0
        : Environment.GetEnvironmentVariable("HP_PROBE_BUDGET_FRACTION") switch
        {
            "heads" => 1,
            "whole" => 2,
            _ => 0,
        };

    /// <summary>HP_PROBE_FRACTION_STEPS=n: f is rounded down to a multiple of 1/n. 0 (unset): f to a point of a block.</summary>
    internal static readonly int FractionSteps =
        int.TryParse(Environment.GetEnvironmentVariable("HP_PROBE_FRACTION_STEPS"), out var steps) && steps > 0 ? steps : 0;

    /// <summary>HP_PROBE_PAGE_CAP=1 (with "whole"): the whole pages stay inside the budget, so pages past it are not there.</summary>
    internal static readonly bool PageCap = Environment.GetEnvironmentVariable("HP_PROBE_PAGE_CAP") == "1";

    /// <summary>Gets or sets the bytes of points staged for the GPU (Issue #567).</summary>
    internal static long UploadedBytes { get; set; }

    /// <summary>Gets or sets the pages staged that were not on the GPU.</summary>
    internal static long NewPages { get; set; }

    /// <summary>Gets or sets the pages staged again with longer heads.</summary>
    internal static long GrownPages { get; set; }

    /// <summary>Gets or sets the pages staged again with shorter heads.</summary>
    internal static long ShrunkPages { get; set; }

    /// <summary>Gets or sets the blocks in view that drew fewer points than the f of the frame gives them (their page absent or its heads shorter).</summary>
    internal static long ShortBlocks { get; set; }

    /// <summary>Gets or sets the blocks in view.</summary>
    internal static long VisibleBlockCount { get; set; }

    /// <summary>Gets or sets the points of a full block the last frame drew (f times the block).</summary>
    internal static int HeadPoints { get; set; }

    /// <summary>Gets or sets the bytes of the pages that hold a block in view, of the last frame.</summary>
    internal static long NeededBytes { get; set; }

    /// <summary>HP_PROBE_NO_RAW_LIMIT=1: a point layer of more than the raw limit draws its points. Read once: a read a frame allocates 24 B.</summary>
    internal static readonly bool NoRawLimit = Environment.GetEnvironmentVariable("HP_PROBE_NO_RAW_LIMIT") == "1";

    /// <summary>Gets or sets the Stopwatch ticks spent in the index path's Record (Issue #472, O4).</summary>
    internal static long RecordTicks { get; set; }

    /// <summary>Gets or sets the calls of the index path's Record.</summary>
    internal static long RecordCalls { get; set; }

    /// <summary>Gets or sets the resident cells the calls of Record wrote a record for (24 B each).</summary>
    internal static long RecordedCells { get; set; }

    /// <summary>Gets or sets the Stopwatch ticks spent reading pages from the copy on the frame's thread (Issue #472, O3).</summary>
    internal static long PageReadTicks { get; set; }

    /// <summary>Gets or sets the pages read from the copy.</summary>
    internal static long PagesRead { get; set; }

    /// <summary>Gets or sets the points the calls drew.</summary>
    internal static long DrawnPoints { get; set; }

    /// <summary>Gets or sets the frame's counts, added by the backend and read and cleared by the scene.</summary>
    internal static long Draws { get; set; }

    /// <summary>Gets or sets the writes of the run's constants.</summary>
    internal static long ConstantWrites { get; set; }

    /// <summary>Gets or sets the binds of a page's view.</summary>
    internal static long PageBinds { get; set; }

    /// <summary>Gets or sets the binds of the pipeline (one at each change of the step kind to resident markers).</summary>
    internal static long PipelineBinds { get; set; }

    /// <summary>Gets or sets the groups of consecutive resident steps.</summary>
    internal static long Groups { get; set; }

    /// <summary>Gets or sets the parts seen in the groups (a change of the cells' offset).</summary>
    internal static long Parts { get; set; }

    /// <summary>Gets or sets the changes of the color between two consecutive calls.</summary>
    internal static long ColorChanges { get; set; }

    /// <summary>Gets or sets the texts the shaping cache lacked (each a miss of <see cref="TextEngine"/>'s Shape).</summary>
    internal static long ShapeMisses { get; set; }

    /// <summary>Gets or sets the bytes of a miss's entry (a new one, or the oldest taken out).</summary>
    internal static long ShapeEntryBytes { get; set; }

    /// <summary>Gets or sets the bytes of a miss's copy of the characters and style into the entry.</summary>
    internal static long ShapeSetBytes { get; set; }

    /// <summary>Gets or sets the bytes of a miss's call to the text backend.</summary>
    internal static long ShapeBackendBytes { get; set; }

    /// <summary>Gets or sets the bytes of a miss's add to the cache's set.</summary>
    internal static long ShapeAddBytes { get; set; }

    /// <summary>Gets or sets the bytes of a miss's add to the cache's order.</summary>
    internal static long ShapeOrderBytes { get; set; }

    /// <summary>Gets or sets the cache's count after the last miss.</summary>
    internal static long ShapeCacheCount { get; set; }

    /// <summary>Gets the switches as one line.</summary>
    internal static string Describe() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"probe: shuffle_blocks={ShuffleBlocks} block_points={BlockPoints} split={Split} order={(Interleave ? "interleave" : "parts")} color={Color} cb={(ConstantsWhenChanged ? "changed" : "always")} draw={(VisibleBlocks ? "visible_blocks" : "pages")} no_raw_limit={NoRawLimit} fraction={Fraction} budget_fraction={BudgetFraction switch { 1 => "heads", 2 => "whole", _ => "off" }} fraction_steps={FractionSteps} page_cap={PageCap} budget={ResidencyCache.DefaultBudget}"
        );

    private static int Read(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 1 ? value : fallback;
}
