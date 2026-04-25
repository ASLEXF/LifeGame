using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParticleLife.Physics
{
    /// <summary>
    /// Advances particle display positions by velocity × remainder-since-last-FixedUpdate.
    /// Runs in parallel on worker threads; reads are safe to overlap with ComputePlayerAverageVelocityJob.
    /// </summary>
    [BurstCompile]
    public struct ExtrapolatePositionsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> PositionsRead;
        [ReadOnly] public NativeArray<float2> Velocities;
        [ReadOnly] public float               Rem;
        [WriteOnly] public NativeArray<float2> PositionsRender;

        public void Execute(int i)
        {
            PositionsRender[i] = PositionsRead[i] + Velocities[i] * Rem;
        }
    }

    /// <summary>
    /// Computes the mean velocity of all player-owned particles.
    /// Single-threaded IJob so the reduction needs no atomics; runs in parallel with
    /// ExtrapolatePositionsJob because both declare Velocities as [ReadOnly].
    /// </summary>
    [BurstCompile]
    public struct ComputePlayerAverageVelocityJob : IJob
    {
        [ReadOnly] public NativeArray<float2> Velocities;
        [ReadOnly] public NativeArray<bool>   IsPlayerOwned;
        [ReadOnly] public int                 ParticleCount;
        public NativeReference<float2>        Result;

        public void Execute()
        {
            float2 sum = float2.zero;
            int    c   = 0;
            for (int i = 0; i < ParticleCount; i++)
            {
                if (!IsPlayerOwned[i]) continue;
                sum += Velocities[i];
                c++;
            }
            Result.Value = c > 0 ? sum / c : float2.zero;
        }
    }
}
