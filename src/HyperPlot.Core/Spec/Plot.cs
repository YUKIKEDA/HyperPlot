namespace HyperPlot;

/// <summary>Starts the declaration of a plot (design §4.1, §4.9).</summary>
public static class Plot
{
    /// <summary>Starts a plot over a table.</summary>
    /// <param name="data">The table whose columns the layers name.</param>
    /// <returns>A builder of the plot's spec.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    public static PlotBuilder Create(DataTable data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PlotBuilder(data);
    }
}
