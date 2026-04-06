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

        private float _timer;
        private Unity.Mathematics.Random _rng;

        public CellularAutomata(
            float spawnInterval,
            float densityRadius,
            int   densityCap,
            int   typeCount,
            uint  seed = 99999u)
        {
            _spawnInterval = spawnInterval;
            _densityRadius = densityRadius;
            _densityCap    = densityCap;
            _typeCounts    = new int[typeCount];
            _rng           = new Unity.Mathematics.Random(seed);
        }

        /// <summary>
        /// Call each FixedUpdate. Spawns one particle if the interval has elapsed
        /// and the particle cap has not been reached.
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
            float               worldHalfY)
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
                float2 candidate = new float2(
                    _rng.NextFloat() * (worldHalfX * 2f) - worldHalfX,
                    _rng.NextFloat() * (worldHalfY * 2f) - worldHalfY);
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
            for (int i = 0; i < particleCount; i++)
                _typeCounts[types[i]]++;

            byte spawnType = 0;
            int  minCount  = int.MaxValue;
            for (int t = 0; t < typeCount; t++)
            {
                if (_typeCounts[t] < minCount)
                {
                    minCount  = _typeCounts[t];
                    spawnType = (byte)t;
                }
            }

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
