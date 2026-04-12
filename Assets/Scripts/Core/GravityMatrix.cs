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
    ///
    /// Special types are appended after the configurable normal types:
    ///   index normalTypeCount + 0 = Black (only attracts other black, zero force with all else)
    ///   index normalTypeCount + 1 = White (strongly attracted to all non-black particles)
    /// </summary>
    public struct GravityMatrix
    {
        /// <summary>Number of hardcoded special particle types appended beyond the configurable normal types.</summary>
        public const int SpecialTypeCount = 2;

        /// <summary>Index of the black special type relative to normalTypeCount.</summary>
        public const int BlackTypeOffset = 0;

        /// <summary>Index of the white special type relative to normalTypeCount.</summary>
        public const int WhiteTypeOffset = 1;

        /// <summary>Total number of particle types (normal + special). Range: [2+2, 8+2].</summary>
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
        /// Creates a default matrix for <paramref name="normalTypeCount"/> configurable types
        /// plus <see cref="SpecialTypeCount"/> hardcoded special types (black + white).
        /// Total matrix size = (normalTypeCount + SpecialTypeCount)².
        /// Allocates a new NativeArray with the provided allocator.
        /// Caller is responsible for disposing.
        /// </summary>
        public static GravityMatrix CreateDefault(int normalTypeCount, Allocator allocator)
        {
            int total   = normalTypeCount + SpecialTypeCount;
            var entries = new NativeArray<GravityEntry>(total * total, allocator);

            // ── Normal × Normal: randomised asymmetric ecosystem ──────────────
            var rng = new Unity.Mathematics.Random(12345u);
            for (int a = 0; a < normalTypeCount; a++)
            for (int b = 0; b < normalTypeCount; b++)
            {
                entries[a * total + b] = new GravityEntry
                {
                    AttractionStrength = a == b ? 20f : rng.NextFloat(-15f, 30f),
                    RepulsionStrength  = 8f,
                    DistanceThreshold  = 3f + rng.NextFloat(0f, 2f),
                };
            }

            int black = normalTypeCount + BlackTypeOffset;  // = normalTypeCount
            int white = normalTypeCount + WhiteTypeOffset;  // = normalTypeCount + 1

            // ── Normal ↔ Black/White ──────────────────────────────────────────
            for (int a = 0; a < normalTypeCount; a++)
            {
                // Normal → Black: 无吸引力，但近距斥力防止重叠
                entries[a * total + black] = new GravityEntry
                    { AttractionStrength = 0f, RepulsionStrength = 8f, DistanceThreshold = 3f };

                // Black → Normal: 无吸引力，但近距斥力防止重叠
                entries[black * total + a] = new GravityEntry
                    { AttractionStrength = 0f, RepulsionStrength = 8f, DistanceThreshold = 3f };

                // Normal → White: randomised (keeps ecosystem variety)
                entries[a * total + white] = new GravityEntry
                {
                    AttractionStrength = rng.NextFloat(-10f, 25f),
                    RepulsionStrength  = 8f,
                    DistanceThreshold  = 3f + rng.NextFloat(0f, 2f),
                };

                // White → Normal: strong attraction (white chases all normal types)
                entries[white * total + a] = new GravityEntry
                    { AttractionStrength = 20f, RepulsionStrength = 5f, DistanceThreshold = 4f };
            }

            // ── Black self + Black ↔ White ────────────────────────────────────
            entries[black * total + black] = new GravityEntry
                { AttractionStrength = 25f, RepulsionStrength = 3f, DistanceThreshold = 3.5f };

            entries[black * total + white] = new GravityEntry
                { AttractionStrength = 0f, RepulsionStrength = 8f, DistanceThreshold = 3f };

            entries[white * total + black] = new GravityEntry
                { AttractionStrength = 0f, RepulsionStrength = 8f, DistanceThreshold = 3f };

            // ── White self ────────────────────────────────────────────────────
            entries[white * total + white] = new GravityEntry
                { AttractionStrength = 15f, RepulsionStrength = 8f, DistanceThreshold = 3.5f };

            return new GravityMatrix { TypeCount = total, Entries = entries };
        }

        /// <summary>Disposes the internal NativeArray.</summary>
        public void Dispose()
        {
            if (Entries.IsCreated) Entries.Dispose();
        }
    }
}
