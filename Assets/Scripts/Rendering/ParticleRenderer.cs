using ParticleLife.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Rendering
{
    /// <summary>
    /// Renders all active particles using GPU Instancing (DrawMeshInstanced).
    /// Visual style: minimal flat circles, color-coded by type, player particles brighter.
    /// Reads particle data after physics jobs complete (called from LateUpdate).
    /// </summary>
    public class ParticleRenderer : MonoBehaviour
    {
        [Header("渲染设置")]
        [SerializeField] private Mesh     _particleMesh;
        [SerializeField] private Material _particleMaterial;
        [SerializeField] private float    _particleScale = 0.4f;
        [SerializeField] private float    _playerBrightnessMult = 1.8f;

        [Header("玩家粒子外边缘")]
        [SerializeField] private float _outlineRelativeScale = 1.6f;
        [SerializeField] private Color _outlineColor         = Color.white;

        [Header("粒子颜色（按类型）")]
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

        // Special-type hardcoded colors (black/white appended beyond normal types).
        private static readonly Color BlackParticleColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        private static readonly Color WhiteParticleColor = new Color(0.95f, 0.95f, 0.95f, 1f);

        // Set by ParticleSimulation.Awake so TypeColor can resolve special indices.
        private int _normalTypeCount;

        // GPU Instancing batch size limit (Unity constraint)
        private const int BatchSize = 1023;

        private Matrix4x4[]          _matrices;
        private MaterialPropertyBlock _propertyBlock;
        private Vector4[]            _colors;
        private Matrix4x4[]          _outlineMatrices;
        private Vector4[]            _outlineColors;

        private void Awake()
        {
            _matrices        = new Matrix4x4[BatchSize];
            _propertyBlock   = new MaterialPropertyBlock();
            _colors          = new Vector4[BatchSize];
            _outlineMatrices = new Matrix4x4[BatchSize];
            _outlineColors   = new Vector4[BatchSize];

            if (_particleMesh == null)
                _particleMesh = CreateCircleMesh();
        }

        /// <summary>
        /// Renders all active particles. Call from LateUpdate after JobHandle.Complete().
        /// </summary>
        /// <param name="positions">Read buffer (positionsRead after swap).</param>
        /// <param name="types">Particle type array.</param>
        /// <param name="isPlayerOwned">Player ownership flags.</param>
        /// <param name="particleCount">Active particle count.</param>
        public void Render(
            NativeArray<float2> positions,
            NativeArray<byte>   types,
            NativeArray<bool>   isPlayerOwned,
            int                 particleCount,
            NativeArray<float>  typeRadii)
        {
            if (_particleMesh == null || _particleMaterial == null) return;

            // ── 轮廓 Pass（仅玩家粒子，z=0.01f，先绘制在下层）────────────────
            Vector4 outlineVec = new(_outlineColor.r, _outlineColor.g, _outlineColor.b, _outlineColor.a);
            int     outlineBatchCount = 0;

            for (int i = 0; i < particleCount; i++)
            {
                if (!isPlayerOwned[i]) continue;

                float outlineScale = ParticleScale(types[i], typeRadii) * _outlineRelativeScale;
                _outlineMatrices[outlineBatchCount] = Matrix4x4.TRS(
                    new Vector3(positions[i].x, positions[i].y, 0.01f),
                    Quaternion.identity,
                    Vector3.one * outlineScale);
                _outlineColors[outlineBatchCount] = outlineVec;
                outlineBatchCount++;

                if (outlineBatchCount == BatchSize)
                {
                    _propertyBlock.SetVectorArray("_BaseColor", _outlineColors);
                    Graphics.DrawMeshInstanced(
                        _particleMesh, 0, _particleMaterial,
                        _outlineMatrices, outlineBatchCount, _propertyBlock);
                    outlineBatchCount = 0;
                }
            }
            if (outlineBatchCount > 0)
            {
                _propertyBlock.SetVectorArray("_BaseColor", _outlineColors);
                Graphics.DrawMeshInstanced(
                    _particleMesh, 0, _particleMaterial,
                    _outlineMatrices, outlineBatchCount, _propertyBlock);
            }

            // ── 主粒子 Pass（z=0f，覆盖在轮廓上层）──────────────────────────
            int batchStart = 0;
            while (batchStart < particleCount)
            {
                int batchCount = Mathf.Min(BatchSize, particleCount - batchStart);

                for (int b = 0; b < batchCount; b++)
                {
                    int i = batchStart + b;

                    _matrices[b] = Matrix4x4.TRS(
                        new Vector3(positions[i].x, positions[i].y, 0f),
                        Quaternion.identity,
                        Vector3.one * ParticleScale(types[i], typeRadii));

                    Color baseColor = TypeColor(types[i]);
                    if (isPlayerOwned[i])
                        baseColor = new Color(
                            Mathf.Min(baseColor.r * _playerBrightnessMult, 1f),
                            Mathf.Min(baseColor.g * _playerBrightnessMult, 1f),
                            Mathf.Min(baseColor.b * _playerBrightnessMult, 1f));

                    _colors[b] = new Vector4(baseColor.r, baseColor.g, baseColor.b, baseColor.a);
                }

                _propertyBlock.SetVectorArray("_BaseColor", _colors);
                Graphics.DrawMeshInstanced(
                    _particleMesh,
                    0,
                    _particleMaterial,
                    _matrices,
                    batchCount,
                    _propertyBlock);

                batchStart += batchCount;
            }
        }

        /// <summary>
        /// Call once from ParticleSimulation.Awake to allow TypeColor to resolve special indices.
        /// </summary>
        public void SetNormalTypeCount(int normalTypeCount) => _normalTypeCount = normalTypeCount;

        /// <summary>Returns the display color for a normal particle type index (0-based, excludes special types).</summary>
        public Color GetTypeColor(int typeIndex) =>
            _typeColors.Length > 0 ? _typeColors[typeIndex % _typeColors.Length] : Color.white;

        // 返回 type 对应的渲染直径（= 半径 × 2）；若 typeRadii 未配置则回退到 _particleScale
        private float ParticleScale(byte type, NativeArray<float> typeRadii) =>
            typeRadii.IsCreated && type < typeRadii.Length
                ? typeRadii[type] * 2f
                : _particleScale;

        private Color TypeColor(byte type)
        {
            if (_normalTypeCount > 0)
            {
                if (type == _normalTypeCount)     return BlackParticleColor;
                if (type == _normalTypeCount + 1) return WhiteParticleColor;
            }
            return _typeColors.Length > 0 ? _typeColors[type % _typeColors.Length] : Color.white;
        }

        /// <summary>Creates a circle mesh for particle rendering. Radius = 0.5 (fits in unit square).</summary>
        private static Mesh CreateCircleMesh(int segments = 24)
        {
            var mesh      = new Mesh { name = "ParticleCircle" };
            int vertCount = segments + 1; // 中心点 + 圆周点

            var vertices  = new Vector3[vertCount];
            var uvs       = new Vector2[vertCount];
            var triangles = new int[segments * 3];

            // 中心点
            vertices[0] = Vector3.zero;
            uvs[0]      = new Vector2(0.5f, 0.5f);

            // 圆周点
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * Mathf.PI * i / segments;
                float x = Mathf.Cos(angle) * 0.5f;
                float y = Mathf.Sin(angle) * 0.5f;
                vertices[i + 1] = new Vector3(x, y, 0f);
                uvs[i + 1]      = new Vector2(x + 0.5f, y + 0.5f);
            }

            // 扇形三角面（逆时针绕序，从摄像机 -Z 看为正面）
            for (int i = 0; i < segments; i++)
            {
                triangles[i * 3]     = 0;
                triangles[i * 3 + 1] = (i + 1) % segments + 1;
                triangles[i * 3 + 2] = i + 1;
            }

            mesh.vertices  = vertices;
            mesh.uv        = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
