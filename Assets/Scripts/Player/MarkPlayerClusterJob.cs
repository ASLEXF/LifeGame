using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParticleLife.Player
{
    /// <summary>
    /// Clears IsInPlayerCluster then marks every player-owned particle and any non-owned
    /// particle within ThresholdSq of a player-owned particle. Replaces the O(p × grid²)
    /// main-thread loop in MarkPlayerCluster with a single-threaded Burst job.
    ///
    /// Uses a serial IJob (not IJobParallelFor) because multiple player particles can reach
    /// the same neighbor, requiring a single writer to avoid data races on IsInPlayerCluster.
    /// </summary>
    [BurstCompile]
    public struct MarkPlayerClusterJob : IJob
    {
        [ReadOnly] public NativeArray<int>                          PlayerScratch;
        [ReadOnly] public int                                        PlayerScratchCount;
        [ReadOnly] public NativeArray<float2>                       Positions;
        [ReadOnly] public NativeArray<bool>                         IsPlayerOwned;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int>     Grid;
        [ReadOnly] public int                                        ParticleCount;
        [ReadOnly] public float                                      CellSize;
        [ReadOnly] public float                                      ThresholdSq;
        [ReadOnly] public int                                        GridRange;

        public NativeArray<bool> IsInPlayerCluster;

        public void Execute()
        {
            // Full clear — Burst compiles this to a vectorised memset (~5000 bytes).
            for (int i = 0; i < ParticleCount; i++)
                IsInPlayerCluster[i] = false;

            for (int s = 0; s < PlayerScratchCount; s++)
            {
                int i = PlayerScratch[s];
                if (!IsPlayerOwned[i]) continue;

                IsInPlayerCluster[i] = true;

                int2 cell = (int2)math.floor(Positions[i] / CellSize);

                for (int dx = -GridRange; dx <= GridRange; dx++)
                for (int dy = -GridRange; dy <= GridRange; dy++)
                {
                    int2 neighborCell = cell + new int2(dx, dy);
                    if (!Grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;
                    do
                    {
                        if (j < ParticleCount && !IsPlayerOwned[j] &&
                            math.distancesq(Positions[i], Positions[j]) < ThresholdSq)
                            IsInPlayerCluster[j] = true;
                    }
                    while (Grid.TryGetNextValue(out j, ref it));
                }
            }
        }
    }
}
