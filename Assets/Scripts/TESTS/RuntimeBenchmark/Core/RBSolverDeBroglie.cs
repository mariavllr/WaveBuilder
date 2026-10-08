// ============================================================================
// RBSolverDeBroglie.cs  (DeBroglie 2.1.0, BorisTheBrave; sin modificar)
//
// Configuración: AC-4, HeapMinEntropy, selección ponderada, SIN backtracking
// (reinicio completo ante contradicción, igual que los otros dos solvers).
//
// Representación de las celdas preasignadas (capas, límite, tiles fijas):
//
//   Matched (por defecto) → misma formulación del CSP que "This work".
//     Las celdas preasignadas se EXCLUYEN de la topología mediante la
//     máscara nativa de DeBroglie (GridTopology.WithMask) y su efecto se
//     aplica como restricción unaria sobre los vecinos libres con
//     TilePropagator.Ban(...) (equivale a los contadores de soporte que
//     "This work" calcula a partir de un vecino fijo). LIMIT no entra en el
//     modelo (como en "This work" y Gumin).
//
//   Native (variante de sensibilidad) → uso "de manual" de DeBroglie.
//     Las celdas preasignadas siguen en el CSP. Tiles fijas con
//     FixedTileConstraint (que en 2.1.0 hace Select() dentro de Clear());
//     capas y anillo con Select(); LIMIT entra en el modelo y se banea fuera
//     del anillo.
//
// Fases: el TilePropagator (que compila el modelo y construye la topología)
// se crea en SetupAttempt, FUERA del cronómetro. InitAttempt llama a Clear()
// (reset del estado, que en DeBroglie reserva memoria nueva por diseño) y
// aplica las restricciones de la instancia; Search ejecuta Step() hasta
// Decided/Contradiction (equivale a Run(), contando las decisiones).
// Índice de DeBroglie: x + y*MX + z*MX*MY (width=X, height=Y, depth=Z).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using DeBroglie;
using DeBroglie.Constraints;
using DeBroglie.Models;
using DeBroglie.Topo;
using DeBroglie.Wfc;
using DBTile = DeBroglie.Tile;
using DBDirection = DeBroglie.Topo.Direction;
using DBResolution = DeBroglie.Resolution;

namespace WFCRuntimeBenchmark
{
    public enum DeBroglieMode { Matched, Native }

    /// <summary>
    /// Restricción vacía (API pública ITileConstraint). Su única función es que
    /// WavePropagator.InitConstraints() llame a Propagate() dentro de Clear():
    /// sin ninguna restricción, DeBroglie deja en cola los baneos iniciales del
    /// Clear() AC-4 (tiles sin vecino posible) y hace la PRIMERA observación
    /// sobre un estado aún no arco-consistente, lo que provoca contradicciones
    /// en la decisión nº 1 que ThisWork y Gumin no pueden sufrir (ambos
    /// propagan antes de decidir). No modifica la lógica de resolución.
    /// </summary>
    public sealed class InitialPropagationConstraint : ITileConstraint
    {
        public void Init(TilePropagator propagator) { }
        public void Check(TilePropagator propagator) { }
    }

    public sealed class DeBroglieSolver : IWFCSolverCore
    {
        private readonly DeBroglieMode mode;
        private readonly bool initialPropagation;
        public DeBroglieSolver(DeBroglieMode mode = DeBroglieMode.Matched, bool initialPropagation = true)
        { this.mode = mode; this.initialPropagation = initialPropagation; }

        public string Name { get { return "DeBroglie"; } }
        public string Variant
        {
            get { return (mode == DeBroglieMode.Matched ? "matched" : "native") + (initialPropagation ? "" : "-lazyinit"); }
        }
        public bool Supports(LevelSpec level) { return true; }

        private static readonly DBDirection[] DirMap =
        {
            DBDirection.XPlus, DBDirection.XMinus, DBDirection.ZPlus,
            DBDirection.ZMinus, DBDirection.YPlus, DBDirection.YMinus
        };

        private CompiledTileset ts;
        private LevelSpec level;
        private int MX, MY, MZ, N;
        private AdjacentModel model;
        private DBTile[] dbTile;          // por id de tile (null-equivalente si no está en el modelo)
        private bool[] inModel;
        private Random rng;

        private ProblemInstance inst;
        private TilePropagator propagator;
        private readonly List<Op> ops = new List<Op>();
        private long decisions;
        private int[] fixedPairs = new int[0];

        private struct Op
        {
            public int X, Y, Z;
            public bool IsSelect;
            public TilePropagatorTileSet Set;
        }

        public int SolverCspCells
        {
            get
            {
                if (inst == null) return 0;
                return mode == DeBroglieMode.Matched ? inst.FreeCells : inst.N;
            }
        }
        public long Decisions { get { return decisions; } }

        // ── Preparación (fuera del cronómetro) ───────────────────────────

        public void Prepare(CompiledTileset ts, int mx, int my, int mz, LevelSpec level)
        {
            this.ts = ts; this.level = level;
            MX = mx; MY = my; MZ = mz; N = mx * my * mz;

            inModel = new bool[ts.TileCount];
            for (int t = 0; t < ts.TileCount; t++) inModel[t] = ts.InDomain[t];
            if (mode == DeBroglieMode.Native && level.Boundary && ts.LimitTile >= 0) inModel[ts.LimitTile] = true;

            dbTile = new DBTile[ts.TileCount];
            for (int t = 0; t < ts.TileCount; t++) dbTile[t] = new DBTile(t); // valor = id de tile (int, igualdad por valor)

            model = new AdjacentModel(DirectionSet.Cartesian3d);
            for (int a = 0; a < ts.TileCount; a++)
            {
                if (!inModel[a]) continue;
                int di = ts.DomainIndexOf[a];
                double w = di >= 0 ? ts.DomainWeights[di] : ts.Probability[a];
                model.SetFrequency(dbTile[a], w);
            }
            for (int d = 0; d < 6; d++)
                for (int a = 0; a < ts.TileCount; a++)
                {
                    if (!inModel[a]) continue;
                    foreach (int b in ts.Allowed[d][a])
                        if (inModel[b]) model.AddAdjacency(dbTile[a], dbTile[b], DirMap[d]);
                }
        }

        public void BeginRun(int seed) { rng = new Random(seed); }

        private int DBIndex(int x, int y, int z) { return x + y * MX + z * MX * MY; }

        private TilePropagatorOptions MakeOptions(ITileConstraint[] constraints)
        {
            if (initialPropagation)
            {
                var l = new List<ITileConstraint>();
                l.Add(new InitialPropagationConstraint());
                if (constraints != null) l.AddRange(constraints);
                constraints = l.ToArray();
            }
            return new TilePropagatorOptions
            {
                BacktrackType = BacktrackType.None,
                ModelConstraintAlgorithm = ModelConstraintAlgorithm.Ac4,
                IndexPickerType = IndexPickerType.HeapMinEntropy,
                TilePickerType = TilePickerType.Weighted,
                RandomDouble = rng.NextDouble,
                Constraints = constraints,
            };
        }

        public void SetupAttempt(ProblemInstance instance)
        {
            inst = instance;
            ops.Clear();
            var topology = new GridTopology(MX, MY, MZ, false);

            if (mode == DeBroglieMode.Matched)
            {
                if (inst.PreassignedCells > 0)
                {
                    var mask = new bool[N];
                    for (int y = 0; y < MY; y++)
                        for (int z = 0; z < MZ; z++)
                            for (int x = 0; x < MX; x++)
                                mask[DBIndex(x, y, z)] = inst.Fixed[inst.Index(x, y, z)] < 0;
                    topology = topology.WithMask(mask);
                }
                propagator = new TilePropagator(model, topology, MakeOptions(null));
                fixedPairs = inst.AdjacentFixedPairs();
                BuildMatchedOps();
            }
            else
            {
                var fixedConstraints = new List<ITileConstraint>();
                for (int y = 0; y < MY; y++)
                    for (int z = 0; z < MZ; z++)
                        for (int x = 0; x < MX; x++)
                        {
                            int i = inst.Index(x, y, z);
                            if (inst.Kind[i] == 3)
                                fixedConstraints.Add(new FixedTileConstraint
                                {
                                    Tiles = new[] { dbTile[inst.Fixed[i]] },
                                    Point = new Point(x, y, z),
                                });
                        }
                propagator = new TilePropagator(model, topology,
                    MakeOptions(fixedConstraints.Count > 0 ? fixedConstraints.ToArray() : null));
                BuildNativeOps();
            }
        }

        /// <summary>Bans unarios sobre las celdas libres vecinas de celdas preasignadas.</summary>
        private void BuildMatchedOps()
        {
            if (inst.PreassignedCells == 0) return;
            int T = ts.T;
            var cache = new Dictionary<string, TilePropagatorTileSet>();
            var banned = new bool[T];
            var sb = new StringBuilder();
            for (int y = 0; y < MY; y++)
                for (int z = 0; z < MZ; z++)
                    for (int x = 0; x < MX; x++)
                    {
                        int i = inst.Index(x, y, z);
                        if (inst.Fixed[i] >= 0) continue;
                        bool any = false;
                        for (int t = 0; t < T; t++) banned[t] = false;
                        for (int dc = 0; dc < 6; dc++)
                        {
                            int x2 = x + Dir.DX[dc], y2 = y + Dir.DY[dc], z2 = z + Dir.DZ[dc];
                            if (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ) continue;
                            int f = inst.Fixed[inst.Index(x2, y2, z2)];
                            if (f < 0) continue;
                            // tiles admitidas en (x,y,z) = celda fija + DIR[OPP[dc]]
                            bool[] sup = ts.SupportFromTile[Dir.OPP[dc]][f];
                            for (int t = 0; t < T; t++) if (!sup[t]) { banned[t] = true; any = true; }
                        }
                        if (!any) continue;
                        sb.Length = 0;
                        var list = new List<DBTile>();
                        for (int t = 0; t < T; t++)
                            if (banned[t]) { sb.Append(t).Append(','); list.Add(dbTile[ts.DomainTiles[t]]); }
                        string key = sb.ToString();
                        TilePropagatorTileSet set;
                        if (!cache.TryGetValue(key, out set)) { set = propagator.CreateTileSet(list); cache[key] = set; }
                        ops.Add(new Op { X = x, Y = y, Z = z, IsSelect = false, Set = set });
                    }
        }

        /// <summary>Select de capas y anillo; LIMIT prohibido fuera del anillo.</summary>
        private void BuildNativeOps()
        {
            var single = new Dictionary<int, TilePropagatorTileSet>();
            TilePropagatorTileSet limitSet = null;
            if (inModel.Length > 0 && ts.LimitTile >= 0 && inModel[ts.LimitTile])
                limitSet = propagator.CreateTileSet(new[] { dbTile[ts.LimitTile] });

            for (int y = 0; y < MY; y++)
                for (int z = 0; z < MZ; z++)
                    for (int x = 0; x < MX; x++)
                    {
                        int i = inst.Index(x, y, z);
                        byte k = inst.Kind[i];
                        if (k == 1 || k == 2)
                        {
                            int f = inst.Fixed[i];
                            TilePropagatorTileSet set;
                            if (!single.TryGetValue(f, out set)) { set = propagator.CreateTileSet(new[] { dbTile[f] }); single[f] = set; }
                            ops.Add(new Op { X = x, Y = y, Z = z, IsSelect = true, Set = set });
                        }
                        else if (limitSet != null)
                        {
                            ops.Add(new Op { X = x, Y = y, Z = z, IsSelect = false, Set = limitSet });
                        }
                    }
        }

        // ── t_init (dentro del cronómetro) ────────────────────────────────

        public bool InitAttempt()
        {
            decisions = 0;
            // matched: las celdas preasignadas no están en la topología; su compatibilidad
            // mutua se comprueba aquí, igual que en "This work" (en native la detecta la propagación).
            if (mode == DeBroglieMode.Matched && !ProblemInstance.FixedPairsConsistent(ts, inst.Fixed, fixedPairs)) return false;
            DBResolution st = propagator.Clear();   // incluye FixedTileConstraint.Init en modo native
            if (st == DBResolution.Contradiction) return false;
            for (int k = 0; k < ops.Count; k++)
            {
                Op op = ops[k];
                st = op.IsSelect ? propagator.Select(op.X, op.Y, op.Z, op.Set)
                                 : propagator.Ban(op.X, op.Y, op.Z, op.Set);
                if (st == DBResolution.Contradiction) return false;
            }
            return propagator.Status != DBResolution.Contradiction;
        }

        // ── t_search (dentro del cronómetro) ──────────────────────────────

        public bool Search()
        {
            long steps = 0;
            while (true)
            {
                DBResolution st = propagator.Step();
                steps++;
                if (st == DBResolution.Undecided) continue;
                decisions = st == DBResolution.Decided ? steps - 1 : steps;
                return st == DBResolution.Decided;
            }
        }

        // ── Diagnóstico y lectura (fuera del cronómetro) ──────────────────

        public int UndecidedAfterInit()
        {
            var sets = propagator.ToValueSets<int>();
            int c = 0;
            for (int y = 0; y < MY; y++)
                for (int z = 0; z < MZ; z++)
                    for (int x = 0; x < MX; x++)
                    {
                        if (inst.Fixed[inst.Index(x, y, z)] >= 0) continue;
                        var s = sets.Get(x, y, z);
                        if (s != null && s.Count > 1) c++;
                    }
            return c;
        }

        public void ReadSolution(int[] tilePerCell)
        {
            var arr = propagator.ToValueArray<int>(-1, -2);
            for (int y = 0; y < MY; y++)
                for (int z = 0; z < MZ; z++)
                    for (int x = 0; x < MX; x++)
                    {
                        int i = inst.Index(x, y, z);
                        if (mode == DeBroglieMode.Matched && inst.Fixed[i] >= 0) { tilePerCell[i] = inst.Fixed[i]; continue; }
                        int v = arr.Get(x, y, z);
                        tilePerCell[i] = v >= 0 ? v : -1;
                    }
        }
    }
}
