using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Rendering
{
    /// <summary>
    /// Renders an expanding ring at each particle spawn position.
    ///
    /// Usage:
    ///   1. Attach to any GameObject in the scene.
    ///   2. Assign a transparent unlit material to _rippleMaterial
    ///      (URP: Unlit shader, Surface Type = Transparent, write _BaseColor).
    ///   3. Assign this component to ParticleSimulation._spawnRipple — the
    ///      simulation wires up CellularAutomata.OnParticleSpawned automatically.
    ///
    /// Rendering: DrawMeshInstanced with a procedural annulus mesh.
    /// Pool: 32 pre-allocated slots; at default 0.5 s spawn interval and 0.6 s
    /// duration only ~2 ripples are ever active simultaneously.
    /// </summary>
    public class SpawnRipple : MonoBehaviour
    {
        [Header("波纹参数")]
        [Tooltip("波纹持续时间（秒）")]
        [SerializeField] private float _duration   = 0.6f;
        [Tooltip("波纹起始缩放（世界单位直径）")]
        [SerializeField] private float _startScale = 0.8f;
        [Tooltip("波纹结束缩放（世界单位直径）")]
        [SerializeField] private float _endScale   = 5.0f;
        [Tooltip("波纹起始透明度")]
        [SerializeField][Range(0f, 1f)] private float _startAlpha = 0.8f;

        [Header("渲染")]
        [Tooltip("透明无光照材质，需支持 _BaseColor 属性（含 alpha）")]
        [SerializeField] private Material _rippleMaterial;

        [Header("过滤")]
        [Tooltip("启用后仅对玩家类型粒子生成时触发波纹效果")]
        [SerializeField] private bool _playerTypeOnly = false;

        [Header("颜色（按类型，与 ParticleRenderer 保持一致）")]
        [SerializeField] private Color[] _typeColors = new Color[]
        {
            new Color(0.20f, 0.60f, 1.00f),  // 0: 蓝
            new Color(1.00f, 0.40f, 0.20f),  // 1: 橙
            new Color(0.20f, 1.00f, 0.40f),  // 2: 绿
            new Color(1.00f, 0.90f, 0.10f),  // 3: 黄
            new Color(0.80f, 0.20f, 1.00f),  // 4: 紫
            new Color(1.00f, 0.40f, 0.60f),  // 5: 粉
            new Color(0.20f, 0.90f, 0.90f),  // 6: 青
            new Color(1.00f, 0.70f, 0.20f),  // 7: 金
        };

        private byte _playerType;

        private const int MaxRipples = 32;

        private struct RippleInstance
        {
            public Vector2 Position;
            public Color   Color;
            public float   Elapsed;
            public bool    Active;
        }

        private RippleInstance[]      _pool;
        private Mesh                  _ringMesh;
        private Matrix4x4[]           _matrices;
        private Vector4[]             _colors;
        private MaterialPropertyBlock _propertyBlock;

        private void Awake()
        {
            _pool          = new RippleInstance[MaxRipples];
            _matrices      = new Matrix4x4[MaxRipples];
            _colors        = new Vector4[MaxRipples];
            _propertyBlock = new MaterialPropertyBlock();
            _ringMesh      = CreateRingMesh();
        }

        /// <summary>
        /// Sets the player particle type. When _playerTypeOnly is enabled, only spawns
        /// of this type will trigger a ripple. Call from ParticleSimulation after the
        /// player type is determined.
        /// </summary>
        public void SetPlayerType(byte type) => _playerType = type;

        /// <summary>
        /// Activates a ripple at the given world position with the matching type color.
        /// Subscribe this to CellularAutomata.OnParticleSpawned (done by ParticleSimulation).
        /// </summary>
        public void Trigger(float2 position, byte type)
        {
            Debug.Log($"Received spawn event at {position} for type {type}, playertype {_playerType}");
            if (_playerTypeOnly && type != _playerType) return;
            Debug.Log($"Triggering ripple at {position} for type {type}");
            // Find first inactive slot
            for (int i = 0; i < MaxRipples; i++)
            {
                if (_pool[i].Active) continue;
                WriteSlot(i, position, type);
                return;
            }
            // Pool full — overwrite slot 0 (visual-only, no gameplay consequence)
            WriteSlot(0, position, type);
        }

        private void WriteSlot(int i, float2 position, byte type)
        {
            _pool[i] = new RippleInstance
            {
                Position = new Vector2(position.x, position.y),
                Color    = TypeColor(type),
                Elapsed  = 0f,
                Active   = true,
            };
        }

        private void LateUpdate()
        {
            if (_rippleMaterial == null || _ringMesh == null) return;

            int   count = 0;
            float dt    = Time.deltaTime;

            for (int i = 0; i < MaxRipples; i++)
            {
                if (!_pool[i].Active) continue;

                _pool[i].Elapsed += dt;
                if (_pool[i].Elapsed >= _duration)
                {
                    _pool[i].Active = false;
                    continue;
                }

                float t     = _pool[i].Elapsed / _duration;
                float scale = Mathf.Lerp(_startScale, _endScale, t);
                float alpha = Mathf.Lerp(_startAlpha, 0f, t);

                _matrices[count] = Matrix4x4.TRS(
                    new Vector3(_pool[i].Position.x, _pool[i].Position.y, 0.005f),
                    Quaternion.identity,
                    Vector3.one * scale);

                Color c = _pool[i].Color;
                _colors[count] = new Vector4(c.r, c.g, c.b, alpha);
                count++;
            }

            if (count == 0) return;

            _propertyBlock.SetVectorArray("_BaseColor", _colors);
            Graphics.DrawMeshInstanced(
                _ringMesh, 0, _rippleMaterial,
                _matrices, count, _propertyBlock);
        }

        private Color TypeColor(byte type)
            => _typeColors.Length > 0 ? _typeColors[type % _typeColors.Length] : Color.white;

        /// <summary>
        /// Creates a flat annulus mesh centered at the origin with unit diameter.
        /// Inner radius 0.42, outer radius 0.50 — thin ring visible at all scales.
        /// </summary>
        private static Mesh CreateRingMesh(int segments = 32)
        {
            var mesh      = new Mesh { name = "RippleRing" };
            var vertices  = new Vector3[segments * 2];
            var triangles = new int[segments * 6];

            const float innerR = 0.42f;
            const float outerR = 0.50f;

            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                float cos   = Mathf.Cos(angle);
                float sin   = Mathf.Sin(angle);
                vertices[i * 2]     = new Vector3(cos * innerR, sin * innerR, 0f);
                vertices[i * 2 + 1] = new Vector3(cos * outerR, sin * outerR, 0f);
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int ti   = i * 6;
                triangles[ti]     = i    * 2;
                triangles[ti + 1] = i    * 2 + 1;
                triangles[ti + 2] = next * 2;
                triangles[ti + 3] = next * 2;
                triangles[ti + 4] = i    * 2 + 1;
                triangles[ti + 5] = next * 2 + 1;
            }

            mesh.vertices  = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
