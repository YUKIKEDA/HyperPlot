using System.Numerics;

namespace HyperPlot.Lines3D;

/// <summary>
/// A frame's view (SPIKE, row K0b): the axis ranges as a center and a scale k per axis that map the normalized space to the
/// box [-1, 1]³, and a perspective camera on the box.
/// </summary>
internal readonly struct View
{
    public const int Width = 1920;
    public const int Height = 1080;

    /// <summary>The near plane, in box units from the eye.</summary>
    public const float Near = 0.01f;

    public readonly Vector3 Center;
    public readonly Vector3 Scale;
    public readonly Vector3 Eye;
    public readonly Vector3 Right;
    public readonly Vector3 Up;
    public readonly Vector3 Forward;

    /// <summary>The focal length in pixels: a unit at depth 1 on the axis of view is this many pixels.</summary>
    public readonly float Focal;

    private readonly Vector3 _absRight;
    private readonly Vector3 _absUp;
    private readonly Vector3 _absForward;

    /// <summary>
    /// Initializes a new instance of the <see cref="View"/> struct. The camera looks at the box's center from the azimuth and
    /// the elevation (z up), at the distance where the box's bounding sphere fits the vertical field of view of 45°.
    /// </summary>
    public View(Vector3 center, Vector3 scale, double azimuth, double elevation)
    {
        Center = center;
        Scale = scale;
        var fovY = Math.PI / 4;
        var distance = Math.Sqrt(3) / Math.Sin(fovY / 2);
        Eye = new Vector3(
            (float)(distance * Math.Cos(elevation) * Math.Cos(azimuth)),
            (float)(distance * Math.Cos(elevation) * Math.Sin(azimuth)),
            (float)(distance * Math.Sin(elevation))
        );
        Forward = Vector3.Normalize(-Eye);
        Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitZ));
        Up = Vector3.Cross(Right, Forward);
        Focal = (float)(Height / 2.0 / Math.Tan(fovY / 2));
        _absRight = Vector3.Abs(Right);
        _absUp = Vector3.Abs(Up);
        _absForward = Vector3.Abs(Forward);
    }

    public Vector3 ToBox(Vector3 normalized) => (normalized - Center) * Scale;

    /// <summary>Box coordinates to the camera's: x right, y up, z the depth along the axis of view.</summary>
    public Vector3 ToCamera(Vector3 box)
    {
        var relative = box - Eye;
        return new Vector3(Vector3.Dot(relative, Right), Vector3.Dot(relative, Up), Vector3.Dot(relative, Forward));
    }

    /// <summary>Camera coordinates (z above the near plane) to pixels, y down.</summary>
    public static Vector2 Screen(Vector3 camera, float focal) =>
        new((Width / 2f) + (focal * camera.X / camera.Z), (Height / 2f) - (focal * camera.Y / camera.Z));

    public Vector2 Screen(Vector3 camera) => Screen(camera, Focal);

    public static bool Inside(Vector3 box) =>
        box.X >= -1 && box.X <= 1 && box.Y >= -1 && box.Y <= 1 && box.Z >= -1 && box.Z <= 1;

    /// <summary>Whether the normalized box [lo, hi] lies wholly outside the axis box, so that nothing of it is drawn.</summary>
    public bool Outside(Vector3 lo, Vector3 hi) => OutsideBox((lo - Center) * Scale, (hi - Center) * Scale);

    /// <summary>
    /// Whether the node with the chord (a, b) and the error vector e (normalized units) is left out: every interior vertex
    /// is outside the axis box, or each one inside it is under the tolerance on the screen from the chord.
    /// </summary>
    /// <remarks>
    /// Every interior vertex p_i lies within e of a point q_i of the chord on each axis, so the node's vertices are in the
    /// box of a and b grown by e; when that box misses the axis box, the node is culled. Otherwise, for a p_i inside the axis
    /// box, the segment from k⊙p_i to k⊙q_i lies in that box clamped to [-1 - k·e, 1 + k·e] on each axis, and its length
    /// is at most ‖k⊙e‖. The projection (f·x/z, f·y/z) has the Jacobian (f/z)[[1, 0, -x/z], [0, 1, -y/z]], whose norm
    /// is f·√(1 + (x/z)² + (y/z)²)/z; over the clamped box it is at most f·√(1 + (X/Z)² + (Y/Z)²)/Z, with Z the box's
    /// least depth and X, Y its largest |x|, |y| in camera coordinates. The rotation keeps lengths, so the distance on the
    /// screen from p_i to the drawn chord is at most that norm times ‖k⊙e‖.
    /// </remarks>
    public bool Prune(Vector3 a, Vector3 b, Vector3 error, float tolerance2)
    {
        var lo = (Vector3.Min(a, b) - error - Center) * Scale;
        var hi = (Vector3.Max(a, b) + error - Center) * Scale;
        if (OutsideBox(lo, hi))
        {
            return true;
        }

        var scaled = error * Scale;
        var bound = Vector3.One + scaled;
        lo = Vector3.Max(lo, -bound);
        hi = Vector3.Min(hi, bound);
        var middle = ((lo + hi) * 0.5f) - Eye;
        var half = (hi - lo) * 0.5f;
        var depth = Vector3.Dot(middle, Forward) - Vector3.Dot(half, _absForward);
        if (depth <= Near)
        {
            return false;
        }

        var x = MathF.Abs(Vector3.Dot(middle, Right)) + Vector3.Dot(half, _absRight);
        var y = MathF.Abs(Vector3.Dot(middle, Up)) + Vector3.Dot(half, _absUp);
        var ux = x / depth;
        var uy = y / depth;
        var gain = Focal / depth;
        return scaled.LengthSquared() * gain * gain * (1 + (ux * ux) + (uy * uy)) < tolerance2;
    }

    private static bool OutsideBox(Vector3 lo, Vector3 hi) =>
        hi.X < -1 || hi.Y < -1 || hi.Z < -1 || lo.X > 1 || lo.Y > 1 || lo.Z > 1;
}
