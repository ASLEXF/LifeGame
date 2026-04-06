using Unity.Mathematics;

namespace ParticleLife.Core
{
    /// <summary>
    /// Represents a single particle in the simulation.
    /// Particles are uniform in size; organism "size" equals cluster particle count.
    /// Designed for Unity Job System: value type, no managed references.
    /// </summary>
    public struct Particle
    {
        /// <summary>Particle type index. Range: [0, typeCount). Fixed at spawn.</summary>
        public byte Type;

        /// <summary>2D world-space position.</summary>
        public float2 Position;

        /// <summary>Current velocity vector.</summary>
        public float2 Velocity;

        /// <summary>Whether this particle belongs to the player cluster.</summary>
        public bool IsPlayerOwned;

        /// <summary>
        /// Accumulated idle time in seconds.
        /// Resets to 0 when velocity exceeds IDLE_VELOCITY_THRESHOLD.
        /// Player-owned particles never accumulate idle time.
        /// </summary>
        public float IdleTime;
    }
}
