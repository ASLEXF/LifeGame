using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ParticleLife.Physics
{
    /// <summary>
    /// Builds a uniform spatial hash grid from particle positions each frame.
    /// Grid cell size equals the maximum gravity distance threshold, ensuring
    /// a 3×3 cell query covers all particles that could exert force on each other.
    ///
    /// World topology is bounded: particles are confined by boundary repulsion forces.
    /// Out-of-bounds neighbor cells simply have no entries in the grid and are skipped.
    /// </summary>
    public static class SpatialGrid
    {
        /// <summary>
        /// Schedules the grid-build job. Complete the returned handle before running PhysicsJob.
        /// </summary>
        public static JobHandle Schedule(
            NativeArray<float2>                      positions,
            int                                      particleCount,
            NativeParallelMultiHashMap<int2, int>    grid,
            float                                    cellSize,
            JobHandle                                dependency = default)
        {
            grid.Clear();
            return new BuildGridJob
            {
                Positions = positions,
                Grid      = grid.AsParallelWriter(),
                CellSize  = cellSize,
            }.Schedule(particleCount, 64, dependency);
        }

        /// <summary>Converts a world-space position to grid cell coordinates.</summary>
        public static int2 PositionToCell(float2 position, float cellSize)
            => new int2(
                (int)math.floor(position.x / cellSize),
                (int)math.floor(position.y / cellSize));
    }

    /// <summary>
    /// Parallel job that inserts particle indices into the spatial grid by cell coordinate.
    /// </summary>
    [BurstCompile]
    public struct BuildGridJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> Positions;
        public NativeParallelMultiHashMap<int2, int>.ParallelWriter Grid;
        public float CellSize;

        public void Execute(int i)
        {
            int2 cell = new int2(
                (int)math.floor(Positions[i].x / CellSize),
                (int)math.floor(Positions[i].y / CellSize));
            Grid.Add(cell, i);
        }
    }
}
