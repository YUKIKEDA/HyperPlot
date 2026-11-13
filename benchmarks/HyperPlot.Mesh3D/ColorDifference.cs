namespace HyperPlot.Mesh3D;

/// <summary>
/// CIEDE2000 (ΔE00) and CIE76 (ΔE*ab) between two 8-bit sRGB colors, through CIELAB with the D65 white (SPIKE, row K0d).
/// The formula is Sharma, Wu, and Dalal 2005, "The CIEDE2000 color-difference formula: implementation notes, supplementary
/// test data, and mathematical observations", Color Res. Appl. 30(1), equations 1 to 22; kL = kC = kH = 1.
/// </summary>
internal static class ColorDifference
{
    private static readonly double[] _linear = MakeLinear();

    /// <summary>The CIELAB of an 8-bit sRGB color.</summary>
    public static (double L, double A, double B) Lab(byte r, byte g, byte b)
    {
        var (lr, lg, lb) = (_linear[r], _linear[g], _linear[b]);
        var x = ((0.4124564 * lr) + (0.3575761 * lg) + (0.1804375 * lb)) / 0.95047;
        var y = (0.2126729 * lr) + (0.7151522 * lg) + (0.0721750 * lb);
        var z = ((0.0193339 * lr) + (0.1191920 * lg) + (0.9503041 * lb)) / 1.08883;
        var (fx, fy, fz) = (F(x), F(y), F(z));
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIE76: the Euclidean distance in CIELAB.</summary>
    public static double Cie76((double L, double A, double B) first, (double L, double A, double B) second)
    {
        var (dl, da, db) = (first.L - second.L, first.A - second.A, first.B - second.B);
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    /// <summary>CIEDE2000.</summary>
    public static double Ciede2000((double L, double A, double B) first, (double L, double A, double B) second)
    {
        const double Pow25To7 = 6103515625.0;
        var (l1, a1, b1) = first;
        var (l2, a2, b2) = second;
        var c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        var c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        var cMean7 = Math.Pow((c1 + c2) / 2, 7);
        var g = 0.5 * (1 - Math.Sqrt(cMean7 / (cMean7 + Pow25To7)));
        var (a1p, a2p) = ((1 + g) * a1, (1 + g) * a2);
        var c1p = Math.Sqrt((a1p * a1p) + (b1 * b1));
        var c2p = Math.Sqrt((a2p * a2p) + (b2 * b2));
        var h1p = Hue(b1, a1p);
        var h2p = Hue(b2, a2p);

        var dLp = l2 - l1;
        var dCp = c2p - c1p;
        double dhp;
        if (c1p * c2p == 0)
        {
            dhp = 0;
        }
        else
        {
            dhp = h2p - h1p;
            dhp =
                dhp > 180 ? dhp - 360
                : dhp < -180 ? dhp + 360
                : dhp;
        }

        var dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Radians(dhp / 2));
        var lMean = (l1 + l2) / 2;
        var cMeanP = (c1p + c2p) / 2;
        double hMean;
        if (c1p * c2p == 0)
        {
            hMean = h1p + h2p;
        }
        else if (Math.Abs(h1p - h2p) <= 180)
        {
            hMean = (h1p + h2p) / 2;
        }
        else
        {
            hMean = h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;
        }

        var t =
            1
            - (0.17 * Math.Cos(Radians(hMean - 30)))
            + (0.24 * Math.Cos(Radians(2 * hMean)))
            + (0.32 * Math.Cos(Radians((3 * hMean) + 6)))
            - (0.20 * Math.Cos(Radians((4 * hMean) - 63)));
        var dTheta = 30 * Math.Exp(-Math.Pow((hMean - 275) / 25, 2));
        var cMeanP7 = Math.Pow(cMeanP, 7);
        var rc = 2 * Math.Sqrt(cMeanP7 / (cMeanP7 + Pow25To7));
        var l50 = (lMean - 50) * (lMean - 50);
        var sl = 1 + (0.015 * l50 / Math.Sqrt(20 + l50));
        var sc = 1 + (0.045 * cMeanP);
        var sh = 1 + (0.015 * cMeanP * t);
        var rt = -Math.Sin(Radians(2 * dTheta)) * rc;
        var (lTerm, cTerm, hTerm) = (dLp / sl, dCp / sc, dHp / sh);
        return Math.Sqrt((lTerm * lTerm) + (cTerm * cTerm) + (hTerm * hTerm) + (rt * cTerm * hTerm));
    }

    private static double Hue(double b, double a)
    {
        if (a == 0 && b == 0)
        {
            return 0;
        }

        var degrees = Math.Atan2(b, a) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (t * 24389 / 27 / 116) + (16.0 / 116);

    private static double[] MakeLinear()
    {
        var table = new double[256];
        for (var i = 0; i < table.Length; i++)
        {
            var c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
