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
    ///   0–5s  : No player particles. Timer counts up.
    ///   5s    : AssignInitialCluster() — picks the densest same-type cluster as player.
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
        [Header("初始化")]
        [SerializeField] private float _initialDelaySec    = 5f;

        [Header("玩家控制")]
        [Tooltip("团簇连接判定距离：同时用于分裂检测（Union-Find）和相邻粒子吸附")]
        [SerializeField] private float _connectionRadius   = 5f;

        [SerializeField] private float _sheddingRate       = 0.6f;
        [SerializeField][Range(0f, 1f)] private float _edgeFraction = 0.2f;
        [SerializeField] private float _zeroPatienceSec    = 30f;

        [Header("引用")]
        [SerializeField] private GameInput        _input;
        [SerializeField] private GameStateManager _gameState;

        private ParticleSimulation _simulation;

        private float _startTimer;
        private bool  _hasAssigned;
        private byte  _playerType;
        private float _zeroTimer;

        // Snapshot of player-owned state at the start of each adoption pass.
        // Prevents cascading adoption: newly adopted particles don't trigger further
        // adoptions within the same frame.
        private bool[] _adoptionSnapshot;

        // Union-Find arrays, reused each frame (allocated once at Start).
        // _ufSize tracks component size and is used for union-by-size (balanced trees).
        private int[] _ufParent;
        private int[] _ufSize;

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
            int maxCount      = _simulation.PositionsRead.Length;
            _adoptionSnapshot = new bool[maxCount];
            _ufParent         = new int[maxCount];
            _ufSize           = new int[maxCount];

            _gameState.OnStateChanged += OnStateChanged;
        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;
        }

        private void OnStateChanged(GameState state)
        {
            if (state == GameState.Running)
                ResetSession();
        }

        private void LateUpdate()
        {
            if (_gameState.CurrentState != GameState.Running) return;

            int count = _simulation.ParticleCount;
            if (count == 0) return;

            if (!_hasAssigned)
            {
                _startTimer += Time.deltaTime;
                if (_startTimer >= _initialDelaySec)
                    AssignInitialCluster(count);
                return;
            }

            NativeArray<float2> positions     = _simulation.PositionsRead;
            NativeArray<bool>   isPlayerOwned = _simulation.IsPlayerOwned;
            NativeArray<byte>   types         = _simulation.Types;
            NativeArray<float2> velocities    = _simulation.Velocities;

            // 1. Split detection: revert stranded fragments that exceed timeout
            HandleSplits(positions, isPlayerOwned, count);

            // 2. Centroid (for input direction, shedding)
            ClusterCentroid = ComputeCentroid(positions, isPlayerOwned, count);

            // 3. Input direction + cluster size → physics job
            _simulation.SetPlayerInput(ResolveDirection(), PlayerParticleCount);

            // 4. Adopt same-type adjacent non-player particles
            AdoptSameTypeAdjacent(positions, isPlayerOwned, types, count);

            // 5. Shed edge particles
            ShedEdge(positions, isPlayerOwned, velocities, count, ClusterCentroid);

            // 6. Count, report peak
            PlayerParticleCount = CountPlayerParticles(isPlayerOwned, count);
            _gameState.ReportParticleCount(PlayerParticleCount);

            // 7. Zero-particle survival countdown
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
            _startTimer     = 0f;
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

            // Step 1: find particle with most same-type neighbors
            int bestIndex     = 0;
            int bestNeighbors = -1;
            for (int i = 0; i < count; i++)
            {
                int    neighbors = 0;
                byte   typeI     = types[i];
                float2 posI      = positions[i];
                for (int j = 0; j < count; j++)
                {
                    if (j == i || types[j] != typeI) continue;
                    if (math.distancesq(posI, positions[j]) < scanSq)
                        neighbors++;
                }
                if (neighbors > bestNeighbors)
                {
                    bestNeighbors = neighbors;
                    bestIndex     = i;
                }
            }

            _playerType = types[bestIndex];

            // Step 2: BFS flood-fill from bestIndex.
            // Only particles reachable via a chain of _connectionRadius-adjacent same-type
            // particles are adopted, guaranteeing one connected component.
            // Reuse _adoptionSnapshot as the "enqueued" marker (one-time allocation is fine).
            for (int i = 0; i < count; i++)
                _adoptionSnapshot[i] = false;

            var queue = new System.Collections.Generic.Queue<int>();
            _adoptionSnapshot[bestIndex] = true;
            _simulation.SetPlayerOwned(bestIndex, true);
            queue.Enqueue(bestIndex);

            while (queue.Count > 0)
            {
                int    current = queue.Dequeue();
                float2 posC    = positions[current];

                for (int j = 0; j < count; j++)
                {
                    if (_adoptionSnapshot[j] || types[j] != _playerType) continue;
                    if (math.distancesq(posC, positions[j]) >= adoptSq) continue;

                    _adoptionSnapshot[j] = true;
                    _simulation.SetPlayerOwned(j, true);
                    queue.Enqueue(j);
                }
            }

            _hasAssigned = true;
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
        private void HandleSplits(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            int count)
        {
            // Initialise Union-Find
            for (int i = 0; i < count; i++)
            {
                _ufParent[i] = i;
                _ufSize[i]   = 1; // every node starts as a size-1 component
            }

            float threshSq = _connectionRadius * _connectionRadius;

            // Union pass: connect adjacent player particles
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                for (int j = i + 1; j < count; j++)
                {
                    if (!isPlayerOwned[j]) continue;
                    if (math.distancesq(positions[i], positions[j]) < threshSq)
                        UFUnion(i, j);
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
            for (int i = 0; i < count; i++)
            {
                if (!isPlayerOwned[i]) continue;
                if (mainRoot >= 0 && UFFind(i) == mainRoot) continue; // in main cluster

                _simulation.SetPlayerOwned(i, false);
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

        // ── Centroid ──────────────────────────────────────────────────────────

        private static float2 ComputeCentroid(
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
            return n > 0 ? sum / n : float2.zero;
        }

        // ── Adoption ──────────────────────────────────────────────────────────

        /// <summary>
        /// Non-player particles of the same type as the player cluster that are
        /// directly adjacent to any player particle are immediately adopted.
        ///
        /// Uses a snapshot of isPlayerOwned taken before the scan begins so that
        /// particles adopted during this pass cannot trigger further adoptions in
        /// the same frame (prevents cascading flood-fill across the whole world).
        /// </summary>
        private void AdoptSameTypeAdjacent(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            NativeArray<byte>   types,
            int count)
        {
            // Snapshot: only particles that were player-owned before this pass
            // serve as adoption sources.
            for (int i = 0; i < count; i++)
                _adoptionSnapshot[i] = isPlayerOwned[i];

            float threshSq = _connectionRadius * _connectionRadius;
            for (int i = 0; i < count; i++)
            {
                if (isPlayerOwned[i] || types[i] != _playerType) continue;

                for (int j = 0; j < count; j++)
                {
                    if (!_adoptionSnapshot[j]) continue;  // only check pre-frame owners
                    if (math.distancesq(positions[i], positions[j]) < threshSq)
                    {
                        _simulation.SetPlayerOwned(i, true);
                        break;
                    }
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

        // ── Utilities ─────────────────────────────────────────────────────────

        private static int CountPlayerParticles(NativeArray<bool> isPlayerOwned, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
                if (isPlayerOwned[i]) n++;
            return n;
        }
    }
}
