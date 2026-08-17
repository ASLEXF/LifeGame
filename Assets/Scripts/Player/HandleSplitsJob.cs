using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParticleLife.Player
{
    /// <summary>
    /// Burst IJob: Union-Find split detection over player-owned particles.
    /// Replaces the main-thread O(p × k²) loop in HandleSplits with Burst-compiled execution.
    ///
    /// Input:  PlayerScratch — player particle indices collected this frame (may include
    ///         stale reverted entries; IsPlayerOwned check filters them).
    /// Output: IsPlayerOwned — particles belonging to non-main fragments are set to false.
    ///
    /// UFParent/UFSize are initialised inside Execute() from PlayerScratch, so the caller
    /// only needs to ensure the arrays are large enough (≥ MaxParticleCount).
    /// </summary>
    [BurstCompile]
    public struct HandleSplitsJob : IJob
    {
        [ReadOnly] public NativeArray<int>                      PlayerScratch;
        [ReadOnly] public int                                    PlayerScratchCount;
        [ReadOnly] public NativeArray<float2>                   Positions;
        public            NativeArray<bool>                     IsPlayerOwned;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int> Grid;
        public            NativeArray<int>                      UFParent;
        public            NativeArray<int>                      UFSize;
        [ReadOnly] public float                                  CellSize;
        [ReadOnly] public float                                  ThresholdSq;
        [ReadOnly] public int                                    GridRange;

        public void Execute()
        {
            // Init UF entries for player particles only (O(p), not O(N)).
            for (int s = 0; s < PlayerScratchCount; s++)
            {
                int i = PlayerScratch[s];
                if (!IsPlayerOwned[i]) continue;
                UFParent[i] = i;
                UFSize[i]   = 1;
            }

            // Build Union-Find via spatial grid (O(p × k²)).
            for (int s = 0; s < PlayerScratchCount; s++)
            {
                int i = PlayerScratch[s];
                if (!IsPlayerOwned[i]) continue;

                float2 posI = Positions[i];
                int2   cell = (int2)math.floor(posI / CellSize);

                for (int dx = -GridRange; dx <= GridRange; dx++)
                for (int dy = -GridRange; dy <= GridRange; dy++)
                {
                    int2 nc = cell + new int2(dx, dy);
                    if (!Grid.TryGetFirstValue(nc, out int j, out var it)) continue;
                    do
                    {
                        if (j <= i || !IsPlayerOwned[j]) continue;
                        if (math.distancesq(posI, Positions[j]) < ThresholdSq)
                            UFUnion(i, j);
                    }
                    while (Grid.TryGetNextValue(out j, ref it));
                }
            }

            // Find the largest connected component (O(p)).
            int mainRoot = -1;
            int mainSize =  0;
            for (int s = 0; s < PlayerScratchCount; s++)
            {
                int i = PlayerScratch[s];
                if (!IsPlayerOwned[i]) continue;
                int r = UFFind(i);
                if (r != i) continue;
                if (UFSize[i] > mainSize) { mainSize = UFSize[i]; mainRoot = i; }
            }

            // Revert particles belonging to non-main fragments (O(p)).
            for (int s = 0; s < PlayerScratchCount; s++)
            {
                int i = PlayerScratch[s];
                if (!IsPlayerOwned[i]) continue;
                if (mainRoot >= 0 && UFFind(i) == mainRoot) continue;
                IsPlayerOwned[i] = false;
            }
        }

        private int UFFind(int x)
        {
            while (UFParent[x] != x)
            {
                UFParent[x] = UFParent[UFParent[x]];
                x = UFParent[x];
            }
            return x;
        }

        private void UFUnion(int a, int b)
        {
            int ra = UFFind(a), rb = UFFind(b);
            if (ra == rb) return;
            if (UFSize[ra] >= UFSize[rb]) { UFParent[rb] = ra; UFSize[ra] += UFSize[rb]; }
            else                          { UFParent[ra] = rb; UFSize[rb] += UFSize[ra]; }
        }
    }
}
