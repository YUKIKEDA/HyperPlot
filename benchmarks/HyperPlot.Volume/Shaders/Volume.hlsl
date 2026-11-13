// SPIKE (row K0e, Issue #491): ray marching of a bricked volume in a pixel shader. Not product code.
// Compiled at startup with D3DCompile (fxc's compiler in d3dcompiler_47.dll), as ps_5_0 and vs_5_0; FILTER_LOAD selects
// the filter that reads the 8 texels itself instead of the sampler's trilinear filter.
//
// Space: the axis box is [-1, 1]^3 on screen ("box"); the view range maps it onto the data's [0, 1]^3 ("u"). The table
// holds one entry per brick of the finest level (Grid^3); an entry names the slot of the brick drawn there and its level
// (a coarser brick's entry is repeated over the cells it covers), or that nothing is drawn there.

cbuffer Frame : register(b0)
{
    row_major float4x4 InverseTransform; // clip -> box
    float4 Eye;                          // the camera, in box space
    float4 RangeLow;                     // u at box -1
    float4 RangeHigh;                    // u at box +1
    float2 Size;
    float2 InverseSize;
    uint Volume;                         // voxels per axis at level 0
    uint Grid;                           // bricks per axis at level 0
    uint Brick;                          // voxels per brick per axis, without the ghosts
    uint Side;                           // Brick + 2: a slot per axis in the atlas
    uint SlotsX;
    uint SlotsY;
    uint SlotsZ;
    uint SlotsPerAtlas;
    float3 InverseAtlas;                 // 1 / the atlas's size in texels
    float Threshold;                     // early termination: the accumulated opacity that stops a ray
    float ReferenceSteps;                // the transfer function's opacity is per 1 / ReferenceSteps of u
    uint MaxSteps;
    uint Atlases;
    float Padding;
};

Texture3D<float> Atlas0 : register(t0);
Texture3D<float> Atlas1 : register(t1);
Texture3D<float> Atlas2 : register(t2);
Texture3D<float> Atlas3 : register(t3);
Texture3D<uint> Table : register(t4);
Texture1D<float4> Transfer : register(t5);
Texture2D<float> Depth : register(t6);
SamplerState Linear : register(s0);

// The low 24 bits of an entry are the slot; this slot means "nothing drawn here". The high 8 bits are the level.
static const uint EmptySlot = 0xFFFFFF;

float4 FullScreenVS(uint id : SV_VertexID) : SV_Position
{
    float2 at = float2((id << 1) & 2, id & 2);
    return float4((at * float2(2, -2)) + float2(-1, 1), 0, 1);
}

// The jitter of a ray's first sample: a hash of the pixel's coordinates, never time or a random source (invariant 11).
uint Hash(uint2 p)
{
    uint h = (p.x * 0x8da6b343u) ^ (p.y * 0xd8163841u);
    h ^= h >> 16;
    h *= 0x7feb352du;
    h ^= h >> 15;
    h *= 0x846ca68bu;
    h ^= h >> 16;
    return h;
}

float3 Unproject(float2 ndc, float z)
{
    float4 p = mul(float4(ndc, z, 1), InverseTransform);
    return p.xyz / p.w;
}

#ifdef FILTER_LOAD
float LoadAtlas(uint atlas, int3 texel)
{
    [branch] switch (atlas)
    {
        case 0: return Atlas0.Load(int4(texel, 0));
        case 1: return Atlas1.Load(int4(texel, 0));
        case 2: return Atlas2.Load(int4(texel, 0));
        default: return Atlas3.Load(int4(texel, 0));
    }
}

// Trilinear from the 8 texels. The weights come from the position inside the brick alone, so the value does not depend on
// the slot the brick was given (which depends on what the cache held before).
float Fetch(uint atlas, int3 origin, float3 local)
{
    float3 t = local + 0.5; // the brick's texel 0 is the ghost; voxel i's center is at local i + 0.5
    float3 cell = floor(t);
    float3 f = t - cell;
    int3 i = origin + int3(cell);
    float c000 = LoadAtlas(atlas, i);
    float c100 = LoadAtlas(atlas, i + int3(1, 0, 0));
    float c010 = LoadAtlas(atlas, i + int3(0, 1, 0));
    float c110 = LoadAtlas(atlas, i + int3(1, 1, 0));
    float c001 = LoadAtlas(atlas, i + int3(0, 0, 1));
    float c101 = LoadAtlas(atlas, i + int3(1, 0, 1));
    float c011 = LoadAtlas(atlas, i + int3(0, 1, 1));
    float c111 = LoadAtlas(atlas, i + int3(1, 1, 1));
    float c00 = lerp(c000, c100, f.x);
    float c10 = lerp(c010, c110, f.x);
    float c01 = lerp(c001, c101, f.x);
    float c11 = lerp(c011, c111, f.x);
    return lerp(lerp(c00, c10, f.y), lerp(c01, c11, f.y), f.z);
}
#else
// The sampler's trilinear filter at the atlas's normalized coordinates: origin + 1 (the ghost) + local, in texels.
float Fetch(uint atlas, int3 origin, float3 local)
{
    float3 uvw = (float3(origin) + 1 + local) * InverseAtlas;
    [branch] switch (atlas)
    {
        case 0: return Atlas0.SampleLevel(Linear, uvw, 0);
        case 1: return Atlas1.SampleLevel(Linear, uvw, 0);
        case 2: return Atlas2.SampleLevel(Linear, uvw, 0);
        default: return Atlas3.SampleLevel(Linear, uvw, 0);
    }
}
#endif

float4 MarchPS(float4 position : SV_Position) : SV_Target
{
    float2 pixel = position.xy;
    float2 ndc = float2((pixel.x * InverseSize.x * 2) - 1, 1 - (pixel.y * InverseSize.y * 2));
    float3 background = float3(1, 1, 1);
    float3 ro = Eye.xyz;
    float3 rd = normalize(Unproject(ndc, 1) - ro);

    // The ray against the axis box.
    float3 inverse = 1 / rd;
    float3 t0 = (-1 - ro) * inverse;
    float3 t1 = (1 - ro) * inverse;
    float3 tMin = min(t0, t1);
    float3 tMax = max(t0, t1);
    float tNear = max(max(max(tMin.x, tMin.y), tMin.z), 0);
    float tFar = min(min(tMax.x, tMax.y), tMax.z);

    // Stop at the depth of the opaque shapes drawn before (decision 3 of Issue #484); cleared to 1 in this prototype.
    float depth = Depth.Load(int3(pixel, 0));
    if (depth < 1)
    {
        tFar = min(tFar, dot(Unproject(ndc, depth) - ro, rd));
    }

    if (tNear >= tFar)
    {
        return float4(background, 1);
    }

    // March in u, by the length of u along the ray.
    float3 scale = (RangeHigh.xyz - RangeLow.xyz) * 0.5;
    float3 uo = RangeLow.xyz + ((ro + 1) * scale);
    float3 ud = rd * scale;
    float speed = length(ud);
    ud /= speed;
    float3 uInverse = 1 / ud;
    float s = tNear * speed;
    float sEnd = tFar * speed;
    float jitter = (Hash(uint2(pixel)) >> 8) * (1.0 / 16777216.0);
    float grid = (float)Grid;

    float3 color = 0;
    float alpha = 0;
    bool aligned = true; // the next sample starts at a face (the box's or a skipped brick's): jitter it first
    uint steps = 0;
    [loop] while (s < sEnd && steps < MaxSteps)
    {
        steps++;
        float3 u = uo + (ud * s);
        uint3 cell = min(uint3(max(u, 0) * grid), Grid - 1);
        uint entry = Table.Load(int4(cell, 0));
        uint level = entry >> 24;
        uint slot = entry & EmptySlot;
        if (slot == EmptySlot)
        {
            // Empty-space skipping: to the exit of the brick at this level, whose cells all hold this entry.
            float span = (float)min(1u << level, Grid);
            float3 low = floor(float3(cell) / span) * span / grid;
            float3 high = min(low + (span / grid), 1);
            float3 exits = ((ud > 0 ? high : low) - u) * uInverse;
            s += max(min(min(exits.x, exits.y), exits.z), 0) + (0.01 / Volume);
            aligned = true;
            continue;
        }

        // Half a voxel of the level drawn here; its opacity is corrected for the length of the step.
        float side = (float)(Volume >> level);
        float stepU = 0.5 / side;
        if (aligned)
        {
            s += jitter * stepU;
            aligned = false;
            continue;
        }

        // The brick of this cell at its level; local is the position inside it in voxels, within [0, Brick] up to rounding
        // (the ghosts hold the voxels just outside).
        float3 brick = float3(cell >> level);
        float3 local = (u * side) - (brick * Brick);
        uint atlas = slot / SlotsPerAtlas;
        uint inAtlas = slot - (atlas * SlotsPerAtlas);
        int3 origin = int3(inAtlas % SlotsX, (inAtlas / SlotsX) % SlotsY, inAtlas / (SlotsX * SlotsY)) * (int)Side;
        float v = Fetch(atlas, origin, local);

        // Codes 0..65534 are the values; 65535 is kept for NaN (Issue #484), and this prototype's data has none.
        float x = saturate(v * (65535.0 / 65534.0));
        float4 tf = Transfer.SampleLevel(Linear, ((x * 255) + 0.5) / 256, 0);
        float a = 1 - pow(max(1 - tf.a, 1e-7), stepU * ReferenceSteps);
        color += (1 - alpha) * a * tf.rgb;
        alpha += (1 - alpha) * a;
        if (alpha >= Threshold)
        {
            break;
        }

        s += stepU;
    }

    return float4(color + ((1 - alpha) * background), 1);
}
