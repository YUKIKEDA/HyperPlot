// SPIKE CODE (row K0d, Issue #490): a triangle mesh drawn opaque, and translucent through weighted blended OIT, for
// measurement only. Not product code.
//
// The mesh is an indexed triangle list (Issue #483, decision 3). MeshVS pulls its vertex from a structured buffer by
// SV_VertexID (the index of an indexed draw), scales the position per axis into the box's coordinates, and decodes the
// normal the CPU gave: the CPU has already applied the inverse transpose of the per-axis scale and normalized it.
// Shading is per-pixel Blinn-Phong on both faces: a back face turns its normal around (SV_IsFrontFace), one directional
// light and an ambient term (design §13.5).
//
// Opaque: OpaquePS writes the shaded color with a D32_FLOAT depth test (LESS) and write.
//
// Translucent, WBOIT (McGuire and Bavoil 2013, JCGT 2(2); design §13.7, Issue #483 decision 1): AccumPS draws every
// triangle once without writing depth, into accum (RGBA16F, blend ONE, ONE: (C·α, α)·w) and revealage (R8, blend ZERO,
// INV_SRC_COLOR: the product of 1 - α). CompositePS then puts accum.rgb / accum.a over the target with weight
// 1 - revealage (blend INV_SRC_ALPHA, SRC_ALPHA; the paper's Listing 3, with the infinity guard of McGuire's 2015 blog
// post).
//
// Reference: CapturePS appends every fragment the rasterizer makes (shaded color, α, the view depth, the depth, the
// primitive) to a per-pixel linked list (heads in an R32_UINT texture, fragments in a structured buffer, a counter in a
// raw buffer). The CPU sorts each pixel's list by depth and composites it exactly; the fragments are the same
// rasterizer's and the same shading's, so the difference to the WBOIT frame is the composite alone.

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProjection;  // the box's coordinates to clip space, for row vectors: mul(float4(p, 1), M)
    row_major float4x4 View;            // the box's coordinates to the camera's (right-handed: in front, z < 0)
    float3 Scale;                       // per axis, the mesh's coordinates to the box's
    float Alpha;                        // the opacity of every triangle of the translucent passes
    float3 ToLight;                     // the unit vector toward the directional light, in the box's coordinates
    float Ambient;                      // the share of the color lit by the ambient light
    float3 Eye;                         // the camera's position in the box's coordinates
    uint WeightKind;                    // the WBOIT weight: 0 none (w = α), 7 to 10 the paper's equations 7 to 10
    uint Capacity;                      // capture: the fragments the list buffer holds
    uint3 Unused;
};

static const float Shininess = 32;
static const float Specular = 0.25;

struct Vertex
{
    float3 Position;                    // in the mesh's coordinates
    uint Normal;                        // the unit normal in the box's coordinates, octahedral: x low, y high, SNORM16
    uint Color;                         // RGBA8, R in the low byte
};

StructuredBuffer<Vertex> Vertices : register(t0);

float4 Unpack(uint color)
{
    return float4(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF, color >> 24) * (1.0 / 255.0);
}

// The octahedral decoding (Cigolle et al. 2014, JCGT 3(2)) of MeshData.Octahedral.
float3 DecodeNormal(uint packed)
{
    float2 e = max(float2((int)(packed << 16) >> 16, (int)packed >> 16) / 32767.0, -1);
    float3 n = float3(e, 1 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += n.xy >= 0 ? -t : t;
    return normalize(n);
}

struct MeshVertex
{
    float4 Position : SV_Position;
    float3 Box : BOX;                   // the position in the box's coordinates
    float3 Normal : NORMAL;
    float4 Color : COLOR;
    float ViewDepth : VIEWDEPTH;        // |z| of the camera's space: the distance along the view axis, > 0 in front
};

MeshVertex MeshVS(uint index : SV_VertexID)
{
    Vertex v = Vertices[index];
    float3 box = v.Position * Scale;
    MeshVertex output;
    output.Position = mul(float4(box, 1), ViewProjection);
    output.Box = box;
    output.Normal = DecodeNormal(v.Normal);
    output.Color = Unpack(v.Color);
    output.ViewDepth = -mul(float4(box, 1), View).z;
    return output;
}

// Blinn-Phong on both faces: the back face of an open surface is lit with its normal turned around.
float3 Shade(MeshVertex input, bool front)
{
    float3 normal = normalize(input.Normal);
    normal = front ? normal : -normal;
    float3 toEye = normalize(Eye - input.Box);
    float3 halfway = normalize(ToLight + toEye);
    float diffuse = saturate(dot(normal, ToLight));

    // The base of the pow stays above 0, as .cursor/rules/interop.mdc asks of a pow (pow of 0 dropped pixels on a GPU).
    float highlight = diffuse > 0 ? pow(max(dot(normal, halfway), 1e-7), Shininess) : 0;
    return input.Color.rgb * (Ambient + (1 - Ambient) * diffuse) + Specular * highlight;
}

// The WBOIT weight w(z, α) of McGuire and Bavoil 2013 §3.4, clamped to [1e-2, 3e3] for 0.1 ≤ |z| ≤ 500 and the 16-bit
// float of accum. z is the view depth; depth is the depth buffer's value (SV_Position.z), for equation 10.
// [The equations as transcribed here are to be checked against the paper's PDF before a figure quotes them.]
float Weight(float z, float depth)
{
    // An if chain and one return: fxc read the switch whose default returned early as a function result that may be
    // uninitialized (X4000), and an early return in an else the same way.
    float w = 1;
    if (WeightKind == 7)
    {
        float a = z / 5;
        float b = z / 200;
        w = 10 / (1e-5 + a * a + b * b * b * b * b * b);
    }
    else if (WeightKind == 8)
    {
        float a = z / 10;
        float b = z / 200;
        w = 10 / (1e-5 + a * a * a + b * b * b * b * b * b);
    }
    else if (WeightKind == 9)
    {
        float b = z / 200;
        w = 0.03 / (1e-5 + b * b * b * b);
    }
    else if (WeightKind == 10)
    {
        float d = 1 - depth;
        w = 3e3 * d * d * d;
    }

    // No weight (any other kind): w stays 1, inside the clamp, so the result is Alpha.
    return Alpha * clamp(w, 1e-2, 3e3);
}

// ---------------------------------------------------------------------------------------------------------------------
// Opaque

float4 OpaquePS(MeshVertex input, bool front : SV_IsFrontFace) : SV_Target
{
    return float4(Shade(input, front), 1);
}

// ---------------------------------------------------------------------------------------------------------------------
// WBOIT

struct Accumulated
{
    float4 Accum : SV_Target0;
    float Revealage : SV_Target1;
};

Accumulated AccumPS(MeshVertex input, bool front : SV_IsFrontFace)
{
    float3 color = Shade(input, front);
    float w = Weight(input.ViewDepth, input.Position.z);
    Accumulated output;
    output.Accum = float4(color * Alpha, Alpha) * w;
    output.Revealage = Alpha;
    return output;
}

Texture2D<float4> AccumTexture : register(t0);
Texture2D<float> RevealageTexture : register(t1);

// One triangle over the whole target: vertices 0, 1, 2 at (-1, 1), (3, 1), (-1, -3) in clip space.
float4 CompositeVS(uint vertex : SV_VertexID) : SV_Position
{
    float2 corner = float2((vertex << 1) & 2, vertex & 2);
    return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 CompositePS(float4 position : SV_Position) : SV_Target
{
    int3 at = int3(position.xy, 0);
    float revealage = RevealageTexture.Load(at);

    // Nothing translucent fell on this pixel: the target stays as it is.
    if (revealage == 1)
    {
        discard;
    }

    float4 accum = AccumTexture.Load(at);

    // An accum that overflowed the 16-bit float: its average falls back to the summed weight (McGuire's 2015 blog post).
    if (any(isinf(abs(accum.rgb))))
    {
        accum.rgb = accum.aaa;
    }

    return float4(accum.rgb / clamp(accum.a, 1e-4, 5e4), revealage);
}

// ---------------------------------------------------------------------------------------------------------------------
// Capture of every fragment, for the CPU's sorted reference

struct Fragment
{
    float ViewDepth;
    float Depth;
    float3 Color;
    float Alpha;
    uint Primitive;
    uint Next;                          // the index of the pixel's next fragment, 0xFFFFFFFF at the end
};

RWTexture2D<uint> Heads : register(u1);
RWStructuredBuffer<Fragment> Fragments : register(u2);
RWByteAddressBuffer Counter : register(u3);

// SV_PrimitiveID comes before SV_IsFrontFace: fxc wants the system-generated values last (X4576).
void CapturePS(MeshVertex input, uint primitive : SV_PrimitiveID, bool front : SV_IsFrontFace)
{
    uint index;
    Counter.InterlockedAdd(0, 1, index);

    // A full buffer drops the fragment; the counter still counts it, and the CPU draws the band again in halves.
    if (index >= Capacity)
    {
        return;
    }

    uint next;
    InterlockedExchange(Heads[uint2(input.Position.xy)], index, next);
    Fragment fragment;
    fragment.ViewDepth = input.ViewDepth;
    fragment.Depth = input.Position.z;
    fragment.Color = Shade(input, front);
    fragment.Alpha = Alpha;
    fragment.Primitive = primitive;
    fragment.Next = next;
    Fragments[index] = fragment;
}
