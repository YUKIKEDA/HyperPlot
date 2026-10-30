// Polylines with joins or caps that are not round, or with more than one dash and gap (design §5.1, §5.3, row R4). Three
// passes per polyline, as Line.hlsl's, with the coverage kept as 32 samples a pixel instead of a distance:
//   1. SegmentVS (Line.hlsl) + SegmentMaskPS, then PieceVS + PieceMaskPS: each segment's box (cut flat at its ends, with
//      the caps of the dashes that end inside it) and each join and cap piece set the bits of the samples they cover in an
//      R32_UINT texture with InterlockedOr, so that the polyline is the exact union of its pieces at the samples.
//   2. BoundsVS (Line.hlsl) + MaskColorPS: the color times the share of the bits set.
//   3. BoundsVS + MaskZeroPS: 0 back for the next polyline.
// The constant buffer begins as Line.hlsl's, so that its vertex shaders read the same fields.

cbuffer Line : register(b0)
{
    float2 InverseSize;
    uint SegmentStart;
    float HalfWidth;       // r
    uint PieceStart;
    uint Cap;              // 0 round, 1 butt, 2 square
    float DashOffset;
    uint IntervalCount;    // 0: solid; else 2 to 8, dash and gap in turn
    float4 Color;          // premultiplied, 0..1
    float4 Bounds;         // left, top, right, bottom in pixels
    float4 Ends[2];        // the ends of the intervals 0 to 7, summed from the pattern's start; the last one used is the period
};

struct Piece
{
    float2 Point;
    float2 In;             // a join's incoming direction; a cap's direction away from the line
    float2 Out;            // a join's outgoing direction
    uint Kind;             // 0 bevel, 1 miter, 2 disk, 3 square cap, 4 square
};

StructuredBuffer<Piece> Pieces : register(t1);
RWTexture2D<uint> Mask : register(u0);
Texture2D<uint> MaskView : register(t0);

struct SegmentVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Start : START;
    nointerpolation float2 End : END;
    nointerpolation float Along : ALONG;
};

struct PieceVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Point : POINT;
    nointerpolation float2 In : IN;
    nointerpolation float2 Out : OUT;
    nointerpolation uint Kind : KIND;
};

// Sample i of a pixel: an n-rooks pattern, one sample in each of the 32 rows and 32 columns (design §5.3).
float2 SampleAt(uint i)
{
    return float2((i + 0.5) / 32.0, (((13 * i) & 31) + 0.5) / 32.0);
}

float IntervalEnd(uint i)
{
    return Ends[(i >> 2) & 1][i & 3];
}

float4 ToClip(float2 pixel)
{
    return float4(pixel.x * InverseSize.x * 2 - 1, 1 - pixel.y * InverseSize.y * 2, 0, 1);
}

// Whether a point at a along the segment and c across it lies in the dash [from, to] (along the segment). An end inside the
// segment gets the cap; an end at 0 or at the segment's length, or past them, is cut flat, since the pieces of the CPU (the
// join, or the cap where a dash ends at a joint) cover it there.
bool InDash(float a, float c, float from, float to, float length)
{
    float low = max(from, 0);
    float high = min(to, length);
    bool zero = from == to && from > 0 && from < length;
    if (!(high > low) && !zero)
    {
        return false;
    }

    bool startCap = from > 0;
    bool endCap = to < length;
    if (Cap == 2)
    {
        low -= startCap ? HalfWidth : 0;
        high += endCap ? HalfWidth : 0;
    }

    if (abs(c) <= HalfWidth && a >= low && a <= high && (Cap != 1 || high > low))
    {
        return true;
    }

    if (Cap == 0)
    {
        float r2 = HalfWidth * HalfWidth;
        return (startCap && dot(float2(a - low, c), float2(a - low, c)) <= r2) || (endCap && dot(float2(a - high, c), float2(a - high, c)) <= r2);
    }

    return false;
}

void SegmentMaskPS(SegmentVertex input)
{
    int2 pixel = int2(input.Position.xy);
    float2 direction = input.End - input.Start;
    float length = sqrt(dot(direction, direction));
    float2 tangent = length > 1e-6 ? direction / length : float2(1, 0);
    float2 normal = float2(-tangent.y, tangent.x);
    float period = IntervalCount == 0 ? 1 : IntervalEnd(IntervalCount - 1);
    float shift = input.Along + DashOffset;
    uint bits = 0;
    [loop]
    for (uint i = 0; i < 32; i++)
    {
        float2 q = float2(pixel) + SampleAt(i) - input.Start;
        float a = dot(q, tangent);
        float c = dot(q, normal);
        bool inside = false;
        if (IntervalCount == 0)
        {
            inside = InDash(a, c, 0, length, length);
        }
        else
        {
            // The dash nearest along the line covers whenever any does: the period of the sample's position and the two beside it.
            float k = floor((clamp(a, 0, length) + shift) / period);
            [loop]
            for (int dk = -1; dk <= 1 && !inside; dk++)
            {
                float base = (k + dk) * period - shift;
                float start = 0;
                [loop]
                for (uint j = 0; j < IntervalCount && !inside; j += 2)
                {
                    inside = InDash(a, c, base + start, base + IntervalEnd(j), length);
                    start = IntervalEnd(j + 1);
                }
            }
        }

        bits |= inside ? (1u << i) : 0u;
    }

    if (bits != 0)
    {
        InterlockedOr(Mask[pixel], bits);
    }
}

// The outer normal of a join's direction: on the side away from the turn.
float2 Outer(float2 direction, float turn)
{
    return (turn > 0 ? -1 : 1) * float2(-direction.y, direction.x);
}

float Cross(float2 a, float2 b)
{
    return a.x * b.y - a.y * b.x;
}

// A square around the piece's point, as far as the piece reaches and 1 pixel more.
PieceVertex PieceVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Piece piece = Pieces[PieceStart + instance];
    float reach = HalfWidth * 1.4143;
    if (piece.Kind == 1)
    {
        float2 sum = Outer(piece.In, Cross(piece.In, piece.Out)) + Outer(piece.Out, Cross(piece.In, piece.Out));
        reach = max(reach, 2 * HalfWidth / max(sqrt(dot(sum, sum)), 1e-6));
    }

    reach += 1;
    float2 corner = piece.Point + float2((vertex & 2) != 0 ? reach : -reach, (vertex & 1) != 0 ? reach : -reach);
    PieceVertex output;
    output.Position = ToClip(corner);
    output.Point = piece.Point;
    output.In = piece.In;
    output.Out = piece.Out;
    output.Kind = piece.Kind;
    return output;
}

// Whether q lies in the convex polygon of up to 4 corners, in either winding.
bool InPolygon(float2 q, float2 c0, float2 c1, float2 c2, float2 c3, uint count)
{
    float e0 = Cross(c1 - c0, q - c0);
    float e1 = Cross(c2 - c1, q - c1);
    float e2 = count == 3 ? Cross(c0 - c2, q - c2) : Cross(c3 - c2, q - c2);
    float e3 = count == 3 ? e2 : Cross(c0 - c3, q - c3);
    return (e0 >= 0 && e1 >= 0 && e2 >= 0 && e3 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0 && e3 <= 0);
}

bool InPiece(float2 q, PieceVertex piece)
{
    float r = HalfWidth;
    if (piece.Kind == 2)
    {
        return dot(q, q) <= r * r;
    }

    if (piece.Kind == 3 || piece.Kind == 4)
    {
        float a = dot(q, piece.In);
        float c = dot(q, float2(-piece.In.y, piece.In.x));
        return abs(c) <= r && a <= r && a >= (piece.Kind == 3 ? 0 : -r);
    }

    float turn = Cross(piece.In, piece.Out);
    if (turn == 0)
    {
        // A reversal: the bevel has no area.
        return false;
    }

    float2 a0 = Outer(piece.In, turn) * r;
    float2 b0 = Outer(piece.Out, turn) * r;
    if (piece.Kind == 0)
    {
        return InPolygon(q, float2(0, 0), a0, b0, b0, 3);
    }

    float2 sum = a0 + b0;
    float2 m = sum * (2 * r * r / dot(sum, sum));
    return InPolygon(q, float2(0, 0), a0, m, b0, 4);
}

void PieceMaskPS(PieceVertex input)
{
    int2 pixel = int2(input.Position.xy);
    uint bits = 0;
    [loop]
    for (uint i = 0; i < 32; i++)
    {
        float2 q = float2(pixel) + SampleAt(i) - input.Point;
        bits |= InPiece(q, input) ? (1u << i) : 0u;
    }

    if (bits != 0)
    {
        InterlockedOr(Mask[pixel], bits);
    }
}

float4 MaskColorPS(float4 position : SV_Position) : SV_Target
{
    return Color * (countbits(MaskView.Load(int3(position.xy, 0))) / 32.0);
}

void MaskZeroPS(float4 position : SV_Position)
{
    Mask[int2(position.xy)] = 0;
}
