namespace HyperPlot;

/// <summary>
/// One job of the build of a spatial tile index, in the order of the stages (design §3.4 "構築の段"). A job of a unit
/// reads at most <see cref="SpatialIndexTuning.RowsPerUnit"/> rows; the others are single jobs, one at a time.
/// </summary>
internal enum SpatialJob
{
    /// <summary>The single job that records the versions of the columns and allocates the counters and the copy.</summary>
    Allocate,

    /// <summary>A unit that reads the least and the greatest finite value of each axis whose range was not passed.</summary>
    Range,

    /// <summary>The single job that joins the ranges of the groups and makes the grid.</summary>
    EndRange,

    /// <summary>A unit of the first count, at level 8.</summary>
    First,

    /// <summary>The single job that totals the first count and publishes the coarse snapshot.</summary>
    PublishCoarse,

    /// <summary>A unit of a counting pass over the cells that still hold too many points.</summary>
    Pass,

    /// <summary>The single job that splits the cells a counting pass counted.</summary>
    EndPass,

    /// <summary>The single job that gives every cell its range of the copy.</summary>
    Table,

    /// <summary>A unit that writes its points to the copy.</summary>
    Write,

    /// <summary>The single job that fills the gaps of the copy and publishes the full snapshot.</summary>
    PublishFull,

    /// <summary>The single job that chooses the cells that get a density tile.</summary>
    PlanDensity,

    /// <summary>A unit that counts one quadrant of a density tile, or one run of the copy in it.</summary>
    Density,

    /// <summary>The single job that ends one level of density tiles and begins the level above it.</summary>
    EndDensityLevel,

    /// <summary>The single job that gives the full snapshot its density tiles and ends the build.</summary>
    PublishDensity,
}
