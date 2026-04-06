using Unity.Collections;
using Unity.Mathematics;

namespace ParticleLife.Core
{
    /// <summary>
    /// Gravity/repulsion parameters for a directed type pair (typeA → typeB).
    /// Marked Serializable so Unity's Inspector can display and edit it directly.
    /// </summary>
    [System.Serializable]
    public struct GravityEntry
    {
        /// <summary>
        /// Attraction strength at distance > DistanceThreshold.
        /// Negative value = long-range repulsion (flee behavior).
        /// </summary>
        public float AttractionStrength;

        /// <summary>
        /// Repulsion strength at distance ≤ DistanceThreshold.
        /// Always positive. Increases as distance approaches zero (prevents overlap).
        /// </summary>
        public float RepulsionStrength;

        /// <summary>
        /// Switching distance between attraction and repulsion zones.
        /// Also determines spatial grid cell size (max across all entries).
        /// Must be > 0.
        /// </summary>
        public float DistanceThreshold;
    }

    /// <summary>
    /// N×N asymmetric gravity matrix — the "genome" of the particle ecosystem.
    /// All ecosystem behavior (predation, clustering, flight) emerges from these parameters.
    /// Does not perform calculations; stores and provides parameters only.
    /// </summary>
    public struct GravityMatrix
    {
        /// <summary>Number of particle types. Range: [2, 8].</summary>
        public int TypeCount;

        /// <summary>
        /// Flat entry storage: Entries[typeA * TypeCount + typeB].
        /// Length = TypeCount * TypeCount.
        /// </summary>
        public NativeArray<GravityEntry> Entries;

        /// <summary>Returns parameters for typeA's interaction with typeB.</summary>
        public GravityEntry Get(int typeA, int typeB) => Entries[typeA * TypeCount + typeB];

        /// <summary>Sets parameters for typeA's interaction with typeB.</summary>
        public void Set(int typeA, int typeB, GravityEntry entry) =>
            Entries[typeA * TypeCount + typeB] = entry;

        /// <summary>
        /// Returns the maximum DistanceThreshold across all entries.
        /// Used to determine spatial grid cell size.
        /// </summary>
        public float MaxDistanceThreshold()
        {
            float max = 0f;
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].DistanceThreshold > max)
                    max = Entries[i].DistanceThreshold;
            return math.max(max, 0.1f); // guard against zero
        }

        /// <summary>
        /// Creates a default matrix with the given type count.
        /// Allocates a new NativeArray with the provided allocator.
        /// Caller is responsible for disposing.
        /// </summary>
        public static GravityMatrix CreateDefault(int typeCount, Allocator allocator)
        {
            var entries = new NativeArray<GravityEntry>(typeCount * typeCount, allocator);

            // Default: same-type mild attraction, cross-type asymmetric
            var rng = new Unity.Mathematics.Random(12345u);
            for (int a = 0; a < typeCount; a++)
            for (int b = 0; b < typeCount; b++)
            {
                entries[a * typeCount + b] = new GravityEntry
                {
                    AttractionStrength = a == b ? 20f : rng.NextFloat(-15f, 30f),
                    RepulsionStrength  = 8f,
                    DistanceThreshold  = 3f + rng.NextFloat(0f, 2f),
                };
            }

            return new GravityMatrix { TypeCount = typeCount, Entries = entries };
        }

        /// <summary>Disposes the internal NativeArray.</summary>
        public void Dispose()
        {
            if (Entries.IsCreated) Entries.Dispose();
        }
    }
}
