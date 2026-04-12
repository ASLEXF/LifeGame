using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace ParticleLife.Simulation
{
    /// <summary>
    /// Manages particle spawning to grow the ecosystem over time.
    /// Runs on the main thread at a fixed interval (not every frame).
    ///
    /// Rules:
    ///   Spawn: one particle at the sparsest sampled location.
    ///   Type:  the type with the fewest existing particles is chosen,
    ///          keeping all type populations as equal as possible.
    ///   Death: disabled — particles never disappear.
    ///   Cap:   no new particles are spawned once maxParticleCount is reached.
    /// </summary>
    public class CellularAutomata
    {
        /// <summary>
        /// Fired on the main thread immediately after a particle is spawned.
        /// Parameters: world-space spawn position, particle type index.
        /// </summary>
        public event Action<float2, byte> OnParticleSpawned;
        private const int SampleCount = 8;

        private readonly float _spawnInterval;
        private readonly float _densityRadius;
        private readonly int   _densityCap;
        private readonly int[] _typeCounts;   // reused each tick to avoid per-frame allocation
        private readonly int   _totalTypeCount;
        private readonly float _specialSpawnChance;

        private float _timer;
        private Unity.Mathematics.Random _rng;

        public CellularAutomata(
            float spawnInterval,
            float densityRadius,
            int   densityCap,
            int   typeCount,
            int   totalTypeCount,
            float specialSpawnChance = 0.1f,
            uint  seed = 99999u)
        {
            _spawnInterval       = spawnInterval;
            _densityRadius       = densityRadius;
            _densityCap          = densityCap;
            _typeCounts          = new int[typeCount];
            _totalTypeCount      = totalTypeCount;
            _specialSpawnChance  = specialSpawnChance;
            _rng                 = new Unity.Mathematics.Random(seed);
        }

        /// <summary>
        /// Call each FixedUpdate. Spawns one particle if the interval has elapsed
        /// and the particle cap has not been reached.
        ///
        /// When <paramref name="spawnRadiusMin"/> &gt; 0 (unbounded world mode), candidates are
        /// sampled from an annulus centred on <paramref name="centerPos"/> with radii
        /// [spawnRadiusMin, spawnRadiusMax] using sqrt-corrected polar sampling for
        /// uniform area distribution. When spawnRadiusMin ≤ 0 the original
        /// world-space random fallback is used (bounded mode).
        ///
        /// When <paramref name="spawnDirectionBias"/> &gt; 0 and <paramref name="moveDir"/>
        /// is non-zero, spawn candidates are restricted to a forward-facing sector:
        /// half-angle = π × (1 − bias). At bias 0.7 the sector is ±54°.
        /// </summary>
        public void Tick(
            NativeArray<float2> positionsRead,
            NativeArray<float2> positionsWrite,
            NativeArray<float2> velocities,
            NativeArray<byte>   types,
            NativeArray<bool>   isPlayerOwned,
            NativeArray<float>  idleTime,
            ref int             particleCount,
            int                 maxParticleCount,
            int                 typeCount,
            float               worldHalfX,
            float               worldHalfY,
            float2              centerPos,
            float               spawnRadiusMin,
            float               spawnRadiusMax,
            float2              moveDir,
            float               spawnDirectionBias)
        {
            _timer += Time.fixedDeltaTime;
            if (_timer < _spawnInterval) return;
            _timer = 0f;

            if (particleCount >= maxParticleCount) return;

            // ── Select spawn position (lowest local density among samples) ──
            float2 spawnPos   = float2.zero;
            float  lowestDens = float.MaxValue;

            for (int s = 0; s < SampleCount; s++)
            {
                float2 candidate;
                if (spawnRadiusMin > 0f)
                {
                    // Unbounded mode: sample within the spawn annulus.
                    // sqrt compensates for polar-coordinate area bias (uniform area distribution).
                    // When a movement direction and bias are provided, the angle is restricted
                    // to a forward-facing sector: half-angle = π × (1 − bias).
                    float baseAngle;
                    if (spawnDirectionBias > 0f && math.lengthsq(moveDir) > 0.001f
                        && _rng.NextFloat() < spawnDirectionBias)
                    {
                        // Biased branch: uniform within forward ±90° hemisphere.
                        float forwardAngle = math.atan2(moveDir.y, moveDir.x);
                        baseAngle = forwardAngle + (_rng.NextFloat() * 2f - 1f) * (math.PI * 0.5f);
                    }
                    else
                    {
                        // Fallback branch (probability 1-bias, or no movement): full circle.
                        baseAngle = _rng.NextFloat() * math.PI * 2f;
                    }
                    float radius = math.sqrt(_rng.NextFloat()) * (spawnRadiusMax - spawnRadiusMin)
                                   + spawnRadiusMin;
                    candidate = centerPos + new float2(math.cos(baseAngle), math.sin(baseAngle)) * radius;
                }
                else
                {
                    // Bounded mode: original world-space random.
                    candidate = new float2(
                        _rng.NextFloat() * (worldHalfX * 2f) - worldHalfX,
                        _rng.NextFloat() * (worldHalfY * 2f) - worldHalfY);
                }
                float density = LocalDensity(candidate, positionsRead, particleCount);
                float weight  = 1f - math.saturate(density / _densityCap);
                if (weight > 0f && density < lowestDens)
                {
                    lowestDens = density;
                    spawnPos   = candidate;
                }
            }

            // ── Select rarest type to keep populations balanced ─────────────
            for (int t = 0; t < typeCount; t++)
                _typeCounts[t] = 0;
            // Skip special-type particles (indices >= typeCount) — CA never spawns them.
            for (int i = 0; i < particleCount; i++)
                if (types[i] < typeCount)
                    _typeCounts[types[i]]++;

            // 先找最小值
            int minCount = int.MaxValue;
            for (int t = 0; t < typeCount; t++)
                if (_typeCounts[t] < minCount)
                    minCount = _typeCounts[t];

            // 统计并随机选取最稀少的 type，避免严格 < 导致低 index 长期独占
            int candidateCount = 0;
            for (int t = 0; t < typeCount; t++)
                if (_typeCounts[t] == minCount)
                    candidateCount++;

            int pick = _rng.NextInt(0, candidateCount);
            byte spawnType = 0;
            int  seen      = 0;
            for (int t = 0; t < typeCount; t++)
            {
                if (_typeCounts[t] != minCount) continue;
                if (seen == pick) { spawnType = (byte)t; break; }
                seen++;
            }

            // ── 特殊类型覆盖（黑/白粒子随机补充）────────────────────────────
            int specialCount = _totalTypeCount - typeCount;
            if (specialCount > 0 && _rng.NextFloat() < _specialSpawnChance)
                spawnType = (byte)(typeCount + _rng.NextInt(0, specialCount));

            // ── Spawn ───────────────────────────────────────────────────────
            int newIdx = particleCount;
            positionsRead[newIdx]  = spawnPos;
            positionsWrite[newIdx] = spawnPos;
            velocities[newIdx]     = float2.zero;
            types[newIdx]          = spawnType;
            isPlayerOwned[newIdx]  = false;
            idleTime[newIdx]       = 0f;
            particleCount++;
            OnParticleSpawned?.Invoke(spawnPos, spawnType);
        }

        private float LocalDensity(float2 center, NativeArray<float2> positions, int count)
        {
            float r2 = _densityRadius * _densityRadius;
            int   n  = 0;
            for (int i = 0; i < count; i++)
            {
                float2 d = positions[i] - center;
                if (math.lengthsq(d) < r2) n++;
            }
            return n;
        }
    }
}
