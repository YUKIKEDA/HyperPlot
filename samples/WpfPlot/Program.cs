using System.Globalization;
using System.Windows;

namespace HyperPlot.Samples;

/// <summary>
/// WpfPlot: <c>HyperPlotView</c> in a window with three tabs (row W1a, design §7.2): two time series with a WPF button over
/// the plot (requirement H-2), a facet, and a figure. Drag to pan, turn the wheel to zoom, double-click for the full view.
/// <code>
/// dotnet run --project samples/WpfPlot -c Release [-- --points 1e8] [--measure]
/// </code>
/// <c>--points</c> sets each time series' points (1e7 by default; 1e9 needs 8 GB). <c>--measure</c> takes the measurements
/// of design §9 "WPF のホスト" and prints them.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var points = 10_000_000L;
        var measure = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--points":
                    points = (long)double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--measure":
                    measure = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option {args[i]}.");
                    return 2;
            }
        }

        if (points is < 2 or > int.MaxValue)
        {
            Console.Error.WriteLine($"--points is {points}; it is from 2 to {int.MaxValue}.");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow((int)points, measure);
        return app.Run(window);
    }
}
