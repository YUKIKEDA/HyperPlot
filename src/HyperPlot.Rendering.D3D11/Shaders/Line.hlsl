// Anti-aliased polylines (design §5.1, §5.3). Three passes per polyline:
//   1. SegmentVS + CoveragePS: one quad per segment writes the share of each pixel the stroke covers into an R8 texture,
//      combined with MAX, so that the polyline covers each pixel once, joins included.
//   2. BoundsVS + ColorPS: one quad over the widened bounding box multiplies the premultiplied color by that share.
//   3. BoundsVS + ZeroPS: the same quad writes 0 back for the next polyline.
// Positions are physical pixels of the target, origin at the top left; SV_Position.xy is the pixel center.

cbuffer Line : register(b0)
{
    float2 InverseSize;   // 1 / target size
    uint SegmentStart;    // the polyline's first segment in Segments
    float HalfWidth;      // r
    float DashOn;
    float DashOff;
    float DashOffset;
    uint Dashed;          // 0: solid
    float4 Color;         // premultiplied, 0..1
    float4 Bounds;        // left, top, right, bottom in pixels
};

struct Segment
{
    float2 Start;
    float2 End;
    float Along;          // length along the run from its start to Start
};

StructuredBuffer<Segment> Segments : register(t0);
Texture2D<float> Coverage : register(t0);

struct SegmentVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Start : START;
    nointerpolation float2 End : END;
    nointerpolation float Along : ALONG;
};

float4 ToClip(float2 pixel)
{
    return float4(pixel.x * InverseSize.x * 2 - 1, 1 - pixel.y * InverseSize.y * 2, 0, 1);
}

// Corners 0..3 of a triangle strip: (0,-1) (0,1) (1,-1) (1,1) in (end, side).
SegmentVertex SegmentVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Segment segment = Segments[SegmentStart + instance];
    float2 direction = segment.End - segment.Start;
    float length = sqrt(dot(direction, direction));
    float2 tangent = length > 1e-6 ? direction / length : float2(1, 0);
    float2 normal = float2(-tangent.y, tangent.x);
    float reach = HalfWidth + 1;
    float side = (vertex & 1) != 0 ? 1 : -1;
    float2 corner = (vertex & 2) != 0 ? segment.End + tangent * reach : segment.Start - tangent * reach;
    SegmentVertex output;
    output.Position = ToClip(corner + normal * side * reach);
    output.Start = segment.Start;
    output.End = segment.End;
    output.Along = segment.Along;
    return output;
}

// Distance from p to the piece [from, to] of the segment, measured along it from its start; to < from means no piece.
float PieceDistance(float2 p, float2 start, float2 tangent, float from, float to)
{
    if (to < from)
    {
        return 1e9;
    }

    float along = clamp(dot(p - start, tangent), from, to);
    return distance(p, start + tangent * along);
}

float CoveragePS(SegmentVertex input) : SV_Target
{
    float2 p = input.Position.xy;
    float2 direction = input.End - input.Start;
    float length = sqrt(dot(direction, direction));
    float2 tangent = length > 1e-6 ? direction / length : float2(1, 0);
    float d;
    if (Dashed == 0)
    {
        d = PieceDistance(p, input.Start, tangent, 0, length);
    }
    else
    {
        // The pattern position of the point of the segment nearest p; the dash there and the next one are the nearest.
        float period = DashOn + DashOff;
        float u = input.Along + clamp(dot(p - input.Start, tangent), 0, length) + DashOffset;
        float k = floor(u / period);
        float shift = input.Along + DashOffset;
        float first = k * period - shift;
        float second = first + period;
        d = min(
            PieceDistance(p, input.Start, tangent, max(first, 0), min(first + DashOn, length)),
            PieceDistance(p, input.Start, tangent, max(second, 0), min(second + DashOn, length)));
    }

    // The length of [-r, r] inside the pixel's 1-wide box around d (design §5.3).
    return saturate(0.5 + HalfWidth - d) - saturate(0.5 - HalfWidth - d);
}

float4 BoundsVS(uint vertex : SV_VertexID) : SV_Position
{
    float2 corner = float2((vertex & 2) != 0 ? Bounds.z : Bounds.x, (vertex & 1) != 0 ? Bounds.w : Bounds.y);
    return ToClip(corner);
}

float4 ColorPS(float4 position : SV_Position) : SV_Target
{
    return Color * Coverage.Load(int3(position.xy, 0));
}

float ZeroPS(float4 position : SV_Position) : SV_Target
{
    return 0;
}
