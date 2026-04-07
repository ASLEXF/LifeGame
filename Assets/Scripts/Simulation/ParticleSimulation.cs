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

        // Idle tracking threshold — not exposed; value doesn't affect any gameplay decision.
        private const float IdleVelocityThreshold = 0.5f;

        [Header("玩家移动")]
        [Tooltip("玩家粒子在输入方向上的保证最低速度（世界单位/秒）")]
        [SerializeField] private float _playerInputSpeed          = 15f;
        [Tooltip("玩家粒子速度上限（世界单位/秒），通常低于 MaxVelocity 以保证可控性")]
        [SerializeField] private float _playerMaxSpeed            = 25f;
        [Tooltip("达到此粒子数时，外部力干扰降至最低（满抗性）")]
        [SerializeField] private int   _playerResistanceFullAt    = 50;
        [Tooltip("最大外力削减比例（0 = 无抗性，1 = 完全免疫外力）建议 0.6–0.8")]
        [SerializeField][Range(0f, 1f)] private float _playerMaxExternalReduction = 0.75f;

        [Header("边界碰撞")]
        [Tooltip("粒子碰撞边界时反弹速度的保留比例（0 = 完全吸收，1 = 完全弹性）")]
        [SerializeField][Range(0f, 1f)] private float _bounceRestitution  = 0.3f;

        [Tooltip("斥力场生效距离（世界单位）；0 = 禁用斥力场")]
        [SerializeField] private float _boundaryThreshold = 5f;
        [Tooltip("斥力场强度系数")]
        [SerializeField] private float _boundaryStrength  = 50f;

        [Header("细胞自动机参数")]
        [SerializeField] private float _spawnInterval = 0.5f;
        [SerializeField] private float _densityRadius  = 5f;
        [SerializeField] private int   _densityCap     = 10;

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
        private NativeArray<float>  _idleTime;
        private NativeArray<float2> _externalForceOnPlayer;

        private int _particleCount;

        // ── Supporting systems ────────────────────────────────────────────────
        private GravityMatrix                         _gravityMatrix;
        private NativeParallelMultiHashMap<int2, int> _grid;
        private CellularAutomata                      _cellularAutomata;

        // ── Job tracking ──────────────────────────────────────────────────────
        private JobHandle _pendingHandle;
        private bool      _jobPending;

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
            _idleTime              = new NativeArray<float> (_maxParticleCount, Allocator.Persistent);
            _externalForceOnPlayer = new NativeArray<float2>(_maxParticleCount, Allocator.Persistent);

            _gravityMatrix = GravityMatrix.CreateDefault(_typeCount, Allocator.Persistent);

            // Override with Inspector-configured values when the array is fully populated.
            if (_gravityMatrixConfig != null && _gravityMatrixConfig.Length == _typeCount * _typeCount)
            {
                for (int a = 0; a < _typeCount; a++)
                for (int b = 0; b < _typeCount; b++)
                    _gravityMatrix.Set(a, b, _gravityMatrixConfig[a * _typeCount + b]);
            }

            _worldHalfY = _worldHeight * 0.5f;
            _worldHalfX = _worldHalfY * ((float)Screen.width / Screen.height);
            _cellSize   = _gravityMatrix.MaxDistanceThreshold();

            int gridCapacity = _maxParticleCount * 4;
            _grid = new NativeParallelMultiHashMap<int2, int>(gridCapacity, Allocator.Persistent);

            _cellularAutomata = new CellularAutomata(_spawnInterval, _densityRadius, _densityCap, _typeCount);
            if (_spawnRipple != null)
                _cellularAutomata.OnParticleSpawned += _spawnRipple.Trigger;

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

            // Recalculate cell size if any distanceThreshold changed since last frame
            if (_cellSizeDirty)
            {
                _cellSize      = _gravityMatrix.MaxDistanceThreshold();
                _cellSizeDirty = false;
            }

            // Cellular automata runs on main thread (reads positionsRead)
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
                _worldHalfY);

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
                TypeCount                  = _typeCount,
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
                PlayerInputSpeed           = _playerInputSpeed,
                PlayerMaxSpeed             = _playerMaxSpeed,
                PlayerParticleCount        = _playerParticleCount,
                PlayerResistanceFullAt     = _playerResistanceFullAt,
                PlayerMaxExternalReduction = _playerMaxExternalReduction,
                ExternalForceOnPlayer      = _externalForceOnPlayer,
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
                _renderer.Render(_positionsRead, _types, _isPlayerOwned, _particleCount);
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
            _idleTime             .Dispose();
            _externalForceOnPlayer.Dispose();
            _gravityMatrix        .Dispose();
            _grid                 .Dispose();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SpawnInitialParticles()
        {
            var rng   = new Unity.Mathematics.Random(12345u);
            int count = Mathf.Min(_initialCount, _maxParticleCount);

            for (int i = 0; i < count; i++)
            {
                var pos = new float2(
                    rng.NextFloat() * (_worldHalfX * 2f) - _worldHalfX,
                    rng.NextFloat() * (_worldHalfY * 2f) - _worldHalfY);
                _positionsRead[i]  = pos;
                _positionsWrite[i] = pos;
                _velocities[i]     = float2.zero;
                _types[i]          = (byte)rng.NextInt(0, _typeCount);
                _isPlayerOwned[i]  = false;
                _idleTime[i]       = 0f;
            }

            _particleCount = count;
        }

        // ── Player input state ────────────────────────────────────────────────
        // _playerInputDir: normalised direction set by PlayerControl each frame.
        // _playerParticleCount: cluster size, used for resistance scaling in the job.
        // PlayerInputForce is kept for CaptureDetection (magnitude ≈ effective push).
        private float2 _playerInputDir;
        private int    _playerParticleCount;

        /// <summary>
        /// Sets the normalised movement direction and current cluster size for the
        /// next FixedUpdate. Call every LateUpdate from PlayerControl.
        /// Pass float2.zero when no input is active.
        /// </summary>
        public void SetPlayerInput(float2 normalisedDir, int clusterSize)
        {
            _playerInputDir      = normalisedDir;
            _playerParticleCount = clusterSize;
        }

        /// <summary>
        /// Effective player input as a force-scale vector (dir × PlayerInputSpeed).
        /// Read by CaptureDetection to compare against external forces.
        /// </summary>
        public float2 PlayerInputForce => _playerInputDir * _playerInputSpeed;

        /// <summary>Returns gravity parameters for typeA acting on typeB.</summary>
        public Core.GravityEntry GetGravityEntry(int typeA, int typeB) =>
            _gravityMatrix.Get(typeA, typeB);

        /// <summary>
        /// Writes a single gravity matrix entry. Physics takes effect the next FixedUpdate.
        /// If entry.DistanceThreshold changed, the spatial grid cell size is recalculated
        /// at the start of the next FixedUpdate. Fires OnGravityMatrixChanged.
        /// </summary>
        public void SetGravityEntry(int typeA, int typeB, Core.GravityEntry entry)
        {
            var previous = _gravityMatrix.Get(typeA, typeB);
            _gravityMatrix.Set(typeA, typeB, entry);

            if (!Mathf.Approximately(entry.DistanceThreshold, previous.DistanceThreshold))
                _cellSizeDirty = true;

            OnGravityMatrixChanged?.Invoke();
        }

        /// <summary>Number of particle types in the gravity matrix.</summary>
        public int TypeCount => _typeCount;

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
            if ((uint)index < (uint)_particleCount)
                _isPlayerOwned[index] = owned;
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

        /// <summary>Clears the Inspector matrix config so runtime random generation is used instead.</summary>
        [ContextMenu("清除引力矩阵配置（恢复随机）")]
        private void ClearMatrixConfig() => _gravityMatrixConfig = null;
    }
}
