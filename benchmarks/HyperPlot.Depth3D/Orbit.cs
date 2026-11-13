using System.Numerics;

namespace HyperPlot.Depth3D;

/// <summary>The camera of the scenes (SPIKE, row K1): a turntable orbit around the box [-1, 1]³.</summary>
internal static class Orbit
{
    public const int Width = 1920;
    public const int Height = 1080;

    /// <summary>The frames of one turn.</summary>
    public const int Turn = 300;

    /// <summary>
    /// The transform of a frame: the camera turns about the box's vertical axis (z), 360° every 300 frames, 30° above the
    /// horizon, at the distance where the box's bounding sphere (radius √3) fits the vertical field of view of 45° (design
    /// §13.8, Issue #470 decision 16); the near and far planes hold the sphere. As in the K0a spike (Issue #477).
    /// </summary>
    public static Matrix4x4 Transform(int frame)
    {
        const float FieldOfView = MathF.PI / 4;
        const float Elevation = MathF.PI / 6;
        var angle = 2 * MathF.PI * (frame % Turn) / Turn;
        var radius = MathF.Sqrt(3);
        var distance = radius / MathF.Sin(FieldOfView / 2);
        var eye = new Vector3(MathF.Cos(Elevation) * MathF.Cos(angle), MathF.Cos(Elevation) * MathF.Sin(angle), MathF.Sin(Elevation)) * distance;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, Width / (float)Height, distance - radius - 0.1f, distance + radius + 0.1f);
        return view * projection;
    }
}
