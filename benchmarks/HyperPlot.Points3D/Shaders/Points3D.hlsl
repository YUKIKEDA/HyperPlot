// SPIKE CODE (row K0a, Issue #477): two ways to draw 3D points, for measurement only. Not product code.
//
// A, the marker pipeline with depth: MarkerVS makes one quad per point from SV_VertexID over the pixels the point covers,
// at the depth of the point's center; DiscPS (a hard-edged disc of a diameter in pixels) or SquarePS (k × k pixels from
// the floor of the center, the pixels B covers) writes an opaque color, with a D32_FLOAT depth test (LESS) and write.
// Coverage blending at the edge is left out: a depth write needs whole pixels (row K0d decides translucency).
//
// B, compute rasterization: DepthCS projects each point and takes InterlockedMin of its depth's bits on an R32_UINT
// texture; IndexCS takes, for each point whose depth equals the stored minimum, InterlockedMin of the point's index on a
// second R32_UINT texture, so that a tie of equal depths goes to the first point as the depth test LESS does in A, and
// the result does not depend on the order the threads ran in; ResolvePS turns the index into the point's color over the
// target and writes the depth to the depth buffer. D3D11 has 32-bit atomics only (D3D11.3 §22.17.16), so the depth and
// the color cannot go into one 64-bit atomicMin as in Schütz et al. 2021.
//
// Positions are float32 in the box's coordinates ([-1, 1]³), not the per-node integers less a per-frame base of design
// §13.2 (Issue #470, decisions 12 and 17): the spike measures the drawing, and keeps the 16 bytes a point of that form.

cbuffer Frame : register(b0)
{
    row_major float4x4 Transform;   // the box's coordinates to clip space, for row vectors: mul(float4(p, 1), Transform)
    float2 Size;                    // the target's size in pixels
    float2 InverseSize;
    float Diameter;                 // A's disc: the diameter in pixels
    uint Footprint;                 // the side k of the k × k pixels of a square (A's SquarePS, and B)
    uint Count;                     // the points
    uint GroupsX;                   // B: the groups along x of the dispatch
};

struct Point3
{
    float3 Position;
    uint Color;                     // RGBA8, R in the low byte
};

StructuredBuffer<Point3> Points : register(t0);

float4 Unpack(uint color)
{
    return float4(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF, color >> 24) * (1.0 / 255.0);
}

// The pixel position of a point (origin at the target's top left) and its depth in [0, 1]; false outside the frustum.
// precise: both passes of B must find the same bits for the same point, so the compiler may not reorder the arithmetic.
bool Project(float3 position, out float2 pixel, out float depth)
{
    precise float4 clipped = mul(float4(position, 1), Transform);
    precise float3 ndc = clipped.xyz / clipped.w;
    pixel = float2((ndc.x * 0.5 + 0.5) * Size.x, (0.5 - ndc.y * 0.5) * Size.y);
    depth = ndc.z;
    return clipped.w > 0 && ndc.z >= 0 && ndc.z <= 1;
}

// The first pixel of a square of Footprint pixels around the pixel the point falls in.
float2 SquareLow(float2 pixel)
{
    return floor(pixel) - (float)((Footprint - 1) / 2);
}

// ---------------------------------------------------------------------------------------------------------------------
// A: the marker pipeline with depth

struct MarkerVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Center : CENTER;
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the point's pixels.
MarkerVertex MarkerVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Point3 p = Points[instance];
    float2 center;
    float depth;
    bool visible = Project(p.Position, center, depth);

    // The disc covers the pixels whose centers lie within its radius; the square, Footprint pixels a side.
    float radius = Diameter * 0.5;
    float2 low = Footprint != 0 ? SquareLow(center) : floor(center - radius);
    float2 high = Footprint != 0 ? low + (float)Footprint : ceil(center + radius);
    float2 corner = float2((vertex & 2) != 0 ? high.x : low.x, (vertex & 1) != 0 ? high.y : low.y);
    MarkerVertex output;

    // A point outside the frustum gets the same corner four times: a quad of no area draws nothing.
    output.Position = visible
        ? float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, depth, 1)
        : float4(-2, -2, 0, 1);
    output.Center = center;
    output.Color = Unpack(p.Color);
    return output;
}

float4 DiscPS(MarkerVertex input) : SV_Target
{
    // Hard edged: a pixel is in or out, so that its depth is the point's.
    clip(Diameter * 0.5 - length(input.Position.xy - input.Center));
    return input.Color;
}

float4 SquarePS(MarkerVertex input) : SV_Target
{
    return input.Color;
}

// ---------------------------------------------------------------------------------------------------------------------
// B: compute rasterization

RWTexture2D<uint> Depth : register(u0);
RWTexture2D<uint> Nearest : register(u1);
Texture2D<uint> DepthRead : register(t1);
Texture2D<uint> NearestRead : register(t2);

// A dispatch has at most 65,535 groups along an axis: the points run over (x, y) groups of 256 threads.
uint PointIndex(uint3 group, uint thread)
{
    return (group.y * GroupsX + group.x) * 256 + thread;
}

// The bits of a depth in [0, 1] order as the depths do; the sign bit is cleared for a depth of -0.
uint DepthKey(float depth)
{
    return asuint(depth) & 0x7FFFFFFF;
}

// Out-of-range addresses of a UAV are ignored by D3D11 (writes and atomics do nothing), so a square that crosses the
// target's edge is not clipped here.
[numthreads(256, 1, 1)]
void DepthCS(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint i = PointIndex(group, thread);
    if (i >= Count)
    {
        return;
    }

    float2 pixel;
    float depth;
    if (!Project(Points[i].Position, pixel, depth))
    {
        return;
    }

    int2 low = (int2)SquareLow(pixel);
    uint key = DepthKey(depth);
    [loop]
    for (uint dy = 0; dy < Footprint; dy++)
    {
        [loop]
        for (uint dx = 0; dx < Footprint; dx++)
        {
            InterlockedMin(Depth[(uint2)(low + int2(dx, dy))], key);
        }
    }
}

[numthreads(256, 1, 1)]
void IndexCS(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint i = PointIndex(group, thread);
    if (i >= Count)
    {
        return;
    }

    float2 pixel;
    float depth;
    if (!Project(Points[i].Position, pixel, depth))
    {
        return;
    }

    int2 low = (int2)SquareLow(pixel);
    uint key = DepthKey(depth);
    [loop]
    for (uint dy = 0; dy < Footprint; dy++)
    {
        [loop]
        for (uint dx = 0; dx < Footprint; dx++)
        {
            int2 at = low + int2(dx, dy);

            // An address outside the texture reads 0; the atomic there does nothing even when the key is 0.
            if (DepthRead.Load(int3(at, 0)) == key)
            {
                InterlockedMin(Nearest[(uint2)at], i);
            }
        }
    }
}

// A triangle over the whole target.
float4 ResolveVS(uint vertex : SV_VertexID) : SV_Position
{
    float2 at = float2((vertex << 1) & 2, vertex & 2);
    return float4(at.x * 2 - 1, 1 - at.y * 2, 0, 1);
}

// The color of the nearest point, and its depth for what is drawn after (the axes' box, lines). A pixel with a depth and
// no index (the two passes found different bits for a depth) is magenta, so that the determinism check counts it.
float4 ResolvePS(float4 position : SV_Position, out float depth : SV_Depth) : SV_Target
{
    int3 at = int3((int2)position.xy, 0);
    uint key = DepthRead.Load(at);
    uint index = NearestRead.Load(at);
    clip(key == 0xFFFFFFFF ? -1 : 1);
    depth = asfloat(key);
    return index == 0xFFFFFFFF ? float4(1, 0, 1, 1) : Unpack(Points[index].Color);
}
