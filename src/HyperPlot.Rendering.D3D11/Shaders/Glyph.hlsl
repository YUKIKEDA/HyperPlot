// Glyphs from the atlas (design §5.3, §5.6). One quad per glyph: GlyphVS makes it from SV_VertexID over the glyph's
// rectangle of whole pixels, and GlyphPS reads the coverage of the pixel's texel by point (no filtering) and multiplies the
// premultiplied color by it. A turned glyph reads its texel through the turn, so the atlas holds every glyph upright.
// Positions are physical pixels of the target, origin at the top left; SV_Position.xy is the pixel center.

cbuffer Glyphs : register(b0)
{
    float2 InverseSize;   // 1 / target size
    uint First;           // the step's first record in Instances
    uint Unused;
};

struct Glyph
{
    int4 Rect;            // left, top, width, height on the target
    int2 Atlas;           // the top left of the coverage on the page
    uint Rotation;        // 0: none, 1: clockwise, 2: counterclockwise
    uint Padding;
    float4 Color;         // premultiplied, 0..1
};

StructuredBuffer<Glyph> Instances : register(t0);
Texture2D<float> Page : register(t1);

struct GlyphVertex
{
    float4 Position : SV_Position;
    nointerpolation int4 Rect : RECT;
    nointerpolation int3 Atlas : ATLAS;   // x, y, rotation
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the rectangle.
GlyphVertex GlyphVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Glyph glyph = Instances[First + instance];
    float2 corner = float2(glyph.Rect.xy) + float2((vertex & 2) != 0 ? glyph.Rect.z : 0, (vertex & 1) != 0 ? glyph.Rect.w : 0);
    GlyphVertex output;
    output.Position = float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, 0, 1);
    output.Rect = glyph.Rect;
    output.Atlas = int3(glyph.Atlas, glyph.Rotation);
    output.Color = glyph.Color;
    return output;
}

float4 GlyphPS(GlyphVertex input) : SV_Target
{
    // The pixel within the rectangle, and the texel it shows: a clockwise turn reads the columns right to left as rows, a
    // counterclockwise one the rows bottom to top as columns (design §5.6). Chosen by value, not by a branch.
    int2 local = int2(input.Position.xy) - input.Rect.xy;
    int2 upright = local;
    int2 clockwise = int2(local.y, input.Rect.z - 1 - local.x);
    int2 counterclockwise = int2(input.Rect.w - 1 - local.y, local.x);
    int2 texel = input.Atlas.z == 1 ? clockwise : (input.Atlas.z == 2 ? counterclockwise : upright);
    return input.Color * Page.Load(int3(input.Atlas.xy + texel, 0));
}
