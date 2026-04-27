using ParticleLife.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParticleLife.Simulation
{
    /// <summary>
    /// Compute-shader backend for particle simulation.
    /// Keeps CPU NativeArray mirrors alive for gameplay systems that still read simulation data.
    /// </summary>
    public sealed class GpuSimulationBackend
    {
        private struct GravityEntryGpu
        {
            public float AttractionStrength;
            public float RepulsionStrength;
            public float DistanceThreshold;
            public float Padding;
        }

        private readonly ComputeShader _shader;
        private readonly int _maxParticleCount;
        private readonly int _hashBucketCount;
        private readonly int _hashMask;
        private readonly int _kernelClearHashHeads;
        private readonly int _kernelBuildHashGrid;
        private readonly int _kernelIntegrate;
        private readonly int _kernelIntegrateBrute;

        private ComputeBuffer _positionsRead;
        private ComputeBuffer _positionsWrite;
        private ComputeBuffer _velocities;
        private ComputeBuffer _types;
        private ComputeBuffer _isPlayerOwned;
        private ComputeBuffer _isInPlayerCluster;
        private ComputeBuffer _idleTime;
        private ComputeBuffer _repelTimer;
        private ComputeBuffer _externalForceOnPlayer;
        private ComputeBuffer _matrixEntries;
        private ComputeBuffer _hashHeads;
        private ComputeBuffer _particleNext;
        private ComputeBuffer _particleCells;

        private int _matrixCapacity;
        private uint[] _typeScratch;
        private uint[] _ownedScratch;
        private uint[] _clusterScratch;
        private float2[] _positionsDownloadScratch;
        private float2[] _velocitiesDownloadScratch;
        private float[] _idleTimeDownloadScratch;
        private float[] _repelTimerDownloadScratch;
        private float2[] _externalForceDownloadScratch;
        private AsyncGPUReadbackRequest _externalForceReadbackRequest;
        private bool _externalForceReadbackInFlight;
        private int _externalForceReadbackCount;

        public GpuSimulationBackend(ComputeShader shader, int maxParticleCount)
        {
            _shader = shader;
            _maxParticleCount = maxParticleCount;
            _hashBucketCount = NextPowerOfTwo(Mathf.Max(1, _maxParticleCount * 2));
            _hashMask = _hashBucketCount - 1;
            _kernelClearHashHeads = _shader.FindKernel("ClearHashHeads");
            _kernelBuildHashGrid = _shader.FindKernel("BuildHashGrid");
            _kernelIntegrate = _shader.FindKernel("Integrate");
            _kernelIntegrateBrute = _shader.FindKernel("IntegrateBrute");
            AllocateBuffers();
        }

        private void AllocateBuffers()
        {
            _positionsRead = new ComputeBuffer(_maxParticleCount, sizeof(float) * 2);
            _positionsWrite = new ComputeBuffer(_maxParticleCount, sizeof(float) * 2);
            _velocities = new ComputeBuffer(_maxParticleCount, sizeof(float) * 2);
            _types = new ComputeBuffer(_maxParticleCount, sizeof(uint));
            _isPlayerOwned = new ComputeBuffer(_maxParticleCount, sizeof(uint));
            _isInPlayerCluster = new ComputeBuffer(_maxParticleCount, sizeof(uint));
            _idleTime = new ComputeBuffer(_maxParticleCount, sizeof(float));
            _repelTimer = new ComputeBuffer(_maxParticleCount, sizeof(float));
            _externalForceOnPlayer = new ComputeBuffer(_maxParticleCount, sizeof(float) * 2);
            _hashHeads = new ComputeBuffer(_hashBucketCount, sizeof(int));
            _particleNext = new ComputeBuffer(_maxParticleCount, sizeof(int));
            _particleCells = new ComputeBuffer(_maxParticleCount, sizeof(int) * 2);
        }

        public void Dispose()
        {
            _positionsRead?.Dispose();
            _positionsWrite?.Dispose();
            _velocities?.Dispose();
            _types?.Dispose();
            _isPlayerOwned?.Dispose();
            _isInPlayerCluster?.Dispose();
            _idleTime?.Dispose();
            _repelTimer?.Dispose();
            _externalForceOnPlayer?.Dispose();
            _matrixEntries?.Dispose();
            _hashHeads?.Dispose();
            _particleNext?.Dispose();
            _particleCells?.Dispose();
            _typeScratch = null;
            _ownedScratch = null;
            _clusterScratch = null;
            _positionsDownloadScratch = null;
            _velocitiesDownloadScratch = null;
            _idleTimeDownloadScratch = null;
            _repelTimerDownloadScratch = null;
            _externalForceDownloadScratch = null;
        }

        public void UploadState(
            NativeArray<float2> positionsRead,
            NativeArray<float2> positionsWrite,
            NativeArray<float2> velocities,
            NativeArray<byte> types,
            NativeArray<bool> isPlayerOwned,
            NativeArray<bool> isInPlayerCluster,
            NativeArray<float> idleTime,
            NativeArray<float> repelTimer,
            int particleCount)
        {
            UploadDynamicState(positionsRead, positionsWrite, velocities, idleTime, repelTimer, particleCount);
            UploadEntityMeta(types, isPlayerOwned, isInPlayerCluster, particleCount);
        }

        public void UploadDynamicState(
            NativeArray<float2> positionsRead,
            NativeArray<float2> positionsWrite,
            NativeArray<float2> velocities,
            NativeArray<float> idleTime,
            NativeArray<float> repelTimer,
            int particleCount)
        {
            _positionsRead.SetData(positionsRead, 0, 0, particleCount);
            _positionsWrite.SetData(positionsWrite, 0, 0, particleCount);
            _velocities.SetData(velocities, 0, 0, particleCount);
            _idleTime.SetData(idleTime, 0, 0, particleCount);
            _repelTimer.SetData(repelTimer, 0, 0, particleCount);
        }

        public void UploadEntityMeta(
            NativeArray<byte> types,
            NativeArray<bool> isPlayerOwned,
            NativeArray<bool> isInPlayerCluster,
            int particleCount)
        {
            EnsureScratchCapacity(particleCount);
            for (int i = 0; i < particleCount; i++)
            {
                _typeScratch[i] = types[i];
                _ownedScratch[i] = isPlayerOwned[i] ? 1u : 0u;
                _clusterScratch[i] = isInPlayerCluster[i] ? 1u : 0u;
            }
            _types.SetData(_typeScratch, 0, 0, particleCount);
            _isPlayerOwned.SetData(_ownedScratch, 0, 0, particleCount);
            _isInPlayerCluster.SetData(_clusterScratch, 0, 0, particleCount);
        }

        public void UploadMatrix(NativeArray<GravityEntry> entries)
        {
            int length = entries.Length;
            if (length <= 0)
            {
                _matrixEntries?.Dispose();
                _matrixEntries = null;
                _matrixCapacity = 0;
                return;
            }

            if (_matrixEntries == null || _matrixCapacity != length)
            {
                _matrixEntries?.Dispose();
                _matrixEntries = new ComputeBuffer(length, sizeof(float) * 4);
                _matrixCapacity = length;
            }

            GravityEntryGpu[] gpuEntries = new GravityEntryGpu[length];
            for (int i = 0; i < length; i++)
            {
                gpuEntries[i] = new GravityEntryGpu
                {
                    AttractionStrength = entries[i].AttractionStrength,
                    RepulsionStrength = entries[i].RepulsionStrength,
                    DistanceThreshold = entries[i].DistanceThreshold,
                    Padding = 0f
                };
            }
            _matrixEntries.SetData(gpuEntries);
        }

        private void EnsureScratchCapacity(int particleCount)
        {
            if (_typeScratch == null || _typeScratch.Length < particleCount)
                _typeScratch = new uint[particleCount];
            if (_ownedScratch == null || _ownedScratch.Length < particleCount)
                _ownedScratch = new uint[particleCount];
            if (_clusterScratch == null || _clusterScratch.Length < particleCount)
                _clusterScratch = new uint[particleCount];
        }

        private void EnsureDownloadScratchCapacity(int particleCount)
        {
            if (_positionsDownloadScratch == null || _positionsDownloadScratch.Length < particleCount)
                _positionsDownloadScratch = new float2[particleCount];
            if (_velocitiesDownloadScratch == null || _velocitiesDownloadScratch.Length < particleCount)
                _velocitiesDownloadScratch = new float2[particleCount];
            if (_idleTimeDownloadScratch == null || _idleTimeDownloadScratch.Length < particleCount)
                _idleTimeDownloadScratch = new float[particleCount];
            if (_repelTimerDownloadScratch == null || _repelTimerDownloadScratch.Length < particleCount)
                _repelTimerDownloadScratch = new float[particleCount];
            if (_externalForceDownloadScratch == null || _externalForceDownloadScratch.Length < particleCount)
                _externalForceDownloadScratch = new float2[particleCount];
        }

        public void Step(
            int particleCount,
            int typeCount,
            float deltaTime,
            float damping,
            float maxVelocity,
            float worldHalfX,
            float worldHalfY,
            float idleVelocityThreshold,
            float bounceRestitution,
            float boundaryThreshold,
            float boundaryStrength,
            float2 playerInputDir,
            float playerInputForce,
            float playerMaxSpeed,
            int playerParticleCount,
            int playerResistanceFullAt,
            float playerMaxExternalReduction,
            float forceScale,
            bool unboundedWorld,
            bool shieldActive,
            float shieldPlayerRepulsionScale,
            float2 playerCentroid,
            float clusterCohesionStrength,
            float cellSize,
            int bruteForceThreshold,
            int sliceCount,
            int sliceIndex,
            bool slicePlayerParticlesOnly)
        {
            _shader.SetInt("_ParticleCount", particleCount);
            _shader.SetInt("_TypeCount", typeCount);
            _shader.SetFloat("_DeltaTime", deltaTime);
            _shader.SetFloat("_Damping", damping);
            _shader.SetFloat("_MaxVelocity", maxVelocity);
            _shader.SetFloat("_WorldHalfX", worldHalfX);
            _shader.SetFloat("_WorldHalfY", worldHalfY);
            _shader.SetFloat("_IdleVelocityThreshold", idleVelocityThreshold);
            _shader.SetFloat("_BounceRestitution", bounceRestitution);
            _shader.SetFloat("_BoundaryThreshold", boundaryThreshold);
            _shader.SetFloat("_BoundaryStrength", boundaryStrength);
            _shader.SetVector("_PlayerInputDir", new Vector4(playerInputDir.x, playerInputDir.y, 0f, 0f));
            _shader.SetFloat("_PlayerInputForce", playerInputForce);
            _shader.SetFloat("_PlayerMaxSpeed", playerMaxSpeed);
            _shader.SetInt("_PlayerParticleCount", playerParticleCount);
            _shader.SetInt("_PlayerResistanceFullAt", playerResistanceFullAt);
            _shader.SetFloat("_PlayerMaxExternalReduction", playerMaxExternalReduction);
            _shader.SetFloat("_ForceScale", forceScale);
            _shader.SetInt("_UnboundedWorld", unboundedWorld ? 1 : 0);
            _shader.SetInt("_ShieldActive", shieldActive ? 1 : 0);
            _shader.SetFloat("_ShieldPlayerRepulsionScale", shieldPlayerRepulsionScale);
            _shader.SetVector("_PlayerCentroid", new Vector4(playerCentroid.x, playerCentroid.y, 0f, 0f));
            _shader.SetFloat("_ClusterCohesionStrength", clusterCohesionStrength);
            _shader.SetFloat("_CellSize", math.max(0.001f, cellSize));
            _shader.SetInt("_SliceCount", math.max(1, sliceCount));
            _shader.SetInt("_SliceIndex", math.max(0, sliceIndex));
            _shader.SetInt("_SlicePlayerParticlesOnly", slicePlayerParticlesOnly ? 1 : 0);
            int activeHashBucketCount = NextPowerOfTwo(Mathf.Max(1, particleCount * 2));
            if (activeHashBucketCount > _hashBucketCount)
                activeHashBucketCount = _hashBucketCount;
            int activeHashMask = activeHashBucketCount - 1;
            _shader.SetInt("_ActiveHashBucketCount", activeHashBucketCount);
            _shader.SetInt("_ActiveHashMask", activeHashMask);

            _shader.SetBuffer(_kernelClearHashHeads, "_HashHeads", _hashHeads);
            _shader.SetBuffer(_kernelBuildHashGrid, "_PositionsRead", _positionsRead);
            _shader.SetBuffer(_kernelBuildHashGrid, "_HashHeads", _hashHeads);
            _shader.SetBuffer(_kernelBuildHashGrid, "_ParticleNext", _particleNext);
            _shader.SetBuffer(_kernelBuildHashGrid, "_ParticleCells", _particleCells);

            _shader.SetBuffer(_kernelIntegrate, "_PositionsRead", _positionsRead);
            _shader.SetBuffer(_kernelIntegrate, "_PositionsWrite", _positionsWrite);
            _shader.SetBuffer(_kernelIntegrate, "_Velocities", _velocities);
            _shader.SetBuffer(_kernelIntegrate, "_Types", _types);
            _shader.SetBuffer(_kernelIntegrate, "_IsPlayerOwned", _isPlayerOwned);
            _shader.SetBuffer(_kernelIntegrate, "_IsInPlayerCluster", _isInPlayerCluster);
            _shader.SetBuffer(_kernelIntegrate, "_IdleTime", _idleTime);
            _shader.SetBuffer(_kernelIntegrate, "_RepelTimer", _repelTimer);
            _shader.SetBuffer(_kernelIntegrate, "_ExternalForceOnPlayer", _externalForceOnPlayer);
            _shader.SetBuffer(_kernelIntegrate, "_MatrixEntries", _matrixEntries);
            _shader.SetBuffer(_kernelIntegrate, "_HashHeads", _hashHeads);
            _shader.SetBuffer(_kernelIntegrate, "_ParticleNext", _particleNext);
            _shader.SetBuffer(_kernelIntegrate, "_ParticleCells", _particleCells);

            _shader.SetBuffer(_kernelIntegrateBrute, "_PositionsRead", _positionsRead);
            _shader.SetBuffer(_kernelIntegrateBrute, "_PositionsWrite", _positionsWrite);
            _shader.SetBuffer(_kernelIntegrateBrute, "_Velocities", _velocities);
            _shader.SetBuffer(_kernelIntegrateBrute, "_Types", _types);
            _shader.SetBuffer(_kernelIntegrateBrute, "_IsPlayerOwned", _isPlayerOwned);
            _shader.SetBuffer(_kernelIntegrateBrute, "_IsInPlayerCluster", _isInPlayerCluster);
            _shader.SetBuffer(_kernelIntegrateBrute, "_IdleTime", _idleTime);
            _shader.SetBuffer(_kernelIntegrateBrute, "_RepelTimer", _repelTimer);
            _shader.SetBuffer(_kernelIntegrateBrute, "_ExternalForceOnPlayer", _externalForceOnPlayer);
            _shader.SetBuffer(_kernelIntegrateBrute, "_MatrixEntries", _matrixEntries);

            int groups = Mathf.CeilToInt(particleCount / 64f);
            if (particleCount <= bruteForceThreshold)
            {
                _shader.Dispatch(_kernelIntegrateBrute, groups, 1, 1);
            }
            else
            {
                int hashGroups = Mathf.CeilToInt(activeHashBucketCount / 64f);
                _shader.Dispatch(_kernelClearHashHeads, hashGroups, 1, 1);
                _shader.Dispatch(_kernelBuildHashGrid, groups, 1, 1);
                _shader.Dispatch(_kernelIntegrate, groups, 1, 1);
            }

            (_positionsRead, _positionsWrite) = (_positionsWrite, _positionsRead);
        }

        public void DownloadCoreToCpu(
            NativeArray<float2> positionsWrite,
            NativeArray<float2> velocities,
            NativeArray<float> idleTime,
            NativeArray<float> repelTimer,
            int particleCount)
        {
            EnsureDownloadScratchCapacity(particleCount);

            _positionsRead.GetData(_positionsDownloadScratch, 0, 0, particleCount);
            _velocities.GetData(_velocitiesDownloadScratch, 0, 0, particleCount);
            _idleTime.GetData(_idleTimeDownloadScratch, 0, 0, particleCount);
            _repelTimer.GetData(_repelTimerDownloadScratch, 0, 0, particleCount);

            for (int i = 0; i < particleCount; i++)
            {
                positionsWrite[i] = _positionsDownloadScratch[i];
                velocities[i] = _velocitiesDownloadScratch[i];
                idleTime[i] = _idleTimeDownloadScratch[i];
                repelTimer[i] = _repelTimerDownloadScratch[i];
            }
        }

        public void RequestExternalForceReadback(int particleCount)
        {
            if (_externalForceReadbackInFlight) return;
            _externalForceReadbackCount = math.max(0, particleCount);
            if (_externalForceReadbackCount <= 0) return;
            int bytes = _externalForceReadbackCount * sizeof(float) * 2;
            _externalForceReadbackRequest = AsyncGPUReadback.Request(_externalForceOnPlayer, bytes, 0);
            _externalForceReadbackInFlight = true;
        }

        public bool TryConsumeExternalForceReadback(NativeArray<float2> externalForceOnPlayer, int particleCount)
        {
            if (!_externalForceReadbackInFlight || !_externalForceReadbackRequest.done)
                return false;

            _externalForceReadbackInFlight = false;
            if (_externalForceReadbackRequest.hasError) return false;

            EnsureDownloadScratchCapacity(particleCount);
            var data = _externalForceReadbackRequest.GetData<float2>();
            int count = math.min(math.min(particleCount, data.Length), _externalForceReadbackCount);
            for (int i = 0; i < count; i++)
            {
                _externalForceDownloadScratch[i] = data[i];
                externalForceOnPlayer[i] = _externalForceDownloadScratch[i];
            }
            return true;
        }

        private static int NextPowerOfTwo(int value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }
}
