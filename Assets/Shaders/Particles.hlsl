// GPU particles - no CPU simulation at all. Each particle derives its position from its instance id and time.
//   type 0: marine snow (alpha)    type 1: glow motes (additive)    type 2: bubbles (rings)
// Treble brightens and sizes particles, bass adds a slow swell, the current moves everything.

struct PartOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
    float4 col : TEXCOORD1;
    nointerpolation uint type : TEXCOORD2;
};

PartOut VSParticle(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    uint type = DrawParams.y;
    float2 corner = float2((vid & 1u) ? 1.0 : -1.0, (vid & 2u) ? 1.0 : -1.0);
    float fid = (float)iid;
    float r1 = hash11(fid * 1.713 + 0.1);
    float r2 = hash11(fid * 2.371 + 3.3);
    float r3 = hash11(fid * 3.917 + 7.7);
    float r4 = hash11(fid * 5.123 + 11.1);
    float t = Time();
    float surfY = SurfaceCol.w;
    float floorY = DeepCol.w;
    float slope = SandCol.w;
    float zBase = -16.0;
    float zSpan = 21.0;
    float cur = Water.w;

    float3 pos;
    float size;
    float alpha;
    float3 tint = ParticleCol.rgb;

    if (type == 0u)
    {
        float z = frac(r3 + t * 0.002 * r4) * zSpan + zBase;
        float hw = (CamPos.z - z) / Screen.z * 1.1;
        float x = (frac(r1 + t * (0.004 + cur * 0.012) * (0.5 + r4)) * 2.0 - 1.0) * hw + sin(t * 0.2 + r2 * 30.0) * 0.3;
        float fl = floorY + slope * z;
        float range = max(surfY - fl, 1.0);
        float y = fl + frac(r2 - t * (0.02 + 0.05 * r4) / range) * range;
        x += sin(t * 0.7 + y * 0.8 + r1 * 20.0) * 0.15 * (Music.x + cur * 0.5);
        pos = float3(x, y, z);
        size = (0.025 + 0.05 * r4 * r4) * (1.0 + Music.z * 0.5);
        alpha = (0.25 + 0.6 * r3) * (0.7 + 0.5 * Music.z * r1);
    }
    else if (type == 1u)
    {
        float z = zBase + r3 * zSpan;
        float hw = (CamPos.z - z) / Screen.z;
        float fl = floorY + slope * z;
        float x = (r1 * 2.0 - 1.0) * hw + sin(t * 0.1 * (0.5 + r2) + r3 * 20.0) * 1.5;
        float y = lerp(fl + 0.5, surfY - 0.5, r2) + sin(t * 0.13 + r4 * 30.0) * 1.0;
        pos = float3(x, y, z + sin(t * 0.09 + r1 * 25.0) * 1.2);
        float pulse = pow(0.5 + 0.5 * sin(t * (0.5 + r4 * 1.5) + r1 * 40.0), 2.0);
        size = 0.05 + 0.08 * r3;
        alpha = (0.15 + 0.85 * pulse) * (1.0 + Music.z * 0.8);
    }
    else
    {
        float col = floor(fid / 14.0);
        float cr1 = hash11(col * 7.7 + 1.0);
        float cr2 = hash11(col * 3.3 + 9.0);
        float z = zBase + cr2 * 18.0;
        float hw = (CamPos.z - z) / Screen.z;
        float fl = floorY + slope * z;
        float range = max(surfY - fl, 1.0);
        float speed = 0.35 + 0.35 * r4;
        float k = frac(r2 + t * speed / range);
        float y = fl + k * range;
        float x = (cr1 * 2.0 - 1.0) * hw * 0.8 + sin(t * 2.0 + r3 * 30.0) * 0.06 * (0.5 + k) + (r1 - 0.5) * 0.3;
        pos = float3(x, y, z);
        size = (0.015 + 0.04 * r3) * (0.6 + 0.8 * k);
        alpha = 0.9 * smoothstep(0.0, 0.03, k) * smoothstep(1.0, 0.95, k);
        tint = lerp(float3(0.85, 0.95, 1.0), SurfaceCol.rgb, 0.3);
    }

    float dist = length(pos - CamPos.xyz);
    float4 clip = mul(float4(pos, 1.0), ViewProj);
    clip.xy += corner * size * float2(Screen.z, Screen.w);

    float da = 0.35 + 0.65 * DepthAtten(pos.y);
    float3 col3 = tint * da * (type == 1u ? 1.0 : SceneLight());
    alpha *= exp(-dist * 0.035);

    PartOut o;
    o.pos = clip;
    o.uv = corner;
    o.col = float4(col3, alpha);
    o.type = type;
    return o;
}

float4 PSParticle(PartOut i) : SV_Target
{
    float d = length(i.uv);
    if (d > 1.0) discard;
    float a;
    float3 c = i.col.rgb;
    if (i.type == 2u)
    {
        float ring = smoothstep(0.55, 0.85, d) * (1.0 - smoothstep(0.9, 1.0, d));
        float hi = smoothstep(0.35, 0.1, length(i.uv - float2(-0.35, 0.35)));
        a = saturate(ring * 0.55 + hi * 0.8) * i.col.a;
        c = lerp(c, float3(1, 1, 1), hi);
    }
    else
    {
        float s = saturate(1.0 - d);
        a = s * s * (3.0 - 2.0 * s) * i.col.a;
    }
    // premultiplied output; additive for glow motes
    if (i.type == 1u) return float4(c * a, 0.0);
    return float4(c * a, a);
}
