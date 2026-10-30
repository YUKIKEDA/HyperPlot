// Column trapezoids (design §5.1, §5.3): area and band fills. One quad per trapezoid: ColumnsVS makes it from SV_VertexID
// over the trapezoid's bounding box widened outward to whole pixels, and ColumnsPS covers each pixel by the area it shares
// with the region between the top and bottom edges, in either order, and multiplies the premultiplied color by it.
// Positions are physical pixels of the target, origin at the top left; SV_Position.xy is the pixel center.

cbuffer Columns : register(b0)
{
    float2 InverseSize;   // 1 / target size
    uint First;           // the step's first record in Instances
    uint Unused;
};

struct Column
{
    float2 Sides;         // left, right
    float2 Top;           // the top edge at the left and right sides
    float2 Bottom;        // the bottom edge at the left and right sides
    float2 Unused;
    float4 Color;         // premultiplied, 0..1
};

StructuredBuffer<Column> Instances : register(t0);

struct ColumnVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Sides : SIDES;
    nointerpolation float2 Top : TOP;
    nointerpolation float2 Bottom : BOTTOM;
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the widened box.
ColumnVertex ColumnsVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Column column = Instances[First + instance];
    float low = floor(min(min(column.Top.x, column.Top.y), min(column.Bottom.x, column.Bottom.y)));
    float high = ceil(max(max(column.Top.x, column.Top.y), max(column.Bottom.x, column.Bottom.y)));
    float2 corner = float2(
        (vertex & 2) != 0 ? ceil(column.Sides.y) : floor(column.Sides.x),
        (vertex & 1) != 0 ? high : low);
    ColumnVertex output;
    output.Position = float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, 0, 1);
    output.Sides = column.Sides;
    output.Top = column.Top;
    output.Bottom = column.Bottom;
    output.Color = column.Color;
    return output;
}

// |clamp(bottom(x)) − clamp(top(x))| at x, both edges clamped to the pixel's rows [row, row + 1].
float Span(float x, float2 sides, float2 top, float2 bottom, float row)
{
    float t = (x - sides.x) / (sides.y - sides.x);
    float a = clamp(lerp(top.x, top.y, t), row, row + 1);
    float b = clamp(lerp(bottom.x, bottom.y, t), row, row + 1);
    return abs(b - a);
}

// The x where the line through (sides, values) takes the value y, or −1 when it does not cross it inside the sides.
float Crossing(float2 sides, float2 values, float y)
{
    float d = values.y - values.x;
    if (d == 0)
    {
        return -1;
    }

    float t = (y - values.x) / d;
    return t > 0 && t < 1 ? lerp(sides.x, sides.y, t) : -1;
}

float4 ColumnsPS(ColumnVertex input) : SV_Target
{
    // The integrand |clamp(bottom) − clamp(top)| is linear between the x where an edge meets the pixel's top or bottom,
    // or the edges meet each other: integrate exactly with the trapezoid rule over the pieces between those points.
    float2 pixel = floor(input.Position.xy);
    float a = max(pixel.x, input.Sides.x);
    float b = min(pixel.x + 1, input.Sides.y);
    if (!(a < b))
    {
        return 0;
    }

    float points[8];
    points[0] = a;
    points[1] = Crossing(input.Sides, input.Top, pixel.y);
    points[2] = Crossing(input.Sides, input.Top, pixel.y + 1);
    points[3] = Crossing(input.Sides, input.Bottom, pixel.y);
    points[4] = Crossing(input.Sides, input.Bottom, pixel.y + 1);
    points[5] = Crossing(input.Sides, input.Bottom - input.Top, 0);
    points[6] = b;
    points[7] = b;

    // Keep the points inside [a, b], then sort the 8 with a fixed bubble sort of compare-and-swap, by min and max.
    [unroll]
    for (int k = 1; k < 6; k++)
    {
        points[k] = points[k] < a || points[k] > b ? a : points[k];
    }

    [unroll]
    for (int i = 0; i < 7; i++)
    {
        [unroll]
        for (int j = 0; j < 7 - i; j++)
        {
            float low = min(points[j], points[j + 1]);
            points[j + 1] = max(points[j], points[j + 1]);
            points[j] = low;
        }
    }

    float coverage = 0;
    float previous = Span(points[0], input.Sides, input.Top, input.Bottom, pixel.y);
    [unroll]
    for (int p = 1; p < 8; p++)
    {
        float current = Span(points[p], input.Sides, input.Top, input.Bottom, pixel.y);
        coverage += (previous + current) * 0.5 * (points[p] - points[p - 1]);
        previous = current;
    }

    return input.Color * saturate(coverage);
}
