using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Simulation
{
    /// <summary>
    /// Identifies the player cluster each frame using BFS flood-fill over the spatial grid.
    ///
    /// "Cluster" definition: player-owned particles plus all particles reachable
    /// transitively through spatial proximity (epsilon = cellSize, DBSCAN-style connectivity).
    /// Connectivity is purely distance-based; gravity matrix attraction direction is not checked.
    ///
    /// Results are written to IsInCluster (per-particle) and ClusterParticleCount each LateUpdate.
    /// Other systems (PlayerControl, future individual-identification systems) read these after
    /// this component runs (ExecutionOrder = 5).
    ///
    /// Setup: attach to the same GameObject as ParticleSimulation, or assign _simulation in Inspector.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class ClusterDetector : MonoBehaviour
    {
        [SerializeField] private ParticleSimulation _simulation;

        /// <summary>Number of particles in the player cluster this frame (player-owned + reachable neighbours).</summary>
        public int ClusterParticleCount { get; private set; }

        /// <summary>
        /// Per-particle cluster membership flag. True for every particle that belongs to the player cluster.
        /// Valid after this component's LateUpdate (ExecutionOrder = 5).
        /// </summary>
        private NativeArray<bool> _isInCluster;
        public NativeArray<bool> IsInCluster => _isInCluster;


        private void Awake()
        {
            if (_simulation == null)
                _simulation = GetComponent<ParticleSimulation>();

            _isInCluster = new NativeArray<bool>(_simulation.MaxParticleCount, Allocator.Persistent);
        }

        private void LateUpdate()
        {
            Detect();
        }

        private void OnDestroy()
        {
            if (_isInCluster.IsCreated)
                _isInCluster.Dispose();
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

            NativeArray<float2>                      positions    = _simulation.PositionsRead;
            NativeArray<bool>                        isPlayerOwned = _simulation.IsPlayerOwned;
            NativeParallelMultiHashMap<int2, int>    grid         = _simulation.Grid;
            float                                    cellSize     = _simulation.CellSize;

            // Reset membership flags for active particles only
            for (int i = 0; i < count; i++)
                _isInCluster[i] = false;

            var queue = new NativeQueue<int>(Allocator.Temp);
            int clusterCount = 0;

            // Seed: all player-owned particles
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                _isInCluster[i] = true;
                queue.Enqueue(i);
                clusterCount++;
            }

            // Flood-fill through spatial grid
            while (queue.TryDequeue(out int idx))
            {
                int2 cell = (int2)math.floor(positions[idx] / cellSize);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int2 neighborCell = cell + new int2(dx, dy);

                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it))
                        continue;

                    do
                    {
                        if (j >= count || _isInCluster[j]) continue;

                        if (math.distance(positions[idx], positions[j]) < cellSize)
                        {
                            _isInCluster[j] = true;
                            queue.Enqueue(j);
                            clusterCount++;
                        }
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }

            ClusterParticleCount = clusterCount;
            queue.Dispose();
        }
    }
}
