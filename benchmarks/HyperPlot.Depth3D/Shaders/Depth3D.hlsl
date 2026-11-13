// SPIKE CODE (row K1, Issue #509): how 3D polylines and markers composite with depth, for measurement only. Not product code.
//
// Every shape is projected on the GPU from float32 positions in the box's coordinates ([-1, 1]^3) by one 4x4 transform;
// the per-node integers of design §13.2 are left out (the spike measures the compositing, not the precision). Pixel
// positions have their origin at the target's top left, and SV_Position.xy is the pixel center.
//
// A projected 3D segment is a straight segment on the screen, and its NDC depth is affine along it in screen space, so the
// depth of a pixel of a stroke is the depth at the point of the segment nearest the pixel's center: lerp(z0, z1, t) with t
// the screen-space parameter of that point (the CPU reference uses the same rule for each of its samples).
//
// Lines (one color per polyline, as DrawPolyline in 2D; design §5.3):
//   none      the 2D three passes, no depth: coverage of every segment into R8 with MAX, the color over the polyline's box
//             times that coverage, 0 back.
//   test      the same, the coverage pass tested against the depth buffer (LESS_EQUAL, no write): a fragment behind what is
//             already there adds no coverage. The polyline writes no depth.
//   prepass   a depth pass first: every segment writes its depth where its own coverage reaches Threshold (LESS_EQUAL), then
//             the three passes with the coverage pass tested against it.
//   postpass  the three passes of test, then the depth pass of prepass, for the polylines drawn later.
//   capsules  one pass for all polylines: each segment blends its color times its coverage over the target, with a depth
//             test (LESS) and write wherever its coverage is above 0. Consecutive segments blend twice where they overlap.
//   uav       the coverage pass, untested, also takes InterlockedMin of its depth bits on an R32_UINT texture; the color pass
//             outputs that least depth (SV_Depth) and is tested against the depth buffer (LESS_EQUAL), all or nothing per
//             pixel; a depth pass writes it where the polyline's coverage reaches Threshold; a reset pass writes 0 and
//             0xFFFFFFFF back.
//   uavfrag   uav with the depth test moved into the coverage pass (per fragment, as test), the color pass untested.
//
// Markers (camera-facing discs at the depth of their center, coverage as in 2D, row R3f):
//   none      2D: color times coverage over the target in the data's order, no depth.
//   quad      depth test (LESS) and write of the center's depth over the whole quad, corners of no coverage included.
//   touched   the same where the coverage is above 0.
//   half      two passes over all markers: the pixels whose coverage reaches Threshold blend and write depth (LESS), then the
//             pixels below it blend with a depth test and no write.

cbuffer Frame : register(b0)
{
    row_major float4x4 Transform;   // the box's coordinates to clip space, for row vectors: mul(float4(p, 1), Transform)
    float2 Size;                    // the target's size in pixels
    float2 InverseSize;
    float HalfWidth;                // lines: half the width in pixels
    float Radius;                   // markers: the disc's radius in pixels
    float Threshold;                // the coverage from which a pass writes depth
    uint SegmentStart;              // the polyline's first segment in Segments
    float4 Color;                   // the polyline's color, premultiplied, 0..1
    float4 Bounds;                  // the polyline's box in pixels: left, top, right, bottom
};

struct Vertex3
{
    float3 Position;
    uint Color;                     // RGBA8, R in the low byte
};

StructuredBuffer<Vertex3> Vertices : register(t0);
StructuredBuffer<uint> Segments : register(t1);     // the first vertex of each segment; the segment ends at the next
Texture2D<float> Coverage : register(t2);
Texture2D<uint> LineDepthRead : register(t3);
RWTexture2D<uint> LineDepth : register(u1);         // slot 0 is the render target's

// The surface: opaque, the color the CPU reference uses (bytes 140, 153, 166).
static const float4 PlaneColor = float4(140.0 / 255.0, 153.0 / 255.0, 166.0 / 255.0, 1);

float4 Unpack(uint color)
{
    return float4(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF, color >> 24) * (1.0 / 255.0);
}

// The pixel position of a point and its depth in [0, 1]; false outside the frustum. precise: every pass must find the same
// bits for the same vertex.
bool Project(float3 position, out float2 pixel, out float depth)
{
    precise float4 clipped = mul(float4(position, 1), Transform);
    precise float3 ndc = clipped.xyz / clipped.w;
    pixel = float2((ndc.x * 0.5 + 0.5) * Size.x, (0.5 - ndc.y * 0.5) * Size.y);
    depth = ndc.z;
    return clipped.w > 0 && ndc.z >= 0 && ndc.z <= 1;
}

float4 ToClip(float2 pixel, float depth)
{
    return float4(pixel.x * InverseSize.x * 2 - 1, 1 - pixel.y * InverseSize.y * 2, depth, 1);
}

// The bits of a depth in [0, 1] order as the depths do; the sign bit is cleared for a depth of -0.
uint DepthKey(float depth)
{
    return asuint(depth) & 0x7FFFFFFF;
}

// ---------------------------------------------------------------------------------------------------------------------
// The surface: the square [-1, 1]^2 at z = 0, two triangles, depth test (LESS) and write.

static const uint PlaneCorners[6] = { 0, 1, 2, 2, 1, 3 };

float4 PlaneVS(uint vertex : SV_VertexID) : SV_Position
{
    uint corner = PlaneCorners[vertex];
    float3 position = float3((corner & 1) != 0 ? 1 : -1, (corner & 2) != 0 ? 1 : -1, 0);
    return mul(float4(position, 1), Transform);
}

float4 PlanePS(float4 position : SV_Position) : SV_Target
{
    return PlaneColor;
}

// ---------------------------------------------------------------------------------------------------------------------
// Lines

struct SegmentVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Start : START;
    nointerpolation float2 End : END;
    nointerpolation float2 Depths : DEPTHS;     // at Start and at End
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,-1) (0,1) (1,-1) (1,1) in (end, side), the segment widened by half the width and 1
// pixel as in 2D. The quad lies at the nearer end's depth, so that a pixel shader that outputs SV_DepthGreaterEqual keeps
// the early depth test.
SegmentVertex SegmentVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    uint first = Segments[SegmentStart + instance];
    Vertex3 a = Vertices[first];
    Vertex3 b = Vertices[first + 1];
    float2 start;
    float2 end;
    float startDepth;
    float endDepth;
    bool visible = Project(a.Position, start, startDepth);
    visible = Project(b.Position, end, endDepth) && visible;
    float2 direction = end - start;
    float length = sqrt(dot(direction, direction));
    float2 tangent = length > 1e-6 ? direction / length : float2(1, 0);
    float2 normal = float2(-tangent.y, tangent.x);
    float reach = HalfWidth + 1;
    float side = (vertex & 1) != 0 ? 1 : -1;
    float2 corner = ((vertex & 2) != 0 ? end + tangent * reach : start - tangent * reach) + normal * side * reach;
    SegmentVertex output;

    // A segment with an end outside the frustum gets the same corner four times: a quad of no area draws nothing.
    output.Position = visible ? ToClip(corner, min(startDepth, endDepth)) : float4(-2, -2, 0, 1);
    output.Start = start;
    output.End = end;
    output.Depths = float2(startDepth, endDepth);
    output.Color = Unpack(a.Color);
    return output;
}

// The share of the pixel the segment's stroke covers (design §5.3) and the depth of the segment's point nearest the
// pixel's center, never below the quad's depth (the bound SV_DepthGreaterEqual promises). precise: the depth pass and the
// coverage pass of prepass compare the depths for equality.
void Stroke(SegmentVertex input, out float coverage, out float depth)
{
    float2 p = input.Position.xy;
    float2 direction = input.End - input.Start;
    precise float lengthSquared = dot(direction, direction);
    precise float t = lengthSquared > 1e-12 ? saturate(dot(p - input.Start, direction) / lengthSquared) : 0;
    float d = distance(p, input.Start + direction * t);
    coverage = saturate(0.5 + HalfWidth - d) - saturate(0.5 - HalfWidth - d);
    precise float along = input.Depths.x + (input.Depths.y - input.Depths.x) * t;
    depth = max(along, min(input.Depths.x, input.Depths.y));
}

// none: the 2D coverage.
float CoveragePS(SegmentVertex input) : SV_Target
{
    float coverage;
    float depth;
    Stroke(input, coverage, depth);
    return coverage;
}

// test, prepass, postpass: the coverage of a fragment that passes the depth test.
float CoverageDepthPS(SegmentVertex input, out float depth : SV_DepthGreaterEqual) : SV_Target
{
    float coverage;
    Stroke(input, coverage, depth);
    return coverage;
}

// prepass, postpass: the depth of the fragments whose coverage reaches Threshold, nothing else.
void DepthOnlyPS(SegmentVertex input, out float depth : SV_DepthGreaterEqual)
{
    float coverage;
    Stroke(input, coverage, depth);
    if (coverage <= 0 || coverage < Threshold)
    {
        discard;
    }
}

// capsules: the color times the coverage, and the depth wherever the coverage is above 0.
float4 CapsulePS(SegmentVertex input, out float depth : SV_DepthGreaterEqual) : SV_Target
{
    float coverage;
    Stroke(input, coverage, depth);
    if (coverage <= 0)
    {
        discard;
    }

    return input.Color * coverage;
}

// uav, uavfrag: the coverage, and the least depth of the fragments that cover the pixel. In uavfrag a fragment that then
// fails the depth test still takes its InterlockedMin: its depth is greater than any fragment's that passes, so the least
// depth of a pixel with coverage is a passing fragment's, and a pixel with only failing fragments has no coverage.
float UavCoveragePS(SegmentVertex input, out float depth : SV_DepthGreaterEqual) : SV_Target
{
    float coverage;
    Stroke(input, coverage, depth);
    if (coverage > 0)
    {
        InterlockedMin(LineDepth[(uint2)input.Position.xy], DepthKey(depth));
    }

    return coverage;
}

// The polyline's box, at depth 0.
float4 BoundsVS(uint vertex : SV_VertexID) : SV_Position
{
    float2 corner = float2((vertex & 2) != 0 ? Bounds.z : Bounds.x, (vertex & 1) != 0 ? Bounds.w : Bounds.y);
    return ToClip(corner, 0);
}

float4 ColorPS(float4 position : SV_Position) : SV_Target
{
    return Color * Coverage.Load(int3((int2)position.xy, 0));
}

float ZeroPS(float4 position : SV_Position) : SV_Target
{
    return 0;
}

// uav: the color where the polyline's least depth passes the depth test (uavfrag binds no test).
float4 UavColorPS(float4 position : SV_Position, out float depth : SV_Depth) : SV_Target
{
    int3 at = int3((int2)position.xy, 0);
    float coverage = Coverage.Load(at);
    depth = asfloat(LineDepthRead.Load(at));
    if (coverage <= 0)
    {
        discard;
    }

    return Color * coverage;
}

// uav, uavfrag: the least depth where the polyline's coverage reaches Threshold.
void UavDepthPS(float4 position : SV_Position, out float depth : SV_Depth)
{
    int3 at = int3((int2)position.xy, 0);
    float coverage = Coverage.Load(at);
    depth = asfloat(LineDepthRead.Load(at));
    if (coverage <= 0 || coverage < Threshold)
    {
        discard;
    }
}

// uav, uavfrag: 0 back into the coverage and no depth back into the least depth, for the next polyline.
float UavResetPS(float4 position : SV_Position) : SV_Target
{
    LineDepth[(uint2)position.xy] = 0xFFFFFFFF;
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Markers

struct MarkerVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Center : CENTER;
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the disc's box widened outward to whole pixels
// and half a pixel more, as in 2D (Markers.hlsl), at the depth of the center.
MarkerVertex MarkerVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Vertex3 point3 = Vertices[instance];
    float2 center;
    float depth;
    bool visible = Project(point3.Position, center, depth);
    float2 low = floor(center - Radius - 0.5);
    float2 high = ceil(center + Radius + 0.5);
    float2 corner = float2((vertex & 2) != 0 ? high.x : low.x, (vertex & 1) != 0 ? high.y : low.y);
    MarkerVertex output;
    output.Position = visible ? ToClip(corner, depth) : float4(-2, -2, 0, 1);
    output.Center = center;
    output.Color = Unpack(point3.Color);
    return output;
}

// The disc's coverage as in 2D: the edge crosses a pixel in a band half a pixel to each side of the radius.
float DiscCoverage(MarkerVertex input)
{
    return saturate(Radius + 0.5 - length(input.Position.xy - input.Center));
}

// none, quad: the whole quad.
float4 MarkerPS(MarkerVertex input) : SV_Target
{
    return input.Color * DiscCoverage(input);
}

// touched: the pixels the disc covers at all.
float4 MarkerTouchedPS(MarkerVertex input) : SV_Target
{
    float coverage = DiscCoverage(input);
    if (coverage <= 0)
    {
        discard;
    }

    return input.Color * coverage;
}

// half, first pass: the pixels whose coverage reaches Threshold.
float4 MarkerCorePS(MarkerVertex input) : SV_Target
{
    float coverage = DiscCoverage(input);
    if (coverage <= 0 || coverage < Threshold)
    {
        discard;
    }

    return input.Color * coverage;
}

// half, second pass: the rest of the edge.
float4 MarkerFringePS(MarkerVertex input) : SV_Target
{
    float coverage = DiscCoverage(input);
    if (coverage <= 0 || coverage >= Threshold)
    {
        discard;
    }

    return input.Color * coverage;
}
