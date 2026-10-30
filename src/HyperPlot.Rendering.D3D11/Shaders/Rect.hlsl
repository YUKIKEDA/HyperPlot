// Filled rectangles (design §5.1, §5.3). One quad per rectangle: RectVS makes it from SV_VertexID over the rectangle widened
// outward to whole pixels, and RectPS covers each pixel by the area it shares with the rectangle, ox · oy, and multiplies the
// premultiplied color by it. Positions are physical pixels of the target, origin at the top left; SV_Position.xy is the
// pixel center, so the pixel's top left is SV_Position.xy − 0.5.

cbuffer Rects : register(b0)
{
    float2 InverseSize;   // 1 / target size
    uint First;           // the step's first record in Instances
    uint Unused;
};

struct Rect
{
    float4 Box;           // left, top, width, height
    float4 Color;         // premultiplied, 0..1
};

StructuredBuffer<Rect> Instances : register(t0);

struct RectVertex
{
    float4 Position : SV_Position;
    nointerpolation float4 Box : BOX;     // left, top, right, bottom
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the widened rectangle.
RectVertex RectVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Rect rect = Instances[First + instance];
    float2 low = rect.Box.xy;
    float2 high = rect.Box.xy + rect.Box.zw;
    float2 outerLow = floor(low);
    float2 outerHigh = ceil(high);
    float2 corner = float2((vertex & 2) != 0 ? outerHigh.x : outerLow.x, (vertex & 1) != 0 ? outerHigh.y : outerLow.y);
    RectVertex output;
    output.Position = float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, 0, 1);
    output.Box = float4(low, high);
    output.Color = rect.Color;
    return output;
}

float4 RectPS(RectVertex input) : SV_Target
{
    // The lengths that the pixel [p, p + 1) shares with the rectangle along x and along y.
    float2 pixel = floor(input.Position.xy);
    float2 overlap = saturate(min(pixel + 1, input.Box.zw) - max(pixel, input.Box.xy));
    return input.Color * (overlap.x * overlap.y);
}
