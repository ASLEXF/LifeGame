using ParticleLife.Input;
using ParticleLife.Management;
using ParticleLife.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ParticleLife.Player
{
    /// <summary>
    /// Manages the player cluster with deferred initialization and split detection.
    ///
    /// Session lifecycle:
    ///   Frame 1: AssignInitialCluster() — picks the densest same-type cluster as player.
    ///            Particles have already settled during the main menu UI transition.
    ///   Each frame (LateUpdate, order 10, after PhysicsJob is complete):
    ///     1. Split detection: Union-Find over player particles, find largest component.
    ///        Non-main-cluster particles are IMMEDIATELY reverted to normal (no timer).
    ///        ClusterCount and MainClusterSize are updated.
    ///     2. Centroid (for input direction, shedding).
    ///     3. Input force injected for next FixedUpdate.
    ///     4. Adopt same-type adjacent non-player particles (snapshot prevents cascade).
    ///     5. Edge shedding: outermost fraction at high speed may detach.
    ///     6. Count, report peak.
    ///     7. Zero-particle survival countdown.
    /// </summary>
    [DefaultExecutionOrder(10)]
    [RequireComponent(typeof(ParticleSimulation))]
    public class PlayerControl : MonoBehaviour
    {
        [Header("玩家控制")]
        [Tooltip("团簇连接判定距离：同时用于分裂检测（Union-Find）和相邻粒子吸附")]
        [SerializeField] private float _connectionRadius   = 5f;

        [SerializeField] private float _sheddingRate       = 0.6f;
        [SerializeField][Range(0f, 1f)] private float _edgeFraction = 0.2f;
        [SerializeField] private float _zeroPatienceSec    = 30f;

        [Header("分裂惩罚")]
        [Tooltip("触发级联惩罚的最小损失比例（0.1 = 损失 10% 时触发）")]
        [SerializeField] private float _splitPenaltyThreshold  = 0.10f;
        [Tooltip("额外驱逐量 = ceil(lostCount × 此倍数）")]
        [SerializeField] private float _splitPenaltyMultiplier = 0.50f;
        [Tooltip("主团簇粒子数低于此值时不触发级联（防止螺旋死亡）")]
        [SerializeField] private int   _splitPenaltyMinSize    = 5;

        [Header("引用")]
        [SerializeField] private GameInput        _input;
        [SerializeField] private GameStateManager _gameState;

        private ParticleSimulation _simulation;

        private bool  _hasAssigned;
        private byte  _playerType;
        private float _zeroTimer;

        // 吸附 BFS 每 N 帧执行一次：稳定大团簇时大多数帧 BFS 无结果，节省约 2/3 开销
        private const int AdoptEveryNFrames = 3;
        private int _adoptFrameCounter;

        // Union-Find arrays, reused each frame (allocated once at Start).
        // _ufSize tracks component size and is used for union-by-size (balanced trees).
        private int[] _ufParent;
        private int[] _ufSize;

        // Visited marker array, reused for BFS in AssignInitialCluster.
        private bool[] _bfsVisited;

        // 持久化 BFS 队列：避免 AdoptSameTypeBFS 每帧 new NativeQueue(Allocator.Temp)
        private NativeQueue<int> _adoptionQueue;

        /// <summary>Player-owned particle count this frame (equals MainClusterSize after split resolution).</summary>
        public int PlayerParticleCount { get; private set; }

        /// <summary>World-space centroid of all player-owned particles.</summary>
        public float2 ClusterCentroid { get; private set; }

        /// <summary>Size of the largest connected player cluster this frame.</summary>
        public int MainClusterSize { get; private set; }

        /// <summary>
        /// Number of disconnected player-particle groups detected before split resolution.
        /// 1 = single connected cluster (healthy). >1 = split detected; smaller groups were reverted.
        /// </summary>
        public int ClusterCount { get; private set; }

        /// <summary>True after the initial 5-second delay and cluster assignment.</summary>
        public bool IsAssigned => _hasAssigned;

        private void Awake()
        {
            _simulation = GetComponent<ParticleSimulation>();
        }

        private void Start()
        {
            int maxCount  = _simulation.MaxParticleCount;
            _bfsVisited   = new bool[maxCount];
            _ufParent     = new int[maxCount];
            _ufSize       = new int[maxCount];
            _adoptionQueue = new NativeQueue<int>(Allocator.Persistent);

            _gameState.OnStateChanged += OnStateChanged;

            // Sync enabled state to initial GameState — simulation starts at MainMenu,
            // so player input must be disabled until the game actually starts.
            enabled = _gameState.CurrentState == GameState.Running;
        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;

            if (_adoptionQueue.IsCreated)
                _adoptionQueue.Dispose();
        }

        private void OnStateChanged(GameState state)
        {
            // Disable player input while in main menu to prevent input injection into
            // the background simulation. Re-enable the moment gameplay starts.
            if (state == GameState.MainMenu)
            {
                enabled = false;
            }
            else if (state == GameState.Running)
            {
                enabled = true;
                ResetSession();
            }
        }

        private void LateUpdate()
        {
            if (_gameState.CurrentState != GameState.Running) return;

            int count = _simulation.ParticleCount;
            if (count == 0) return;

            if (!_hasAssigned)
            {
                AssignInitialCluster(count);
                return;
            }

            NativeArray<float2> positions     = _simulation.PositionsRead;
            NativeArray<bool>   isPlayerOwned = _simulation.IsPlayerOwned;
            NativeArray<byte>   types         = _simulation.Types;
            NativeArray<float2> velocities    = _simulation.Velocities;

            // 1. Split detection: revert stranded fragments, return how many were reverted
            int prevPlayerCount = PlayerParticleCount;  // last frame's count — used for loss ratio
            int reverted = HandleSplits(positions, isPlayerOwned, count, _simulation.Grid, _simulation.CellSize);

            // 2. Centroid + count in one pass (was two separate O(N) scans)
            (ClusterCentroid, PlayerParticleCount) = ComputeCentroidAndCount(positions, isPlayerOwned, count);

            // 2b. Split cascade penalty: large splits incur an extra edge-shedding cost
            if (reverted > 0 && prevPlayerCount > 0)
            {
                float lossRatio = (float)reverted / prevPlayerCount;
                if (lossRatio > _splitPenaltyThreshold && MainClusterSize > _splitPenaltyMinSize)
                {
                    int penalty = Mathf.CeilToInt(reverted * _splitPenaltyMultiplier);
                    ForceShedEdgeParticles(penalty, positions, isPlayerOwned, ClusterCentroid);
                }
            }

            // 3. Mark cluster membership for input-force injection (next FixedUpdate)
            MarkPlayerCluster(positions, isPlayerOwned, count);

            // 4. Input direction + cluster size + centroid → physics job
            _simulation.SetPlayerInput(ResolveDirection(), PlayerParticleCount, ClusterCentroid);

            // 5. Adopt same-type non-player particles via BFS over spatial grid
            // 每 3 帧执行一次：稳定大团簇时 BFS 几乎找不到新粒子，每帧播种 3000 个入口纯属浪费
            if (_adoptFrameCounter++ % AdoptEveryNFrames == 0)
                AdoptSameTypeBFS(positions, isPlayerOwned, types, count);

            // 6. Shed edge particles
            ShedEdge(positions, isPlayerOwned, velocities, count, ClusterCentroid);

            // 7. Report peak (count already updated in step 2)
            _gameState.ReportParticleCount(PlayerParticleCount);

            // 8. Zero-particle survival countdown
            if (PlayerParticleCount == 0)
            {
                _zeroTimer += Time.deltaTime;
                if (_zeroTimer >= _zeroPatienceSec)
                    _gameState.TransitionTo(GameState.Failed);
            }
            else
            {
                _zeroTimer = 0f;
            }
        }

        // ── Session reset ─────────────────────────────────────────────────────

        private void ResetSession()
        {
            _hasAssigned    = false;
            _zeroTimer      = 0f;
            MainClusterSize = 0;
            ClusterCount    = 0;

            int count = _simulation.ParticleCount;
            for (int i = 0; i < count; i++)
                _simulation.SetPlayerOwned(i, false);
        }

        // ── Initial cluster assignment ────────────────────────────────────────

        /// <summary>
        /// Finds the particle with the most same-type neighbors within _connectionRadius,
        /// records its type as the player type, and BFS-adopts all same-type particles
        /// reachable via _connectionRadius chains from that center.
        /// Guarantees a single connected component from frame 1. O(N²) one-time cost.
        /// </summary>
        private void AssignInitialCluster(int count)
        {
            NativeArray<float2> positions = _simulation.PositionsRead;
            NativeArray<byte>   types     = _simulation.Types;

            float scanSq  = _connectionRadius * _connectionRadius;
            float adoptSq = _connectionRadius * _connectionRadius;

            // Randomly select player type each session — prevents always starting as the same type.
            // Special types (index >= TypeCount) are excluded.
            _playerType = (byte)UnityEngine.Random.Range(0, _simulation.TypeCount);

            // Step 1: find the particle of _playerType with the most same-type neighbors.
            int bestIndex     = -1;
            int bestNeighbors = -1;
            for (int i = 0; i < count; i++)
            {
                if (types[i] != _playerType) continue;

                int    neighbors = 0;
                float2 posI      = positions[i];
                for (int j = 0; j < count; j++)
                {
                    if (j == i || types[j] != _playerType) continue;
                    if (math.distancesq(posI, positions[j]) < scanSq)
                        neighbors++;
                }
                if (neighbors > bestNeighbors)
                {
                    bestNeighbors = neighbors;
                    bestIndex     = i;
                }
            }

            // Fallback: chosen type has no particles — pick any non-special particle.
            if (bestIndex < 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (types[i] < _simulation.TypeCount) { bestIndex = i; break; }
                }
                if (bestIndex < 0) return;
                _playerType = types[bestIndex];
            }

            // Step 2: BFS flood-fill from bestIndex.
            // Only particles reachable via a chain of _connectionRadius-adjacent same-type
            // particles are adopted, guaranteeing one connected component.
            // Reuse _bfsVisited as the "enqueued" marker (one-time allocation is fine).
            for (int i = 0; i < count; i++)
                _bfsVisited[i] = false;

            var queue = new System.Collections.Generic.Queue<int>();
            _bfsVisited[bestIndex] = true;
            _simulation.SetPlayerOwned(bestIndex, true);
            queue.Enqueue(bestIndex);

            while (queue.Count > 0)
            {
                int    current = queue.Dequeue();
                float2 posC    = positions[current];

                for (int j = 0; j < count; j++)
                {
                    if (_bfsVisited[j] || types[j] != _playerType) continue;
                    if (math.distancesq(posC, positions[j]) >= adoptSq) continue;

                    _bfsVisited[j] = true;
                    _simulation.SetPlayerOwned(j, true);
                    queue.Enqueue(j);
                }
            }

            _hasAssigned = true;
            _simulation.SetRipplePlayerType(_playerType);
        }

        // ── Split detection (Union-Find, immediate revert) ────────────────────

        /// <summary>
        /// Builds a Union-Find over all player-owned particles connected within
        /// _connectionRadius. Identifies the largest connected component (main cluster).
        /// All player particles NOT in the main cluster are IMMEDIATELY reverted to
        /// normal particles — no grace-period timer, no oscillation.
        ///
        /// Updates ClusterCount (groups found before resolution) and MainClusterSize.
        ///
        /// Union-by-size keeps trees balanced for correct component identification.
        /// Path halving keeps Find amortised O(α(n)).
        /// </summary>
        /// <returns>Number of player particles reverted to normal this frame.</returns>
        private int HandleSplits(
            NativeArray<float2>                      positions,
            NativeArray<bool>                        isPlayerOwned,
            int                                      count,
            NativeParallelMultiHashMap<int2, int>    grid,
            float                                    cellSize)
        {
            // Initialise Union-Find
            for (int i = 0; i < count; i++)
            {
                _ufParent[i] = i;
                _ufSize[i]   = 1; // every node starts as a size-1 component
            }

            float threshSq = _connectionRadius * _connectionRadius;

            // 查询半径（格子数）：确保覆盖 _connectionRadius，哪怕 cellSize < connectionRadius
            int gridRange = (int)math.ceil(_connectionRadius / cellSize);

            // Union pass: 用空间网格替换 O(N²) 暴力遍历，降为 O(P × k)
            // j > i 避免重复处理同一对
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;

                int2 cell = new int2(
                    (int)math.floor(positions[i].x / cellSize),
                    (int)math.floor(positions[i].y / cellSize));

                for (int dx = -gridRange; dx <= gridRange; dx++)
                for (int dy = -gridRange; dy <= gridRange; dy++)
                {
                    int2 neighborCell = new int2(cell.x + dx, cell.y + dy);
                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;
                    do
                    {
                        if (j <= i || !isPlayerOwned[j]) continue;
                        if (math.distancesq(positions[i], positions[j]) < threshSq)
                            UFUnion(i, j);
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }

            // Find main cluster root (largest component among player particles)
            int mainRoot  = -1;
            int mainSize  = 0;
            int clusterN  = 0;

            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                if (UFFind(i) != i) continue; // not a root — skip

                // i is a root of a player-particle component
                clusterN++;
                if (_ufSize[i] > mainSize)
                {
                    mainSize = _ufSize[i];
                    mainRoot = i;
                }
            }

            ClusterCount    = clusterN;
            MainClusterSize = mainSize;

            // Immediate revert: any player particle outside the main cluster
            // becomes a normal particle right now.
            int reverted = 0;
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                if (mainRoot >= 0 && UFFind(i) == mainRoot) continue; // in main cluster

                _simulation.SetPlayerOwned(i, false);
                reverted++;
            }
            return reverted;
        }

        /// <summary>
        /// Forcibly reverts the <paramref name="n"/> outermost player-owned particles
        /// (by distance from <paramref name="centroid"/>) to normal particles.
        /// Respects _splitPenaltyMinSize: will not shed below that floor.
        /// O(n × ParticleCount) — only called on split events, not every frame.
        /// </summary>
        private void ForceShedEdgeParticles(int n, NativeArray<float2> positions,
            NativeArray<bool> isPlayerOwned, float2 centroid)
        {
            int totalCount    = _simulation.ParticleCount;
            int actualPenalty = Mathf.Min(n, Mathf.Max(0, MainClusterSize - _splitPenaltyMinSize));

            for (int k = 0; k < actualPenalty; k++)
            {
                float maxDistSq = -1f;
                int   farthest  = -1;
                for (int i = 0; i < totalCount; i++)
                {
                    if (!isPlayerOwned[i]) continue;
                    float dsq = math.distancesq(positions[i], centroid);
                    if (dsq > maxDistSq) { maxDistSq = dsq; farthest = i; }
                }
                if (farthest < 0) break;
                _simulation.SetPlayerOwned(farthest, false);
            }
        }

        private int UFFind(int x)
        {
            // Path halving
            while (_ufParent[x] != x)
            {
                _ufParent[x] = _ufParent[_ufParent[x]];
                x            = _ufParent[x];
            }
            return x;
        }

        private void UFUnion(int a, int b)
        {
            int ra = UFFind(a), rb = UFFind(b);
            if (ra == rb) return;
            // Union-by-size: attach smaller tree under larger tree root
            if (_ufSize[ra] >= _ufSize[rb])
            {
                _ufParent[rb] = ra;
                _ufSize[ra]  += _ufSize[rb];
            }
            else
            {
                _ufParent[ra] = rb;
                _ufSize[rb]  += _ufSize[ra];
            }
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private float2 ResolveDirection() => _input.DirectionThisFrame;

        // ── Centroid + Count (single pass) ───────────────────────────────────

        private static (float2 centroid, int playerCount) ComputeCentroidAndCount(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            int count)
        {
            float2 sum = float2.zero;
            int    n   = 0;
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                sum += positions[i];
                n++;
            }
            return n > 0 ? (sum / n, n) : (float2.zero, 0);
        }

        // ── Cluster membership marking ────────────────────────────────────────

        /// <summary>
        /// Clears and rebuilds IsInPlayerCluster each LateUpdate.
        /// Marks every player-owned particle plus any particle within _connectionRadius
        /// of a player-owned particle (regardless of type). Result is read by PhysicsJob
        /// the next FixedUpdate to apply player input force to the whole visual cluster.
        /// O(P × gridRange² × k) — P=player count, k=avg particles per cell.
        /// </summary>
        private void MarkPlayerCluster(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            int                 count)
        {
            _simulation.ClearPlayerClusterFlags();

            NativeParallelMultiHashMap<int2, int> grid      = _simulation.Grid;
            float                                 cellSize  = _simulation.CellSize;
            float                                 threshSq  = _connectionRadius * _connectionRadius;
            int gridRange = (int)math.ceil(_connectionRadius / cellSize);

            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;

                _simulation.SetInPlayerCluster(i, true);

                int2 cell = new int2(
                    (int)math.floor(positions[i].x / cellSize),
                    (int)math.floor(positions[i].y / cellSize));

                for (int dx = -gridRange; dx <= gridRange; dx++)
                for (int dy = -gridRange; dy <= gridRange; dy++)
                {
                    int2 neighborCell = new int2(cell.x + dx, cell.y + dy);
                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;
                    do
                    {
                        if (j < count && !isPlayerOwned[j] &&
                            math.distancesq(positions[i], positions[j]) < threshSq)
                            _simulation.SetInPlayerCluster(j, true);
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }
        }

        // ── Adoption ──────────────────────────────────────────────────────────

        /// <summary>
        /// Adopts non-player particles of the same type as the player cluster via
        /// BFS over the spatial grid. Transitively expands through chains of same-type
        /// neighbours within cellSize, so the entire reachable same-type neighbourhood
        /// is adopted in one pass rather than one hop per frame.
        /// </summary>
        private void AdoptSameTypeBFS(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            NativeArray<byte>   types,
            int                 count)
        {
            NativeParallelMultiHashMap<int2, int> grid     = _simulation.Grid;
            float                                 cellSize = _simulation.CellSize;
            NativeQueue<int>                      queue    = _adoptionQueue;
            queue.Clear();

            // Seed: all currently player-owned particles
            for (int i = 0; i < count; i++)
            {
                if (isPlayerOwned[i])
                    queue.Enqueue(i);
            }

            while (queue.TryDequeue(out int idx))
            {
                int2 cell = (int2)math.floor(positions[idx] / cellSize);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int2 neighborCell = cell + new int2(dx, dy);
                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;

                    do
                    {
                        if (j >= count || isPlayerOwned[j] || types[j] != _playerType) continue;
                        if (math.distance(positions[idx], positions[j]) < cellSize)
                        {
                            _simulation.SetPlayerOwned(j, true);
                            queue.Enqueue(j);
                        }
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }

        }

        // ── Shedding ──────────────────────────────────────────────────────────

        private void ShedEdge(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            NativeArray<float2> velocities,
            int count, float2 centroid)
        {
            float maxDistSq   = 0f;
            int   playerCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                float dsq = math.distancesq(positions[i], centroid);
                if (dsq > maxDistSq) maxDistSq = dsq;
                playerCount++;
            }
            if (playerCount == 0) return;
            if (maxDistSq <= 0f) return; // 单粒子或所有粒子重叠——无分布可削减

            float edgeThreshSq = (1f - _edgeFraction) * maxDistSq;
            float maxVel       = _simulation.MaxVelocity;
            float dt           = Time.deltaTime;

            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                if (math.distancesq(positions[i], centroid) < edgeThreshSq) continue;

                float speedRatio = math.saturate(math.length(velocities[i]) / maxVel);
                if (Random.value < speedRatio * _sheddingRate * dt)
                    _simulation.SetPlayerOwned(i, false);
            }
        }

    }
}
