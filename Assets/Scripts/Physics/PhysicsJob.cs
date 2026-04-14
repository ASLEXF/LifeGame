using ParticleLife.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParticleLife.Physics
{
    /// <summary>
    /// Per-frame particle physics simulation.
    /// Reads positions from positionsRead (double-buffer), writes to positionsWrite.
    /// Computes gravity forces via the spatial grid, applies velocity damping,
    /// updates idle time, and applies boundary repulsion at world edges.
    ///
    /// World topology: bounded rectangle [-WorldHalfX, +WorldHalfX] × [-WorldHalfY, +WorldHalfY].
    /// World dimensions are derived from screen aspect ratio so the world always fills the display.
    ///
    /// Force formulas (validated in prototype):
    ///   Attraction (dist > threshold): F = direction × attraction × (1 - t),  t ∈ [0,1]
    ///   Repulsion  (dist ≤ threshold): F = -direction × repulsion × (1-t)/(t+0.05),  t = dist/threshold
    ///   Boundary: position clamped to world rect; perpendicular velocity component reflected × BounceRestitution.
    /// </summary>
    [BurstCompile]
    public struct PhysicsJob : IJobParallelFor
    {
        // ── Inputs (read-only) ──────────────────────────────────────────────
        [ReadOnly] public NativeArray<float2>                    PositionsRead;
        [ReadOnly] public NativeArray<byte>                      Types;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int>  Grid;
        [ReadOnly] public NativeArray<GravityEntry>              MatrixEntries;
        [ReadOnly] public int                                    TypeCount;
        [ReadOnly] public float                                  CellSize;
        [ReadOnly] public float                                  DeltaTime;
        [ReadOnly] public float                                  Damping;
        [ReadOnly] public float                                  MaxVelocity;
        [ReadOnly] public float                                  WorldHalfX;
        [ReadOnly] public float                                  WorldHalfY;
        [ReadOnly] public float                                  IdleVelocityThreshold;
        [ReadOnly] public float                                  BounceRestitution;
        [ReadOnly] public float                                  BoundaryThreshold;
        [ReadOnly] public float                                  BoundaryStrength;
        /// <summary>Normalised player input direction. Zero vector when no input.</summary>
        [ReadOnly] public float2 PlayerInputDir;
        /// <summary>Force magnitude injected each frame in PlayerInputDir for player particles.</summary>
        [ReadOnly] public float  PlayerInputForce;
        /// <summary>Hard velocity cap applied to player particles (replaces MaxVelocity for them).</summary>
        [ReadOnly] public float  PlayerMaxSpeed;
        /// <summary>Current player cluster size. Higher → stronger external-force resistance.</summary>
        [ReadOnly] public int    PlayerParticleCount;
        /// <summary>Cluster size at which external-force resistance reaches its maximum.</summary>
        [ReadOnly] public int    PlayerResistanceFullAt;
        /// <summary>Maximum fraction of external force that can be cancelled (0–1). Default 0.75.</summary>
        [ReadOnly] public float  PlayerMaxExternalReduction;

        /// <summary>Global multiplier applied to all particle-particle attraction and repulsion forces.</summary>
        [ReadOnly] public float  ForceScale;

        /// <summary>When true, boundary repulsion field and hard-clamp bounce are both disabled.</summary>
        public bool UnboundedWorld;

        /// <summary>
        /// Multiplier applied to repulsion between player-owned particles while the shield is active.
        /// Values > 1 cause owned particles to spread apart. Default 5.
        /// </summary>
        [ReadOnly] public float ShieldPlayerRepulsionScale;

        /// <summary>
        /// When true, external forces on player-owned particles are zeroed before force integration.
        /// This makes player particles immune to gravity from non-player particles for the duration.
        /// </summary>
        public bool ShieldActive;

        /// <summary>World-space centroid of the player cluster. Used for cohesion force.</summary>
        [ReadOnly] public float2 PlayerCentroid;
        /// <summary>
        /// Spring-like cohesion force magnitude per unit of distance from the cluster centroid.
        /// Pulls each player-owned particle toward the centroid, reducing stretch during fast movement.
        /// Not scaled by ForceScale — this is an internal cohesion force, not particle–particle gravity.
        /// 0 = disabled.
        /// </summary>
        [ReadOnly] public float ClusterCohesionStrength;

        // ── Outputs ─────────────────────────────────────────────────────────
        [WriteOnly] public NativeArray<float2> PositionsWrite;
        [NativeDisableParallelForRestriction] public NativeArray<float2> Velocities;
        [NativeDisableParallelForRestriction] public NativeArray<float>  IdleTime;
        [ReadOnly]  public NativeArray<bool>   IsPlayerOwned;
        /// <summary>
        /// Superset of IsPlayerOwned: includes player-owned particles AND non-owned particles
        /// within _connectionRadius of any player-owned particle. Used for input-force injection
        /// so the entire visual cluster responds to player input, not just owned particles.
        /// </summary>
        [ReadOnly]  public NativeArray<bool>   IsInPlayerCluster;
        [WriteOnly] public NativeArray<float2> ExternalForceOnPlayer;

        public void Execute(int i)
        {
            float2 pos       = PositionsRead[i];
            float2 vel       = Velocities[i];
            byte   typeA     = Types[i];
            float2 force     = float2.zero;
            bool   isPlayer  = IsPlayerOwned[i];
            bool   inCluster = IsInPlayerCluster[i];
            float2 extForce  = float2.zero;

            int2 cell = new int2(
                (int)math.floor(pos.x / CellSize),
                (int)math.floor(pos.y / CellSize));

            // ── Particle–particle forces over 3×3 neighborhood ────────────
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int2 neighborCell = new int2(cell.x + dx, cell.y + dy);
                if (!Grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;

                    float2 diff = PositionsRead[j] - pos;
                    float  dist = math.length(diff);
                    if (dist < 0.001f) continue;

                    GravityEntry entry     = MatrixEntries[typeA * TypeCount + Types[j]];
                    float        threshold = entry.DistanceThreshold;
                    float2       dir       = diff / dist;
                    float2       f;

                    if (dist >= threshold)
                    {
                        float maxDist = threshold * 3f;
                        if (dist < maxDist)
                        {
                            float t = (dist - threshold) / (maxDist - threshold);
                            f = dir * (entry.AttractionStrength * (1f - t));
                        }
                        else f = float2.zero;
                    }
                    else
                    {
                        float t = dist / threshold;
                        float repulsion = entry.RepulsionStrength;
                        if (ShieldActive && isPlayer && IsPlayerOwned[j])
                            repulsion *= ShieldPlayerRepulsionScale;
                        f = -dir * (repulsion * (1f - t) / (t + 0.05f));
                    }

                    force += f;

                    if (isPlayer && !IsPlayerOwned[j])
                        extForce += f;
                }
                while (Grid.TryGetNextValue(out j, ref it));
            }

            // 全局力缩放（仅粒子间引力/斥力，不影响玩家输入和边界力）
            force    *= ForceScale;
            extForce *= ForceScale;

            // Shield active: strip external forces from the total force accumulator before
            // zeroing the tracker. Simply zeroing extForce leaves the already-accumulated
            // external contribution inside `force` untouched.
            if (ShieldActive && isPlayer)
            {
                force   -= extForce;   // remove external-particle contribution from total
                extForce = float2.zero;
            }

            ExternalForceOnPlayer[i] = isPlayer ? extForce : float2.zero;

            // ── Player input force injection ───────────────────────────────
            // Applied to the entire visual cluster (IsInPlayerCluster), not just owned particles.
            // This makes the whole cluster respond cohesively to input rather than only the
            // owned core particles being pushed. Resistance and ExternalForce stay on IsPlayerOwned.
            if (inCluster && math.length(PlayerInputDir) > 0.01f)
                force += PlayerInputDir * PlayerInputForce;

            // ── Player external-force resistance ───────────────────────────
            // Only active when the player is actively pressing input.
            // Without input, external forces are 100% effective (natural drift).
            if (isPlayer && PlayerResistanceFullAt > 0 && math.length(PlayerInputDir) > 0.01f)
            {
                float resistance = math.saturate(PlayerParticleCount / (float)PlayerResistanceFullAt);
                float extScale   = 1f - resistance * PlayerMaxExternalReduction;
                force           -= extForce;
                force           += extForce * extScale;
            }

            // ── Player cluster cohesion ───────────────────────────────────
            // Spring force toward the cluster centroid for each player-owned particle.
            // Reduces stretching during fast movement without affecting non-player physics.
            // Applied after resistance scaling so it is never cancelled by the shield or resistance.
            if (isPlayer && ClusterCohesionStrength > 0f)
                force += (PlayerCentroid - pos) * ClusterCohesionStrength;

            // ── Boundary repulsion field ───────────────────────────────────
            if (!UnboundedWorld && BoundaryThreshold > 0f)
            {
                float bStr = BoundaryStrength;
                float bTh  = BoundaryThreshold;

                float dLeft   = pos.x + WorldHalfX;
                float dRight  = WorldHalfX - pos.x;
                float dBottom = pos.y + WorldHalfY;
                float dTop    = WorldHalfY - pos.y;

                if (dLeft   > 0f && dLeft   < bTh) { float t = dLeft   / bTh; force.x += bStr * (1f - t) / (t + 0.05f); }
                if (dRight  > 0f && dRight  < bTh) { float t = dRight  / bTh; force.x -= bStr * (1f - t) / (t + 0.05f); }
                if (dBottom > 0f && dBottom < bTh) { float t = dBottom / bTh; force.y += bStr * (1f - t) / (t + 0.05f); }
                if (dTop    > 0f && dTop    < bTh) { float t = dTop    / bTh; force.y -= bStr * (1f - t) / (t + 0.05f); }
            }

            // ── Integrate ──────────────────────────────────────────────────
            vel += force * DeltaTime;
            vel *= Damping;

            float speed;
            if (isPlayer)
            {
                // Player-specific velocity cap (typically tighter than MaxVelocity)
                speed = math.length(vel);
                if (speed > PlayerMaxSpeed)
                    vel = vel / speed * PlayerMaxSpeed;
            }
            else
            {
                speed = math.length(vel);
                if (speed > MaxVelocity)
                    vel = vel / speed * MaxVelocity;
            }

            // ── Hard boundary collision ────────────────────────────────────
            float2 newPos = pos + vel * DeltaTime;

            if (!UnboundedWorld)
            {
                if (newPos.x < -WorldHalfX) { newPos.x = -WorldHalfX; vel.x =  math.abs(vel.x) * BounceRestitution; }
                if (newPos.x >  WorldHalfX) { newPos.x =  WorldHalfX; vel.x = -math.abs(vel.x) * BounceRestitution; }
                if (newPos.y < -WorldHalfY) { newPos.y = -WorldHalfY; vel.y =  math.abs(vel.y) * BounceRestitution; }
                if (newPos.y >  WorldHalfY) { newPos.y =  WorldHalfY; vel.y = -math.abs(vel.y) * BounceRestitution; }
            }

            // ── Idle time ──────────────────────────────────────────────────
            if (!IsPlayerOwned[i])
            {
                IdleTime[i] = speed < IdleVelocityThreshold
                    ? IdleTime[i] + DeltaTime
                    : 0f;
            }

            Velocities[i]     = vel;
            PositionsWrite[i] = newPos;
        }
    }
}
