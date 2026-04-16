using System;
using ParticleLife.Core;
using ParticleLife.Physics;
using ParticleLife.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Simulation
{
    /// <summary>
    /// Root MonoBehaviour that owns all particle state and orchestrates the simulation loop.
    ///
    /// Frame flow:
    ///   FixedUpdate:
    ///     1. Complete any pending JobHandle (safety)
    ///     2. CellularAutomata.Tick() — spawn/remove (main thread, reads positionsRead)
    ///     3. Build spatial grid from positionsRead
    ///     4. Schedule PhysicsJob (reads positionsRead, writes positionsWrite)
    ///     5. Swap position buffers
    ///   LateUpdate:
    ///     6. Complete JobHandle
    ///     7. ParticleRenderer.Render() from positionsRead (now the just-written buffer)
    ///
    /// NativeArray lifetime: allocated in Awake, disposed in OnDestroy.
    /// </summary>
    [RequireComponent(typeof(ParticleRenderer))]
    public class ParticleSimulation : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────
        [Header("模拟参数")]
        [SerializeField] private int   _maxParticleCount  = 5000;
        [SerializeField] private int   _initialCount      = 3000;
        [Tooltip("世界高度（世界单位）。宽度在运行时按屏幕宽高比自动计算")]
        [SerializeField] private float _worldHeight       = 100f;
        [SerializeField] private int   _typeCount         = 5;

        [Header("物理参数")]
        [SerializeField] private float _damping     = 0.85f;
        [SerializeField] private float _maxVelocity = 20f;

        [Header("全局力缩放")]
        [Tooltip("实时调整所有粒子间引力/斥力的全局倍数。\n1 = 默认，0 = 无力场（纯惯性运动），2 = 翻倍。\n不影响玩家输入力和边界斥力。")]
        [SerializeField] private float _forceScale = 1f;

        // Idle tracking threshold — not exposed; value doesn't affect any gameplay decision.
        private const float IdleVelocityThreshold = 0.5f;

        [Header("玩家移动")]
        [Tooltip("玩家粒子每帧注入的输入力大小。此力与引力竞争：引力越强，移动越慢。建议调参范围 80–200。")]
        [SerializeField] private float _playerInputForce          = 150f;
        [Tooltip("玩家粒子速度上限（世界单位/秒），通常低于 MaxVelocity 以保证可控性")]
        [SerializeField] private float _playerMaxSpeed            = 25f;
        [Tooltip("达到此粒子数时，外部力干扰降至最低（满抗性）")]
        [SerializeField] private int   _playerResistanceFullAt    = 50;
        [Tooltip("团簇凝聚力（每单位距离的力）：将玩家粒子向质心拉近，减少高速移动时的拉伸。0 = 禁用。建议 10–40")]
        [SerializeField] private float _clusterCohesionStrength   = 20f;
        [Tooltip("最大外力削减比例（0 = 无抗性，1 = 完全免疫外力）建议 0.6–0.8")]
        [SerializeField][Range(0f, 1f)] private float _playerMaxExternalReduction = 0.75f;

        [Header("边界碰撞")]
        [Tooltip("关闭时粒子可自由飞出世界矩形，边界反弹和斥力场均禁用；边界参数保留供回退调试")]
        [SerializeField] private bool _unboundedWorld = false;
        [Tooltip("粒子碰撞边界时反弹速度的保留比例（0 = 完全吸收，1 = 完全弹性）")]
        [SerializeField][Range(0f, 1f)] private float _bounceRestitution  = 0.3f;

        [Tooltip("斥力场生效距离（世界单位）；0 = 禁用斥力场")]
        [SerializeField] private float _boundaryThreshold = 5f;
        [Tooltip("斥力场强度系数")]
        [SerializeField] private float _boundaryStrength  = 50f;

        [Header("特殊粒子")]
        [Tooltip("游戏开始时生成的黑色粒子数量（仅与同类聚集，与其他类型零交互）")]
        [SerializeField] private int _initialBlackCount = 50;
        [Tooltip("游戏开始时生成的白色粒子数量（对所有非黑粒子有吸引力）")]
        [SerializeField] private int _initialWhiteCount = 30;

        [Header("粒子半径（按类型）")]
        [Tooltip("长度必须为 10（最多 8 种普通粒子 + 2 种特殊粒子）。\n" +
                 "索引 0–7：普通类型（仅前 TypeCount 个生效）。\n" +
                 "索引 8：黑色粒子半径。索引 9：白色粒子半径。\n" +
                 "留空则使用默认值：普通类型 0.2，黑色 0.35，白色 0.30。")]
        [SerializeField] private float[] _typeRadii;

        [Header("细胞自动机参数")]
        [SerializeField] private float _spawnInterval = 0.5f;
        [SerializeField] private float _densityRadius  = 5f;
        [SerializeField] private int   _densityCap     = 10;

        [Header("无边界世界 — 生成半径")]
        [Tooltip("无边界模式下生成环的最小半径（世界单位）。建议 = 摄像机 orthoSize × 1.4")]
        [SerializeField] private float _spawnRadiusMin = 49f;
        [Tooltip("无边界模式下生成环的最大半径（世界单位）。建议 = 摄像机 orthoSize × 1.8")]
        [SerializeField] private float _spawnRadiusMax = 63f;
        [Tooltip("距玩家质心超过此距离的非玩家粒子将被传送复用（世界单位）。建议 = 摄像机 orthoSize × 3")]
        [SerializeField] private float _despawnRadius  = 105f;
        [Tooltip("生成方向偏向强度：0 = 全圆均匀，1 = 正前方；推荐 0.6–0.8")]
        [SerializeField][Range(0f, 1f)] private float _spawnDirectionBias = 0.7f;
        [Tooltip("计算平滑前进方向所用的历史窗口（秒）；历史不足时退化为实时输入方向")]
        [SerializeField] private float _spawnDirWindowSec = 2f;

        [Header("无边界世界 — 初始生成半径")]
        [Tooltip("无边界模式下初始生成环的最小半径（世界单位）。" +
                 "应小于 _spawnRadiusMin，允许粒子在玩家起点附近形成团簇。0 = 从原点向外均匀分布")]
        [SerializeField] private float _initialSpawnRadiusMin = 5f;

        [Header("无边界世界 — 集群预分组")]
        [Tooltip("每组共享种子位置的粒子数下限。1 = 每粒子独立（退化为原行为）")]
        [SerializeField] private int   _spawnGroupSizeMin  = 2;
        [Tooltip("每组共享种子位置的粒子数上限。与 Min 相等时固定组大小")]
        [SerializeField] private int   _spawnGroupSizeMax  = 4;
        [Tooltip("组内每个粒子相对种子位置的最大散布半径（世界单位）。0 = 全部重叠在种子位置")]
        [SerializeField] private float _clusterSpawnJitter = 0.5f;

        [Header("引力矩阵初始配置")]
        [Tooltip("TypeCount×TypeCount 个条目，按 typeA * TypeCount + typeB 排列。\n" +
                 "留空则运行时随机生成。右键组件 → 生成默认引力矩阵 可填入默认值。")]
        [SerializeField] private Core.GravityEntry[] _gravityMatrixConfig;

        [Header("引用")]
        [SerializeField] private ParticleRenderer  _renderer;
        [SerializeField] private SpawnRipple       _spawnRipple;

        // ── Particle state (NativeArrays) ────────────────────────────────────
        private NativeArray<float2> _positionsRead;
        private NativeArray<float2> _positionsWrite;
        private NativeArray<float2> _velocities;
        private NativeArray<byte>   _types;
        private NativeArray<bool>   _isPlayerOwned;
        private NativeArray<bool>   _isInPlayerCluster;
        private NativeArray<float>  _idleTime;
        private NativeArray<float2> _externalForceOnPlayer;
        private NativeArray<float>  _typeRadiiNative;

        private int _particleCount;

        // ── Supporting systems ────────────────────────────────────────────────
        private GravityMatrix                         _gravityMatrix;
        private NativeParallelMultiHashMap<int2, int> _grid;
        private CellularAutomata                      _cellularAutomata;

        // ── Spawn direction smoothing ─────────────────────────────────────────
        private struct CentroidSample { public float2 Position; public float Time; }
        // Pre-allocated to 256 slots: 60 fps × 2.2 s ≈ 132 entries, no resizing needed.
        private readonly System.Collections.Generic.Queue<CentroidSample> _centroidHistory
            = new(256);

        // ── Cull & respawn (unbounded mode) ──────────────────────────────────
        private int   _cullFrameCounter;
        private const int CullEveryNFrames = 30;
        private Unity.Mathematics.Random _cullRng;
        private int[] _cullTypeCounts;   // allocated in Awake; reused each cull batch

        // ── Job tracking ──────────────────────────────────────────────────────
        private JobHandle _pendingHandle;
        private bool      _jobPending;

        // ── Skill state ───────────────────────────────────────────────────────
        private bool  _shieldActive;
        private float _shieldPlayerRepulsionScale = 1f;

        // Scatter request: set via RequestScatterPlayerParticles(), consumed in FixedUpdate
        // after the pending job is completed (NativeArray write safety).
        private bool  _pendingScatter;
        private float _pendingScatterFraction;

        // ── Runtime matrix editing ────────────────────────────────────────────
        private bool _cellSizeDirty;

        /// <summary>
        /// Fired whenever SetGravityEntry() is called. Subscribe to persist changes.
        /// Raised on the main thread, after the physics job has been completed for this frame.
        /// </summary>
        public event Action OnGravityMatrixChanged;

        // ── Derived world constants ───────────────────────────────────────────
        private float _worldHalfX;   // half width  — screen-aspect-derived
        private float _worldHalfY;   // half height — equals _worldHeight * 0.5
        private float _cellSize;

        private void Awake()
        {
            if (_renderer == null)
                _renderer = GetComponent<ParticleRenderer>();

            // Allocate particle arrays at max capacity
            _positionsRead         = new NativeArray<float2>(_maxParticleCount, Allocator.Persistent);
            _positionsWrite        = new NativeArray<float2>(_maxParticleCount, Allocator.Persistent);
            _velocities            = new NativeArray<float2>(_maxParticleCount, Allocator.Persistent);
            _types                 = new NativeArray<byte>  (_maxParticleCount, Allocator.Persistent);
            _isPlayerOwned         = new NativeArray<bool>  (_maxParticleCount, Allocator.Persistent);
            _isInPlayerCluster     = new NativeArray<bool>  (_maxParticleCount, Allocator.Persistent);
            _idleTime              = new NativeArray<float> (_maxParticleCount, Allocator.Persistent);
            _externalForceOnPlayer = new NativeArray<float2>(_maxParticleCount, Allocator.Persistent);

            _gravityMatrix = GravityMatrix.CreateDefault(_typeCount, Allocator.Persistent);

            // Override normal-type entries with Inspector-configured values when fully populated.
            // Special type entries (black/white) are always hardcoded; Inspector config covers only
            // the normal _typeCount × _typeCount sub-matrix.
            if (_gravityMatrixConfig != null && _gravityMatrixConfig.Length == _typeCount * _typeCount)
            {
                for (int a = 0; a < _typeCount; a++)
                for (int b = 0; b < _typeCount; b++)
                    _gravityMatrix.Set(a, b, _gravityMatrixConfig[a * _typeCount + b]);
            }

            _renderer.SetNormalTypeCount(_typeCount);

            _worldHalfY = _worldHeight * 0.5f;
            _worldHalfX = _worldHalfY * ((float)Screen.width / Screen.height);
            _cellSize   = _gravityMatrix.MaxDistanceThreshold();

            int gridCapacity = _maxParticleCount * 4;
            _grid = new NativeParallelMultiHashMap<int2, int>(gridCapacity, Allocator.Persistent);

            _cellularAutomata = new CellularAutomata(_spawnInterval, _densityRadius, _densityCap, _typeCount, _gravityMatrix.TypeCount);
            if (_spawnRipple != null)
                _cellularAutomata.OnParticleSpawned += _spawnRipple.Trigger;

            _cullRng        = new Unity.Mathematics.Random(54321u);
            _cullTypeCounts = new int[_typeCount];

            // 初始化各类型粒子半径
            // Inspector 数组固定长度 10：[0..7] 普通类型，[8] 黑色，[9] 白色
            const int RadiiArrayLength = 10;
            int totalTypes = _gravityMatrix.TypeCount;
            _typeRadiiNative = new NativeArray<float>(totalTypes, Allocator.Persistent);
            if (_typeRadii != null && _typeRadii.Length == RadiiArrayLength)
            {
                for (int i = 0; i < _typeCount; i++)
                    _typeRadiiNative[i] = Mathf.Max(_typeRadii[i], 0.01f);
                if (totalTypes > _typeCount)
                    _typeRadiiNative[_typeCount]     = Mathf.Max(_typeRadii[8], 0.01f); // 黑色固定在 [8]
                if (totalTypes > _typeCount + 1)
                    _typeRadiiNative[_typeCount + 1] = Mathf.Max(_typeRadii[9], 0.01f); // 白色固定在 [9]
            }
            else
            {
                for (int i = 0; i < _typeCount; i++)
                    _typeRadiiNative[i] = 0.2f;
                if (totalTypes > _typeCount)
                    _typeRadiiNative[_typeCount]     = 0.35f; // 黑色默认
                if (totalTypes > _typeCount + 1)
                    _typeRadiiNative[_typeCount + 1] = 0.30f; // 白色默认
            }

            // 按粒子半径比例调整引力矩阵，使不同尺寸粒子的表面间距保持不变
            // 参考半径 0.2（默认普通粒子）：pair 合并半径 / (2×参考) 得缩放比
            const float r_ref = 0.2f;
            for (int a = 0; a < _gravityMatrix.TypeCount; a++)
            for (int b = 0; b < _gravityMatrix.TypeCount; b++)
            {
                float scale = (_typeRadiiNative[a] + _typeRadiiNative[b]) / (2f * r_ref);
                var   entry = _gravityMatrix.Get(a, b);
                entry.AttractionStrength *= scale;
                entry.RepulsionStrength  *= scale;
                _gravityMatrix.Set(a, b, entry);
            }

            SpawnInitialParticles();
        }

        private void FixedUpdate()
        {
            // Safety: complete any job that escaped LateUpdate (e.g. first frame)
            if (_jobPending)
            {
                _pendingHandle.Complete();
                _jobPending = false;
            }

            // Execute deferred scatter (requested by PlayerSkill.Activate on the Update thread).
            if (_pendingScatter)
            {
                ExecuteScatterPlayerParticles(_pendingScatterFraction);
                _pendingScatter = false;
            }

            // Recalculate cell size if any distanceThreshold changed since last frame
            if (_cellSizeDirty)
            {
                _cellSize      = _gravityMatrix.MaxDistanceThreshold();
                _cellSizeDirty = false;
            }

            // Cellular automata runs on main thread (reads positionsRead).
            // In unbounded mode candidates are sampled from the spawn annulus around the
            // player centroid; in bounded mode the original world-space random is used.
            _cellularAutomata.Tick(
                _positionsRead,
                _positionsWrite,
                _velocities,
                _types,
                _isPlayerOwned,
                _idleTime,
                ref _particleCount,
                _maxParticleCount,
                _typeCount,
                _worldHalfX,
                _worldHalfY,
                _playerCentroid,
                _unboundedWorld ? _spawnRadiusMin : 0f,
                _unboundedWorld ? _spawnRadiusMax : 0f,
                _unboundedWorld ? ComputeSmoothedMoveDir(_playerInputDir) : float2.zero,
                _unboundedWorld ? _spawnDirectionBias : 0f);

            // Unbounded mode: teleport distant non-player particles back into the spawn ring.
            // Runs every CullEveryNFrames to amortise the O(N) scan cost.
            if (_unboundedWorld && ++_cullFrameCounter % CullEveryNFrames == 0)
                CullAndRespawn(_playerCentroid,
                               ComputeSmoothedMoveDir(_playerInputDir),
                               _spawnDirectionBias);

            if (_particleCount <= 0) return;

            // Build spatial grid from current read positions
            JobHandle gridHandle = SpatialGrid.Schedule(
                _positionsRead, _particleCount, _grid, _cellSize);

            // Schedule physics
            var physicsJob = new PhysicsJob
            {
                PositionsRead              = _positionsRead,
                PositionsWrite             = _positionsWrite,
                Velocities                 = _velocities,
                Types                      = _types,
                IsPlayerOwned              = _isPlayerOwned,
                IdleTime                   = _idleTime,
                Grid                       = _grid,
                MatrixEntries              = _gravityMatrix.Entries,
                TypeCount                  = _gravityMatrix.TypeCount,
                CellSize                   = _cellSize,
                DeltaTime                  = Time.fixedDeltaTime,
                Damping                    = _damping,
                MaxVelocity                = _maxVelocity,
                WorldHalfX                 = _worldHalfX,
                WorldHalfY                 = _worldHalfY,
                IdleVelocityThreshold      = IdleVelocityThreshold,
                BounceRestitution          = _bounceRestitution,
                BoundaryThreshold          = _boundaryThreshold,
                BoundaryStrength           = _boundaryStrength,
                PlayerInputDir             = _playerInputDir,
                PlayerInputForce           = _playerInputForce,
                PlayerMaxSpeed             = _playerMaxSpeed,
                PlayerParticleCount        = _playerParticleCount,
                PlayerResistanceFullAt     = _playerResistanceFullAt,
                PlayerMaxExternalReduction = _playerMaxExternalReduction,
                ExternalForceOnPlayer      = _externalForceOnPlayer,
                IsInPlayerCluster          = _isInPlayerCluster,
                ForceScale                 = _forceScale,
                UnboundedWorld             = _unboundedWorld,
                ShieldActive               = _shieldActive,
                ShieldPlayerRepulsionScale = _shieldPlayerRepulsionScale,
                PlayerCentroid             = _playerCentroid,
                ClusterCohesionStrength    = _clusterCohesionStrength,
            };

            _pendingHandle = physicsJob.Schedule(_particleCount, 64, gridHandle);
            _jobPending    = true;

            // Swap buffers: next read = just-written output
            (_positionsRead, _positionsWrite) = (_positionsWrite, _positionsRead);
        }

        private void LateUpdate()
        {
            if (_jobPending)
            {
                _pendingHandle.Complete();
                _jobPending = false;
            }

            if (_particleCount > 0)
            {
                _renderer.Render(_positionsRead, _types, _isPlayerOwned, _particleCount, _typeRadiiNative);
            }
        }

        private void OnDestroy()
        {
            // Complete any running job before disposal
            if (_jobPending)
            {
                _pendingHandle.Complete();
                _jobPending = false;
            }

            _positionsRead        .Dispose();
            _positionsWrite       .Dispose();
            _velocities           .Dispose();
            _types                .Dispose();
            _isPlayerOwned        .Dispose();
            _isInPlayerCluster    .Dispose();
            _idleTime             .Dispose();
            _externalForceOnPlayer.Dispose();
            _typeRadiiNative      .Dispose();
            _gravityMatrix        .Dispose();
            _grid                 .Dispose();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SpawnInitialParticles()
        {
            var rng        = new Unity.Mathematics.Random(12345u);
            int normalCount = Mathf.Min(_initialCount, _maxParticleCount);

            // Normal types — cyclic assignment keeps populations perfectly balanced.
            for (int i = 0; i < normalCount; i++)
            {
                var pos = SampleInitialPosition(ref rng);
                _positionsRead[i]  = pos;
                _positionsWrite[i] = pos;
                _velocities[i]     = float2.zero;
                _types[i]          = (byte)(i % _typeCount);
                _isPlayerOwned[i]  = false;
                _idleTime[i]       = 0f;
            }
            _particleCount = normalCount;

            // Special types appended after normal particles.
            byte blackType = (byte)_typeCount;
            byte whiteType = (byte)(_typeCount + 1);

            int blackCount = Mathf.Min(_initialBlackCount, _maxParticleCount - _particleCount);
            for (int i = 0; i < blackCount; i++)
            {
                int idx = _particleCount + i;
                var pos = SampleInitialPosition(ref rng);
                _positionsRead[idx]  = pos;
                _positionsWrite[idx] = pos;
                _velocities[idx]     = float2.zero;
                _types[idx]          = blackType;
                _isPlayerOwned[idx]  = false;
                _idleTime[idx]       = 0f;
            }
            _particleCount += blackCount;

            int whiteCount = Mathf.Min(_initialWhiteCount, _maxParticleCount - _particleCount);
            for (int i = 0; i < whiteCount; i++)
            {
                int idx = _particleCount + i;
                var pos = SampleInitialPosition(ref rng);
                _positionsRead[idx]  = pos;
                _positionsWrite[idx] = pos;
                _velocities[idx]     = float2.zero;
                _types[idx]          = whiteType;
                _isPlayerOwned[idx]  = false;
                _idleTime[idx]       = 0f;
            }
            _particleCount += whiteCount;
        }

        /// <summary>
        /// Returns a spawn position for initial particle placement.
        /// Unbounded mode: uniform random point in the annulus [_spawnRadiusMin, _spawnRadiusMax]
        ///   centred on the world origin (player start position).
        /// Bounded mode: uniform random point in the world rectangle (original behaviour).
        /// </summary>
        private float2 SampleInitialPosition(ref Unity.Mathematics.Random rng)
        {
            if (_unboundedWorld)
            {
                // Use _initialSpawnRadiusMin (typically smaller) so particles can form
                // clusters near the player start position, distinct from the runtime
                // CullAndRespawn ring which places particles off-screen (_spawnRadiusMin).
                float angle  = rng.NextFloat() * math.PI * 2f;
                float radius = _initialSpawnRadiusMin + rng.NextFloat() * (_spawnRadiusMax - _initialSpawnRadiusMin);
                return new float2(math.cos(angle), math.sin(angle)) * radius;
            }
            return new float2(
                rng.NextFloat() * (_worldHalfX * 2f) - _worldHalfX,
                rng.NextFloat() * (_worldHalfY * 2f) - _worldHalfY);
        }

        // ── Player input state ────────────────────────────────────────────────
        // _playerInputDir: normalised direction set by PlayerControl each frame.
        // _playerParticleCount: cluster size, used for resistance scaling in the job.
        // _playerCentroid: world-space centroid, forwarded to CellularAutomata for ring spawning.
        // PlayerInputForce is kept for CaptureDetection (magnitude ≈ effective push).
        private float2 _playerInputDir;
        private int    _playerParticleCount;
        private float2 _playerCentroid;

        /// <summary>
        /// Sets the normalised movement direction, cluster size, and world-space centroid
        /// for the next FixedUpdate. Call every LateUpdate from PlayerControl.
        /// Pass float2.zero for normalisedDir and centroid when no input is active.
        /// </summary>
        public void SetPlayerInput(float2 normalisedDir, int clusterSize, float2 centroid)
        {
            _playerInputDir      = normalisedDir;
            _playerParticleCount = clusterSize;
            _playerCentroid      = centroid;
            RecordCentroid(centroid);
        }

        /// <summary>
        /// Effective player input as a force-scale vector (dir × PlayerInputSpeed).
        /// Read by CaptureDetection to compare against external forces.
        /// </summary>
        public float2 PlayerInputForce => _playerInputDir * _playerInputForce;

        /// <summary>Returns gravity parameters for typeA acting on typeB.</summary>
        public Core.GravityEntry GetGravityEntry(int typeA, int typeB)
        {
            int total = _gravityMatrix.TypeCount;
            if (typeA < 0 || typeB < 0 || typeA >= total || typeB >= total)
                return default;

            return _gravityMatrix.Get(typeA, typeB);
        }

        /// <summary>
        /// Writes a single gravity matrix entry. Physics takes effect the next FixedUpdate.
        /// If entry.DistanceThreshold changed, the spatial grid cell size is recalculated
        /// at the start of the next FixedUpdate. Fires OnGravityMatrixChanged.
        /// </summary>
        public void SetGravityEntry(int typeA, int typeB, Core.GravityEntry entry)
        {
            CompletePendingJobIfNeeded();

            int total = _gravityMatrix.TypeCount;
            if (typeA < 0 || typeB < 0 || typeA >= total || typeB >= total)
                return;

            var previous = _gravityMatrix.Get(typeA, typeB);
            _gravityMatrix.Set(typeA, typeB, entry);

            if (!Mathf.Approximately(entry.DistanceThreshold, previous.DistanceThreshold))
                _cellSizeDirty = true;

            OnGravityMatrixChanged?.Invoke();
        }

        /// <summary>
        /// Resets every gravity matrix entry to the hardcoded defaults produced by
        /// <see cref="Core.GravityMatrix.CreateDefault"/>. Uses a temporary allocation;
        /// no long-lived memory overhead. Fires <see cref="OnGravityMatrixChanged"/> for
        /// each entry so <see cref="ConfigPersistence"/> will schedule a debounced save.
        /// </summary>
        public void ResetMatrixToDefault()
        {
            CompletePendingJobIfNeeded();

            var def = Core.GravityMatrix.CreateDefault(_typeCount, Unity.Collections.Allocator.Temp);
            int n   = def.TypeCount;
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                SetGravityEntry(a, b, def.Get(a, b));
            def.Dispose();
        }

        private void CompletePendingJobIfNeeded()
        {
            if (!_jobPending) return;
            _pendingHandle.Complete();
            _jobPending = false;
        }

        /// <summary>Number of configurable (normal) particle types. Does NOT include black/white special types.</summary>
        public int TypeCount => _typeCount;

        /// <summary>Total particle type count including the 2 special types (black + white).</summary>
        public int TotalTypeCount => _gravityMatrix.TypeCount;

        // ── Public accessors (for PlayerControl, HUD, etc.) ──────────────────

        /// <summary>Active particle count.</summary>
        public int ParticleCount => _particleCount;

        /// <summary>Half world width in world units (screen-aspect-derived).</summary>
        public float WorldHalfX => _worldHalfX;

        /// <summary>Half world height in world units (equals worldHeight * 0.5).</summary>
        public float WorldHalfY => _worldHalfY;

        /// <summary>Read-only view of current positions (valid after LateUpdate completes job).</summary>
        public NativeArray<float2> PositionsRead => _positionsRead;

        /// <summary>Read-only view of types array.</summary>
        public NativeArray<byte> Types => _types;

        /// <summary>Read-only view of player ownership flags.</summary>
        public NativeArray<bool> IsPlayerOwned => _isPlayerOwned;

        /// <summary>
        /// Per-particle external force from non-player sources.
        /// Valid for player-owned particles after LateUpdate completes the job.
        /// Used by CaptureDetection to compute control ratio.
        /// </summary>
        public NativeArray<float2> ExternalForceOnPlayer => _externalForceOnPlayer;

        /// <summary>Read-only view of velocities (valid after LateUpdate completes job).</summary>
        public NativeArray<float2> Velocities => _velocities;

        /// <summary>Inspector-configured max velocity; used by PlayerControl for shedding ratio.</summary>
        public float MaxVelocity => _maxVelocity;

        /// <summary>Spatial grid built each FixedUpdate. Valid for reading after LateUpdate completes the job.</summary>
        public NativeParallelMultiHashMap<int2, int> Grid => _grid;

        /// <summary>Current spatial grid cell size (= MaxDistanceThreshold). Recalculated when distanceThreshold changes.</summary>
        public float CellSize => _cellSize;

        /// <summary>Maximum particle capacity. Use to allocate companion NativeArrays at the same size.</summary>
        public int MaxParticleCount => _maxParticleCount;

        /// <summary>Per-type visual radii (world units). Index matches particle type byte, including special types.</summary>
        public NativeArray<float> TypeRadii => _typeRadiiNative;

        /// <summary>True when boundary bounce and repulsion are disabled (unbounded world mode).</summary>
        public bool UnboundedWorld => _unboundedWorld;

        /// <summary>
        /// Activates or deactivates the gravity shield. While active, external forces on
        /// player-owned particles are zeroed in PhysicsJob. Called by PlayerSkill.
        /// <paramref name="playerRepulsionScale"/> multiplies repulsion between player-owned
        /// particles; pass 1 (default) when deactivating.
        /// </summary>
        public void SetShieldActive(bool active, float playerRepulsionScale = 1f)
        {
            _shieldActive               = active;
            _shieldPlayerRepulsionScale = active ? playerRepulsionScale : 1f;
        }

        /// <summary>
        /// Schedules a one-shot conversion of approximately <paramref name="fraction"/> of
        /// player-owned particles into random non-special types. Executes safely at the
        /// start of the next FixedUpdate, after any pending job is completed.
        /// </summary>
        public void RequestScatterPlayerParticles(float fraction)
        {
            _pendingScatter         = true;
            _pendingScatterFraction = fraction;
        }

        /// <summary>
        /// Converts each player-owned particle to a random non-special type with probability
        /// <paramref name="fraction"/>. Converted particles are released from player ownership.
        /// Must be called on the main thread with no job running.
        /// </summary>
        private void ExecuteScatterPlayerParticles(float fraction)
        {
            // Pass 1: probabilistic conversion.
            int converted = 0;
            for (int i = 0; i < _particleCount; i++)
            {
                if (!_isPlayerOwned[i]) continue;
                if (_cullRng.NextFloat() >= fraction) continue;

                _types[i]             = (byte)_cullRng.NextInt(0, _typeCount);
                _isPlayerOwned[i]     = false;
                _isInPlayerCluster[i] = false;
                converted++;
            }

            if (converted > 0) return;

            // Guarantee: always convert at least one particle.
            // Reservoir sampling — uniform random selection in a single pass, no allocation.
            int chosen = -1;
            int seen   = 0;
            for (int i = 0; i < _particleCount; i++)
            {
                if (!_isPlayerOwned[i]) continue;
                seen++;
                if (_cullRng.NextInt(0, seen) == 0) chosen = i;
            }
            if (chosen < 0) return;

            _types[chosen]             = (byte)_cullRng.NextInt(0, _typeCount);
            _isPlayerOwned[chosen]     = false;
            _isInPlayerCluster[chosen] = false;
        }

        /// <summary>
        /// Resets all particle state and re-spawns the initial population.
        /// Call before GameStateManager.RestartSession() to ensure a clean slate.
        /// </summary>
        public void Reinitialize()
        {
            if (_jobPending)
            {
                _pendingHandle.Complete();
                _jobPending = false;
            }

            for (int i = 0; i < _maxParticleCount; i++)
            {
                _positionsRead[i]         = float2.zero;
                _positionsWrite[i]        = float2.zero;
                _velocities[i]            = float2.zero;
                _types[i]                 = 0;
                _isPlayerOwned[i]         = false;
                _isInPlayerCluster[i]     = false;
                _idleTime[i]              = 0f;
                _externalForceOnPlayer[i] = float2.zero;
            }

            _particleCount    = 0;
            _playerInputDir      = float2.zero;
            _playerParticleCount = 0;

            SpawnInitialParticles();
        }

        /// <summary>
        /// Tags a particle as player-owned. Called by PlayerControl on session start.
        /// </summary>
        public void SetPlayerOwned(int index, bool owned)
        {
            CompletePendingJobIfNeeded();
            if ((uint)index < (uint)_particleCount)
                _isPlayerOwned[index] = owned;
        }

        /// <summary>
        /// Marks a particle as part of the player's visual cluster for input-force purposes.
        /// Called by PlayerControl.MarkPlayerCluster() each LateUpdate.
        /// </summary>
        public void SetInPlayerCluster(int index, bool inCluster)
        {
            if ((uint)index < (uint)_particleCount)
                _isInPlayerCluster[index] = inCluster;
        }

        /// <summary>
        /// Clears all cluster membership flags. Call before re-marking each frame.
        /// </summary>
        public void ClearPlayerClusterFlags()
        {
            for (int i = 0; i < _particleCount; i++)
                _isInPlayerCluster[i] = false;
        }

        /// <summary>Read-only view of cluster membership flags (valid after PlayerControl LateUpdate).</summary>
        public NativeArray<bool> IsInPlayerCluster => _isInPlayerCluster;

        // ── Spawn direction smoothing ─────────────────────────────────────────

        /// <summary>
        /// Appends the current centroid to the history queue and prunes entries
        /// older than 1.1 × _spawnDirWindowSec so the oldest surviving entry
        /// approximates _spawnDirWindowSec ago.
        /// </summary>
        private void RecordCentroid(float2 centroid)
        {
            float now = Time.time;
            _centroidHistory.Enqueue(new CentroidSample { Position = centroid, Time = now });

            float pruneAge = now - _spawnDirWindowSec * 1.1f;
            while (_centroidHistory.Count > 1 && _centroidHistory.Peek().Time < pruneAge)
                _centroidHistory.Dequeue();
        }

        /// <summary>
        /// Returns the net-displacement direction over the configured history window.
        /// Falls back to <paramref name="fallback"/> (real-time input direction) when
        /// less than 80 % of the window has been recorded.
        /// Returns float2.zero when the player is stationary (no bias → full-circle spawn).
        /// </summary>
        private float2 ComputeSmoothedMoveDir(float2 fallback)
        {
            if (_centroidHistory.Count < 2) return Normalized(fallback);

            var   oldest  = _centroidHistory.Peek();
            float elapsed = Time.time - oldest.Time;

            if (elapsed < _spawnDirWindowSec * 0.8f)
                return Normalized(fallback);   // not enough history → real-time direction

            float2 delta = _playerCentroid - oldest.Position;
            float  dsq   = math.lengthsq(delta);
            return dsq > 0.0001f ? math.normalize(delta) : float2.zero;
        }

        private static float2 Normalized(float2 v)
        {
            float dsq = math.lengthsq(v);
            return dsq > 0.001f ? v * math.rsqrt(dsq) : float2.zero;
        }

        // ── Cull & respawn ────────────────────────────────────────────────────

        /// <summary>
        /// Teleports non-player particles that are farther than _despawnRadius from
        /// <paramref name="center"/> into the spawn annulus [_spawnRadiusMin, _spawnRadiusMax].
        /// Type distribution is re-balanced across the entire batch in a single O(N) pass.
        /// Safe to call on the main thread: the pending physics job is always completed
        /// before FixedUpdate modifies any NativeArray.
        /// </summary>
        private void CullAndRespawn(float2 center, float2 moveDir, float directionBias)
        {
            float despawnR2 = _despawnRadius * _despawnRadius;

            // Precompute forward direction once; bias probability applied per group seed.
            bool  useBias      = directionBias > 0f && math.lengthsq(moveDir) > 0.001f;
            float forwardAngle = useBias ? math.atan2(moveDir.y, moveDir.x) : 0f;

            // Count current type distribution once for the whole batch.
            for (int t = 0; t < _typeCount; t++) _cullTypeCounts[t] = 0;
            for (int i = 0; i < _particleCount; i++)
                if (_types[i] < _typeCount)
                    _cullTypeCounts[_types[i]]++;

            // Group spawn state — recycled particles share a seed position per group,
            // so they appear near each other and begin forming clusters immediately.
            // Set _spawnGroupSizeMin = _spawnGroupSizeMax = 1 to restore original behaviour.
            int    groupCounter     = 0;
            int    currentGroupSize = 1;
            float2 groupSeedPos     = float2.zero;

            for (int i = 0; i < _particleCount; i++)
            {
                if (_isPlayerOwned[i]) continue;
                if (math.distancesq(_positionsRead[i], center) <= despawnR2) continue;

                // Pick a new seed position at the start of each group.
                if (groupCounter == 0)
                {
                    currentGroupSize = (_spawnGroupSizeMax <= _spawnGroupSizeMin)
                        ? _spawnGroupSizeMin
                        : _cullRng.NextInt(_spawnGroupSizeMin, _spawnGroupSizeMax + 1);
                    currentGroupSize = math.max(1, currentGroupSize);

                    // Mixture distribution: biased toward forward hemisphere or full circle.
                    float seedAngle = useBias && _cullRng.NextFloat() < directionBias
                        ? forwardAngle + (_cullRng.NextFloat() * 2f - 1f) * (math.PI * 0.5f)
                        : _cullRng.NextFloat() * math.PI * 2f;
                    float seedRadius = _spawnRadiusMin + _cullRng.NextFloat() * (_spawnRadiusMax - _spawnRadiusMin);
                    groupSeedPos = center + new float2(math.cos(seedAngle), math.sin(seedAngle)) * seedRadius;
                }

                // Scatter each particle within the group around the seed (uniform disc).
                float jitterAngle  = _cullRng.NextFloat() * math.PI * 2f;
                float jitterRadius = _cullRng.NextFloat() * _clusterSpawnJitter;
                float2 newPos = groupSeedPos + new float2(math.cos(jitterAngle), math.sin(jitterAngle)) * jitterRadius;

                groupCounter = (groupCounter + 1) % currentGroupSize;

                byte newType = PickRarestType();

                _positionsRead[i]  = newPos;
                _positionsWrite[i] = newPos;
                _velocities[i]     = float2.zero;
                _types[i]          = newType;
                _idleTime[i]       = 0f;

                _cullTypeCounts[newType]++;   // keep distribution balanced within the batch
            }
        }

        /// <summary>
        /// Returns the normal type (index &lt; _typeCount) with the lowest current count
        /// according to _cullTypeCounts. Breaks ties randomly.
        /// </summary>
        private byte PickRarestType()
        {
            int minCount = int.MaxValue;
            for (int t = 0; t < _typeCount; t++)
                if (_cullTypeCounts[t] < minCount) minCount = _cullTypeCounts[t];

            int candidates = 0;
            for (int t = 0; t < _typeCount; t++)
                if (_cullTypeCounts[t] == minCount) candidates++;

            int pick = _cullRng.NextInt(0, candidates);
            int seen = 0;
            for (int t = 0; t < _typeCount; t++)
            {
                if (_cullTypeCounts[t] != minCount) continue;
                if (seen == pick) return (byte)t;
                seen++;
            }
            return 0;
        }

        // ── Editor helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Populates _gravityMatrixConfig with the same deterministic defaults used
        /// by GravityMatrix.CreateDefault(). Run this from the Inspector context menu
        /// (right-click the component header) to get an editable starting point.
        /// </summary>
        [ContextMenu("生成默认引力矩阵")]
        private void GenerateDefaultMatrixConfig()
        {
            int n = _typeCount;
            _gravityMatrixConfig = new Core.GravityEntry[n * n];
            var rng = new Unity.Mathematics.Random(12345u);
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                _gravityMatrixConfig[a * n + b] = new Core.GravityEntry
                {
                    AttractionStrength = a == b ? 20f : rng.NextFloat(-15f, 30f),
                    RepulsionStrength  = 8f,
                    DistanceThreshold  = 3f + rng.NextFloat(0f, 2f),
                };
            }
        }

        /// <summary>
        /// Forwards the player type to SpawnRipple for player-type-only filtering.
        /// Call after the player type is determined (e.g. from PlayerControl.AssignInitialCluster).
        /// </summary>
        public void SetRipplePlayerType(byte type) => _spawnRipple?.SetPlayerType(type);

        /// <summary>Clears the Inspector matrix config so runtime random generation is used instead.</summary>
        [ContextMenu("清除引力矩阵配置（恢复随机）")]
        private void ClearMatrixConfig() => _gravityMatrixConfig = null;
    }
}
