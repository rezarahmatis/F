using System.Numerics;
using NEREA.Core.Engine.AudioAnalysis;

namespace NEREA.Core.Engine.FishSimulation;

public enum FishState : byte { Cruise, Rest, Curious, Flee }

/// <summary>State of one animal. A plain struct stored in a pooled array - no per-frame allocation.</summary>
public struct FishAgent
{
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Forward;
    public Vector3 Up;
    public Vector3 Home;
    public Vector3 Target;

    /// <summary>Index into <see cref="EcosystemSimulation.ActiveSpecies"/>.</summary>
    public int Species;
    /// <summary>Index of the school this individual belongs to.</summary>
    public int Group;

    public float Size;
    public float Seed;
    public float Phase;
    public float TailRate;
    public float Bend;
    public float Roll;
    public float YawRate;
    /// <summary>0..1 swimming effort (drives tail/fin amplitude).</summary>
    public float Intensity;
    public float Fear;
    public float StateTimer;
    public float TargetTimer;
    public float DepthBias;
    public float SpeedFactor;
    public float Boldness;
    public float GlowPhase;

    public FishState State;
}

/// <summary>Everything the simulation needs to know about the outside world for one step.</summary>
public struct SimulationInputs
{
    public MusicState Music;
    /// <summary>Mood x music derived multiplier for overall activity (about 0.6 .. 1.5).</summary>
    public float Activity;
    public bool PointerActive;
    public Vector3 PointerOrigin;
    public Vector3 PointerDirection;
    /// <summary>0 (still) .. 1 (fast movement).</summary>
    public float PointerSpeed;
    /// <summary>User setting: fish reaction strength multiplier.</summary>
    public float ReactionStrength;
    public float DeltaTime;
}
