// Vegetation and structures: blades, kelp ribbons, reeds, coral lobes, crystals and stone pillars.
// One ring-tube mesh serves all of them; the species-like parameters come from per-instance data.
// Sway is driven by the world current plus the mid and bass bands of the music.

struct PlantIn
{
    float4 mesh : MESH;     // u (0..1 along height), angle fraction, cap flag, unused
    float4 iPos : IPOS;     // base xyz, height
    float4 iPar : IPAR;     // width, seed, kind, glow
    float4 iA   : ICOLA;    // colour at the base (rgb), seed2
    float4 iB   : ICOLB;    // colour at the tip (rgb)
};

struct PlantOut
{
    float4 pos    : SV_Position;
    float3 wpos   : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 info   : TEXCOORD2;                 // u, angle, kind, glow
    nointerpolation float4 colA : TEXCOORD3;
    nointerpolation float4 colB : TEXCOORD4;
};

PlantOut VSPlant(PlantIn v)
{
    float u = v.mesh.x;
    float ang = v.mesh.y * TAU;
    bool cap = v.mesh.z > 0.5;
    float h = v.iPos.w;
    float width = v.iPar.x;
    float seed = v.iPar.y;
    int kind = (int)(v.iPar.z + 0.5);
    float glow = v.iPar.w;
    float t = Time();

    float yaw = seed * TAU;
    float2 wd = float2(cos(yaw), sin(yaw));

    float w;
    float flex;
    float thin = 1.0;
    if (kind == 0)       { w = width * (1.0 - 0.92 * u);                                                   flex = 1.0;  thin = 0.07; }
    else if (kind == 1)  { w = width * (0.55 + 0.45 * sin(PI * min(1.0, u * 1.6))) * (1.0 - 0.85 * pow(u, 6.0));
                           w *= 1.0 + 0.25 * sin(u * 18.0 + seed * 20.0);                                  flex = 1.2;  thin = 0.07; }
    else if (kind == 2)  { w = width * (1.0 - 0.75 * u);                                                   flex = 0.45; thin = 0.25; }
    else if (kind == 3)  { w = width * (0.5 + 0.5 * sin(u * PI * 0.9 + 0.3)) * (1.0 + 0.25 * sin(u * 14.0 + ang * 3.0 + seed * 30.0));
                           flex = 0.05; }
    else if (kind == 4)  { w = width * pow(saturate(1.0 - u), 0.8);                                         flex = 0.0; }
    else                 { w = width;                                                                       flex = 0.0; }
    float rad = cap ? 0.0 : w;

    float cs = cos(ang);
    float sn = sin(ang);
    float3 local = float3(wd.x * cs * rad - wd.y * sn * rad * thin, 0.0, wd.y * cs * rad + wd.x * sn * rad * thin);

    // sway: ambient current + mid band (vegetation movement) + a touch of bass
    float motion = Quality.z;
    float amp = (0.06 + Music.y * 0.12 + Music.x * 0.05) * (0.4 + Water.w * 1.4);
    float s1 = sin(t * 0.9 + seed * 40.0 + u * 1.8 + v.iPos.x * 0.3);
    float s2 = sin(t * 0.55 + seed * 17.0 + v.iPos.z * 0.2);
    float bendAmount = flex * h * u * u * motion;
    float2 sway = float2(s1 * amp + s2 * Water.w * 0.18, cos(t * 0.7 + seed * 9.0) * amp * 0.4) * bendAmount;

    float3 p = v.iPos.xyz + float3(0.0, u * h, 0.0) + local + float3(sway.x, -dot(sway, sway) * 0.12, sway.y);

    float3 n = float3(wd.x * cs * thin - wd.y * sn, 0.0, wd.y * cs * thin + wd.x * sn);
    if (cap) n = float3(0, 1, 0);
    else if (kind == 4) n.y += 0.5;

    PlantOut o;
    o.pos = mul(float4(p, 1.0), ViewProj);
    o.wpos = p;
    o.normal = n;
    o.info = float4(u, ang, (float)kind, glow);
    o.colA = float4(v.iA.rgb, v.iA.w);
    o.colB = float4(v.iB.rgb, 1.0);
    return o;
}

float4 PSPlant(PlantOut i) : SV_Target
{
    float3 V = normalize(CamPos.xyz - i.wpos);
    int kind = (int)(i.info.z + 0.5);
    float3 N = normalize(i.normal);
    // derivatives must be computed outside of flow control
    float3 fn = cross(ddx(i.wpos), ddy(i.wpos));
    float fl = length(fn);
    if (kind >= 4 && fl > 1e-10) N = fn / fl;
    if (dot(N, V) < 0.0) N = -N;

    float u = saturate(i.info.x);
    float seedVar = i.colA.w;
    float3 albedo = lerp(i.colA.rgb, i.colB.rgb, pow(u, 0.85));
    albedo *= 0.85 + 0.3 * seedVar;
    float emissive = 0.0;
    float glow = i.info.w;

    if (kind == 3)
    {
        float sp = noise3(i.wpos * 14.0);
        albedo *= 0.8 + 0.4 * sp;
        albedo = lerp(albedo, i.colB.rgb * 1.2, smoothstep(0.7, 0.9, sp) * 0.5);
    }
    else if (kind == 4)
    {
        emissive = glow * (0.35 + 0.65 * u);
    }
    else if (kind == 5)
    {
        albedo *= 0.8 + 0.25 * fbm2(float2(i.wpos.y * 2.0, i.wpos.x * 3.0 + i.wpos.z * 3.0));
        albedo = lerp(albedo, float3(0.18, 0.30, 0.16), saturate(1.0 - u * 3.0) * 0.5);
        emissive = glow * 0.5;
    }
    else if (glow > 0.0)
    {
        emissive = glow * pow(u, 1.5);
    }

    float3 lit = LightSurface(albedo, N, V, i.wpos, 0.7, kind >= 4 ? 0.5 : 0.1);

    if (kind <= 2)
    {
        float tr = pow(saturate(dot(-V, LIGHT_DIR)), 2.0) * u * 0.6;
        lit += albedo * LightCol.rgb * tr * DepthAtten(i.wpos.y);
    }
    lit += i.colB.rgb * emissive * (1.0 + Music.z * 0.3);

    float dist = length(CamPos.xyz - i.wpos);
    float3 col = ApplyFog(lit, dist, -V);
    col += i.colB.rgb * emissive * 0.3 * exp(-dist * 0.05);
    return float4(col, 1.0);
}
