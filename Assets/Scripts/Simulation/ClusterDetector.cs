using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Simulation
{
    /// <summary>
    /// Identifies the player cluster each frame using BFS flood-fill over the spatial grid.
    ///
    /// "Cluster" definition: player-owned particles plus non-owned particles reachable
    /// within _expansionRadius, up to _maxNonOwnedHops non-owned hops from the nearest
    /// player-owned particle. Particles reachable only through longer chains are excluded,
    /// preventing loose structures far from the core from inflating the count.
    ///
    /// Results are written to IsInCluster (per-particle) and ClusterParticleCount each LateUpdate.
    ///
    /// Setup: attach to the same GameObject as ParticleSimulation, or assign _simulation in Inspector.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class ClusterDetector : MonoBehaviour
    {
        [SerializeField] private ParticleSimulation _simulation;

        [Tooltip("BFS 扩展时粒子间的最大距离阈值（世界单位）。建议略小于引力矩阵最大作用距离。")]
        [SerializeField] private float _expansionRadius = 5f;

        [Tooltip("从最近归属粒子出发，允许经过的最大非归属粒子跳数。超过此值则截断扩展。")]
        [SerializeField][Min(0)] private int _maxNonOwnedHops = 2;

        [Tooltip("每隔几帧执行一次 BFS。团簇成员关系变化慢，无需每帧重算。")]
        [SerializeField] private int _detectEveryNFrames = 3;

        private NativeArray<bool>      _isInCluster;
        private NativeQueue<int2>      _bfsQueue;   // x = particle index, y = non-owned hop depth
        private int                    _frameCounter;
        private float                  _expansionRadiusSq;

        /// <summary>Number of particles in the player cluster this frame.</summary>
        public int ClusterParticleCount { get; private set; }

        /// <summary>
        /// Per-particle cluster membership flag. Valid after this component's LateUpdate (ExecutionOrder = 5).
        /// </summary>
        public NativeArray<bool> IsInCluster => _isInCluster;

        private void Awake()
        {
            if (_simulation == null)
                _simulation = GetComponent<ParticleSimulation>();

            _isInCluster       = new NativeArray<bool>(_simulation.MaxParticleCount, Allocator.Persistent);
            _bfsQueue          = new NativeQueue<int2>(Allocator.Persistent);
            _expansionRadiusSq = _expansionRadius * _expansionRadius;
        }

        private void LateUpdate()
        {
            if (_frameCounter++ % _detectEveryNFrames == 0)
                Detect();
        }

        private void OnDestroy()
        {
            if (_isInCluster.IsCreated) _isInCluster.Dispose();
            if (_bfsQueue.IsCreated)    _bfsQueue.Dispose();
        }

        // ── BFS flood-fill ────────────────────────────────────────────────────

        private void Detect()
        {
            int count = _simulation.ParticleCount;
            if (count == 0)
            {
                ClusterParticleCount = 0;
                return;
            }

            NativeArray<float2>                   positions     = _simulation.PositionsRead;
            NativeArray<bool>                     isPlayerOwned = _simulation.IsPlayerOwned;
            NativeParallelMultiHashMap<int2, int> grid          = _simulation.Grid;
            float                                 cellSize      = _simulation.CellSize;

            for (int i = 0; i < count; i++)
                _isInCluster[i] = false;

            NativeQueue<int2> queue = _bfsQueue;
            queue.Clear();
            int clusterCount = 0;

            // Seed: all player-owned particles at hop depth 0
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                _isInCluster[i] = true;
                queue.Enqueue(new int2(i, 0));
                clusterCount++;
            }

            int gridRange = (int)math.ceil(_expansionRadius / cellSize);

            while (queue.TryDequeue(out int2 entry))
            {
                int idx   = entry.x;
                int depth = entry.y;

                int2 cell = (int2)math.floor(positions[idx] / cellSize);

                for (int dx = -gridRange; dx <= gridRange; dx++)
                for (int dy = -gridRange; dy <= gridRange; dy++)
                {
                    int2 neighborCell = cell + new int2(dx, dy);

                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it))
                        continue;

                    do
                    {
                        if (j >= count || _isInCluster[j]) continue;

                        if (math.distancesq(positions[idx], positions[j]) > _expansionRadiusSq)
                            continue;

                        // Player-owned neighbors reset depth to 0; non-owned increment depth
                        int nextDepth = isPlayerOwned[j] ? 0 : depth + 1;
                        if (nextDepth > _maxNonOwnedHops) continue;

                        _isInCluster[j] = true;
                        queue.Enqueue(new int2(j, nextDepth));
                        clusterCount++;
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }

            ClusterParticleCount = clusterCount;
        }
    }
}
