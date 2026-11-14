// Image tiles (design §5.3, row G9). One quad a tile: its corners are the record's origin plus the drawn texel range along the
// tile's column and row vectors, a parallelogram that flip and a turned-over extent set. The pixel shader reads the tile's
// face of 258 x 258 (the tile and a border of a texel from its neighbors) with Load, nearest or bilinear, and paints a number
// through the scale's transform and the color table as ColorMap.Map does, or takes a premultiplied color as it is. A density
// tile (row R3b1) is a face of numbers read nearest, whose value 0 the flag of bit 6 leaves transparent.

cbuffer Images : register(b0)
{
    float2 InverseSize;
    uint First;
    uint Unused;
};

struct Image
{
    float2 Origin;
    float2 AlongU;
    float2 AlongV;
    float4 Range;        // u0, v0, u1, v1 in the tile's texels
    float2 SampleOrigin; // the face texel (border left out) that tile texel (0, 0) reads
    float SampleScale;
    uint Slice;
    uint Flags;          // bit 0 bilinear, bit 1 RGBA, bits 2-3 transform, bit 4 diverging, bit 5 reversed, bit 6 0 transparent
    uint Table;
    float Constant;
    float Low;
    float High;
    float Middle;
    float Opacity;
    uint Highlight;      // the number of the region whose outside the tile fades: its index in Highlights plus 1; 0 for none
    float2 Padding;
};

StructuredBuffer<Image> Instances : register(t0);
Texture2DArray<float> Values : register(t1);
Texture2DArray<float4> Colors : register(t2);
Texture2D<float4> Tables : register(t3);

// The regions of the selections the frame's highlights keep (design §6.4 "密度の強調", row R3c): as HighlightRegion in Core,
// and as Markers.hlsl has them, whose Holds this one repeats (a file is compiled alone).
struct HighlightRegion
{
    float4 Box;
    int2 MaskOrigin;
    uint MaskFirst;
    uint MaskStride;          // 0 for a region of no mask
};

StructuredBuffer<HighlightRegion> Highlights : register(t4);
StructuredBuffer<uint> HighlightMasks : register(t5);

// The share of its opacity a pixel outside the region keeps (Issue #471 Decision 12).
static const float Faded = 0.2;

// Whether a point of the target lies in a region, as RenderList.Holds judges it; true for the number 0, no region.
bool Holds(uint number, float2 position)
{
    if (number == 0)
    {
        return true;
    }

    HighlightRegion region = Highlights[number - 1];
    if (!(all(position >= region.Box.xy) && all(position <= region.Box.zw)))
    {
        return false;
    }

    if (region.MaskStride == 0)
    {
        return true;
    }

    uint2 at = (uint2)(int2(floor(position)) - region.MaskOrigin);
    uint word = HighlightMasks[region.MaskFirst + at.y * region.MaskStride + (at.x >> 5)];
    return ((word >> (at.x & 31)) & 1) != 0;
}

struct ImageVertex
{
    float4 Position : SV_Position;
    noperspective float2 Texel : TEXEL;
    nointerpolation uint Instance : INSTANCE;
};

ImageVertex ImageVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Image image = Instances[First + instance];
    float u = (vertex & 2) != 0 ? image.Range.z : image.Range.x;
    float v = (vertex & 1) != 0 ? image.Range.w : image.Range.y;
    float2 corner = image.Origin + (u * image.AlongU) + (v * image.AlongV);
    ImageVertex output;
    output.Position = float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, 0, 1);
    output.Texel = float2(u, v);
    output.Instance = First + instance;
    return output;
}

// The scale's transform (design §4.2): linear, log10, symlog with its constant, pow with its exponent.
float Forward(float value, uint kind, float constant)
{
    if (kind == 1)
    {
        return log10(value);
    }

    if (kind == 2)
    {
        return sign(value) * log10(1 + abs(value) / constant);
    }

    if (kind == 3)
    {
        return sign(value) * pow(abs(value), constant);
    }

    return value;
}

// A number painted as ColorScale.Of and ColorMap.Map do: NaN and a value that does not map are transparent; t below 0 takes
// the first color, and the index is floor(t * 256), 255 at most. With bit 6, a value of exactly 0 is transparent too: an
// empty bin of a density tile. Read bilinear, that is a value whose four texels are all 0.
float4 Paint(Image image, float value)
{
    if (!(value == value) || ((image.Flags & 64) != 0 && value == 0))
    {
        return 0;
    }

    float s = Forward(value, (image.Flags >> 2) & 3, image.Constant);
    if (!isfinite(s))
    {
        return 0;
    }

    float t = (image.Flags & 16) != 0
        ? (s <= image.Middle ? 0.5 * (s - image.Low) / (image.Middle - image.Low) : 0.5 + 0.5 * (s - image.Middle) / (image.High - image.Middle))
        : (s - image.Low) / (image.High - image.Low);
    if ((image.Flags & 32) != 0)
    {
        t = 1 - t;
    }

    if (!(t == t))
    {
        return 0;
    }

    int index = t < 0 ? 0 : (t >= 1 ? 255 : (int)min(floor(t * 256), 255));
    float4 color = Tables.Load(int3(index, image.Table, 0));
    return float4(color.rgb * color.a, color.a);
}

// A face texel by its texel of the tile, the border included: -1 and 256 read the border.
int4 Face(int2 texel, uint slice)
{
    return int4(clamp(texel, -1, 256) + 1, slice, 0);
}

float4 ImagePS(ImageVertex input) : SV_Target
{
    Image image = Instances[input.Instance];
    float2 at = image.SampleOrigin + input.Texel * image.SampleScale;
    bool rgba = (image.Flags & 2) != 0;
    float4 color;
    if ((image.Flags & 1) == 0)
    {
        int4 texel = Face(int2(floor(at)), image.Slice);
        color = rgba ? Colors.Load(texel) : Paint(image, Values.Load(texel));
    }
    else
    {
        float2 corner = at - 0.5;
        float2 low = floor(corner);
        float2 weight = corner - low;
        int2 i = int2(low);
        int4 t00 = Face(i, image.Slice);
        int4 t10 = Face(i + int2(1, 0), image.Slice);
        int4 t01 = Face(i + int2(0, 1), image.Slice);
        int4 t11 = Face(i + int2(1, 1), image.Slice);
        float4 w = float4((1 - weight.x) * (1 - weight.y), weight.x * (1 - weight.y), (1 - weight.x) * weight.y, weight.x * weight.y);
        if (rgba)
        {
            color = Colors.Load(t00) * w.x + Colors.Load(t10) * w.y + Colors.Load(t01) * w.z + Colors.Load(t11) * w.w;
        }
        else
        {
            // The values are interpolated before they are painted (matplotlib's "data" stage); a NaN leaves its weight out.
            float4 values = float4(Values.Load(t00), Values.Load(t10), Values.Load(t01), Values.Load(t11));
            float4 present = float4(values.x == values.x, values.y == values.y, values.z == values.z, values.w == values.w);
            float total = dot(w, present);
            float4 kept = present > 0 ? values : 0;
            color = total > 0 ? Paint(image, dot(w, kept) / total) : 0;
        }
    }

    // A density's highlight cuts the tile by the region at each pixel's center (design §6.4 "密度の強調").
    return color * (image.Opacity * (Holds(image.Highlight, input.Position.xy) ? 1 : Faded));
}
