// Underwater volume: floor, water surface / ceiling, cave walls, depth fog and god rays.
// Everything is analytic ray-plane shading - no textures, no meshes, resolution independent.

struct BGOut
{
    float4 pos : SV_Position;
    float2 ndc : TEXCOORD0;
};

BGOut VSBackground(uint id : SV_VertexID)
{
    float2 uv = FullscreenUV(id);
    BGOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.ndc = o.pos.xy;
    return o;
}

float3 ShadeFloor(float3 p, float3 rd, float slope)
{
    float2 q = p.xz;
    float n1 = fbm2(q * 0.7);
    float n2 = fbm2(q * 0.22 + 7.3);
    float3 sand = SandCol.rgb * (0.72 + 0.55 * n1);
    float rockMask = smoothstep(0.55, 0.78, n2);
    float3 albedo = lerp(sand, RockCol.rgb * (0.6 + 0.8 * n1), rockMask);
    albedo *= 0.92 + 0.08 * sin(q.x * 2.6 + n1 * 7.0);

    float e = 0.12;
    float hx = fbm2((q + float2(e, 0)) * 0.7);
    float hz = fbm2((q + float2(0, e)) * 0.7);
    float3 n0 = normalize(float3(0.0, 1.0, -slope));
    float3 n = normalize(n0 + float3(-(hx - n1) / e * 0.5, 0.0, -(hz - n1) / e * 0.5));
    return LightSurface(albedo, n, -rd, p, 1.6, 0.0);
}

float3 ShadeSurface(float3 p, float3 rd, float surfY, int ceilKind)
{
    float t = Time();
    float2 q = p.xz;
    float tt = t * 0.35;
    float2 flow = float2(tt, tt * 0.7);
    float w1 = fbm2(q * 0.35 + flow);
    float w2 = fbm2(q * 0.9 + float2(-tt * 1.3, tt));
    float e = 0.15;
    float wx = fbm2((q + float2(e, 0)) * 0.35 + flow);
    float wz = fbm2((q + float2(0, e)) * 0.35 + flow);
    float amp = (0.55 + Music.y * 0.5 + Music.x * 0.25) * (ceilKind == 2 ? 0.08 : 1.0);
    float3 nrm = normalize(float3((wx - w1) / e * amp * 0.9, -1.0, (wz - w1) / e * amp * 0.9));

    float cosI = saturate(dot(rd, -nrm));
    float window = smoothstep(0.62, 0.70, cosI);

    float3 sky = lerp(SurfaceCol.rgb * 1.1, LightCol.rgb, 0.5) * (1.2 + 0.6 * saturate(SceneLight())) * 1.5;
    float3 mirror = FogColorAt(reflect(rd, nrm)) * 0.9;
    float ridges = pow(saturate(1.0 - abs(w2 * 2.0 - 1.0)), 6.0) * 0.25;
    float3 col = lerp(mirror, sky, window);
    col += LightCol.rgb * ridges * window * 0.5 * (1.0 + Music.z * 0.6);
    if (ceilKind == 2)
        col = lerp(col, SurfaceCol.rgb * 1.15, 0.35) + float3(0.04, 0.07, 0.1) * ridges * 4.0;
    return col;
}

float3 ShadeCeilingRock(float3 p, float3 rd)
{
    float n = fbm2(p.xz * 0.6);
    float3 albedo = RockCol.rgb * (0.35 + 0.8 * n);
    float3 lit = LightSurface(albedo, float3(0, -1, 0), -rd, p, 0.3, 0.0);
    float hole = smoothstep(3.0, 1.6, length(p.xz - float2(-3.0, -9.0)));
    float3 sky = lerp(SurfaceCol.rgb * 1.1, LightCol.rgb, 0.5) * 2.2;
    return lerp(lit, sky, hole);
}

float3 ShadeWall(float3 p, float3 rd, float3 normal)
{
    float2 c = float2(p.z, p.y);
    float n = fbm2(c * 0.5 + normal.x * 5.0 + normal.z * 3.0);
    float n2 = fbm2(c * float2(0.15, 1.2) + 4.0);
    float3 albedo = RockCol.rgb * (0.45 + 0.9 * n) * (0.8 + 0.3 * n2);
    float e = 0.2;
    float nx = fbm2((c + float2(e, 0)) * 0.5 + normal.x * 5.0 + normal.z * 3.0);
    float ny = fbm2((c + float2(0, e)) * 0.5 + normal.x * 5.0 + normal.z * 3.0);
    float3 pn = normalize(normal + float3(0.0, -(ny - n) / e * 0.6, -(nx - n) / e * 0.6));
    return LightSurface(albedo, pn, -rd, p, 0.8, 0.0);
}

float4 PSBackground(BGOut i) : SV_Target
{
    float t = Time();
    float4 a = mul(float4(i.ndc, 0.0, 1.0), InvViewProj);
    float4 b = mul(float4(i.ndc, 1.0, 1.0), InvViewProj);
    float3 ro = CamPos.xyz;
    float3 rd = normalize(b.xyz / b.w - a.xyz / a.w);

    float surfY = SurfaceCol.w;
    float floorY = DeepCol.w;
    float slope = SandCol.w;
    float enclosure = RockCol.w;
    float wallX = Extra.y;
    int ceilKind = (int)(Grade.w + 0.5);

    float3 col = FogColorAt(rd);
    float hitT = 1e5;
    int hitKind = 0;
    float3 wallN = float3(0, 0, 0);

    float denom = rd.y - slope * rd.z;
    if (denom < -1e-5)
    {
        float tf = (floorY + slope * ro.z - ro.y) / denom;
        if (tf > 0.0) { hitT = tf; hitKind = 1; }
    }
    if (rd.y > 1e-4)
    {
        float ts = (surfY - ro.y) / rd.y;
        if (ts > 0.0 && ts < hitT) { hitT = ts; hitKind = 2; }
    }
    if (wallX > 0.0)
    {
        if (rd.x > 1e-4)
        {
            float tw = (wallX - ro.x) / rd.x;
            if (tw > 0.0 && tw < hitT) { hitT = tw; hitKind = 3; wallN = float3(-1, 0, 0); }
        }
        else if (rd.x < -1e-4)
        {
            float tw = (-wallX - ro.x) / rd.x;
            if (tw > 0.0 && tw < hitT) { hitT = tw; hitKind = 3; wallN = float3(1, 0, 0); }
        }
        if (enclosure > 0.4 && rd.z < -1e-4)
        {
            float tz = (-19.0 - ro.z) / rd.z;
            if (tz > 0.0 && tz < hitT) { hitT = tz; hitKind = 3; wallN = float3(0, 0, 1); }
        }
    }

    if (hitKind != 0)
    {
        float3 p = ro + rd * hitT;
        float3 surf;
        if (hitKind == 1)      surf = ShadeFloor(p, rd, slope);
        else if (hitKind == 2) surf = (ceilKind == 1) ? ShadeCeilingRock(p, rd) : ShadeSurface(p, rd, surfY, ceilKind);
        else                   surf = ShadeWall(p, rd, wallN);
        col = ApplyFog(surf, hitT, rd);
    }

    // God rays - angular noise radiating from the light source (sun above, or a skylight in caves)
    float3 sunWorld = (ceilKind == 1 || enclosure > 0.6) ? float3(-3.0, surfY, -9.0) : float3(5.0, surfY + 30.0, -14.0);
    float4 sc = mul(float4(sunWorld, 1.0), ViewProj);
    float2 sNdc = sc.xy / max(sc.w, 0.1);
    float aspect = Screen.x / Screen.y;
    float2 d = (i.ndc - sNdc) * float2(aspect, 1.0);
    float ang = atan2(d.y, d.x) / TAU + 0.5;
    float dl = length(d);
    float r = pow(noise1p(ang * 14.0 + t * 0.03, 14.0), 2.0) * 0.9
            + pow(noise1p(ang * 33.0 - t * 0.05 + 11.0, 33.0), 2.0) * 0.6
            + pow(noise1p(ang * 61.0 + t * 0.08 + 3.0, 61.0), 2.0) * 0.2;
    r *= smoothstep(3.4, 0.2, dl);
    r *= 0.3 + 0.7 * smoothstep(-1.2, 1.0, i.ndc.y);
    float shimmer = 1.0 + Music.z * 0.35 * sin(ang * 380.0 + t * 2.0);
    float rayFade = (hitKind == 2) ? 0.45 : 1.0;
    col += LightCol.rgb * r * Water.x * 0.22 * (1.0 - Water.z * 0.5) * SceneLight() * shimmer * rayFade;

    return float4(col, 1.0);
}
