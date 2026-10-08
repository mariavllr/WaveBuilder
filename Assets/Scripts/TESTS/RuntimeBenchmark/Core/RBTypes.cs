// ============================================================================
// RBTypes.cs  (Runtime Benchmark, núcleo independiente de Unity)
//
// Tipos compartidos por los tres solvers del benchmark de coste computacional:
//   · CompiledTileset : problema WFC compilado UNA vez a partir del
//                       TilePreprocessor del framework (dominio, pesos,
//                       adyacencias). Los tres solvers reciben exactamente
//                       el mismo objeto, por lo que dominio, variantes, pesos
//                       y adyacencias son idénticos por construcción.
//   · LevelSpec       : nivel de restricciones (A / B0..B4).
//   · ProblemInstance : celdas preasignadas de una instancia concreta.
//   · InstanceBuilder : genera la instancia (capas, límite, tiles fijas)
//                       FUERA del cronómetro con un RNG propio.
//   · Seeds           : derivación determinista de semillas.
//
// Este fichero NO depende de UnityEngine: se compila también en el arnés
// de pruebas fuera de Unity (sanity checks). Sintaxis limitada a C# 7.3.
//
// Convenciones (idénticas a MyWFC / GuminWFC / REFACTOR):
//   Direcciones: 0 +X | 1 -X | 2 +Z | 3 -Z | 4 +Y | 5 -Y
//   Índice lineal de celda: i = x + z*MX + y*MX*MZ
// ============================================================================

using System;
using System.Collections.Generic;

namespace WFCRuntimeBenchmark
{
    public static class Dir
    {
        public static readonly int[] DX = { 1, -1, 0, 0, 0, 0 };
        public static readonly int[] DY = { 0, 0, 0, 0, 1, -1 };
        public static readonly int[] DZ = { 0, 0, 1, -1, 0, 0 };
        public static readonly int[] OPP = { 1, 0, 3, 2, 5, 4 };
        public static readonly string[] NAME = { "+X", "-X", "+Z", "-Z", "+Y", "-Y" };
    }

    /// <summary>Especificación de tiles fijas (pre-assignment): k copias de una tile base.</summary>
    public sealed class FixedTileSpec
    {
        public int BaseTile;          // id de la tile base (rotación 0)
        public int Count;             // número exacto de copias
        public int Layer = -1;        // -1 = cualquier celda libre (semántica de REFACTOR); >=0 = solo esa capa y
        public int[] Variants;        // ids de la base y sus variantes rotadas que pertenecen al dominio
    }

    /// <summary>
    /// Problema WFC compilado. Contiene TODAS las tiles producidas por el
    /// preprocesado (incluida LIMIT, que no pertenece al dominio de colapso
    /// pero sí puede ocupar celdas preasignadas del anillo de límite).
    /// </summary>
    public sealed class CompiledTileset
    {
        public string Name;
        public bool NegativeRules;            // true si las adyacencias se infirieron con excludedNeighbours
        public int TileCount;
        public string[] TileNames;
        public string[] TileTypes;
        public int[] BaseTile;                // id de la tile base de la que procede cada variante
        public int[] RotationSteps;           // 0..3 pasos de 90º
        public int[] Probability;             // Tile.probability en bruto
        public bool[] InDomain;               // false para LIMIT
        public bool[] Infrastructure;         // Tile.isInfrastructureTile (SOLID, EMPTY, LIMIT); solo lo usan las métricas de calidad
        public int[][][] Allowed;             // [dir][tile] → tiles admitidas en celda + DIR[dir]
        public int FloorTile = -1, EmptyTile = -1, LimitTile = -1;
        public List<FixedTileSpec> FixedTiles = new List<FixedTileSpec>();
        public object[] Payload;              // opcional (refs de Unity para depuración); el núcleo no lo usa

        // ── Derivados (Finish) ───────────────────────────────────────
        public int[] DomainTiles;             // índice de dominio → id de tile
        public int[] DomainIndexOf;           // id de tile → índice de dominio, -1 si fuera
        public double[] DomainWeights;        // peso de muestreo por índice de dominio
        public int[][] DomainPropagator;      // [d*T + t] → índices de dominio admitidos en +d
        public bool[][][] SupportFromTile;    // [dir][tile id][dominio t] ¿t admitida en tile + DIR[dir]?
        public bool[] AllowedMatrix;          // [(d*TileCount + a)*TileCount + b] ¿b admitida en a + DIR[d]?
        public int DirectedRelations;         // nº de relaciones dirigidas (todas las tiles)
        public int DomainDirectedRelations;   // nº de relaciones dirigidas dentro del dominio
        public int AsymmetricRelations;       // relaciones a→b(d) sin b→a(opp d)
        public int NonPositiveWeightTiles;    // tiles del dominio con probability <= 0 (peso forzado a 1)

        public int T { get { return DomainTiles.Length; } }

        public void Finish()
        {
            var dom = new List<int>();
            DomainIndexOf = new int[TileCount];
            for (int t = 0; t < TileCount; t++)
            {
                DomainIndexOf[t] = -1;
                if (InDomain[t]) { DomainIndexOf[t] = dom.Count; dom.Add(t); }
            }
            DomainTiles = dom.ToArray();
            int T = DomainTiles.Length;

            // Pesos: mismo criterio que MyWFC/GuminWFC/REFACTOR (probability > 0 ? probability : 1).
            DomainWeights = new double[T];
            NonPositiveWeightTiles = 0;
            for (int k = 0; k < T; k++)
            {
                int p = Probability[DomainTiles[k]];
                if (p <= 0) NonPositiveWeightTiles++;
                DomainWeights[k] = p > 0 ? p : 1;
            }

            // Relaciones y simetría
            var sets = new HashSet<long>();
            DirectedRelations = 0;
            for (int d = 0; d < 6; d++)
                for (int a = 0; a < TileCount; a++)
                    foreach (int b in Allowed[d][a]) { sets.Add(Key(d, a, b)); DirectedRelations++; }
            AsymmetricRelations = 0;
            for (int d = 0; d < 6; d++)
                for (int a = 0; a < TileCount; a++)
                    foreach (int b in Allowed[d][a])
                        if (!sets.Contains(Key(Dir.OPP[d], b, a))) AsymmetricRelations++;

            // Propagador sobre el dominio
            DomainPropagator = new int[6 * T][];
            DomainDirectedRelations = 0;
            for (int d = 0; d < 6; d++)
                for (int k = 0; k < T; k++)
                {
                    var l = new List<int>();
                    foreach (int b in Allowed[d][DomainTiles[k]])
                        if (DomainIndexOf[b] >= 0) l.Add(DomainIndexOf[b]);
                    l.Sort();
                    DomainPropagator[d * T + k] = l.ToArray();
                    DomainDirectedRelations += l.Count;
                }

            AllowedMatrix = new bool[6 * TileCount * TileCount];
            for (int d = 0; d < 6; d++)
                for (int a = 0; a < TileCount; a++)
                    foreach (int b in Allowed[d][a]) AllowedMatrix[(d * TileCount + a) * TileCount + b] = true;

            SupportFromTile = new bool[6][][];
            for (int d = 0; d < 6; d++)
            {
                SupportFromTile[d] = new bool[TileCount][];
                for (int f = 0; f < TileCount; f++)
                {
                    var s = new bool[T];
                    foreach (int b in Allowed[d][f])
                        if (DomainIndexOf[b] >= 0) s[DomainIndexOf[b]] = true;
                    SupportFromTile[d][f] = s;
                }
            }
        }

        private static long Key(int d, int a, int b) { return ((long)d << 40) | ((long)a << 20) | (long)b; }

        /// <summary>Base + variantes rotadas de una tile base que estén en el dominio.</summary>
        public int[] VariantsOf(int baseTile)
        {
            var l = new List<int>();
            for (int t = 0; t < TileCount; t++)
                if (BaseTile[t] == baseTile && InDomain[t]) l.Add(t);
            return l.ToArray();
        }

        public bool IsAllowed(int d, int a, int b)
        {
            return AllowedMatrix[(d * TileCount + a) * TileCount + b];
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Niveles de restricción
    // ════════════════════════════════════════════════════════════════════

    public sealed class LevelSpec
    {
        public string Benchmark;      // "A" o "B"
        public string Id;             // A_UNRESTRICTED, B0_BASELINE, B1_LAYERS, ...
        public int Order;             // 0..4 dentro de B; 0 para A
        public bool Layers;           // suelo SOLID en y=0 y techo EMPTY en y=MY-1
        public bool Boundary;         // anillo LIMIT en el perímetro de y=1
        public bool FixedTiles;       // pre-assignment
        public bool NegativeRules;    // adyacencias inferidas con excludedNeighbours

        public bool AnyPreassigned { get { return Layers || Boundary || FixedTiles; } }

        public static LevelSpec A_Unrestricted()
        { return new LevelSpec { Benchmark = "A", Id = "A_UNRESTRICTED", Order = 0 }; }

        public static LevelSpec B(int order)
        {
            var l = new LevelSpec { Benchmark = "B", Order = order };
            l.Layers = order >= 1;
            l.Boundary = order >= 2;
            l.FixedTiles = order >= 3;
            l.NegativeRules = order >= 4;
            switch (order)
            {
                case 0: l.Id = "B0_BASELINE"; break;
                case 1: l.Id = "B1_LAYERS"; break;
                case 2: l.Id = "B2_LAYERS_BOUNDARY"; break;
                case 3: l.Id = "B3_LAYERS_BOUNDARY_FIXED"; break;
                case 4: l.Id = "B4_LAYERS_BOUNDARY_FIXED_NEGATIVE"; break;
                default: throw new ArgumentException("order");
            }
            return l;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Instancia del problema (celdas preasignadas)
    // ════════════════════════════════════════════════════════════════════

    public sealed class ProblemInstance
    {
        public int MX, MY, MZ, N;
        public int[] Fixed;               // id de tile preasignada, -1 si la celda es libre
        public byte[] Kind;               // 0 libre | 1 capa (layers) | 2 límite (boundary) | 3 tile fija
        public int LayerCells, BoundaryCells, FixedTileCells, FixedTilesRequested;

        public int PreassignedCells { get { return LayerCells + BoundaryCells + FixedTileCells; } }
        /// <summary>Celdas libres/colapsables: variables que el solver debe asignar.</summary>
        public int FreeCells { get { return N - PreassignedCells; } }

        public int Index(int x, int y, int z) { return x + z * MX + y * MX * MZ; }

        /// <summary>
        /// Pares de celdas preasignadas adyacentes (i, j = i + DIR[d], d ∈ {+X,+Z,+Y}).
        /// Su compatibilidad es una restricción binaria entre variables ya fijadas:
        /// "This work" y DeBroglie-matched la comprueban en InitAttempt (DeBroglie-native
        /// la detecta implícitamente al propagar). Se construye fuera del cronómetro.
        /// </summary>
        public int[] AdjacentFixedPairs()
        {
            var l = new List<int>();
            for (int y = 0; y < MY; y++)
                for (int z = 0; z < MZ; z++)
                    for (int x = 0; x < MX; x++)
                    {
                        int i = Index(x, y, z);
                        if (Fixed[i] < 0) continue;
                        for (int d = 0; d < 6; d += 2)
                        {
                            int x2 = x + Dir.DX[d], y2 = y + Dir.DY[d], z2 = z + Dir.DZ[d];
                            if (x2 >= MX || y2 >= MY || z2 >= MZ) continue;
                            int j = Index(x2, y2, z2);
                            if (Fixed[j] < 0) continue;
                            l.Add(i); l.Add(j); l.Add(d);
                        }
                    }
            return l.ToArray();
        }

        /// <summary>Comprueba los pares preasignados adyacentes. False = instancia contradictoria.</summary>
        public static bool FixedPairsConsistent(CompiledTileset ts, int[] fixedTile, int[] pairs)
        {
            bool[] m = ts.AllowedMatrix;
            int tc = ts.TileCount;
            for (int k = 0; k < pairs.Length; k += 3)
                if (!m[(pairs[k + 2] * tc + fixedTile[pairs[k]]) * tc + fixedTile[pairs[k + 1]]]) return false;
            return true;
        }
    }

    public static class InstanceBuilder
    {
        /// <summary>
        /// Construye la instancia. Orden idéntico a MyWFC.ApplyGlobalConstraints:
        /// límite → capas → tiles fijas. Las tiles fijas se colocan en celdas
        /// libres elegidas al azar (mismo procedimiento que MyWFC/REFACTOR) con
        /// una rotación elegida al azar entre la base y sus variantes.
        /// </summary>
        public static ProblemInstance Build(CompiledTileset ts, int mx, int my, int mz, LevelSpec lv, Random rng)
        {
            var inst = new ProblemInstance { MX = mx, MY = my, MZ = mz, N = mx * my * mz };
            inst.Fixed = new int[inst.N];
            inst.Kind = new byte[inst.N];
            for (int i = 0; i < inst.N; i++) inst.Fixed[i] = -1;

            if (lv.Boundary)
            {
                if (ts.LimitTile < 0) throw new InvalidOperationException("Boundary activo pero el tileset no tiene tile LIMIT.");
                if (my >= 2)
                {
                    int y = 1;
                    for (int z = 0; z < mz; z++)
                        for (int x = 0; x < mx; x++)
                            if (x == 0 || x == mx - 1 || z == 0 || z == mz - 1)
                            { int ib = inst.Index(x, y, z); inst.Fixed[ib] = ts.LimitTile; inst.Kind[ib] = 2; inst.BoundaryCells++; }
                }
            }
            if (lv.Layers)
            {
                if (ts.FloorTile < 0 || ts.EmptyTile < 0) throw new InvalidOperationException("Layers activo pero faltan floor/empty.");
                for (int z = 0; z < mz; z++)
                    for (int x = 0; x < mx; x++)
                    {
                        int i0 = inst.Index(x, 0, z), i1 = inst.Index(x, my - 1, z);
                        if (inst.Fixed[i0] < 0) { inst.Fixed[i0] = ts.FloorTile; inst.Kind[i0] = 1; inst.LayerCells++; }
                        if (inst.Fixed[i1] < 0) { inst.Fixed[i1] = ts.EmptyTile; inst.Kind[i1] = 1; inst.LayerCells++; }
                    }
            }
            if (lv.FixedTiles)
            {
                var free = new List<int>(inst.N);
                foreach (FixedTileSpec spec in ts.FixedTiles)
                {
                    inst.FixedTilesRequested += spec.Count;
                    for (int k = 0; k < spec.Count; k++)
                    {
                        free.Clear();
                        for (int i = 0; i < inst.N; i++)
                        {
                            if (inst.Fixed[i] >= 0) continue;
                            if (spec.Layer >= 0 && i / (mx * mz) != spec.Layer) continue;
                            free.Add(i);
                        }
                        if (free.Count == 0) break;
                        int target = free[rng.Next(0, free.Count)];
                        inst.Fixed[target] = spec.Variants[rng.Next(0, spec.Variants.Length)];
                        inst.Kind[target] = 3;
                        inst.FixedTileCells++;
                    }
                }
            }
            return inst;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Semillas deterministas (FNV-1a 64 + SplitMix64)
    // ════════════════════════════════════════════════════════════════════

    public static class Seeds
    {
        public static int Derive(ulong baseSeed, string key)
        {
            ulong h = 14695981039346656037UL ^ baseSeed;
            foreach (char c in key) { h ^= c; h *= 1099511628211UL; }
            ulong z = h + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z & 0x7FFFFFFF);
        }
    }
}
