// SPIKE CODE (row K0c, Issue #489): the tiles of a surface z = f(x, y), for measurement only. Not product code.
//
// Each tile is drawn from its heights in a face of an R32_FLOAT texture array (the residency pool's float faces, design
// §5.1 and §13.5, decision 3) and an index list of its edge shape (Stitch). SurfaceVS takes the vertex's grid number
// (i, j) from SV_VertexID, reads z at the tile's place in the face, and makes x and y from the sample's number: on a
// uniform grid from the first coordinate and the step, on a non-uniform grid from the coordinate arrays. Positions are
// float32 in the box's coordinates ([-1, 1]³), the same arithmetic for every tile, so that two tiles that share a vertex
// put it at the same bits. SurfacePS lights the triangle's own normal (from the derivatives of the position: no
// neighbor heights are read) with one directional light and an ambient term, Blinn-Phong on both sides, and writes an
// opaque color; the depth test is LESS with a D32_FLOAT write.

cbuffer Frame : register(b0)
{
    row_major float4x4 Transform;   // the box's coordinates to clip space, for row vectors: mul(float4(p, 1), Transform)
    float3 Eye;                     // the eye in the box's coordinates
    float ZScale;                   // the box's z per unit of z
    float3 Light;                   // toward the light, normalized
    float ZOffset;                  // the box's z of z = 0
    float2 XUniform;                // a uniform grid: the first x and the step between samples
    float2 YUniform;
    uint Side;                      // the vertices along a tile's side: tile + 1
    uint Uniform;                   // 1: x and y from XUniform and YUniform; 0: from Xs and Ys
    uint2 FramePad;
};

cbuffer Group : register(b1)
{
    uint InstanceBase;              // the first tile of this draw's edge shape in Tiles (SV_InstanceID starts at 0)
    uint3 GroupPad;
};

struct Tile
{
    uint Face;                      // the face of the pool
    uint OriginX;                   // the tile's first texel in the face
    uint OriginY;
    uint SampleX;                   // the tile's first sample in the original grid
    uint SampleY;
    uint Step;                      // 2^level: the original samples between two vertices
    uint2 Pad;
};

StructuredBuffer<Tile> Tiles : register(t0);
Texture2DArray<float> Heights : register(t1);
Buffer<float> Xs : register(t2);
Buffer<float> Ys : register(t3);

struct Vertex
{
    float4 Position : SV_Position;
    float3 World : WORLD;
};

Vertex SurfaceVS(uint id : SV_VertexID, uint instance : SV_InstanceID)
{
    Tile tile = Tiles[InstanceBase + instance];
    uint i = id % Side;
    uint j = id / Side;
    float z = Heights.Load(int4((int)(tile.OriginX + i), (int)(tile.OriginY + j), (int)tile.Face, 0));
    uint gx = tile.SampleX + i * tile.Step;
    uint gy = tile.SampleY + j * tile.Step;
    float x = Uniform != 0 ? XUniform.x + (float)gx * XUniform.y : Xs[gx];
    float y = Uniform != 0 ? YUniform.x + (float)gy * YUniform.y : Ys[gy];
    Vertex output;
    output.World = float3(x, y, z * ZScale + ZOffset);
    output.Position = mul(float4(output.World, 1), Transform);
    return output;
}

float4 SurfacePS(Vertex input) : SV_Target
{
    float3 normal = normalize(cross(ddx(input.World), ddy(input.World)));
    float3 view = normalize(Eye - input.World);
    normal = dot(normal, view) < 0 ? -normal : normal;
    float diffuse = saturate(dot(normal, Light));
    float specular = pow(max(saturate(dot(normal, normalize(Light + view))), 1e-7), 32);
    float height = saturate(input.World.z * 0.5 + 0.5);
    float3 base = lerp(float3(0.15, 0.35, 0.75), float3(0.95, 0.75, 0.25), height);
    return float4(base * (0.25 + 0.75 * diffuse) + 0.3 * specular, 1);
}
