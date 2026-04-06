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
                _particleMesh = CreateQuadMesh();
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
            int                 particleCount)
        {
            if (_particleMesh == null || _particleMaterial == null) return;

            // ── 轮廓 Pass（仅玩家粒子，z=0.01f，先绘制在下层）────────────────
            Vector4 outlineVec = new(_outlineColor.r, _outlineColor.g, _outlineColor.b, _outlineColor.a);
            float   outlineScale = _particleScale * _outlineRelativeScale;
            int     outlineBatchCount = 0;

            for (int i = 0; i < particleCount; i++)
            {
                if (!isPlayerOwned[i]) continue;

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
                        Vector3.one * _particleScale);

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

        private Color TypeColor(byte type)
            => _typeColors.Length > 0 ? _typeColors[type % _typeColors.Length] : Color.white;

        /// <summary>Creates a minimal quad mesh for particle rendering.</summary>
        private static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh { name = "ParticleQuad" };
            mesh.vertices = new Vector3[]
            {
                new(-0.5f, -0.5f, 0f),
                new( 0.5f, -0.5f, 0f),
                new( 0.5f,  0.5f, 0f),
                new(-0.5f,  0.5f, 0f),
            };
            mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            mesh.uv = new Vector2[]
            {
                new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f),
            };
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
