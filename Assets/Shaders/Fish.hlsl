// Procedural creatures. Species silhouettes are *data*: the vertex shader lofts the body from the
// species table (height, width, peak, snout, fork ...) so adding a species never touches the renderer.
// Three vertex programs share one pixel pipeline:
//   VSFish  - fusiform fish / sharks / eels (body + dorsal, pectoral, tail, anal fins)
//   VSRay   - flat winged bodies (rays, sea turtle) and crustaceans (legs)
//   VSJelly - pulsing bells with tentacles (jellyfish) and mantle + arms (cephalopods)

struct CreatureIn
{
    float4 mesh  : MESH;    // a, b, kind, aux
    float4 iPos  : IPOS;    // xyz position, length
    float4 iFwd  : IFWD;    // xyz forward, phase
    float4 iUp   : IUP;     // xyz up, swim effort
    float4 iMisc : IMISC;   // species, bend, seed, glow phase
};

struct CreatureOut
{
    float4 pos    : SV_Position;
    float3 wpos   : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 uvk    : TEXCOORD2;                   // a, b (or theta), kind, aux
    float4 extra  : TEXCOORD3;                   // shadow: height above floor, visibility
    nointerpolation float4 inst : TEXCOORD4;     // species, seed, glow phase, swim
};

// ── shared helpers ───────────────────────────────────────────────────────────
float FloorHeightAt(float z) { return DeepCol.w + SandCol.w * z; }

float3 ApplyPass(float3 wp, out float4 extra)
{
    extra = float4(0, 0, 0, 0);
    uint pass = DrawParams.x;
    if (pass == 1u)
    {
        // project onto the floor along the light direction
        float3 d = -LIGHT_DIR;
        float denom = d.y - SandCol.w * d.z;
        float s = (DeepCol.w + SandCol.w * wp.z - wp.y) / min(denom, -1e-3);
        float3 sp = wp + d * s;
        sp.y += 0.015;
        float h = max(0.0, wp.y - FloorHeightAt(wp.z));
        extra = float4(h, Misc.z, 0, 0);
        return sp;
    }
    if (pass == 2u)
    {
        wp.y = 2.0 * SurfaceCol.w - wp.y;
    }
    return wp;
}

CreatureOut Finish(CreatureIn v, float3 wp, float3 wn, float a, float b, float kind, float aux)
{
    CreatureOut o;
    float4 extra;
    float3 outPos = ApplyPass(wp, extra);
    if (DrawParams.x == 2u) wn.y = -wn.y;
    o.pos = mul(float4(outPos, 1.0), ViewProj);
    o.wpos = outPos;
    o.normal = wn;
    o.uvk = float4(a, b, kind, aux);
    o.extra = extra;
    o.inst = float4(v.iMisc.x, v.iMisc.z, v.iMisc.w, v.iUp.w);
    return o;
}

// ════════════════════════════════ FISH ═══════════════════════════════════════
float Profile(float u, float peak, float snout)
{
    float p = lerp(1.8, 0.45, snout);
    float a = u < peak ? (peak - u) / peak : (u - peak) / (1.0 - peak);
    float expo = u < peak ? p : 1.35;
    float g = pow(saturate(1.0 - pow(a, expo)), 0.55);
    return u > 0.5 ? max(g, 0.09) : g;
}

float WaveZ(float u, float phase, float swim, float bend)
{
    float amp = swim * (0.012 + 0.09 * u * u);
    float w = sin(phase - u * 5.5) * amp;
    float uc = max(0.0, u - 0.15);
    return w + bend * uc * uc * 0.9;
}

float3 FishPos(float a, float b, float kind, float aux, Species S, float phase, float swim, float bend)
{
    float H = S.s3.x, W = S.s3.y, tailLen = S.s3.z, D = S.s3.w * 0.65;
    float peak = S.s4.x, fork = S.s4.y, snout = S.s4.z, hammer = S.s4.w;
    float curl = S.s5.x;
    float3 p;

    if (kind < 0.5)
    {
        // body: a = u (0 nose .. 1 tail), b = theta around the spine
        float u = a;
        float prof = Profile(u, peak, snout);
        float sy = sin(b);
        float cz = cos(b);
        float belly = sy < 0.0 ? 1.08 : 1.0;
        p = float3(0.5 - u, sy * H * 0.5 * prof * belly, cz * W * 0.5 * prof);
        float bump = exp(-pow((u - 0.05) / 0.04, 2.0));
        p.z += (cz >= 0.0 ? 1.0 : -1.0) * hammer * bump * (1.0 - abs(sy) * 0.3);
        p.z += WaveZ(u, phase, swim, bend);
        p.y -= curl * 0.55 * u * u;
        p.x -= curl * 0.10 * u * u;
    }
    else if (kind < 1.5)
    {
        // dorsal fin: a along the base, b = height fraction
        float u0 = max(0.05, peak - 0.17);
        float u1 = min(0.88, peak + 0.33);
        float u = lerp(u0, u1, a);
        float prof = Profile(u, peak, snout);
        float baseY = H * 0.5 * prof;
        float shape = pow(saturate(1.0 - a), 0.55) * smoothstep(0.0, 0.12, a);
        float fh = b * D * shape;
        p = float3(0.5 - u - fh * 0.35, baseY - 0.01 * H + fh, WaveZ(u, phase, swim, bend) + b * b * 0.012 * sin(phase * 1.3 - u * 6.0));
        p.y -= curl * 0.55 * u * u;
    }
    else if (kind < 2.5)
    {
        // pectoral fin: a along the attachment, b outward, aux = side
        float u = peak - 0.1 + a * 0.12;
        float prof = Profile(u, peak, snout);
        float finLen = 0.10 + H * 0.28;
        float side = aux;
        float3 baseP = float3(0.5 - u, -H * 0.12 * prof, side * W * 0.5 * prof);
        float flap = sin(phase * 0.8 + side) * (0.25 + swim * 0.35);
        float3 tip = float3(-b * finLen * (0.55 + 0.45 * a), -b * finLen * 0.25, side * b * finLen * (0.6 + flap * 0.5));
        p = baseP + tip;
        p.z += WaveZ(u, phase, swim, bend);
        p.y -= curl * 0.55 * u * u;
    }
    else if (kind < 4.5)
    {
        // tail fin: a along its length, b across (0..1 -> -1..1)
        float uT = 0.965;
        float x0 = 0.5 - uT;
        float tailH = tailLen * 0.8 + H * 0.12;
        float hh = tailH * lerp(0.2, 1.0, pow(a, 0.7));
        float v = b * 2.0 - 1.0;
        float x = x0 - a * tailLen;
        x += fork * tailLen * 0.7 * (1.0 - abs(v)) * a * a;
        float uw = uT + a * tailLen * 1.1;
        p = float3(x, v * hh, WaveZ(uw, phase, swim, bend) + v * 0.02 * sin(phase - 6.0 * a));
        p.y -= curl * 0.55 * uT * uT;
    }
    else
    {
        // anal fin: mirror of the dorsal fin below the body
        float u0 = min(0.8, peak + 0.15);
        float u1 = 0.92;
        float u = lerp(u0, u1, a);
        float prof = Profile(u, peak, snout);
        float baseY = -H * 0.5 * prof * 1.08;
        float shape = pow(saturate(1.0 - a), 0.4) * smoothstep(0.0, 0.15, a);
        float fh = b * D * 0.55 * shape;
        p = float3(0.5 - u - fh * 0.3, baseY + 0.01 * H - fh, WaveZ(u, phase, swim, bend));
        p.y -= curl * 0.55 * u * u;
    }
    return p;
}

CreatureOut VSFish(CreatureIn v)
{
    Species S = GetSpecies((uint)(v.iMisc.x + 0.5));
    float len = v.iPos.w;
    float3 fwd = normalize(v.iFwd.xyz);
    float3 up = normalize(v.iUp.xyz - fwd * dot(v.iUp.xyz, fwd));
    float3 side = cross(fwd, up);
    float phase = v.iFwd.w;
    float swim = v.iUp.w;
    float bend = v.iMisc.y;

    float a = v.mesh.x, b = v.mesh.y, kind = v.mesh.z, aux = v.mesh.w;
    float3 p = FishPos(a, b, kind, aux, S, phase, swim, bend);

    float da = kind < 0.5 ? 0.012 : 0.03;
    float db = kind < 0.5 ? 0.12 : 0.03;
    float3 pa = FishPos(a + da, b, kind, aux, S, phase, swim, bend) - p;
    float3 pb = FishPos(a, b + db, kind, aux, S, phase, swim, bend) - p;
    float3 nm = cross(pb, pa);
    float l = length(nm);
    nm = l > 1e-9 ? nm / l : float3(0, 1, 0);

    float3 wp = v.iPos.xyz + (p.x * fwd + p.y * up + p.z * side) * len;
    float3 wn = normalize(nm.x * fwd + nm.y * up + nm.z * side);
    return Finish(v, wp, wn, a, b, kind, aux);
}

// ════════════════════════════════ RAY / TURTLE / CRAB ════════════════════════
float3 RayPos(float a, float b, float kind, float aux, Species S, float phase, float swim)
{
    float H = S.s3.x, Wd = S.s3.y, tailLen = S.s3.z, flap = S.s5.z, legs = S.s5.y;
    float3 p;
    if (kind < 0.5)
    {
        float u = a;
        float vn = b * 2.0 - 1.0;
        float planform;
        if (legs > 0.5 || tailLen < 0.5)
            planform = pow(saturate(sin(PI * u)), 0.55);
        else
            planform = pow(saturate(sin(PI * pow(u, 0.75))), 0.9);
        float halfSpan = Wd * 0.5 * planform;
        float edge = pow(abs(vn), 1.7);
        float thick = H * 0.5 * pow(saturate(1.0 - edge), 0.7) * pow(saturate(sin(PI * lerp(0.05, 0.95, u))), 0.5);
        bool bottom = aux > 0.5;
        float y = (bottom ? -0.6 : 1.0) * thick;
        float outer = smoothstep(0.25, 1.0, abs(vn));
        float fl = flap * sin(phase - abs(vn) * 2.5 + u * 1.2) * halfSpan * 0.55 * outer * (0.35 + swim * 0.65);
        y += fl - outer * outer * halfSpan * 0.12 * flap;
        p = float3(0.5 - u, y, vn * halfSpan);
    }
    else if (kind < 1.5)
    {
        // tail strip
        float s = a;
        float w = 0.035 * (1.0 - s * 0.85) * (b * 2.0 - 1.0);
        p = float3(-0.5 - s * tailLen, -0.01 * s, w + sin(phase * 0.6 - s * 4.0) * 0.06 * s);
        if (tailLen < 0.01) p = float3(-0.5, 0, 0);
    }
    else
    {
        // legs (crustaceans): a along the leg, b strip side, aux = leg index 0..7
        float idx = aux;
        float side = idx < 4.0 ? 1.0 : -1.0;
        float k = idx - (idx < 4.0 ? 0.0 : 4.0);
        float x0 = lerp(0.18, -0.2, k / 3.0);
        float baseZ = side * Wd * 0.5 * 0.55;
        float outward = a * Wd * 0.28;
        float lift = sin(a * PI * 0.8) * 0.12;
        p = float3(x0 + sin(phase * 1.5 + idx * 1.7) * 0.03 * a + (b * 2.0 - 1.0) * 0.012 * (1.0 - a * 0.5),
                   -H * 0.2 + lift - a * 0.22,
                   baseZ + side * outward);
        if (legs < 0.5) p = float3(0, 0, 0);
    }
    return p;
}

CreatureOut VSRay(CreatureIn v)
{
    Species S = GetSpecies((uint)(v.iMisc.x + 0.5));
    float len = v.iPos.w;
    float3 fwd = normalize(v.iFwd.xyz);
    float3 up = normalize(v.iUp.xyz - fwd * dot(v.iUp.xyz, fwd));
    float3 side = cross(fwd, up);
    float phase = v.iFwd.w;
    float swim = v.iUp.w;

    float a = v.mesh.x, b = v.mesh.y, kind = v.mesh.z, aux = v.mesh.w;
    float3 p = RayPos(a, b, kind, aux, S, phase, swim);
    float da = 0.02, db = 0.03;
    float3 pa = RayPos(a + da, b, kind, aux, S, phase, swim) - p;
    float3 pb = RayPos(a, b + db, kind, aux, S, phase, swim) - p;
    float3 nm = cross(pa, pb);
    float l = length(nm);
    nm = l > 1e-9 ? nm / l : float3(0, 1, 0);

    float3 wp = v.iPos.xyz + (p.x * fwd + p.y * up + p.z * side) * len;
    float3 wn = normalize(nm.x * fwd + nm.y * up + nm.z * side);

    // map to the same (u, theta) pattern space as fish bodies
    float vn = b * 2.0 - 1.0;
    float theta = aux > 0.5 ? (3.0 * PI * 0.5 + vn * 0.9) : (PI * 0.5 + vn * 0.9);
    return Finish(v, wp, wn, a, theta, kind, aux);
}

// ════════════════════════════════ JELLY / CEPHALOPOD ═════════════════════════
float3 JellyPos(float a, float b, float kind, float aux, Species S, float phase, float swim, float seed)
{
    float plan = S.s5.w;
    bool jelly = plan < 2.5;
    float pulse = 0.5 + 0.5 * sin(phase);
    float contract = pulse * S.s5.z;
    float phi = a * TAU;
    float3 p;

    if (kind < 0.5)
    {
        if (jelly)
        {
            float Hb = S.s3.x * 0.55;
            float rr = 0.5 * pow(max(cos(b * PI * 0.5), 0.0), 0.75) * (1.0 - 0.16 * contract);
            rr *= 1.0 + 0.08 * (1.0 - b) * sin(phase - 1.0);
            float yy = Hb * pow(max(sin(b * PI * 0.5), 0.0), 0.85) * (1.0 + 0.12 * contract);
            yy -= 0.02 * (1.0 - b) * (0.5 + 0.5 * cos(phi * 8.0));
            p = float3(cos(phi) * rr, yy - 0.12, sin(phi) * rr);
        }
        else
        {
            float R = S.s3.y * 0.9;
            float rr = R * (0.85 + 0.3 * sin(b * PI * 0.6)) * pow(saturate(1.0 - pow(b, 2.2)), 0.6);
            rr *= 1.0 + 0.06 * contract;
            p = float3(cos(phi) * rr, b * 0.45 - 0.2, sin(phi) * rr);
        }
    }
    else
    {
        // tentacles / arms: a = angular slot, b = along, aux = strip side
        float side = aux;
        float ph = a * TAU;
        float r0 = jelly ? 0.41 : S.s3.y * 0.7;
        float tl = S.s3.z * (jelly ? 0.75 : 0.55);
        float taper = 1.0 - 0.6 * b;
        float sw = sin(phase * 0.7 - b * 5.0 + ph * 3.0) * (jelly ? 0.05 : 0.07) * b;
        float3 radial = float3(cos(ph), 0, sin(ph));
        float3 tang = float3(-sin(ph), 0, cos(ph));
        float width = (jelly ? 0.012 : 0.03) * (1.0 - 0.5 * b);
        float spread = jelly ? 0.0 : b * 0.12;
        p = radial * (r0 * taper + spread) + tang * (side * width + sw)
            + float3(sw * 0.6, -b * tl - (jelly ? 0.12 : 0.2), sw * 0.4);
    }
    return p;
}

CreatureOut VSJelly(CreatureIn v)
{
    Species S = GetSpecies((uint)(v.iMisc.x + 0.5));
    float len = v.iPos.w;
    float3 Y = normalize(v.iUp.xyz);
    float3 refv = abs(Y.y) > 0.9 ? float3(1, 0, 0) : float3(0, 1, 0);
    float3 X = normalize(cross(refv, Y));
    float3 Z = cross(X, Y);
    float phase = v.iFwd.w;
    float swim = v.iUp.w;
    float seed = v.iMisc.z;

    float a = v.mesh.x, b = v.mesh.y, kind = v.mesh.z, aux = v.mesh.w;
    float3 p = JellyPos(a, b, kind, aux, S, phase, swim, seed);
    float3 nm;
    if (kind < 0.5)
    {
        float3 pa = JellyPos(a + 0.02, b, kind, aux, S, phase, swim, seed) - p;
        float3 pb = JellyPos(a, min(b + 0.03, 1.0), kind, aux, S, phase, swim, seed) - p;
        nm = cross(pb, pa);
        float l = length(nm);
        nm = l > 1e-9 ? nm / l : float3(0, 1, 0);
    }
    else
    {
        float ph = a * TAU;
        nm = float3(cos(ph), 0, sin(ph));
    }

    // slow rotation about the bell axis so no two animals look identical
    float rot = seed * TAU + Time() * 0.05;
    float cr = cos(rot), sr = sin(rot);
    float2 rxz = float2(p.x * cr - p.z * sr, p.x * sr + p.z * cr);
    p = float3(rxz.x, p.y, rxz.y);
    float2 nxz = float2(nm.x * cr - nm.z * sr, nm.x * sr + nm.z * cr);
    nm = float3(nxz.x, nm.y, nxz.y);

    float3 wp = v.iPos.xyz + (p.x * X + p.y * Y + p.z * Z) * len;
    float3 wn = normalize(nm.x * X + nm.y * Y + nm.z * Z);
    float theta = a * TAU;
    return Finish(v, wp, wn, b, theta, kind, aux);
}

// ════════════════════════════════ PIXEL SHADING ══════════════════════════════
float3 CreatureAlbedo(Species S, float u, float theta, float kind, float seed, float aux, out float emissive)
{
    float3 prim = S.s0.rgb;
    float3 sec = S.s1.rgb;
    float3 acc = S.s2.rgb;
    int pat = (int)(S.s0.w + 0.5);
    float ps = S.s2.w;
    float glow = S.s1.w;
    emissive = 0.0;

    float sy = sin(theta);
    float cz = cos(theta);
    float3 col = prim;

    // body
    float cs = (pat == 5) ? 1.0 : 0.55;
    float belly = smoothstep(0.15, -0.75, sy);
    float3 bellyCol = lerp(sec, float3(1, 1, 1), 0.35);
    col = lerp(prim * 0.85, bellyCol, belly * cs * (pat == 5 ? 1.0 : 0.6));

    if (pat == 1)
    {
        float x = u * ps - 0.5;
        float f = abs(x - round(x));
        float m = smoothstep(0.17, 0.11, f) * smoothstep(0.1, 0.18, u) * smoothstep(0.95, 0.8, u);
        float edge = smoothstep(0.23, 0.17, f) * smoothstep(0.1, 0.18, u) * smoothstep(0.95, 0.8, u) - m;
        col = lerp(col, acc, saturate(edge) * 0.85);
        col = lerp(col, sec, m);
    }
    else if (pat == 2)
    {
        float wdt = 0.09 + 0.05 * ps;
        float st = smoothstep(wdt * 2.0, wdt, abs(sy - 0.05)) * smoothstep(0.1, 0.2, u) * smoothstep(0.97, 0.7, u);
        col = lerp(col, sec, st);
        emissive += st * glow;
    }
    else if (pat == 3)
    {
        float3 q = float3(u * ps * 2.2 + seed * 13.0, cz * 0.9, sy * 0.9);
        float m1 = smoothstep(0.50, 0.56, fbm3(q));
        float m2 = smoothstep(0.64, 0.70, fbm3(q * 1.7 + 5.0));
        col = lerp(prim, sec, m1);
        col = lerp(col, acc, m2);
        col = lerp(col, bellyCol, belly * 0.25);
    }
    else if (pat == 4)
    {
        float2 g = float2(u * ps * 4.0 + seed * 5.0, theta / TAU * round(ps * 2.0 + 1.0));
        float2 cell = floor(g);
        float2 f = frac(g) - 0.5;
        float2 jit = float2(hash21(cell), hash21(cell + 17.0)) - 0.5;
        float d = length(f - jit * 0.5);
        float r = 0.12 + 0.18 * hash21(cell + 3.0);
        float m = smoothstep(r, r * 0.6, d) * step(0.35, hash21(cell + 9.0));
        col = lerp(col, sec, m);
    }
    else if (pat == 6)
    {
        col = lerp(prim * 0.7, bellyCol * 0.5, belly * 0.5);
        float rowY = smoothstep(0.18, 0.08, abs(sy + 0.45));
        float dots = smoothstep(0.55, 0.8, sin(u * ps * 14.0) * 0.5 + 0.5);
        float m = rowY * dots * smoothstep(0.12, 0.2, u) * smoothstep(0.95, 0.8, u) * (0.6 + 0.4 * abs(cz));
        float rowY2 = smoothstep(0.1, 0.04, abs(sy - 0.1));
        float dots2 = smoothstep(0.7, 0.9, sin(u * ps * 9.0 + 1.3) * 0.5 + 0.5);
        m = saturate(m + rowY2 * dots2 * 0.35 * smoothstep(0.2, 0.3, u) * smoothstep(0.9, 0.7, u));
        emissive += m * glow * 2.0;
        col = lerp(col, sec, m);
    }
    else if (pat == 7)
    {
        float s = sin(sy * ps * 2.6);
        float m = smoothstep(0.82, 0.95, s) * smoothstep(0.08, 0.18, u) * smoothstep(0.98, 0.8, u);
        col = lerp(col, sec, m);
    }

    if (pat != 6) emissive += glow * 0.18;

    // eyes (fish plan only)
    if (S.s5.w < 0.5)
    {
        float ue = 0.07 + 0.12 * S.s4.z + 0.01 * seed;
        float dt = min(abs(theta - 0.28), abs(theta - (PI - 0.28)));
        float e = length(float2((u - ue) / 0.022, dt / 0.26));
        if (e < 1.0)
        {
            col = lerp(col, acc * 0.7 + 0.12, smoothstep(0.7, 0.9, e));
            col = lerp(col, float3(0.015, 0.015, 0.02), smoothstep(0.85, 0.55, e));
        }
    }
    return col;
}

float3 FinAlbedo(Species S, float a, float b, float kind, float aux, out float alpha)
{
    float3 prim = S.s0.rgb;
    float3 sec = S.s1.rgb;
    float3 acc = S.s2.rgb;
    float3 c = lerp(acc, prim, 0.25);
    c = lerp(c, sec, b * 0.3);
    bool isTail = kind > 2.5 && kind < 4.5;
    if (isTail) c = lerp(prim, acc, 0.6);
    float streak = 0.8 + 0.2 * sin(a * 70.0 + b * 3.0);
    float tip = isTail ? a : b;
    alpha = 0.88 * streak * (1.0 - 0.5 * smoothstep(0.7, 1.0, tip));
    return c;
}

float4 PSCreature(CreatureOut i) : SV_Target
{
    uint pass = DrawParams.x;
    Species S = GetSpecies((uint)(i.inst.x + 0.5));
    float kind = i.uvk.z;
    float plan = S.s5.w;

    if (pass == 1u)
    {
        float sa = 0.30 * exp(-i.extra.x * 0.18) * i.extra.y;
        return float4(0, 0, 0, sa);
    }

    float3 V = normalize(CamPos.xyz - i.wpos);
    float3 N = normalize(i.normal);
    if (dot(N, V) < 0.0) N = -N;
    float dist = length(CamPos.xyz - i.wpos);

    float emissive = 0.0;
    float alpha = 1.0;
    float3 albedo;

    bool isFin = (plan < 0.5 && kind > 0.5);
    if (isFin)
        albedo = FinAlbedo(S, i.uvk.x, i.uvk.y, kind, i.uvk.w, alpha);
    else
        albedo = CreatureAlbedo(S, i.uvk.x, i.uvk.y, kind, i.inst.y, i.uvk.w, emissive);

    float3 lit = LightSurface(albedo, N, V, i.wpos, 0.8, 0.35);
    float pulse = 0.75 + 0.25 * sin(i.inst.z);
    float3 emissiveCol = lerp(S.s1.rgb, S.s2.rgb, 0.3);
    lit += emissiveCol * emissive * pulse * (1.0 + Music.z * 0.4);

    float3 rd = -V;
    float3 outCol = ApplyFog(lit, dist, rd);
    // bioluminescence is only partly swallowed by fog
    outCol += emissiveCol * emissive * pulse * 0.25 * exp(-dist * 0.05);

    if (pass == 2u)
    {
        float3 dir = normalize(i.wpos - CamPos.xyz);
        float mask = smoothstep(0.72, 0.58, abs(dir.y));
        float fade = exp(-(i.wpos.y - SurfaceCol.w) * 0.35);
        return float4(outCol * 0.6, mask * fade * 0.32 * alpha);
    }
    return float4(outCol, alpha);
}

float4 PSJelly(CreatureOut i) : SV_Target
{
    uint pass = DrawParams.x;
    Species S = GetSpecies((uint)(i.inst.x + 0.5));
    if (pass == 1u) return float4(0, 0, 0, 0);

    float kind = i.uvk.z;
    float3 V = normalize(CamPos.xyz - i.wpos);
    float3 N = normalize(i.normal);
    if (dot(N, V) < 0.0) N = -N;
    float dist = length(CamPos.xyz - i.wpos);

    float fres = pow(1.0 - saturate(dot(N, V)), 2.2);
    float3 baseCol = lerp(S.s0.rgb, S.s1.rgb, saturate(i.uvk.x));
    float glow = S.s1.w;
    float pulse = 0.5 + 0.5 * sin(i.inst.z);
    float3 lit = LightSurface(baseCol, N, V, i.wpos, 0.0, 0.0) * 0.7;
    lit += baseCol * glow * (0.35 + 0.35 * pulse) * (1.0 + Music.z * 0.5);
    lit += S.s2.rgb * fres * 0.6;
    float alpha = lerp(0.16, 0.7, fres);
    if (kind > 0.5) alpha = 0.55;
    alpha *= saturate(1.2 - dist * 0.02);

    float3 outCol = ApplyFog(lit, dist, -V);
    outCol += baseCol * glow * 0.3 * exp(-dist * 0.05);

    if (pass == 2u)
    {
        float3 dir = normalize(i.wpos - CamPos.xyz);
        float mask = smoothstep(0.72, 0.58, abs(dir.y));
        float fade = exp(-(i.wpos.y - SurfaceCol.w) * 0.35);
        return float4(outCol * 0.6, mask * fade * 0.3 * alpha);
    }
    return float4(outCol, alpha);
}
