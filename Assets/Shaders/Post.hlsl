// Final composition: ripple refraction (pointer interaction), gentle water wobble, soft bloom,
// chromatic fringe, vignette, grade, tone mapping and dithering.

Texture2D<float4> SceneTex : register(t0);
SamplerState LinearClamp : register(s0);

struct PostOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

PostOut VSPost(uint id : SV_VertexID)
{
    float2 uv = FullscreenUV(id);
    PostOut o;
    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    o.uv = uv;
    return o;
}

float4 PSPost(PostOut i) : SV_Target
{
    float t = Time();
    float2 uv = i.uv;
    float aspect = Screen.x / Screen.y;
    float motion = Quality.z;

    // Pointer ripples refract the whole scene
    float2 offs = float2(0, 0);
    int count = (int)Misc.x;
    for (int r = 0; r < count; r++)
    {
        float4 R = Ripples[r];
        float age = t - R.z;
        if (age < 0.0 || age > 5.0) continue;
        float2 d = (uv - R.xy) * float2(aspect, 1.0);
        float dist = length(d);
        float radius = age * 0.26;
        float ring = exp(-pow((dist - radius) * 16.0, 2.0));
        float fade = exp(-age * 0.85);
        float2 dir = d / max(dist, 1e-4);
        offs += dir * ring * sin((dist - radius) * 70.0) * R.w * 0.007 * fade;
    }

    // Ambient wobble of the water column (stronger with the mid band, off for reduced motion)
    float wob = 0.0009 * (1.0 + Music.y * 0.8) * motion;
    offs += float2(sin(uv.y * 9.0 + t * 0.7), cos(uv.x * 7.0 + t * 0.6)) * wob;

    float2 suv = uv + offs;
    float3 col;
    float edge = length((uv - 0.5) * float2(aspect, 1.0));
    float ca = 0.0016 * edge;
    col.r = SceneTex.SampleLevel(LinearClamp, suv + float2(ca, 0), 0).r;
    col.g = SceneTex.SampleLevel(LinearClamp, suv, 0).g;
    col.b = SceneTex.SampleLevel(LinearClamp, suv - float2(ca, 0), 0).b;

    // Cheap golden-spiral bloom
    if (Quality.w > 0.5)
    {
        float3 bloom = float3(0, 0, 0);
        const int taps = 12;
        for (int k = 0; k < taps; k++)
        {
            float fk = (float)k + 0.5;
            float rr = sqrt(fk / taps) * 0.018;
            float th = fk * 2.39996;
            float2 o = float2(cos(th), sin(th)) * rr * float2(1.0 / aspect, 1.0);
            float3 s = SceneTex.SampleLevel(LinearClamp, suv + o, 0).rgb;
            bloom += max(s - 0.75, 0.0);
        }
        col += bloom / taps * (0.9 + Music.z * 0.4 + Music2.x * 0.2);
    }

    // Grade
    float exposure = Grade.x * (1.0 + Music2.x * 0.03 + Music.x * 0.03);
    col *= exposure;
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(luma.xxx, col, Grade.y);
    float vig = 1.0 - Grade.z * smoothstep(0.35, 1.05, edge);
    col *= vig;

    // Tone map + transition fade
    col = col / (1.0 + 0.22 * col);
    col *= Extra.x;

    // Dither (kills banding in the dark gradients)
    float n = hash21(i.pos.xy + frac(t) * 61.0) - 0.5;
    col += n / 255.0;
    return float4(saturate(col), 1.0);
}
