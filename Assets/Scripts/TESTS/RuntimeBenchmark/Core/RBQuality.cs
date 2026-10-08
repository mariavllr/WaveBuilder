// ============================================================================
// RBQuality.cs  (benchmark de CALIDAD de las soluciones)
//
// Reutiliza íntegramente la infraestructura del benchmark de runtime:
// BenchmarkPlan (matriz de configuraciones), LevelSpec (restricciones),
// InstanceBuilder (capas, límite, tiles fijas), los solvers corregidos,
// SingleRun (reinicio hasta solución) y la derivación de semillas. Con las
// mismas semillas, los mapas evaluados aquí son exactamente los del benchmark
// de runtime (solution_hash idéntico).
//
// Protocolo: por configuración y solver, R runs (50); cada run reinicia hasta
// obtener solución, de modo que se obtienen exactamente R mapas válidos.
// Sin warm-up, sin descartes y sin medición de tiempo.
//
// Definiciones (ver QUALITY_BENCHMARK.md):
//   · Celdas evaluadas: celdas LIBRES de la instancia (no preasignadas).
//   · Identidad de tile para JS y entropía: tipo de tile (rotaciones
//     agregadas), solo tiles jugables (no infraestructura).
//   · Peso de un tipo: suma de los pesos de sus variantes (cada variante
//     rotada recibe el peso completo de su tile base en el muestreo).
//   · JS global: observada frente a pesos originales.
//   · JS condicionada: observada frente a la referencia condicionada por las
//     restricciones (dominios arco-consistentes tras la propagación inicial,
//     calculados por una sonda independiente del solver evaluado).
//   · Entropía: Shannon (nats) de la distribución observada por tipo.
//   · Diversidad: Hamming normalizada entre pares de mapas sobre las celdas
//     libres en ambos, identidad de variante, incluidas SOLID/EMPTY.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WFCRuntimeBenchmark
{
    public sealed class QualitySettings
    {
        public int Runs = 50;
        public int MaxAttemptsPerRun = 1000;
        public ulong BaseSeed = 20261007UL;
        public bool SaveMaps = true;
    }

    /// <summary>Tipos jugables de un tileset y distribución objetivo original.</summary>
    public sealed class TypeIndex
    {
        public string[] Types;            // tipos jugables (orden de primera aparición por id)
        public int[] TypeOfTile;          // id de tile → índice de tipo jugable, -1 si infraestructura o fuera del dominio
        public double[] OriginalTarget;   // pesos originales agregados por tipo, normalizados

        public static TypeIndex Build(CompiledTileset ts)
        {
            var ti = new TypeIndex { TypeOfTile = new int[ts.TileCount] };
            var types = new List<string>();
            var w = new List<double>();
            for (int t = 0; t < ts.TileCount; t++)
            {
                ti.TypeOfTile[t] = -1;
                if (!ts.InDomain[t] || IsInfra(ts, t)) continue;
                string ty = ts.TileTypes[t];
                int k = types.IndexOf(ty);
                if (k < 0) { k = types.Count; types.Add(ty); w.Add(0); }
                ti.TypeOfTile[t] = k;
                w[k] += ts.DomainWeights[ts.DomainIndexOf[t]];
            }
            ti.Types = types.ToArray();
            double sum = w.Sum();
            ti.OriginalTarget = w.Select(x => x / sum).ToArray();
            return ti;
        }

        public static bool IsInfra(CompiledTileset ts, int t)
        {
            return ts.Infrastructure != null && ts.Infrastructure[t];
        }
    }

    public sealed class MapRecord
    {
        public ConfigGroup Group;
        public string Solver, Variant;
        public int RunIndex, SolverSeed, Attempts, FreeCells, PlayableCells, InfraFreeCells;
        public int CondSupportTypes, CondImpossibleTypes, PresentTypes, AdjacencyViolations;
        public int LayerCells, BoundaryCells, FixedTileCells;   // de la instancia del intento con éxito
        public double JsGlobal, JsConditional, Entropy, EntropyQuadMean, EntropyQuadVar, DiversityToOthers, DiversityUndecidedToOthers, DiversityUndecidedTypeToOthers;
        public int UndecidedCells;          // celdas libres con |D_c| > 1 tras la propagación inicial (sonda)
        public bool[] Undecided;            // por celda
        public string Hash;
        public double[] Q, PCond;          // por tipo jugable
        public int[] Solution;
        public int[] Fixed;
    }

    public static class QualityMetrics
    {
        /// <summary>JS divergence (logaritmo natural, rango [0, ln 2]) entre dos distribuciones del mismo soporte.</summary>
        public static double JS(double[] p, double[] q)
        {
            double js = 0;
            for (int k = 0; k < p.Length; k++)
            {
                double m = 0.5 * (p[k] + q[k]);
                if (m <= 0) continue;
                if (p[k] > 0) js += p[k] * Math.Log(p[k] / m);
                if (q[k] > 0) js += q[k] * Math.Log(q[k] / m);
            }
            return 0.5 * js;
        }

        public static double Entropy(double[] q)
        {
            double h = 0;
            foreach (double x in q) if (x > 0) h -= x * Math.Log(x);
            return h;
        }

        /// <summary>
        /// Referencia condicionada por las restricciones. Para cada celda libre c
        /// con dominio arco-consistente D_c: P_c(t) = w_t / Σ_{s∈D_c} w_s.
        /// La composición esperada es Σ_c P_c agregada por tipo; se restringe a los
        /// tipos jugables y se normaliza.
        /// </summary>
        public static double[] ConditionalReference(CompiledTileset ts, TypeIndex ti, ProblemInstance inst, ThisWorkSolver probe)
        {
            bool[] und;
            return ConditionalReference(ts, ti, inst, probe, out und);
        }

        /// <param name="undecided">Celdas libres cuyo dominio arco-consistente tiene más de una tile.</param>
        public static double[] ConditionalReference(CompiledTileset ts, TypeIndex ti, ProblemInstance inst, ThisWorkSolver probe, out bool[] undecided)
        {
            undecided = new bool[inst.N];
            probe.SetupAttempt(inst);
            if (!probe.InitAttempt())
                throw new InvalidOperationException("La sonda de referencia encontró una instancia contradictoria (no debería ocurrir con una instancia resuelta).");
            int T = ts.T;
            var dom = new bool[T];
            var e = new double[ti.Types.Length];
            for (int i = 0; i < inst.N; i++)
            {
                if (!probe.GetCellDomain(i, dom)) continue;
                double wsum = 0;
                int size = 0;
                for (int t = 0; t < T; t++) if (dom[t]) { wsum += ts.DomainWeights[t]; size++; }
                undecided[i] = size > 1;
                if (wsum <= 0) continue;
                for (int t = 0; t < T; t++)
                {
                    if (!dom[t]) continue;
                    int k = ti.TypeOfTile[ts.DomainTiles[t]];
                    if (k >= 0) e[k] += ts.DomainWeights[t] / wsum;
                }
            }
            double s = e.Sum();
            if (s > 0) for (int k = 0; k < e.Length; k++) e[k] /= s;
            return e;
        }

        /// <summary>Métricas de un mapa (celdas libres, tipos jugables).</summary>
        public static void Compute(MapRecord r, CompiledTileset ts, TypeIndex ti, ProblemInstance inst, ThisWorkSolver probe)
        {
            int K = ti.Types.Length;
            var counts = new double[K];
            var quad = new double[4][];
            for (int q = 0; q < 4; q++) quad[q] = new double[K];
            int midX = inst.MX / 2, midZ = inst.MZ / 2;

            for (int i = 0; i < inst.N; i++)
            {
                if (inst.Fixed[i] >= 0) continue;          // solo celdas libres
                r.FreeCells++;
                int k = ti.TypeOfTile[r.Solution[i]];
                if (k < 0) { r.InfraFreeCells++; continue; }
                r.PlayableCells++;
                counts[k]++;
                int x = i % inst.MX, z = (i / inst.MX) % inst.MZ;
                quad[(x >= midX ? 1 : 0) | (z >= midZ ? 2 : 0)][k]++;
            }

            r.Q = Normalize(counts);
            r.PresentTypes = counts.Count(c => c > 0);
            r.JsGlobal = JS(ti.OriginalTarget, r.Q);
            bool[] und;
            r.PCond = ConditionalReference(ts, ti, inst, probe, out und);
            r.Undecided = und;
            r.UndecidedCells = und.Count(u => u);
            r.JsConditional = JS(r.PCond, r.Q);
            r.CondSupportTypes = r.PCond.Count(p => p > 0);
            r.CondImpossibleTypes = Enumerable.Range(0, K).Count(k => ti.OriginalTarget[k] > 0 && r.PCond[k] <= 0);
            r.Entropy = Entropy(r.Q);

            // Entropía por cuadrantes XZ (definición de la versión anterior, por continuidad con la Tabla VI)
            var hq = new double[4];
            for (int q = 0; q < 4; q++) hq[q] = Entropy(Normalize(quad[q]));
            r.EntropyQuadMean = hq.Average();
            r.EntropyQuadVar = hq.Select(h => (h - r.EntropyQuadMean) * (h - r.EntropyQuadMean)).Average();
        }

        /// <summary>Identificador de tipo para todas las tiles (incluida infraestructura): mismo TileTypes, mismo id.</summary>
        public static int[] AllTypeIds(CompiledTileset ts)
        {
            var ids = new Dictionary<string, int>();
            var o = new int[ts.TileCount];
            for (int t = 0; t < ts.TileCount; t++)
            {
                int k;
                if (!ids.TryGetValue(ts.TileTypes[t], out k)) { k = ids.Count; ids[ts.TileTypes[t]] = k; }
                o[t] = k;
            }
            return o;
        }

        public static double[] Normalize(double[] c)
        {
            double s = c.Sum();
            var o = new double[c.Length];
            if (s > 0) for (int k = 0; k < c.Length; k++) o[k] = c[k] / s;
            return o;
        }

        /// <summary>
        /// Distancia de Hamming normalizada. onlyUndecided = false: celdas libres en ambos mapas.
        /// onlyUndecided = true: celdas con más de una tile posible tras la propagación inicial en ambas
        /// instancias (excluye las celdas libres que la propagación ya determina, p. ej. EMPTY forzadas).
        /// </summary>
        public static double Hamming(MapRecord a, MapRecord b, out int compared)
        {
            return Hamming(a, b, false, out compared);
        }

        public static double Hamming(MapRecord a, MapRecord b, bool onlyUndecided, out int compared)
        {
            return Hamming(a, b, onlyUndecided, null, out compared);
        }

        /// <param name="typeOf">Si no es null, compara la identidad de tipo (rotaciones agregadas) en lugar del id de variante.</param>
        public static double Hamming(MapRecord a, MapRecord b, bool onlyUndecided, int[] typeOf, out int compared)
        {
            compared = 0;
            int diff = 0;
            for (int i = 0; i < a.Solution.Length; i++)
            {
                if (a.Fixed[i] >= 0 || b.Fixed[i] >= 0) continue;
                if (onlyUndecided && !(a.Undecided[i] && b.Undecided[i])) continue;
                compared++;
                if (typeOf == null ? a.Solution[i] != b.Solution[i] : typeOf[a.Solution[i]] != typeOf[b.Solution[i]]) diff++;
            }
            return compared > 0 ? (double)diff / compared : double.NaN;
        }

        /// <summary>Cuantil 0.975 de la t de Student (aproximación de Cornish-Fisher; error < 1e-3 para df ≥ 5).</summary>
        public static double T975(int df)
        {
            if (df <= 0) return double.NaN;
            double z = 1.959963984540054, z3 = z * z * z, z5 = z3 * z * z, z7 = z5 * z * z;
            double d = df;
            return z + (z3 + z) / (4 * d) + (5 * z5 + 16 * z3 + 3 * z) / (96 * d * d)
                     + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * d * d * d);
        }
    }

    public interface IQualitySink
    {
        void Map(MapRecord r);
        void Summary(string line);
        void Reference(string line);
        void Log(string message);
    }

    public static class QualityEngine
    {
        public static string QualityId(LevelSpec lv)
        {
            if (lv.Benchmark == "A") return "Q_UNRESTRICTED";
            return "Q" + lv.Order + lv.Id.Substring(2);   // B2_LAYERS_BOUNDARY → Q2_LAYERS_BOUNDARY
        }

        public static string QualityBenchmark(LevelSpec lv) { return lv.Benchmark == "A" ? "Q-NEUTRAL" : "Q-ABLATION"; }

        public static string QualityConfigId(ConfigGroup g)
        {
            return QualityId(g.Level) + "/" + g.TilesetName.ToUpperInvariant() + "/" + g.SizeLabel;
        }

        public static readonly string MapHeader =
            "config_id,benchmark,level_id,level_order,runtime_config_id,tileset,size_label,dim_x,dim_y,dim_z,total_cells,solver,solver_variant," +
            "run_index,solver_seed,attempts,solution_hash,solution_valid,adjacency_violations," +
            "layer_cells,boundary_cells,fixed_tile_cells,free_cells,playable_free_cells,infra_free_cells," +
            "js_global,js_conditional,entropy,entropy_quadrant_mean,entropy_quadrant_var,diversity_to_others_mean," +
            "types_present,cond_support_types,cond_impossible_types,undecided_cells,diversity_undecided_to_others_mean,diversity_undecided_type_to_others_mean";

        public static readonly string SummaryHeader =
            "config_id,benchmark,level_id,level_order,runtime_config_id,tileset,size_label,dim_x,dim_y,dim_z,total_cells,solver,solver_variant," +
            "n_maps,attempts_total,attempts_mean,attempt_success_rate,all_solutions_valid,free_cells_mean,playable_free_cells_mean," +
            "js_global_mean,js_global_sd,js_global_ci95_lo,js_global_ci95_hi," +
            "js_conditional_mean,js_conditional_sd,js_conditional_ci95_lo,js_conditional_ci95_hi," +
            "entropy_mean,entropy_sd,entropy_ci95_lo,entropy_ci95_hi," +
            "entropy_quadrant_mean_mean,entropy_quadrant_var_mean," +
            "diversity_mean,diversity_sd,diversity_pairs,compared_cells_mean," +
            "types_present_mean,cond_support_types_mean,cond_impossible_types_mean," +
            "undecided_cells_mean,diversity_undecided_mean,diversity_undecided_sd,compared_undecided_cells_mean," +
            "diversity_undecided_type_mean,diversity_undecided_type_sd";

        public static readonly string ReferenceHeader =
            "config_id,tileset,size_label,level_id,solver,solver_variant,tile_type,p_original,p_conditional_mean,q_empirical_mean";

        /// <summary>Manifiesto del benchmark de calidad (protocolo, tilesets, tipos jugables y matriz).</summary>
        public static string Describe(List<TilesetPair> tilesets, List<ConfigGroup> groups, QualitySettings s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Quality settings");
            sb.AppendLine("maps_per_config_and_solver=" + s.Runs + " (exactamente; cada run reinicia hasta obtener solución)");
            sb.AppendLine("warmup=none (no se descarta ninguna generación; el run 0 se evalúa)");
            sb.AppendLine("timing=none");
            sb.AppendLine("max_attempts_per_run=" + s.MaxAttemptsPerRun + " (tope de seguridad; si se alcanza se registra [ERROR] en log.txt)");
            sb.AppendLine("base_seed=" + s.BaseSeed + " (semillas de la fase 'measured' del benchmark de runtime)");
            sb.AppendLine("save_maps=" + s.SaveMaps);
            sb.AppendLine("evaluated_cells=celdas libres (no preasignadas) de la instancia del intento con éxito");
            sb.AppendLine("tile_identity_js_entropy=tipo de tile jugable (rotaciones agregadas; SOLID/EMPTY/LIMIT excluidas)");
            sb.AppendLine("tile_identity_diversity=id de variante (rotación incluida), infraestructura incluida; diversity = celdas libres en ambos mapas; diversity_undecided = celdas con |D_c|>1 tras la propagación inicial en ambas instancias; diversity_undecided_type = mismas celdas, identidad de tipo (rotaciones agregadas)");
            sb.AppendLine("js=Jensen-Shannon, logaritmo natural, rango [0, ln 2]; entropy=Shannon en nats");
            sb.AppendLine("conditional_reference=Σ_celdas libres w_t/Σ_{s∈D_c} w_s con D_c el dominio arco-consistente tras la propagación inicial (sonda ThisWork independiente)");
            sb.AppendLine();
            string plan = BenchmarkPlan.Describe(tilesets, groups, new BenchmarkSettings(), new PlanOptions());
            int cut = plan.IndexOf("## Tilesets", StringComparison.Ordinal), end = plan.IndexOf("## Configurations", StringComparison.Ordinal);
            if (cut >= 0 && end > cut) sb.Append(plan.Substring(cut, end - cut));
            sb.AppendLine("## Playable types (p_original)");
            foreach (var t in tilesets)
            {
                TypeIndex ti = TypeIndex.Build(t.Plain);
                var parts = new List<string>();
                for (int k = 0; k < ti.Types.Length; k++) parts.Add(ti.Types[k] + "=" + ti.OriginalTarget[k].ToString("0.####", CultureInfo.InvariantCulture));
                sb.AppendLine(t.Name + " K=" + ti.Types.Length + ": " + string.Join(" ", parts.ToArray()));
            }
            sb.AppendLine();
            sb.AppendLine("## Quality configurations (" + groups.Count + ")");
            foreach (var g in groups)
            {
                var names = new List<string>();
                foreach (var f in g.Solvers) { var sv = f(); if (sv.Supports(g.Level)) names.Add(sv.Name + "/" + sv.Variant); }
                sb.AppendLine(QualityConfigId(g) + " | " + QualityBenchmark(g.Level) + " | runtime_config_id=" + g.ConfigId + " | solvers=" + string.Join(";", names.ToArray()));
            }
            return sb.ToString();
        }

        public static IEnumerable<string> Execute(List<ConfigGroup> groups, QualitySettings s, IQualitySink sink)
        {
            int gi = 0;
            foreach (ConfigGroup g in groups)
            {
                gi++;
                CompiledTileset ts = g.Tileset;
                TypeIndex ti = TypeIndex.Build(ts);

                // Sonda de referencia independiente del solver evaluado (mismo AC para todos).
                var probe = new ThisWorkSolver(ThisWorkSelector.Heap);
                probe.Prepare(ts, g.MX, g.MY, g.MZ, g.Level);
                probe.BeginRun(0);

                foreach (var f in g.Solvers)
                {
                    IWFCSolverCore sv = f();
                    if (!sv.Supports(g.Level)) continue;
                    sv.Prepare(ts, g.MX, g.MY, g.MZ, g.Level);

                    var maps = new List<MapRecord>();
                    for (int r = 0; r < s.Runs; r++)
                    {
                        int rr = r;
                        int solverSeed = SingleRun.BenchmarkSolverSeed(s.BaseSeed, g.TilesetName, g.MX, g.MY, g.MZ, "measured", rr);
                        SingleRunResult res = SingleRun.Execute(ts, sv, g.Level, g.MX, g.MY, g.MZ, solverSeed,
                            a => SingleRun.BenchmarkInstanceSeed(s.BaseSeed, g.TilesetName, g.MX, g.MY, g.MZ, "measured", rr, a),
                            s.MaxAttemptsPerRun, false, false, null);
                        if (!res.Solved)
                        {
                            sink.Log("[ERROR] Run sin solución tras " + s.MaxAttemptsPerRun + " intentos: " + QualityConfigId(g) + " " + sv.Name + " run " + r);
                            continue;
                        }
                        var m = new MapRecord
                        {
                            Group = g, Solver = sv.Name, Variant = sv.Variant, RunIndex = r, SolverSeed = solverSeed,
                            Attempts = res.Attempts, Hash = res.SolutionHash, AdjacencyViolations = res.AdjacencyViolations,
                            Solution = res.Solution, Fixed = res.Instance.Fixed,
                            LayerCells = res.Instance.LayerCells, BoundaryCells = res.Instance.BoundaryCells,
                            FixedTileCells = res.Instance.FixedTileCells,
                        };
                        QualityMetrics.Compute(m, ts, ti, res.Instance, probe);
                        maps.Add(m);
                        if (res.AdjacencyViolations > 0)
                            sink.Log("[ERROR] Solución inválida: " + QualityConfigId(g) + " " + sv.Name + " run " + r);
                        yield return string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] {2} {3} mapa {4}/{5}",
                            gi, groups.Count, QualityConfigId(g), sv.Name, r + 1, s.Runs);
                    }

                    // Diversidad: todos los pares de mapas de esta configuración y solver
                    var pair = new List<double>();
                    var cmp = new List<double>();
                    var pairU = new List<double>();
                    var cmpU = new List<double>();
                    var pairUT = new List<double>();
                    var perMapUT = new double[maps.Count];
                    int[] typeIds = QualityMetrics.AllTypeIds(ts);
                    var perMap = new double[maps.Count];
                    var perMapU = new double[maps.Count];
                    var perMapN = new int[maps.Count];
                    var perMapNU = new int[maps.Count];
                    for (int a = 0; a < maps.Count; a++)
                        for (int b = a + 1; b < maps.Count; b++)
                        {
                            int c;
                            double d = QualityMetrics.Hamming(maps[a], maps[b], false, out c);
                            pair.Add(d); cmp.Add(c);
                            perMap[a] += d; perMap[b] += d; perMapN[a]++; perMapN[b]++;
                            double du = QualityMetrics.Hamming(maps[a], maps[b], true, out c);
                            cmpU.Add(c);
                            if (!double.IsNaN(du)) { pairU.Add(du); perMapU[a] += du; perMapU[b] += du; perMapNU[a]++; perMapNU[b]++; }
                            double dut = QualityMetrics.Hamming(maps[a], maps[b], true, typeIds, out c);
                            if (!double.IsNaN(dut)) { pairUT.Add(dut); perMapUT[a] += dut; perMapUT[b] += dut; }
                        }
                    for (int a = 0; a < maps.Count; a++)
                    {
                        maps[a].DiversityToOthers = perMapN[a] > 0 ? perMap[a] / perMapN[a] : double.NaN;
                        maps[a].DiversityUndecidedToOthers = perMapNU[a] > 0 ? perMapU[a] / perMapNU[a] : double.NaN;
                        maps[a].DiversityUndecidedTypeToOthers = perMapNU[a] > 0 ? perMapUT[a] / perMapNU[a] : double.NaN;
                    }

                    foreach (var m in maps) sink.Map(m);
                    sink.Summary(Summarize(g, sv, maps, pair, cmp, pairU, cmpU, pairUT));
                    foreach (string line in ReferenceLines(g, sv, ti, maps)) sink.Reference(line);
                }
            }
        }

        private static string[] Common(ConfigGroup g, string solver, string variant)
        {
            return new[] { QualityConfigId(g), QualityBenchmark(g.Level), QualityId(g.Level), g.Level.Order.ToString(CultureInfo.InvariantCulture),
                g.ConfigId, g.TilesetName, g.SizeLabel, g.MX.ToString(), g.MY.ToString(), g.MZ.ToString(), (g.MX * g.MY * g.MZ).ToString(), solver, variant };
        }

        public static string MapLine(MapRecord m)
        {
            var g = m.Group;
            var vals = new List<object>(Common(g, m.Solver, m.Variant));
            vals.AddRange(new object[] {
                m.RunIndex, m.SolverSeed, m.Attempts, m.Hash, m.AdjacencyViolations == 0 ? 1 : 0, m.AdjacencyViolations,
                m.LayerCells, m.BoundaryCells, m.FixedTileCells, m.FreeCells, m.PlayableCells, m.InfraFreeCells,
                Csv.F(m.JsGlobal), Csv.F(m.JsConditional), Csv.F(m.Entropy), Csv.F(m.EntropyQuadMean), Csv.F(m.EntropyQuadVar), Csv.F(m.DiversityToOthers),
                m.PresentTypes, m.CondSupportTypes, m.CondImpossibleTypes, m.UndecidedCells, Csv.F(m.DiversityUndecidedToOthers), Csv.F(m.DiversityUndecidedTypeToOthers) });
            return Csv.Join(vals.ToArray());
        }

        private static void MeanSdCi(IList<double> v, out double mean, out double sd, out double lo, out double hi)
        {
            int n = v.Count;
            mean = n > 0 ? v.Average() : double.NaN;
            double mm = mean;
            sd = n > 1 ? Math.Sqrt(v.Sum(x => (x - mm) * (x - mm)) / (n - 1)) : double.NaN;
            double half = n > 1 ? QualityMetrics.T975(n - 1) * sd / Math.Sqrt(n) : double.NaN;
            lo = mean - half; hi = mean + half;
        }

        private static string Summarize(ConfigGroup g, IWFCSolverCore sv, List<MapRecord> maps, List<double> pair, List<double> cmp,
            List<double> pairU, List<double> cmpU, List<double> pairUT)
        {
            double um, usd, ul, uh, tm, tsd, tl, th;
            MeanSdCi(pairU, out um, out usd, out ul, out uh);
            MeanSdCi(pairUT, out tm, out tsd, out tl, out th);
            double jm, js, jl, jh, cm, cs, cl, ch, em, es, el, eh, dsd, dm, dl, dh;
            MeanSdCi(maps.Select(m => m.JsGlobal).ToList(), out jm, out js, out jl, out jh);
            MeanSdCi(maps.Select(m => m.JsConditional).ToList(), out cm, out cs, out cl, out ch);
            MeanSdCi(maps.Select(m => m.Entropy).ToList(), out em, out es, out el, out eh);
            MeanSdCi(pair, out dm, out dsd, out dl, out dh);   // IC no se reporta: los pares no son independientes

            var vals = new List<object>(Common(g, sv.Name, sv.Variant));
            vals.AddRange(new object[] {
                maps.Count, maps.Sum(m => m.Attempts), Csv.F(maps.Count > 0 ? maps.Average(m => (double)m.Attempts) : double.NaN),
                Csv.F(maps.Count > 0 ? (double)maps.Count / maps.Sum(m => m.Attempts) : double.NaN),
                maps.All(m => m.AdjacencyViolations == 0) ? 1 : 0,
                Csv.F(Avg(maps, m => m.FreeCells)), Csv.F(Avg(maps, m => m.PlayableCells)),
                Csv.F(jm), Csv.F(js), Csv.F(jl), Csv.F(jh),
                Csv.F(cm), Csv.F(cs), Csv.F(cl), Csv.F(ch),
                Csv.F(em), Csv.F(es), Csv.F(el), Csv.F(eh),
                Csv.F(Avg(maps, m => m.EntropyQuadMean)), Csv.F(Avg(maps, m => m.EntropyQuadVar)),
                Csv.F(dm), Csv.F(dsd), pair.Count, Csv.F(cmp.Count > 0 ? cmp.Average() : double.NaN),
                Csv.F(Avg(maps, m => m.PresentTypes)), Csv.F(Avg(maps, m => m.CondSupportTypes)), Csv.F(Avg(maps, m => m.CondImpossibleTypes)),
                Csv.F(Avg(maps, m => m.UndecidedCells)), Csv.F(um), Csv.F(usd), Csv.F(cmpU.Count > 0 ? cmpU.Average() : double.NaN),
                Csv.F(tm), Csv.F(tsd) });
            return Csv.Join(vals.ToArray());
        }

        private static double Avg(List<MapRecord> maps, Func<MapRecord, double> f) { return maps.Count > 0 ? maps.Average(f) : double.NaN; }

        private static IEnumerable<string> ReferenceLines(ConfigGroup g, IWFCSolverCore sv, TypeIndex ti, List<MapRecord> maps)
        {
            for (int k = 0; k < ti.Types.Length; k++)
            {
                double pc = maps.Count > 0 ? maps.Average(m => m.PCond[k]) : double.NaN;
                double q = maps.Count > 0 ? maps.Average(m => m.Q[k]) : double.NaN;
                yield return Csv.Join(QualityConfigId(g), g.TilesetName, g.SizeLabel, QualityId(g.Level), sv.Name, sv.Variant, ti.Types[k],
                    Csv.F(ti.OriginalTarget[k]), Csv.F(pc), Csv.F(q));
            }
        }

        /// <summary>Tabla de tiles de un tileset compilado (para decodificar los mapas guardados).</summary>
        public static IEnumerable<string> TileTableLines(CompiledTileset ts)
        {
            TypeIndex ti = TypeIndex.Build(ts);
            foreach (int t in Enumerable.Range(0, ts.TileCount))
            {
                int di = ts.DomainIndexOf[t];
                yield return Csv.Join(ts.Name, ts.NegativeRules ? 1 : 0, t, ts.TileNames[t], ts.TileTypes[t], ts.TileNames[ts.BaseTile[t]],
                    ts.RotationSteps[t] * 90, ts.Probability[t], di >= 0 ? Csv.F(ts.DomainWeights[di]) : "", ts.InDomain[t] ? 1 : 0,
                    TypeIndex.IsInfra(ts, t) ? 1 : 0, ti.TypeOfTile[t] >= 0 ? ti.Types[ti.TypeOfTile[t]] : "");
            }
        }

        public static readonly string TileTableHeader =
            "tileset,negative_rules,tile_id,tile_name,tile_type,base_tile,rotation_deg,probability,sampling_weight,in_domain,infrastructure,playable_type";

        /// <summary>Línea de mapa para el fichero comprimido: celdas libres "id", preasignadas "*id". Índice x + z*MX + y*MX*MZ.</summary>
        public static string MapDataLine(MapRecord m)
        {
            var sb = new StringBuilder();
            sb.Append(QualityConfigId(m.Group)).Append(Csv.Separator).Append(m.Solver).Append(Csv.Separator)
              .Append(m.RunIndex).Append(Csv.Separator).Append(m.Hash).Append(Csv.Separator);
            for (int i = 0; i < m.Solution.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                if (m.Fixed[i] >= 0) sb.Append('*');
                sb.Append(m.Solution[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Comprobación opcional: compara el solution_hash de cada mapa de calidad
    /// con el del run "measured" equivalente de un runs.csv del benchmark de
    /// runtime (mismo config_id de runtime, solver y run_index). Acepta ambos
    /// formatos de CSV (',' o ';'). Solo lectura; no altera ningún resultado.
    /// </summary>
    public sealed class RuntimeHashCheck
    {
        private readonly Dictionary<string, string> runtime = new Dictionary<string, string>();
        public int Compared, Equal, Missing;
        public readonly List<string> Mismatches = new List<string>();
        public readonly string Source;

        public RuntimeHashCheck(string runsCsvPath)
        {
            Source = runsCsvPath;
            string[] lines = File.ReadAllLines(runsCsvPath);
            if (lines.Length == 0) throw new InvalidDataException("runs.csv vacío");
            char sep = lines[0].Contains(";") ? ';' : ',';
            string[] h = lines[0].Split(sep);
            int iCfg = Array.IndexOf(h, "config_id"), iSv = Array.IndexOf(h, "solver"), iPh = Array.IndexOf(h, "phase"),
                iRun = Array.IndexOf(h, "run_index"), iHash = Array.IndexOf(h, "solution_hash");
            if (iCfg < 0 || iSv < 0 || iPh < 0 || iRun < 0 || iHash < 0)
                throw new InvalidDataException("runs.csv sin las columnas config_id/solver/phase/run_index/solution_hash");
            for (int k = 1; k < lines.Length; k++)
            {
                string[] c = lines[k].Split(sep);
                if (c.Length <= iHash || c[iPh] != "measured") continue;
                runtime[c[iCfg] + "|" + c[iSv] + "|" + c[iRun]] = c[iHash];
            }
        }

        public int RuntimeRuns { get { return runtime.Count; } }

        public void Check(MapRecord m)
        {
            string key = m.Group.ConfigId + "|" + m.Solver + "|" + m.RunIndex;
            string h;
            if (!runtime.TryGetValue(key, out h) || string.IsNullOrEmpty(h)) { Missing++; return; }
            Compared++;
            if (h == m.Hash) Equal++;
            else if (Mismatches.Count < 50) Mismatches.Add(key + " runtime=" + h + " quality=" + m.Hash);
        }

        public string Report()
        {
            return "runtime_hash_check source=" + Source + " compared=" + Compared + " equal=" + Equal +
                   " different=" + (Compared - Equal) + " not_in_runtime=" + Missing +
                   (Mismatches.Count > 0 ? "\n  " + string.Join("\n  ", Mismatches.ToArray()) : "");
        }
    }

    /// <summary>Escribe los ficheros del benchmark de calidad en una carpeta nueva.</summary>
    public sealed class QualityCsvSink : IQualitySink, IDisposable
    {
        private readonly StreamWriter maps, summary, reference, log;
        private readonly StreamWriter mapData;
        private readonly GZipStream mapGz;
        public readonly string Folder;
        public Action<string> Echo;

        public QualityCsvSink(string folder, bool saveMaps)
        {
            Folder = folder;
            Directory.CreateDirectory(folder);
            var enc = new UTF8Encoding(false);
            maps = new StreamWriter(Path.Combine(folder, "quality_maps.csv"), false, enc);
            summary = new StreamWriter(Path.Combine(folder, "quality_summary.csv"), false, enc);
            reference = new StreamWriter(Path.Combine(folder, "quality_reference.csv"), false, enc);
            log = new StreamWriter(Path.Combine(folder, "log.txt"), false, enc);
            maps.WriteLine(Csv.Header(QualityEngine.MapHeader));
            summary.WriteLine(Csv.Header(QualityEngine.SummaryHeader));
            reference.WriteLine(Csv.Header(QualityEngine.ReferenceHeader));
            if (saveMaps)
            {
                mapGz = new GZipStream(File.Create(Path.Combine(folder, "quality_maps_data.csv.gz")), CompressionMode.Compress);
                mapData = new StreamWriter(mapGz, enc);
                mapData.WriteLine(Csv.Header("config_id,solver,run_index,solution_hash,tiles"));
            }
        }

        public void WriteTileTable(IEnumerable<CompiledTileset> tilesets)
        {
            using (var w = new StreamWriter(Path.Combine(Folder, "quality_tiles.csv"), false, new UTF8Encoding(false)))
            {
                w.WriteLine(Csv.Header(QualityEngine.TileTableHeader));
                foreach (var ts in tilesets)
                    foreach (string l in QualityEngine.TileTableLines(ts)) w.WriteLine(l);
            }
        }

        public void Map(MapRecord r)
        {
            maps.WriteLine(QualityEngine.MapLine(r));
            maps.Flush();
            if (mapData != null) mapData.WriteLine(QualityEngine.MapDataLine(r));
        }
        public void Summary(string line) { summary.WriteLine(line); summary.Flush(); }
        public void Reference(string line) { reference.WriteLine(line); reference.Flush(); }
        public void Log(string m) { log.WriteLine(m); log.Flush(); if (Echo != null) Echo(m); }

        public void Dispose()
        {
            maps.Dispose(); summary.Dispose(); reference.Dispose(); log.Dispose();
            if (mapData != null) mapData.Dispose();   // cierra también el GZipStream
        }
    }
}
