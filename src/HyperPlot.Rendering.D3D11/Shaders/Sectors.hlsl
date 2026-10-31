// Polar trapezoids (design §5.1, §5.3, row G11): the fills of a polar coordinate system. One call is one shape, drawn in
// three passes as LineMask.hlsl's polylines, so that trapezoids that share an edge leave no seam:
//   1. SectorVS + SectorMaskPS: each piece (a trapezoid cut to at most π / 16 on the CPU) is a quad around its wedge, and
//      the samples of the 32-sample pattern that lie in it are ORed into the R32_UINT mask with InterlockedOr.
//   2. SectorBoundsVS + SectorColorPS: one quad over the call's box multiplies the premultiplied color by the share set.
//   3. SectorBoundsVS + SectorZeroPS: the same quad writes 0 back for the next call.
// Angles are radians from 12 o'clock, clockwise on the target: the point at angle a and radius r is center + r (sin a, −cos a).
// Positions are physical pixels of the target, origin at the top left; SV_Position.xy is the pixel center.

cbuffer Sectors : register(b0)
{
    float2 InverseSize;    // 1 / target size
    uint First;            // the call's first piece in Instances
    uint Unused;
    float2 Center;         // in pixels
    float2 Unused2;
    float4 Color;          // premultiplied, 0..1
    float4 Bounds;         // left, top, right, bottom in pixels
};

struct Sector
{
    float From;
    float To;
    float2 Outer;          // at From, at To
    float2 Inner;          // at From, at To
    float2 Start;          // (sin From, −cos From), made on the CPU in double: the GPU's sin and cos err past a sample
    float2 End;            // (sin To, −cos To)
    float2 Unused;         // 48 bytes, as RenderList's SectorInstance
};

StructuredBuffer<Sector> Instances : register(t1);
RWTexture2D<uint> Mask : register(u0);
Texture2D<uint> MaskView : register(t0);

struct SectorVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Start : START;    // the direction of From
    nointerpolation float2 End : END;        // the direction of To
    nointerpolation float Span : SPAN;       // To − From
    nointerpolation float2 Outer : OUTER;
    nointerpolation float2 Inner : INNER;
};

// Sample i of a pixel: the n-rooks pattern of LineMask.hlsl (design §5.3).
float2 SampleAt(uint i)
{
    return float2((i + 0.5) / 32.0, (((13 * i) & 31) + 0.5) / 32.0);
}

float4 ToClip(float2 pixel)
{
    return float4(pixel.x * InverseSize.x * 2 - 1, 1 - pixel.y * InverseSize.y * 2, 0, 1);
}

// The point p with dot(p, a) = s and dot(p, b) = q.
float2 Meet(float2 a, float s, float2 b, float q)
{
    float determinant = a.x * b.y - a.y * b.x;
    return float2(s * b.y - a.y * q, a.x * q - b.x * s) / determinant;
}

// Corners 0..3 of a triangle strip, as RenderList.SectorQuad: the wedge between the sides' rays, cut by the line that
// touches the outer radius and by the chord of the inner one, each side pushed out by a pixel.
SectorVertex SectorVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Sector sector = Instances[First + instance];
    float low = max(0, min(min(sector.Outer.x, sector.Outer.y), min(sector.Inner.x, sector.Inner.y)));
    float high = max(0, max(max(sector.Outer.x, sector.Outer.y), max(sector.Inner.x, sector.Inner.y)));
    // The middle direction halves the piece; each side's normal is its direction turned a quarter back.
    float2 axis = normalize(sector.Start + sector.End);
    bool end = (vertex & 2) != 0;
    float2 direction = end ? sector.End : sector.Start;
    float2 side = float2(-direction.y, direction.x);
    float q = (vertex & 1) != 0 ? high + 1 : low * dot(axis, sector.Start) - 1;
    SectorVertex output;
    output.Position = ToClip(Center + Meet(side, end ? 1 : -1, axis, q));
    output.Start = sector.Start;
    output.End = sector.End;
    output.Span = sector.To - sector.From;
    output.Outer = sector.Outer;
    output.Inner = sector.Inner;
    return output;
}

float Cross(float2 a, float2 b)
{
    return a.x * b.y - a.y * b.x;
}

void SectorMaskPS(SectorVertex input)
{
    int2 pixel = int2(input.Position.xy);
    float low = min(min(input.Outer.x, input.Outer.y), min(input.Inner.x, input.Inner.y));
    float high = max(max(input.Outer.x, input.Outer.y), max(input.Inner.x, input.Inner.y));

    // The samples lie within √2 / 2 of the pixel's center. A pixel farther than that outside the radii or past a side is
    // left at once: most of the quads' margins near the center, where thin wedges meet.
    float2 center = float2(pixel) + 0.5 - Center;
    float centerRadius = length(center);
    float fromSide = Cross(input.Start, center);
    float toSide = Cross(input.End, center);
    if (centerRadius < low - 0.7072 || centerRadius > high + 0.7072 || fromSide < -0.7072 || toSide > 0.7072)
    {
        return;
    }

    // Radii that do not change with the angle, and a pixel that far inside every edge, hold all 32 samples.
    bool constant = input.Outer.x == input.Outer.y && input.Inner.x == input.Inner.y;
    if (constant && centerRadius >= low + 0.7072 && centerRadius <= high - 0.7072 && fromSide >= 0.7072 && toSide <= -0.7072)
    {
        InterlockedOr(Mask[pixel], 0xFFFFFFFF);
        return;
    }

    // Each sample's sides and squared radius are the pixel center's plus a constant of the sample: the unrolled loop is a
    // few multiply-adds a sample. Radii that change with the angle (a sloped edge, rarer) take the angle of each sample in
    // a loop of their own, chosen once a pixel: inside the common loop, the compiler evaluates atan2 for every sample.
    uint bits = 0;
    float centerSquared = dot(center, center);
    [branch]
    if (constant)
    {
        float near = max(low, 0);
        float2 bounds = float2(near * near, high * high);
        [unroll]
        for (uint i = 0; i < 32; i++)
        {
            float2 offset = SampleAt(i) - 0.5;
            float squared = centerSquared + 2 * dot(center, offset) + dot(offset, offset);
            bool inside = fromSide + Cross(input.Start, offset) >= 0 && toSide + Cross(input.End, offset) <= 0;
            bits |= inside && squared >= bounds.x && squared <= bounds.y ? 1u << i : 0;
        }
    }
    else
    {
        [loop]
        for (uint i = 0; i < 32; i++)
        {
            float2 p = center + SampleAt(i) - 0.5;
            float along = Cross(input.Start, p);
            if (along < 0 || Cross(input.End, p) > 0)
            {
                continue;
            }

            float share = input.Span > 0 ? saturate(atan2(along, dot(input.Start, p)) / input.Span) : 0;
            float outer = lerp(input.Outer.x, input.Outer.y, share);
            float inner = lerp(input.Inner.x, input.Inner.y, share);
            float r = length(p);
            bits |= r >= min(inner, outer) && r <= max(inner, outer) ? 1u << i : 0;
        }
    }

    if (bits != 0)
    {
        InterlockedOr(Mask[pixel], bits);
    }
}

// Corners 0..3 of a triangle strip over the call's box.
float4 SectorBoundsVS(uint vertex : SV_VertexID) : SV_Position
{
    return ToClip(float2((vertex & 2) != 0 ? Bounds.z : Bounds.x, (vertex & 1) != 0 ? Bounds.w : Bounds.y));
}

float4 SectorColorPS(float4 position : SV_Position) : SV_Target
{
    return Color * (countbits(MaskView.Load(int3(position.xy, 0))) / 32.0);
}

void SectorZeroPS(float4 position : SV_Position)
{
    Mask[int2(position.xy)] = 0;
}
