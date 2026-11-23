using System.Numerics;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Models;

namespace NEREA.Core.Engine.FishSimulation;

/// <summary>
/// Procedural behavioural simulation of a whole aquatic ecosystem.
/// Per animal: wandering, schooling (alignment / cohesion / separation), avoidance of other animals and
/// boundaries, curiosity towards the camera, fear and scattering, resting / hovering, depth preference,
/// territory and species specific locomotion - all with acceleration and turning inertia.
/// Agents live in a pooled array; <see cref="Update"/> performs no allocations.
/// </summary>
public sealed class EcosystemSimulation
{
    public const int MaxAgents = 360;

    private readonly FishAgent[] agents = new FishAgent[MaxAgents];
    private int count;
    private SpeciesDefinition[] species = Array.Empty<SpeciesDefinition>();

    private readonly Vector3[] groupTarget = new Vector3[MaxAgents];
    private readonly float[] groupTimer = new float[MaxAgents];
    private readonly int[] groupStart = new int[MaxAgents];
    private readonly int[] groupEnd = new int[MaxAgents];
    private float[] groupAlarm = new float[MaxAgents];
    private float[] nextAlarm = new float[MaxAgents];
    private int groupCount;

    private WorldDefinition world = new();
    private readonly SceneCamera camera;
    private readonly Random rng = new(7);
    private float time;

    private readonly object impulseLock = new();
    private readonly List<Impulse> pendingImpulses = new(8);
    private readonly List<Impulse> workingImpulses = new(8);

    private const float ZMin = -15f;

    private readonly record struct Impulse(Vector3 Origin, Vector3 Direction, float Strength);

    public EcosystemSimulation(SceneCamera camera) => this.camera = camera;

    public ReadOnlySpan<FishAgent> Agents => agents.AsSpan(0, count);
    public IReadOnlyList<SpeciesDefinition> ActiveSpecies => species;
    public int Count => count;

    /// <summary>Rebuilds the population for a world. Call from the simulation thread only.</summary>
    public void Configure(WorldDefinition newWorld, IEnumerable<SpeciesPopulation> populations,
        ContentCatalog catalog, float populationScale)
    {
        world = newWorld;
        count = 0;
        groupCount = 0;
        Array.Clear(groupAlarm);
        Array.Clear(nextAlarm);

        var specList = new List<SpeciesDefinition>();
        foreach (var pop in populations)
        {
            if (!pop.Enabled || pop.Count <= 0) continue;
            var sp = catalog.FindSpecies(pop.SpeciesId);
            if (sp == null || specList.Count >= 64) continue; // the GPU style table holds 64 species

            int n = (int)MathF.Round(pop.Count * populationScale);
            if (n < 1) n = 1;
            n = Math.Min(n, MaxAgents - count);
            if (n <= 0) break;

            int speciesIndex = specList.Count;
            specList.Add(sp);
            SpawnSpecies(sp, speciesIndex, n);
        }
        species = specList.ToArray();
    }

    private void SpawnSpecies(SpeciesDefinition sp, int speciesIndex, int n)
    {
        int perGroup = sp.Schooling > 0.05f ? Math.Max(2, sp.GroupSize) : 1;
        int spawned = 0;
        while (spawned < n && groupCount < MaxAgents)
        {
            int members = Math.Min(perGroup, n - spawned);
            int g = groupCount++;
            groupStart[g] = count;
            groupEnd[g] = count + members;
            groupTimer[g] = 0f;
            var center = RandomPoint(sp, sp.Length);

            for (int m = 0; m < members && count < MaxAgents; m++)
            {
                ref var a = ref agents[count++];
                a = default;
                a.Species = speciesIndex;
                a.Group = g;
                a.Size = sp.Length * Lerp(0.85f, 1.15f, Next());
                a.Seed = Next();
                a.Phase = Next() * MathF.Tau;
                a.SpeedFactor = Lerp(0.85f, 1.15f, Next());
                a.Boldness = Next();
                a.DepthBias = Lerp(-0.08f, 0.08f, Next());
                a.GlowPhase = Next() * MathF.Tau;

                var offset = new Vector3(Gauss(), Gauss() * 0.5f, Gauss()) * (sp.Length * 1.5f + 0.4f);
                a.Position = center + (perGroup > 1 ? offset : Vector3.Zero);
                ClampPosition(ref a);
                a.Home = a.Position;
                float yaw = Next() * MathF.Tau;
                a.Forward = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
                a.Up = Vector3.UnitY;
                a.Velocity = a.Forward * 0.3f;
                a.Target = a.Position;
                a.TargetTimer = Next() * 2f;
                a.StateTimer = 1f + Next() * 4f;
            }
            spawned += members;
        }
    }

    /// <summary>A sudden disturbance along a ray (click, double click). Thread-safe.</summary>
    public void Scare(Vector3 origin, Vector3 direction, float strength)
    {
        lock (impulseLock) pendingImpulses.Add(new Impulse(origin, direction, strength));
    }

    public void Update(float dt, in SimulationInputs inp)
    {
        dt = Math.Clamp(dt, 0.0001f, 0.05f);
        time += dt;

        lock (impulseLock)
        {
            workingImpulses.Clear();
            workingImpulses.AddRange(pendingImpulses);
            pendingImpulses.Clear();
        }
        foreach (var imp in workingImpulses) ApplyImpulse(imp, inp.ReactionStrength);

        (groupAlarm, nextAlarm) = (nextAlarm, groupAlarm);
        Array.Clear(nextAlarm, 0, groupCount);

        for (int i = 0; i < count; i++) Step(i, dt, inp);
    }

    // ───────────────────────────── per-agent step ─────────────────────────────

    private void Step(int index, float dt, in SimulationInputs inp)
    {
        ref var a = ref agents[index];
        var sp = species[a.Species];
        float size = a.Size;
        bool bottom = sp.Bottom > 0.5f;
        bool isJelly = sp.Plan == BodyPlan.Jelly;
        bool isCeph = sp.Plan == BodyPlan.Cephalopod;

        float cruiseBase = MathF.Min(sp.Speed * MathF.Pow(sp.Length, 0.8f) * 1.3f * a.SpeedFactor, 3.4f);
        float bassBoost = 1f + (sp.Length > 1.4f ? 0.28f : 0.1f) * inp.Music.Bass;
        float cruise = cruiseBase * inp.Activity * (0.85f + 0.3f * inp.Music.Energy) * bassBoost;

        a.StateTimer -= dt;
        a.TargetTimer -= dt;
        a.Fear = MathF.Max(0f, a.Fear - dt * 0.4f);
        float alarm = groupAlarm[a.Group];
        if (alarm > a.Fear && sp.Timidity > 0.1f) a.Fear = MathF.Max(a.Fear, alarm * 0.85f);

        UpdateState(ref a, sp, bottom, isJelly);

        // Choose destination
        if (a.State == FishState.Cruise || a.State == FishState.Rest)
        {
            if (a.TargetTimer <= 0f || Vector3.DistanceSquared(a.Position, a.Target) < Sq(size * 1.5f + 0.4f))
                PickTarget(ref a, sp, bottom);
        }

        float targetSpeed = cruise;
        switch (a.State)
        {
            case FishState.Rest: targetSpeed = cruise * 0.06f; break;
            case FishState.Flee: targetSpeed = cruise * 2.6f; break;
            case FishState.Curious: targetSpeed = cruise * 0.9f; break;
        }
        if (isCeph) targetSpeed *= 0.25f + 1.2f * Sq(MathF.Max(0f, MathF.Sin(a.Phase * 0.45f)));
        if (isJelly) targetSpeed *= 0.35f;

        Vector3 toTarget = a.Target - a.Position;
        float dist = toTarget.Length();
        Vector3 dir = dist > 1e-3f ? toTarget / dist : a.Forward;
        float arrive = Math.Clamp(dist / (size * 3f + 0.5f), 0.35f, 1f);
        Vector3 desired = dir * targetSpeed * arrive;

        float responsiveness = Math.Clamp(2.4f / (0.5f + size * 0.6f), 0.5f, 3.2f);
        Vector3 accel = (desired - a.Velocity) * responsiveness;

        accel += SchoolingForce(index, ref a, sp, size, inp.Music);
        accel += AvoidanceForce(index, ref a, size);
        accel += BoundaryForce(a.Position, size, cruise);
        accel += DepthForce(ref a, sp);

        if (sp.Territory > 0f)
        {
            Vector3 toHome = a.Home - a.Position;
            float d = toHome.Length();
            if (d > sp.Territory) accel += toHome / d * cruise * 1.5f * MathF.Min(2f, (d - sp.Territory) / sp.Territory);
        }

        // Gentle meandering so paths are never mechanical
        accel += new Vector3(
            MathF.Sin(time * 0.6f + a.Seed * 31f),
            0.3f * MathF.Sin(time * 0.5f + a.Seed * 17f),
            0.5f * MathF.Sin(time * 0.45f + a.Seed * 47f)) * cruise * 0.25f;

        accel += PointerForce(ref a, sp, size, cruise, inp);

        if (isJelly)
        {
            // Pulse propulsion: upward thrust on the contraction, slow sink in between
            accel.Y += (MathF.Max(0f, MathF.Sin(a.Phase)) * 0.9f - 0.28f) * (0.4f + 0.3f * inp.Activity);
        }

        float maxAccel = cruise * (a.State == FishState.Flee ? 8f : 3.5f) + 0.5f;
        float accLen = accel.Length();
        if (accLen > maxAccel) accel *= maxAccel / accLen;

        a.Velocity += accel * dt;

        if (!isJelly)
        {
            // Fish cannot slide sideways: bleed lateral velocity so motion follows the heading
            float along = Vector3.Dot(a.Velocity, a.Forward);
            Vector3 lateral = a.Velocity - a.Forward * along;
            a.Velocity = a.Forward * along + lateral * MathF.Exp(-dt * 3f);
        }

        float maxSpeed = cruise * (a.State == FishState.Flee ? 3.2f : 1.7f) + 0.05f;
        float spd = a.Velocity.Length();
        if (spd > maxSpeed) { a.Velocity *= maxSpeed / spd; spd = maxSpeed; }

        UpdateOrientation(ref a, sp, spd, cruise, dt);

        a.Position += a.Velocity * dt;
        ClampPosition(ref a);

        if (bottom)
        {
            float floor = world.FloorHeightAt(a.Position.Z) + 0.05f + size * 0.12f;
            a.Position.Y += (floor - a.Position.Y) * (1f - MathF.Exp(-dt * 2.5f));
            a.Velocity.Y *= 0.5f;
        }

        // Alarm spreads through the school
        if (a.State == FishState.Flee)
            nextAlarm[a.Group] = MathF.Max(nextAlarm[a.Group], a.Fear);
    }

    private void UpdateState(ref FishAgent a, SpeciesDefinition sp, bool bottom, bool isJelly)
    {
        if (a.Fear > 0.4f && sp.Timidity > 0.05f)
        {
            if (a.State != FishState.Flee)
            {
                a.State = FishState.Flee;
                a.StateTimer = 1.5f + Next();
                // Scatter in a random direction biased away from the camera
                var scatter = new Vector3(Gauss(), Gauss() * 0.4f, -MathF.Abs(Gauss()) * 0.8f);
                if (scatter.LengthSquared() < 1e-4f) scatter = -Vector3.UnitZ;
                a.Target = a.Position + Vector3.Normalize(scatter) * (4f + a.Size * 2f);
            }
            return;
        }

        switch (a.State)
        {
            case FishState.Flee:
                if (a.Fear < 0.12f) { a.State = FishState.Cruise; a.StateTimer = 2f + Next() * 4f; a.TargetTimer = 0f; }
                break;
            case FishState.Cruise:
                if (a.StateTimer <= 0f)
                {
                    float r = Next();
                    float restChance = sp.Restfulness * 0.4f;
                    float curiousChance = (isJelly || bottom) ? 0f : sp.Curiosity * 0.12f * (0.4f + a.Boldness);
                    if (r < restChance)
                    {
                        a.State = FishState.Rest;
                        a.StateTimer = 2f + Next() * 5f;
                        a.TargetTimer = a.StateTimer;
                        a.Target = a.Position;
                    }
                    else if (r < restChance + curiousChance)
                    {
                        a.State = FishState.Curious;
                        a.StateTimer = 6f + Next() * 4f;
                        float zNear = ZMaxFor(a.Size);
                        BoundsAt(zNear, a.Size, out float hw, out float yLo, out float yHi);
                        a.Target = new Vector3(Lerp(-hw * 0.45f, hw * 0.45f, Next()), Lerp(yLo, yHi, 0.35f + Next() * 0.4f), zNear);
                        a.TargetTimer = a.StateTimer;
                    }
                    else a.StateTimer = 3f + Next() * 5f;
                }
                break;
            case FishState.Rest:
                if (a.StateTimer <= 0f) { a.State = FishState.Cruise; a.StateTimer = 3f + Next() * 5f; a.TargetTimer = 0f; }
                break;
            case FishState.Curious:
                if (a.StateTimer <= 0f || a.Position.Z > ZMaxFor(a.Size) - 0.6f)
                { a.State = FishState.Cruise; a.StateTimer = 4f + Next() * 4f; a.TargetTimer = 0f; }
                break;
        }
    }

    private void PickTarget(ref FishAgent a, SpeciesDefinition sp, bool bottom)
    {
        int g = a.Group;
        bool schooled = sp.Schooling > 0.3f && groupEnd[g] - groupStart[g] > 1;
        if (schooled)
        {
            if (time >= groupTimer[g])
            {
                // First member to notice an expired target selects the next shared destination
                groupTarget[g] = RandomPoint(sp, a.Size);
                groupTimer[g] = time + 6f + Next() * 8f;
            }
            var jitter = new Vector3(Gauss(), Gauss() * 0.5f, Gauss()) * (a.Size * 2f);
            a.Target = groupTarget[g] + jitter;
            a.TargetTimer = 2f + Next() * 2f;
            return;
        }

        Vector3 p;
        if (sp.Territory > 0f)
        {
            p = a.Home + new Vector3(Gauss(), Gauss() * 0.4f, Gauss()) * sp.Territory * 0.6f;
        }
        else p = RandomPoint(sp, a.Size);

        if (bottom) p.Y = world.FloorHeightAt(p.Z) + 0.1f;
        a.Target = p;
        a.TargetTimer = 4f + Next() * 6f;
    }

    private Vector3 RandomPoint(SpeciesDefinition sp, float size)
    {
        float z = Lerp(ZMin + 1f, ZMaxFor(size), Next());
        BoundsAt(z, size, out float hw, out float yLo, out float yHi);
        float x = Lerp(-hw, hw, Next());
        float frac = Lerp(sp.DepthMin, sp.DepthMax, Next());
        float y = Math.Clamp(DepthY(z, frac), yLo, yHi);
        return new Vector3(x, y, z);
    }

    // ───────────────────────────── forces ─────────────────────────────

    private Vector3 SchoolingForce(int self, ref FishAgent a, SpeciesDefinition sp, float size, in MusicState music)
    {
        if (sp.Schooling <= 0.05f) return Vector3.Zero;
        int gs = groupStart[a.Group], ge = groupEnd[a.Group];
        if (ge - gs < 2) return Vector3.Zero;

        Vector3 avgVel = Vector3.Zero, center = Vector3.Zero, sep = Vector3.Zero;
        int n = 0;
        float perceptionSq = Sq(size * 8f + 1.5f);
        float sepRadius = size * 1.6f + 0.12f;
        float sepSq = sepRadius * sepRadius;

        for (int j = gs; j < ge; j++)
        {
            if (j == self) continue;
            ref var o = ref agents[j];
            Vector3 d = o.Position - a.Position;
            float d2 = d.LengthSquared();
            if (d2 > perceptionSq) continue;
            n++;
            avgVel += o.Velocity;
            center += o.Position;
            if (d2 < sepSq) sep -= d / MathF.Max(d2, 0.01f);
        }
        if (n == 0) return Vector3.Zero;

        float w = sp.Schooling * (0.8f + 0.6f * music.Mid);
        avgVel /= n;
        center /= n;
        Vector3 toCenter = center - a.Position;
        float tc = toCenter.Length();
        if (tc > 2f) toCenter *= 2f / tc;

        return (avgVel - a.Velocity) * 1.4f * w          // alignment
             + toCenter * 0.9f * w                       // cohesion
             + sep * size * 2.2f * (0.6f + w);           // separation
    }

    private Vector3 AvoidanceForce(int self, ref FishAgent a, float size)
    {
        Vector3 push = Vector3.Zero;
        int gs = groupStart[a.Group], ge = groupEnd[a.Group];
        for (int j = 0; j < count; j++)
        {
            if (j == self || (j >= gs && j < ge)) continue; // same school handled by separation
            ref var o = ref agents[j];
            Vector3 d = a.Position - o.Position;
            float r = (size + o.Size) * 0.55f + 0.1f;
            float d2 = d.LengthSquared();
            if (d2 > r * r || d2 < 1e-6f) continue;
            float dist = MathF.Sqrt(d2);
            push += d / dist * (1f - dist / r);
        }
        return push * (0.8f + size);
    }

    private Vector3 BoundaryForce(in Vector3 p, float size, float cruise)
    {
        BoundsAt(p.Z, size, out float hw, out float yLo, out float yHi);
        float m = 1.0f + size * 0.7f;
        float fx = 0, fy = 0, fz = 0;
        if (p.X > hw - m) fx = -(p.X - (hw - m)) / m;
        else if (p.X < -hw + m) fx = (-hw + m - p.X) / m;
        if (p.Y > yHi - m * 0.6f) fy = -(p.Y - (yHi - m * 0.6f)) / m;
        else if (p.Y < yLo + m * 0.6f) fy = (yLo + m * 0.6f - p.Y) / m;
        float zMax = ZMaxFor(size);
        if (p.Z > zMax - m) fz = -(p.Z - (zMax - m)) / m;
        else if (p.Z < ZMin + m) fz = (ZMin + m - p.Z) / m;
        return new Vector3(fx, fy, fz) * (cruise * 2.5f + 0.4f);
    }

    private Vector3 DepthForce(ref FishAgent a, SpeciesDefinition sp)
    {
        float z = a.Position.Z;
        float top = DepthY(z, sp.DepthMin + a.DepthBias);
        float low = DepthY(z, sp.DepthMax + a.DepthBias);
        float y = a.Position.Y;
        float mid = (top + low) * 0.5f;
        if (y > top) return new Vector3(0, (top - y) * 0.9f, 0);
        if (y < low) return new Vector3(0, (low - y) * 0.9f, 0);
        return new Vector3(0, (mid - y) * 0.08f, 0);
    }

    private Vector3 PointerForce(ref FishAgent a, SpeciesDefinition sp, float size, float cruise, in SimulationInputs inp)
    {
        if (!inp.PointerActive || inp.ReactionStrength <= 0.01f) return Vector3.Zero;

        Vector3 rel = a.Position - inp.PointerOrigin;
        float along = Vector3.Dot(rel, inp.PointerDirection);
        if (along < 1f) return Vector3.Zero;

        Vector3 closest = rel - inp.PointerDirection * along;
        float d = closest.Length();
        float radius = 0.09f * along + size * 0.6f + 0.4f;
        if (d > radius * 4f) return Vector3.Zero;

        float reaction = inp.ReactionStrength;
        Vector3 away = d > 1e-3f ? closest / d : Vector3.UnitX;

        if (d < radius)
        {
            float k = 1f - d / radius;
            float scare = inp.PointerSpeed * sp.Timidity * (1.2f - a.Boldness * 0.6f) * reaction;
            if (scare > 0.25f)
            {
                a.Fear = MathF.Max(a.Fear, MathF.Min(1f, k * scare * 1.4f));
                return away * k * cruise * 4f * reaction;
            }
            // Slow movement: a gentle nudge aside, never panic
            return away * k * cruise * 0.8f * reaction;
        }

        // Slow, calm pointer: curious species drift closer and hover nearby
        if (inp.PointerSpeed < 0.25f && sp.Curiosity > 0.5f && along < 22f && a.State != FishState.Flee)
        {
            Vector3 onRay = inp.PointerOrigin + inp.PointerDirection * along;
            Vector3 to = onRay - a.Position;
            float len = to.Length();
            if (len > radius * 1.5f)
                return to / len * cruise * 0.7f * sp.Curiosity * reaction;
        }
        return Vector3.Zero;
    }

    private void ApplyImpulse(Impulse imp, float reaction)
    {
        for (int i = 0; i < count; i++)
        {
            ref var a = ref agents[i];
            var sp = species[a.Species];
            if (sp.Timidity <= 0.05f) continue;

            Vector3 rel = a.Position - imp.Origin;
            float along = Vector3.Dot(rel, imp.Direction);
            if (along < 0.5f) continue;
            Vector3 closest = rel - imp.Direction * along;
            float d = closest.Length();
            float radius = 0.2f * along + 2.5f;
            if (d > radius) continue;

            float k = 1f - d / radius;
            float fear = k * imp.Strength * sp.Timidity * reaction * (1.3f - a.Boldness * 0.5f);
            if (fear > 0.05f)
            {
                a.Fear = MathF.Max(a.Fear, MathF.Min(1f, fear));
                Vector3 away = d > 1e-3f ? closest / d : Vector3.UnitX;
                a.Velocity += away * k * imp.Strength * reaction * (0.8f + sp.Timidity);
            }
        }
    }

    // ───────────────────────────── orientation / animation ─────────────────────────────

    private void UpdateOrientation(ref FishAgent a, SpeciesDefinition sp, float speed, float cruise, float dt)
    {
        Vector3 oldFwd = a.Forward;
        bool isJelly = sp.Plan == BodyPlan.Jelly;

        if (!isJelly)
        {
            Vector3 desiredFwd = speed > 0.04f ? a.Velocity / speed : a.Forward;
            float maxPitch = sp.Plan == BodyPlan.Ray ? 0.35f : 0.5f;
            desiredFwd.Y = Math.Clamp(desiredFwd.Y, -maxPitch, maxPitch);
            desiredFwd = Vector3.Normalize(desiredFwd);

            float turnRate = Math.Clamp(3f / (0.6f + a.Size * 0.8f), 0.8f, 4.5f) * (a.State == FishState.Flee ? 1.8f : 1f);
            a.Forward = RotateToward(a.Forward, desiredFwd, turnRate * dt);
        }
        else
        {
            var flat = new Vector3(a.Velocity.X, 0f, a.Velocity.Z);
            if (flat.LengthSquared() > 1e-4f)
                a.Forward = RotateToward(a.Forward, Vector3.Normalize(flat), 0.8f * dt);
        }

        float yawRate = Vector3.Cross(oldFwd, a.Forward).Y / MathF.Max(dt, 1e-4f);
        float k = 1f - MathF.Exp(-dt * 5f);
        a.YawRate += (yawRate - a.YawRate) * k;
        float bendTarget = Math.Clamp(a.YawRate * 0.22f, -0.55f, 0.55f);
        a.Bend += (bendTarget - a.Bend) * (1f - MathF.Exp(-dt * 6f));
        float rollTarget = Math.Clamp(-a.YawRate * 0.18f, -0.5f, 0.5f);
        if (sp.Plan == BodyPlan.Ray) rollTarget = Math.Clamp(rollTarget + 0.5f * MathF.Sin(time * 0.17f + a.Seed * MathF.Tau), -0.9f, 0.9f);
        a.Roll += (rollTarget - a.Roll) * (1f - MathF.Exp(-dt * 4f));

        switch (sp.Plan)
        {
            case BodyPlan.Cephalopod:
                a.Up = a.Forward; // mantle points along the swim direction, arms trail behind
                break;
            case BodyPlan.Jelly:
                a.Up = Vector3.Normalize(Vector3.UnitY + new Vector3(a.Velocity.X, 0f, a.Velocity.Z) * 0.15f);
                break;
            default:
                Vector3 up = Vector3.UnitY - a.Forward * a.Forward.Y;
                up = up.LengthSquared() > 1e-5f ? Vector3.Normalize(up) : Vector3.UnitY;
                a.Up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(a.Forward, a.Roll));
                break;
        }

        // Tail / fin / bell cycle - smaller animals beat faster, effort follows speed
        float effort = Math.Clamp(speed / MathF.Max(cruise, 0.05f), 0f, 1.6f);
        float hz;
        if (sp.Plan == BodyPlan.Jelly) hz = 0.55f + 0.25f * effort;
        else hz = 3.2f / (1f + a.Size * 0.9f) * (0.35f + 0.65f * effort);
        if (a.State == FishState.Rest) hz *= 0.55f;
        a.TailRate = hz * MathF.Tau;
        a.Phase += a.TailRate * dt;
        if (a.Phase > 6283f) a.Phase %= MathF.Tau; // keep float precision healthy
        a.GlowPhase += dt * (0.6f + a.Seed);
        float targetIntensity = a.State == FishState.Rest ? 0.12f : Math.Clamp(0.2f + effort * 0.6f, 0.15f, 1f);
        a.Intensity += (targetIntensity - a.Intensity) * (1f - MathF.Exp(-dt * 3f));
    }

    private static Vector3 RotateToward(Vector3 from, Vector3 to, float maxAngle)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        float angle = MathF.Acos(dot);
        if (angle <= maxAngle || angle < 1e-4f) return to;
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.UnitY;
        axis = Vector3.Normalize(axis);
        return Vector3.Normalize(Vector3.Transform(from, Quaternion.CreateFromAxisAngle(axis, maxAngle)));
    }

    // ───────────────────────────── geometry helpers ─────────────────────────────

    private float ZMaxFor(float size) => SceneCamera.BasePosition.Z - (4f + size * 1.1f);

    private float DepthY(float z, float frac)
    {
        float top = world.SurfaceY - 0.6f;
        float low = world.FloorHeightAt(z) + 0.35f;
        return Lerp(top, low, Math.Clamp(frac, 0f, 1f));
    }

    private void BoundsAt(float z, float size, out float halfWidth, out float yLo, out float yHi)
    {
        var (hw, hh) = camera.VisibleHalfExtentsAt(z);
        halfWidth = MathF.Max(1.5f, hw * 0.98f - size * 0.25f);
        if (world.Enclosure > 0f)
            halfWidth = MathF.Min(halfWidth, Lerp(16f, 4.2f, world.Enclosure));

        float viewCenter = 0.3f + (z - 9f) * 0.04333f;
        float margin = size * 0.15f;
        yLo = MathF.Max(world.FloorHeightAt(z) + 0.25f + margin, viewCenter - hh * 0.98f);
        yHi = MathF.Min(world.SurfaceY - 0.5f - margin, viewCenter + hh * 0.98f);
        if (yHi < yLo + 0.5f) yHi = yLo + 0.5f;
    }

    private void ClampPosition(ref FishAgent a)
    {
        float zMax = ZMaxFor(a.Size);
        if (a.Position.Z > zMax) { a.Position.Z = zMax; a.Velocity.Z = MathF.Min(0f, a.Velocity.Z); }
        if (a.Position.Z < ZMin) { a.Position.Z = ZMin; a.Velocity.Z = MathF.Max(0f, a.Velocity.Z); }

        BoundsAt(a.Position.Z, a.Size, out float hw, out float yLo, out float yHi);
        hw += 0.3f;
        if (a.Position.X > hw) { a.Position.X = hw; a.Velocity.X = MathF.Min(0f, a.Velocity.X); }
        if (a.Position.X < -hw) { a.Position.X = -hw; a.Velocity.X = MathF.Max(0f, a.Velocity.X); }
        if (a.Position.Y > yHi + 0.2f) { a.Position.Y = yHi + 0.2f; a.Velocity.Y = MathF.Min(0f, a.Velocity.Y); }
        if (a.Position.Y < yLo - 0.2f) { a.Position.Y = yLo - 0.2f; a.Velocity.Y = MathF.Max(0f, a.Velocity.Y); }
    }

    // ───────────────────────────── utilities ─────────────────────────────

    private float Next() => (float)rng.NextDouble();

    /// <summary>Approximate standard normal (sum of uniforms) - cheap and allocation free.</summary>
    private float Gauss() => (Next() + Next() + Next() - 1.5f) * 1.15f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Sq(float x) => x * x;
}
