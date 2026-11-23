// ─────────────────────────────────────────────────────────────────────────────
// NEREA - common shader code: constants, noise, caustics, underwater light & fog.
// This file is prepended to every shader at load time (see ShaderLibrary.cs).
// ─────────────────────────────────────────────────────────────────────────────
#define MAX_RIPPLES 16
#define PI  3.14159265
#define TAU 6.28318531

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 InvViewProj;
    float4 CamPos;        // xyz, time (seconds)
    float4 Screen;        // scene width, scene height, projX, projY
    float4 Music;         // bass, mid, treble, energy
    float4 Music2;        // beat, calm, brightness, activity
    float4 SurfaceCol;    // rgb, surfaceY
    float4 DeepCol;       // rgb, floorY
    float4 FogCol;        // rgb, turbidity
    float4 LightCol;      // rgb, intensity
    float4 SandCol;       // rgb, floorSlope
    float4 RockCol;       // rgb, enclosure
    float4 Water;         // rays, caustics, depth, current
    float4 Grade;         // exposure, saturation, vignette, ceilingKind
    float4 ParticleCol;   // rgb, glow
    float4 Quality;       // waterQuality, particleScale, motionScale, bloom
    float4 Misc;          // rippleCount, reflections, shadows, bubbles
    float4 Extra;         // fade, wallDistance, reserved, reserved
    float4 Ripples[MAX_RIPPLES]; // uv.x, uv.y, startTime, strength
};

// Root constants: x = pass (0 main, 1 shadow, 2 reflection), y = particle type
cbuffer Draw : register(b1)
{
    uint4 DrawParams;
};

// Species style table: 6 float4 per species
//  s0: primary rgb, pattern        s1: secondary rgb, glow       s2: accent rgb, patternScale
//  s3: height, width, tail, dorsal s4: peak, fork, snout, hammer s5: curl, legs, flap, plan
cbuffer SpeciesTable : register(b2)
{
    float4 Style[64 * 6];
};

static const float3 LIGHT_DIR = float3(0.2297, 0.9187, 0.3215);

float Time()      { return CamPos.w; }
float SceneLight() { return LightCol.w * (1.0 + Music.x * 0.10 + Music2.x * 0.05); }

// ── hashing & noise ──────────────────────────────────────────────────────────
float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float hash21(float2 p)
{
    float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
    p3 += dot(p3, float3(p3.y, p3.z, p3.x) + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float hash31(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, float3(p3.z, p3.y, p3.x) + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

float wrapi(float i, float p) { return i - p * floor(i / p); }

// periodic 1D value noise (period is an integer) - used for seamless angular god rays
float noise1p(float x, float period)
{
    float i = floor(x);
    float f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(hash11(wrapi(i, period)), hash11(wrapi(i + 1.0, period)), f);
}

float noise1(float x)
{
    float i = floor(x);
    float f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(hash11(i), hash11(i + 1.0), f);
}

float noise2(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + float2(1, 0));
    float c = hash21(i + float2(0, 1));
    float d = hash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash31(i);
    float n100 = hash31(i + float3(1, 0, 0));
    float n010 = hash31(i + float3(0, 1, 0));
    float n110 = hash31(i + float3(1, 1, 0));
    float n001 = hash31(i + float3(0, 0, 1));
    float n101 = hash31(i + float3(1, 0, 1));
    float n011 = hash31(i + float3(0, 1, 1));
    float n111 = hash31(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

float fbm2(float2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += a * noise2(p);
        p = p * 2.03 + 17.1;
        a *= 0.5;
    }
    return v;
}

float fbm3(float3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 3; i++)
    {
        v += a * noise3(p);
        p = p * 2.07 + 11.3;
        a *= 0.5;
    }
    return v;
}

// ── caustics (tiling interference pattern, two overlapping layers) ──────────
float CausticLayer(float2 uv, float t, int iters)
{
    float2 p = frac(uv) * TAU - 250.0;
    float2 i = p;
    float c = 1.0;
    float inten = 0.005;
    for (int n = 0; n < iters; n++)
    {
        float tt = t * (1.0 - (3.5 / (float)(n + 1)));
        i = p + float2(cos(tt - i.x) + sin(tt + i.y), sin(tt - i.y) + cos(tt + i.x));
        c += 1.0 / length(float2(p.x / (sin(i.x + tt) / inten), p.y / (cos(i.y + tt) / inten)));
    }
    c /= (float)iters;
    c = 1.17 - pow(abs(c), 1.4);
    return pow(abs(c), 8.0);
}

float Caustic(float2 xz, float t, int iters)
{
    float2 r = float2(xz.x * 0.8 - xz.y * 0.6, xz.x * 0.6 + xz.y * 0.8);
    float a = CausticLayer(xz * 0.31, t, iters);
    float b = CausticLayer(r * 0.23 + 0.31, t * 0.8 + 3.0, iters);
    return saturate((a + b) * 0.55);
}

// ── underwater light ─────────────────────────────────────────────────────────
float DepthAtten(float y)
{
    float d = max(0.0, SurfaceCol.w - y);
    return exp(-d * lerp(0.05, 0.28, Water.z));
}

float3 FogColorAt(float3 d)
{
    float up = saturate(d.y * 0.5 + 0.5);
    float L = saturate(SceneLight());
    float3 shallow = SurfaceCol.rgb * LightCol.rgb * 0.78 * (0.55 + 0.45 * L);
    float k = pow(up, 1.7) * (1.0 - Water.z * 0.75);
    return lerp(FogCol.rgb, shallow, k) * (0.85 + 0.15 * L);
}

float3 ApplyFog(float3 c, float dist, float3 rd)
{
    float f = 1.0 - exp(-dist * (FogCol.w + 0.012));
    return lerp(c, FogColorAt(rd), saturate(f));
}

// Shared surface lighting for fish, plants and the floor.
float3 LightSurface(float3 albedo, float3 N, float3 V, float3 wpos, float causticAmt, float specAmt)
{
    float da = DepthAtten(wpos.y);
    float3 sky = SurfaceCol.rgb * LightCol.rgb;
    float3 amb = lerp(DeepCol.rgb * 0.8 + 0.02, sky * 0.75, saturate(N.y * 0.5 + 0.5)) * da;
    float diff = saturate(dot(N, LIGHT_DIR) * 0.6 + 0.4);
    float3 sun = LightCol.rgb * SceneLight() * diff * da;

    float c = 0.0;
    if (causticAmt > 0.001)
    {
        int iters = Quality.x < 0.5 ? 2 : 3;
        c = Caustic(wpos.xz + wpos.y * 0.25, Time() * (0.5 + 0.25 * Music.y), iters)
            * Water.y * da * saturate(N.y * 0.5 + 0.6);
    }

    float3 H = normalize(LIGHT_DIR + V);
    float sp = pow(saturate(dot(N, H)), 48.0) * specAmt * da;
    float rim = pow(1.0 - saturate(dot(N, V)), 3.0) * 0.25 * da;
    return albedo * (amb * 0.9 + sun * 0.8 + LightCol.rgb * c * causticAmt * 0.6) + sky * rim + LightCol.rgb * sp;
}

// ── species table access ─────────────────────────────────────────────────────
struct Species
{
    float4 s0; float4 s1; float4 s2; float4 s3; float4 s4; float4 s5;
};

Species GetSpecies(uint idx)
{
    Species s;
    uint b = min(idx, 63u) * 6u;
    s.s0 = Style[b + 0u];
    s.s1 = Style[b + 1u];
    s.s2 = Style[b + 2u];
    s.s3 = Style[b + 3u];
    s.s4 = Style[b + 4u];
    s.s5 = Style[b + 5u];
    return s;
}

// full-screen triangle helper
float2 FullscreenUV(uint id) { return float2((id << 1) & 2, id & 2); }
