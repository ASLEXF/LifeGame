using ParticleLife.Input;
using ParticleLife.Management;
using ParticleLife.Simulation;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ParticleLife.Player
{
    /// <summary>
    /// Manages the player cluster with deferred initialization.
    ///
    /// Session lifecycle:
    ///   Frame 1: AssignInitialCluster() — picks the densest same-type cluster as player.
    ///            Particles have already settled during the main menu UI transition.
    ///   Each frame (LateUpdate, order 10, after PhysicsJob is complete):
    ///     1. Centroid + count over all isPlayerOwned particles.
    ///     2. Input force injected for next FixedUpdate.
    ///     3. Adopt same-type adjacent non-player particles (snapshot prevents cascade).
    ///     4. Edge shedding: outermost fraction at high speed may detach.
    ///     5. Count, report peak.
    ///     6. Zero-particle survival countdown.
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
        [Header("性能重构开关")]
        [SerializeField] private bool _useIncrementalPlayerState = true;
        [SerializeField] private bool _useBudgetedAdopt = true;
        [SerializeField][Min(32)] private int _adoptFrontierBudgetPerFrame = 256;
        [SerializeField] private bool _useDirtySplitCheck = true;
        [SerializeField][Min(1)] private int _splitCheckEveryNFrames = 6;
        [SerializeField][Min(0)] private int _splitDirtyThreshold = 8;
        [SerializeField][Min(1)] private int _clusterMarkEveryNFrames = 2;
        [SerializeField][Min(1)] private int _minFramesBetweenSplitChecks = 2;
        [SerializeField] private bool _avoidSplitAndClusterSameFrame = true;
        [SerializeField][Min(8)] private int _adoptBudgetOnHeavyFrame = 32;
        [SerializeField] private bool _enablePlayerPerfLogging = false;
        [SerializeField][Min(0.25f)] private float _playerPerfLogIntervalSec = 1f;
        [Header("最低帧率保护")]
        [SerializeField] private bool _enableFrameTimeGuard = true;
        [SerializeField][Min(8f)] private float _frameTimeGuardMs = 15.5f;
        [SerializeField] private bool _skipSplitWhenOverBudget = true;
        [SerializeField] private bool _skipClusterMarkWhenOverBudget = true;
        [SerializeField][Min(8)] private int _adoptBudgetWhenOverBudget = 64;

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
        private int _splitFrameCounter;
        private int _clusterMarkFrameCounter;
        private int _lastSplitFrame = -99999;

        // Union-Find arrays, reused each frame (allocated once at Start).
        private int[] _ufParent;
        private int[] _ufSize;

        // Scratch buffer: player-owned indices collected each HandleSplits call.
        // NativeArray so it can be passed directly to MarkPlayerClusterJob without copying.
        private NativeArray<int> _playerScratch;
        private int              _playerScratchCount;

        // Visited marker array, reused for BFS in AssignInitialCluster.
        private bool[] _bfsVisited;

        // 持久化 BFS 队列：避免 AdoptSameTypeBFS 每帧 new NativeQueue(Allocator.Temp)
        private NativeQueue<int> _adoptionQueue;
        private bool[] _isPlayerTracked;
        private int[] _playerDense;
        private int[] _playerDenseRev;
        private int _playerDenseCount;
        private bool _splitDirty;
        private int _membershipDeltaCounter;
        private float2 _centroidSum;
        private readonly System.Collections.Generic.Queue<int> _adoptFrontierManaged = new();
        private bool _adoptFrontierSeeded;
        private float _perfElapsed;
        private float _perfSplitMs;
        private float _perfAdoptMs;
        private float _perfClusterMs;
        private int _perfSamples;
        private int _localFrameCounter;

        /// <summary>Total number of player-owned particles this frame.</summary>
        public int PlayerParticleCount { get; private set; }

        /// <summary>World-space centroid of all player-owned particles.</summary>
        public float2 ClusterCentroid { get; private set; }

        /// <summary>True after the initial 5-second delay and cluster assignment.</summary>
        public bool IsAssigned => _hasAssigned;

        /// <summary>The particle type index assigned to the player. Valid only when IsAssigned is true.</summary>
        public byte PlayerType => _playerType;

        private void Awake()
        {
            _simulation = GetComponent<ParticleSimulation>();
        }

        private void Start()
        {
            int maxCount   = _simulation.MaxParticleCount;
            _bfsVisited    = new bool[maxCount];
            _ufParent      = new int[maxCount];
            _ufSize        = new int[maxCount];
            _playerScratch = new NativeArray<int>(maxCount, Allocator.Persistent);
            _adoptionQueue = new NativeQueue<int>(Allocator.Persistent);
            _isPlayerTracked = new bool[maxCount];
            _playerDense = new int[maxCount];
            _playerDenseRev = new int[maxCount];
            for (int i = 0; i < maxCount; i++) _playerDenseRev[i] = -1;

            _gameState.OnStateChanged += OnStateChanged;

            // Sync enabled state to initial GameState — simulation starts at MainMenu,
            // so player input must be disabled until the game actually starts.
            enabled = _gameState.CurrentState == GameState.Running;
        }

        private void OnDestroy()
        {
            if (_gameState != null)
                _gameState.OnStateChanged -= OnStateChanged;

            if (_playerScratch.IsCreated) _playerScratch.Dispose();
            if (_adoptionQueue.IsCreated) _adoptionQueue.Dispose();
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
            bool overFrameBudget = _enableFrameTimeGuard && (Time.unscaledDeltaTime * 1000f > _frameTimeGuardMs);
            _localFrameCounter++;

            // 1. Split detection: dirty-driven + periodic safeguard.
            bool shouldSplitCheck = !_useDirtySplitCheck
                || _splitDirty
                || (++_splitFrameCounter % _splitCheckEveryNFrames == 0);
            if (overFrameBudget && _skipSplitWhenOverBudget)
                shouldSplitCheck = false;
            if (_localFrameCounter - _lastSplitFrame < _minFramesBetweenSplitChecks)
                shouldSplitCheck = false;
            float splitStart = _enablePlayerPerfLogging ? Time.realtimeSinceStartup : 0f;
            bool ranSplitThisFrame = false;
            if (shouldSplitCheck)
            {
                HandleSplits(positions, isPlayerOwned, count, _simulation.Grid, _simulation.CellSize);
                ranSplitThisFrame = true;
                _lastSplitFrame = _localFrameCounter;
            }
            if (_enablePlayerPerfLogging)
                _perfSplitMs += (Time.realtimeSinceStartup - splitStart) * 1000f;

            // 2. Centroid + count — incremental player set.
            (ClusterCentroid, PlayerParticleCount) = ComputeCentroidAndCountIncremental(positions);

            // 3. Mark cluster membership for input-force injection (next FixedUpdate).
            bool shouldMarkCluster = !_useDirtySplitCheck
                || _splitDirty
                || (++_clusterMarkFrameCounter % _clusterMarkEveryNFrames == 0);
            if (overFrameBudget && _skipClusterMarkWhenOverBudget)
                shouldMarkCluster = false;
            if (_avoidSplitAndClusterSameFrame && ranSplitThisFrame)
                shouldMarkCluster = false;
            float clusterStart = _enablePlayerPerfLogging ? Time.realtimeSinceStartup : 0f;
            bool ranClusterThisFrame = false;
            if (shouldMarkCluster)
            {
                new MarkPlayerClusterJob
                {
                    PlayerScratch      = _playerScratch,
                    PlayerScratchCount = _playerScratchCount,
                    Positions          = positions,
                    IsPlayerOwned      = isPlayerOwned,
                    Grid               = _simulation.Grid,
                    ParticleCount      = count,
                    CellSize           = _simulation.CellSize,
                    ThresholdSq        = _connectionRadius * _connectionRadius,
                    GridRange          = (int)math.ceil(_connectionRadius / _simulation.CellSize),
                    IsInPlayerCluster  = _simulation.IsInPlayerCluster,
                }.Schedule().Complete();
                ranClusterThisFrame = true;
            }
            if (_enablePlayerPerfLogging)
                _perfClusterMs += (Time.realtimeSinceStartup - clusterStart) * 1000f;

            // 4. Input direction + cluster size + centroid → physics job
            _simulation.SetPlayerInput(ResolveDirection(), PlayerParticleCount, ClusterCentroid);

            // 5. Adopt same-type non-player particles via BFS over spatial grid
            float adoptStart = _enablePlayerPerfLogging ? Time.realtimeSinceStartup : 0f;
            int adoptBudget = overFrameBudget ? math.min(_adoptFrontierBudgetPerFrame, _adoptBudgetWhenOverBudget) : _adoptFrontierBudgetPerFrame;
            if (ranSplitThisFrame || ranClusterThisFrame)
                adoptBudget = math.min(adoptBudget, _adoptBudgetOnHeavyFrame);
            if (_useBudgetedAdopt)
                AdoptSameTypeFrontierBudgeted(positions, isPlayerOwned, types, count, adoptBudget);
            else if (_adoptFrameCounter++ % AdoptEveryNFrames == 0)
                AdoptSameTypeBFS(positions, isPlayerOwned, types, count);
            if (_enablePlayerPerfLogging)
                _perfAdoptMs += (Time.realtimeSinceStartup - adoptStart) * 1000f;

            // 6. Shed edge particles
            ShedEdge(positions, isPlayerOwned, velocities, count, ClusterCentroid);

            // 7. Report peak
            _gameState.ReportParticleCount(PlayerParticleCount);

            // 8. Zero-particle → immediate failure
            if (PlayerParticleCount == 0)
                _gameState.TransitionTo(GameState.Failed);

            // Supply player scratch for the renderer's outline pass next frame (O(p) vs O(n)).
            // Runs at order 10 — ParticleSimulation.Render() (order 0) already fired this frame,
            // so this scratch is consumed on the next frame. One-frame stale; imperceptible.
            _simulation.SetPlayerRenderScratch(_playerScratch, _playerScratchCount);

            // [DISABLED] Previous 30-second grace period before failing:
            // if (PlayerParticleCount == 0)
            // {
            //     _zeroTimer += Time.deltaTime;
            //     if (_zeroTimer >= _zeroPatienceSec)
            //         _gameState.TransitionTo(GameState.Failed);
            // }
            // else
            // {
            //     _zeroTimer = 0f;
            // }

            if (_enablePlayerPerfLogging)
            {
                _perfElapsed += Time.unscaledDeltaTime;
                _perfSamples++;
                if (_perfElapsed >= _playerPerfLogIntervalSec && _perfSamples > 0)
                {
                    Debug.Log(
                        $"[PlayerPerf] playerCount={PlayerParticleCount} " +
                        $"split={_perfSplitMs / _perfSamples:F2}ms adopt={_perfAdoptMs / _perfSamples:F2}ms " +
                        $"cluster={_perfClusterMs / _perfSamples:F2}ms dirty={_splitDirty} frontier={_adoptFrontierManaged.Count} " +
                        $"overBudget={overFrameBudget} ranSplit={ranSplitThisFrame} ranCluster={ranClusterThisFrame} adoptBudget={adoptBudget}");
                    _perfElapsed = 0f;
                    _perfSplitMs = 0f;
                    _perfAdoptMs = 0f;
                    _perfClusterMs = 0f;
                    _perfSamples = 0;
                }
            }
        }

        // ── Session reset ─────────────────────────────────────────────────────

        private void ResetSession()
        {
            _hasAssigned = false;
            _zeroTimer   = 0f;

            int count = _simulation.ParticleCount;
            for (int i = 0; i < count; i++)
                SetPlayerOwnedTracked(i, false);
            _adoptFrontierManaged.Clear();
            _adoptFrontierSeeded = false;
            _splitDirty = false;
            _membershipDeltaCounter = 0;
            _centroidSum = float2.zero;
        }

        // ── Initial cluster assignment ────────────────────────────────────────

        /// <summary>
        /// Finds the particle with the most same-type neighbors within _connectionRadius,
        /// records its type as the player type, and BFS-adopts all same-type particles
        /// reachable via _connectionRadius chains from that center.
        /// Guarantees a single connected component from frame 1.
        /// Uses spatial grid when available: O(n×k) vs O(n²) fallback (k = grid cell density).
        /// </summary>
        private void AssignInitialCluster(int count)
        {
            NativeArray<float2>                   positions = _simulation.PositionsRead;
            NativeArray<byte>                     types     = _simulation.Types;
            NativeParallelMultiHashMap<int2, int> grid      = _simulation.Grid;
            float                                 cellSize  = _simulation.CellSize;

            float scanSq  = _connectionRadius * _connectionRadius;
            float adoptSq = _connectionRadius * _connectionRadius;
            bool  useGrid = cellSize > 0f && grid.IsCreated;
            int   gridRange = useGrid ? (int)math.ceil(_connectionRadius / cellSize) : 0;

            _playerType = (byte)UnityEngine.Random.Range(0, _simulation.TypeCount);

            // Step 1: find the particle of _playerType with the most same-type neighbors.
            int bestIndex     = -1;
            int bestNeighbors = -1;
            for (int i = 0; i < count; i++)
            {
                if (types[i] != _playerType) continue;

                float2 posI      = positions[i];
                int    neighbors = 0;

                if (useGrid)
                {
                    int2 cell = (int2)math.floor(posI / cellSize);
                    for (int dx = -gridRange; dx <= gridRange; dx++)
                    for (int dy = -gridRange; dy <= gridRange; dy++)
                    {
                        int2 nc = cell + new int2(dx, dy);
                        if (!grid.TryGetFirstValue(nc, out int j, out var it)) continue;
                        do
                        {
                            if (j != i && types[j] == _playerType &&
                                math.distancesq(posI, positions[j]) < scanSq)
                                neighbors++;
                        }
                        while (grid.TryGetNextValue(out j, ref it));
                    }
                }
                else
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (j == i || types[j] != _playerType) continue;
                        if (math.distancesq(posI, positions[j]) < scanSq) neighbors++;
                    }
                }

                if (neighbors > bestNeighbors) { bestNeighbors = neighbors; bestIndex = i; }
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

            // Step 2: BFS flood-fill from bestIndex via spatial grid.
            for (int i = 0; i < count; i++)
                _bfsVisited[i] = false;

            var queue = new System.Collections.Generic.Queue<int>();
            _bfsVisited[bestIndex] = true;
            SetPlayerOwnedTracked(bestIndex, true);
            queue.Enqueue(bestIndex);

            while (queue.Count > 0)
            {
                int    current = queue.Dequeue();
                float2 posC    = positions[current];

                if (useGrid)
                {
                    int2 cell = (int2)math.floor(posC / cellSize);
                    for (int dx = -gridRange; dx <= gridRange; dx++)
                    for (int dy = -gridRange; dy <= gridRange; dy++)
                    {
                        int2 nc = cell + new int2(dx, dy);
                        if (!grid.TryGetFirstValue(nc, out int j, out var it)) continue;
                        do
                        {
                            if (_bfsVisited[j] || types[j] != _playerType) continue;
                            if (math.distancesq(posC, positions[j]) >= adoptSq) continue;
                            _bfsVisited[j] = true;
                            SetPlayerOwnedTracked(j, true);
                            queue.Enqueue(j);
                        }
                        while (grid.TryGetNextValue(out j, ref it));
                    }
                }
                else
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (_bfsVisited[j] || types[j] != _playerType) continue;
                        if (math.distancesq(posC, positions[j]) >= adoptSq) continue;
                        _bfsVisited[j] = true;
                        SetPlayerOwnedTracked(j, true);
                        queue.Enqueue(j);
                    }
                }
            }

            _hasAssigned = true;
            RebuildPlayerScratchFromDense();
            _simulation.SetRipplePlayerType(_playerType);
        }

        // ── Split detection (Union-Find, immediate revert) ────────────────────

        private void HandleSplits(
            NativeArray<float2>                   positions,
            NativeArray<bool>                     isPlayerOwned,
            int                                   count,
            NativeParallelMultiHashMap<int2, int> grid,
            float                                 cellSize)
        {
            // Loop 1: init UF only for player particles + collect their indices.
            // Skips ~(n-p)/n writes vs the old full-n init.
            RebuildPlayerScratchFromDense();
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
                _ufParent[i] = i;
                _ufSize[i]   = 1;
            }

            // Loop 2: build UF via spatial grid (iterate scratch instead of full array).
            float threshSq  = _connectionRadius * _connectionRadius;
            int   gridRange = (int)math.ceil(_connectionRadius / cellSize);

            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];

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

            // Loop 3: find largest component — O(p) via scratch.
            int mainRoot = -1;
            int mainSize = 0;
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
                if (UFFind(i) != i) continue;
                if (_ufSize[i] > mainSize) { mainSize = _ufSize[i]; mainRoot = i; }
            }

            // Loop 4: revert non-main fragments — O(p) via scratch.
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
                if (mainRoot >= 0 && UFFind(i) == mainRoot) continue;
                SetPlayerOwnedTracked(i, false);
            }
            _splitDirty = false;
        }

        private int UFFind(int x)
        {
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
            if (_ufSize[ra] >= _ufSize[rb]) { _ufParent[rb] = ra; _ufSize[ra] += _ufSize[rb]; }
            else                             { _ufParent[ra] = rb; _ufSize[rb] += _ufSize[ra]; }
        }

        // ── Input ─────────────────────────────────────────────────────────────

        private float2 ResolveDirection() => _input.DirectionThisFrame;

        // ── Centroid + Count (single pass) ───────────────────────────────────

        // Iterates playerScratch (O(p)) instead of the full particle array (O(n)).
        // playerScratch is a superset of current player-owned indices (HandleSplits may have
        // reverted some), so isPlayerOwned[i] check filters out any stale entries.
        private (float2 centroid, int playerCount) ComputeCentroidAndCountIncremental(NativeArray<float2> positions)
        {
            float2 sum = float2.zero;
            int n = _playerDenseCount;
            for (int s = 0; s < _playerDenseCount; s++)
            {
                int i = _playerDense[s];
                sum += positions[i];
            }
            _centroidSum = sum;
            return n > 0 ? (sum / n, n) : (float2.zero, 0);
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
            NativeParallelMultiHashMap<int2, int> grid       = _simulation.Grid;
            float                                 cellSize   = _simulation.CellSize;
            float                                 cellSizeSq = cellSize * cellSize;
            NativeQueue<int>                      queue      = _adoptionQueue;
            queue.Clear();

            // Seed from _playerScratch (O(p)) instead of full array (O(n)).
            // Scratch built by HandleSplits this frame; check isPlayerOwned for reverted entries.
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
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
                        if (math.distancesq(positions[idx], positions[j]) < cellSizeSq)
                        {
                            SetPlayerOwnedTracked(j, true);
                            _playerScratch[_playerScratchCount++] = j;  // keep scratch current for ShedEdge
                            queue.Enqueue(j);
                        }
                    }
                    while (grid.TryGetNextValue(out j, ref it));
                }
            }

        }

        private void AdoptSameTypeFrontierBudgeted(
            NativeArray<float2> positions,
            NativeArray<bool> isPlayerOwned,
            NativeArray<byte> types,
            int count,
            int budget)
        {
            if (!_adoptFrontierSeeded)
            {
                _adoptFrontierManaged.Clear();
                for (int i = 0; i < _playerDenseCount; i++)
                    _adoptFrontierManaged.Enqueue(_playerDense[i]);
                _adoptFrontierSeeded = true;
            }

            var grid = _simulation.Grid;
            float cellSize = _simulation.CellSize;
            float cellSizeSq = cellSize * cellSize;
            budget = math.max(8, budget);
            int processed = 0;
            int seededThisFrame = 0;

            while (_adoptFrontierManaged.Count > 0 && processed < budget)
            {
                int idx = _adoptFrontierManaged.Dequeue();
                if ((uint)idx >= (uint)count || !isPlayerOwned[idx]) { processed++; continue; }

                int2 cell = (int2)math.floor(positions[idx] / cellSize);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int2 neighborCell = cell + new int2(dx, dy);
                    if (!grid.TryGetFirstValue(neighborCell, out int j, out var it)) continue;
                    do
                    {
                        if (j >= count || isPlayerOwned[j] || types[j] != _playerType) continue;
                        if (math.distancesq(positions[idx], positions[j]) >= cellSizeSq) continue;
                        SetPlayerOwnedTracked(j, true);
                        _adoptFrontierManaged.Enqueue(j);
                        seededThisFrame++;
                    } while (grid.TryGetNextValue(out j, ref it));
                }
                processed++;
            }

            if (seededThisFrame == 0 && _adoptFrontierManaged.Count == 0)
                _adoptFrontierSeeded = false;
        }

        // ── Shedding ──────────────────────────────────────────────────────────

        private void ShedEdge(
            NativeArray<float2> positions,
            NativeArray<bool>   isPlayerOwned,
            NativeArray<float2> velocities,
            int count, float2 centroid)
        {
            // Scratch built by HandleSplits Loop 1 + augmented by AdoptSameTypeBFS.
            // May contain stale reverted entries — isPlayerOwned[i] check filters them.
            if (_playerScratchCount == 0) return;

            // Loop 1: O(p) — compute maxDistSq from existing scratch (no O(n) rebuild).
            float maxDistSq = 0f;
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
                if (!isPlayerOwned[i]) continue;
                float dsq = math.distancesq(positions[i], centroid);
                if (dsq > maxDistSq) maxDistSq = dsq;
            }
            if (maxDistSq <= 0f) return; // 单粒子或所有粒子重叠——无分布可削减

            float edgeThreshSq = (1f - _edgeFraction) * maxDistSq;
            float maxVel       = _simulation.MaxVelocity;
            float dt           = Time.deltaTime;

            // Loop 2: O(p) — shed outermost fast-moving player particles.
            for (int s = 0; s < _playerScratchCount; s++)
            {
                int i = _playerScratch[s];
                if (!isPlayerOwned[i]) continue;
                if (math.distancesq(positions[i], centroid) < edgeThreshSq) continue;

                float speedRatio = math.saturate(math.length(velocities[i]) / maxVel);
                if (Random.value < speedRatio * _sheddingRate * dt)
                    SetPlayerOwnedTracked(i, false);
            }
        }

        private bool SetPlayerOwnedTracked(int index, bool owned)
        {
            if ((uint)index >= (uint)_simulation.ParticleCount) return false;
            bool current = _simulation.IsPlayerOwned[index];
            if (current == owned) return false;
            _simulation.SetPlayerOwned(index, owned);

            if (_useIncrementalPlayerState)
            {
                if (owned)
                {
                    if (!_isPlayerTracked[index])
                    {
                        _isPlayerTracked[index] = true;
                        _playerDenseRev[index] = _playerDenseCount;
                        _playerDense[_playerDenseCount++] = index;
                    }
                }
                else
                {
                    if (_isPlayerTracked[index])
                    {
                        int denseIndex = _playerDenseRev[index];
                        int lastIdx = _playerDenseCount - 1;
                        if (denseIndex >= 0 && denseIndex <= lastIdx)
                        {
                            int moved = _playerDense[lastIdx];
                            _playerDense[denseIndex] = moved;
                            _playerDenseRev[moved] = denseIndex;
                        }
                        _playerDenseCount = math.max(0, _playerDenseCount - 1);
                        _playerDenseRev[index] = -1;
                        _isPlayerTracked[index] = false;
                    }
                }
            }

            _membershipDeltaCounter++;
            if (_membershipDeltaCounter >= _splitDirtyThreshold)
            {
                _splitDirty = true;
                _membershipDeltaCounter = 0;
            }
            _adoptFrontierSeeded = false;
            return true;
        }

        private void RebuildPlayerScratchFromDense()
        {
            _playerScratchCount = 0;
            if (_useIncrementalPlayerState)
            {
                for (int i = 0; i < _playerDenseCount; i++)
                    _playerScratch[_playerScratchCount++] = _playerDense[i];
                return;
            }

            int count = _simulation.ParticleCount;
            var isPlayerOwned = _simulation.IsPlayerOwned;
            for (int i = 0; i < count; i++)
                if (isPlayerOwned[i])
                    _playerScratch[_playerScratchCount++] = i;
        }

    }
}
