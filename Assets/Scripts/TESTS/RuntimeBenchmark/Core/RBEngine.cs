// ============================================================================
// RBEngine.cs  (motor del benchmark de coste computacional)
//
// ÚNICO lugar donde se mide el tiempo. Protocolo por configuración
// (benchmark × nivel × tileset × tamaño) y solver:
//
//   1. Prepare(): construcción del modelo/propagador ........ FUERA
//   2. W runs de calentamiento (phase = warmup) ............. registrados, excluidos del resumen
//   3. R runs medidos (phase = measured), intercalando solvers con orden rotado.
//      Cada run = reinicios hasta éxito (máx. MaxAttemptsPerRun):
//        a. InstanceBuilder.Build (posiciones de tiles fijas) ... FUERA
//        b. solver.SetupAttempt (topología/máscara) .............. FUERA
//        c. GC.Collect (opcional) ................................ FUERA
//        d. solver.InitAttempt ................................... t_init   (DENTRO)
//        e. diagnóstico de celdas indecisas + GC ................. FUERA
//        f. solver.Search ........................................ t_search (DENTRO)
//        g. lectura y validación de la solución .................. FUERA
//      t_solve(run) = Σ_intentos (t_init + t_search)
//
// Semillas: solver_seed depende de (tileset, tamaño, fase, run) y
// instance_seed de (tileset, tamaño, fase, run, intento). NO dependen del
// solver ni del nivel: todos los solvers y niveles reciben la misma secuencia
// de semillas (A y B0 son, por tanto, réplicas exactas).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WFCRuntimeBenchmark
{
    public sealed class BenchmarkSettings
    {
        public int Runs = 50;
        public int WarmupRuns = 3;
        public int MaxAttemptsPerRun = 1000;
        public ulong BaseSeed = 20261007UL;
        public bool GcBeforeTimedPhases = true;
        public bool MeasureUndecidedAfterInit = true;
        public bool ValidateSolutions = true;
        public bool InterleaveSolvers = true;
    }

    public sealed class ConfigGroup
    {
        public LevelSpec Level;
        public string TilesetName;
        public CompiledTileset TilesetPlain;       // sin negative rules
        public CompiledTileset TilesetNegative;    // con negative rules
        public int MX, MY, MZ;
        public List<Func<IWFCSolverCore>> Solvers = new List<Func<IWFCSolverCore>>();

        public CompiledTileset Tileset { get { return Level.NegativeRules ? TilesetNegative : TilesetPlain; } }
        public string SizeLabel { get { return MX + "x" + MZ + "x" + MY; } }
        public string ConfigId { get { return Level.Id + "/" + TilesetName.ToUpperInvariant() + "/" + SizeLabel; } }
    }

    // ── Registros ───────────────────────────────────────────────────────

    public sealed class AttemptRecord
    {
        public string ConfigId, Benchmark, LevelId, Tileset, SizeLabel, Solver, Variant, Phase, FailPhase;
        public int LevelOrder, MX, MY, MZ, TotalCells, RunIndex, AttemptIndex, SolverSeed, InstanceSeed;
        public int FreeCells, CspCells, LayerCells, BoundaryCells, FixedTileCells, FixedTilesRequested, UndecidedAfterInit;
        public bool InitOk, Success;
        public long Decisions;
        public double TInitMs, TSearchMs;

        public static readonly string Header =
            "config_id,benchmark,level_id,level_order,tileset,size_label,dim_x,dim_y,dim_z,total_cells,solver,solver_variant,phase,run_index,attempt_index," +
            "solver_seed,instance_seed,effective_free_cells,solver_csp_cells,layer_cells,boundary_cells,fixed_tile_cells,fixed_tiles_requested," +
            "undecided_after_init,init_ok,success,fail_phase,decisions,t_init_ms,t_search_ms,t_attempt_ms";

        public string ToCsv()
        {
            return Csv.Join(ConfigId, Benchmark, LevelId, LevelOrder, Tileset, SizeLabel, MX, MY, MZ, TotalCells, Solver, Variant, Phase, RunIndex, AttemptIndex,
                SolverSeed, InstanceSeed, FreeCells, CspCells, LayerCells, BoundaryCells, FixedTileCells, FixedTilesRequested,
                UndecidedAfterInit, InitOk ? 1 : 0, Success ? 1 : 0, FailPhase, Decisions, Csv.F(TInitMs), Csv.F(TSearchMs), Csv.F(TInitMs + TSearchMs));
        }
    }

    public sealed class RunRecord
    {
        public string ConfigId, Benchmark, LevelId, Tileset, SizeLabel, Solver, Variant, Phase;
        public int LevelOrder, MX, MY, MZ, TotalCells, RunIndex, OrderPosition, SolverSeed;
        public int Attempts, Contradictions, ContradictionsInit, ContradictionsSearch;
        public bool Solved, FirstAttemptSuccess, SolutionValid;
        public int FreeCells, CspCells, FixedTileCells, LayerCells, BoundaryCells, UndecidedAfterInit, AdjacencyViolations;
        public long Decisions;
        public double TSolveMs, TInitMs, TSearchMs, TSuccessAttemptMs, TFailedAttemptsMs;
        public string SolutionHash = "";

        public double TimePerFreeCellUs { get { return FreeCells > 0 ? TSolveMs * 1000.0 / FreeCells : double.NaN; } }
        public double SuccessAttemptPerFreeCellUs { get { return FreeCells > 0 && Solved ? TSuccessAttemptMs * 1000.0 / FreeCells : double.NaN; } }

        public static readonly string Header =
            "config_id,benchmark,level_id,level_order,tileset,size_label,dim_x,dim_y,dim_z,total_cells,solver,solver_variant,phase,run_index,order_position,solver_seed," +
            "attempts,contradictions,contradictions_init,contradictions_search,solved,first_attempt_success," +
            "effective_free_cells,solver_csp_cells,layer_cells,boundary_cells,fixed_tile_cells,undecided_after_init,decisions," +
            "t_solve_total_ms,t_init_total_ms,t_search_total_ms,t_success_attempt_ms,t_failed_attempts_ms," +
            "time_per_free_cell_us,success_attempt_per_free_cell_us,solution_valid,adjacency_violations,solution_hash";

        public string ToCsv()
        {
            return Csv.Join(ConfigId, Benchmark, LevelId, LevelOrder, Tileset, SizeLabel, MX, MY, MZ, TotalCells, Solver, Variant, Phase, RunIndex, OrderPosition, SolverSeed,
                Attempts, Contradictions, ContradictionsInit, ContradictionsSearch, Solved ? 1 : 0, FirstAttemptSuccess ? 1 : 0,
                FreeCells, CspCells, LayerCells, BoundaryCells, FixedTileCells, UndecidedAfterInit, Decisions,
                Csv.F(TSolveMs), Csv.F(TInitMs), Csv.F(TSearchMs), Csv.F(TSuccessAttemptMs), Csv.F(TFailedAttemptsMs),
                Csv.F(TimePerFreeCellUs), Csv.F(SuccessAttemptPerFreeCellUs), SolutionValid ? 1 : 0, AdjacencyViolations, SolutionHash);
        }
    }

    public interface IResultSink
    {
        void Attempt(AttemptRecord r);
        void Run(RunRecord r);
        void Summary(string csvLine);
        void Log(string message);
    }

    // ── Motor ───────────────────────────────────────────────────────────

    public static class RuntimeBenchmarkEngine
    {
        public static readonly string SummaryHeader =
            "config_id,benchmark,level_id,level_order,tileset,size_label,dim_x,dim_y,dim_z,total_cells,solver,solver_variant," +
            "n_runs,runs_solved,n_attempts_total,contradictions_total,contradictions_init,contradictions_search," +
            "success_rate_first_attempt,success_first_ci95_lo,success_first_ci95_hi,success_rate_pooled_attempts," +
            "effective_free_cells,solver_csp_cells,undecided_after_init_mean,decisions_mean," +
            "t_solve_sum_ms,t_solve_mean_ms,t_solve_median_ms,t_solve_q1_ms,t_solve_q3_ms,t_solve_iqr_ms,t_solve_sd_ms,t_solve_min_ms,t_solve_max_ms," +
            "t_success_attempt_mean_ms,t_success_attempt_median_ms,t_init_total_median_ms,t_search_total_median_ms,t_failed_attempt_mean_ms," +
            "time_per_free_cell_mean_us,time_per_free_cell_median_us,success_attempt_per_free_cell_median_us," +
            "warmup_run_times_ms,measured_run0_ms,all_solutions_valid";

        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public static IEnumerable<string> Execute(List<ConfigGroup> groups, BenchmarkSettings s, IResultSink sink)
        {
            int gi = 0;
            foreach (ConfigGroup g in groups)
            {
                gi++;
                CompiledTileset ts = g.Tileset;
                var solvers = new List<IWFCSolverCore>();
                foreach (var f in g.Solvers)
                {
                    IWFCSolverCore sv = f();
                    if (!sv.Supports(g.Level)) { sink.Log("[skip] " + sv.Name + "/" + sv.Variant + " no soporta " + g.Level.Id); continue; }
                    long p0 = Stopwatch.GetTimestamp();
                    sv.Prepare(ts, g.MX, g.MY, g.MZ, g.Level);
                    long p1 = Stopwatch.GetTimestamp();
                    sink.Log(string.Format(CultureInfo.InvariantCulture, "[prepare] {0} {1}/{2}: {3:F2} ms (fuera del cronómetro)",
                        g.ConfigId, sv.Name, sv.Variant, Ms(p1 - p0)));
                    solvers.Add(sv);
                }

                var measured = new Dictionary<IWFCSolverCore, List<RunRecord>>();
                var warm = new Dictionary<IWFCSolverCore, List<RunRecord>>();
                foreach (var sv in solvers) { measured[sv] = new List<RunRecord>(); warm[sv] = new List<RunRecord>(); }

                for (int phaseIdx = 0; phaseIdx < 2; phaseIdx++)
                {
                    string phase = phaseIdx == 0 ? "warmup" : "measured";
                    int n = phaseIdx == 0 ? s.WarmupRuns : s.Runs;
                    for (int r = 0; r < n; r++)
                    {
                        for (int k = 0; k < solvers.Count; k++)
                        {
                            int pos = s.InterleaveSolvers ? (k + r) % solvers.Count : k;
                            IWFCSolverCore sv = solvers[pos];
                            RunRecord rr = ExecuteRun(g, ts, sv, phase, r, k, s, sink);
                            (phaseIdx == 0 ? warm : measured)[sv].Add(rr);
                            sink.Run(rr);
                        }
                        yield return string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] {2} {3} run {4}/{5}",
                            gi, groups.Count, g.ConfigId, phase, r + 1, n);
                    }
                }

                foreach (var sv in solvers)
                {
                    string line = Summarize(g, sv, measured[sv], warm[sv]);
                    sink.Summary(line);
                }
            }
        }

        private static RunRecord ExecuteRun(ConfigGroup g, CompiledTileset ts, IWFCSolverCore sv, string phase, int r, int orderPos,
                                            BenchmarkSettings s, IResultSink sink)
        {
            string dims = g.MX + "x" + g.MY + "x" + g.MZ;
            int solverSeed = Seeds.Derive(s.BaseSeed, "solver|" + g.TilesetName + "|" + dims + "|" + phase + "|" + r);
            sv.BeginRun(solverSeed);

            var rr = new RunRecord
            {
                ConfigId = g.ConfigId, Benchmark = g.Level.Benchmark, LevelId = g.Level.Id, LevelOrder = g.Level.Order,
                Tileset = g.TilesetName, SizeLabel = g.SizeLabel, MX = g.MX, MY = g.MY, MZ = g.MZ, TotalCells = g.MX * g.MY * g.MZ,
                Solver = sv.Name, Variant = sv.Variant, Phase = phase, RunIndex = r, OrderPosition = orderPos, SolverSeed = solverSeed,
            };
            int[] solution = new int[rr.TotalCells];

            for (int a = 0; a < s.MaxAttemptsPerRun; a++)
            {
                int instSeed = Seeds.Derive(s.BaseSeed, "instance|" + g.TilesetName + "|" + dims + "|" + phase + "|" + r + "|" + a);
                ProblemInstance inst = InstanceBuilder.Build(ts, g.MX, g.MY, g.MZ, g.Level, new Random(instSeed));
                sv.SetupAttempt(inst);

                if (s.GcBeforeTimedPhases) Collect();
                long t0 = Stopwatch.GetTimestamp();
                bool initOk = sv.InitAttempt();
                long t1 = Stopwatch.GetTimestamp();

                int undecided = -1;
                if (initOk && s.MeasureUndecidedAfterInit) undecided = sv.UndecidedAfterInit();

                bool ok = false;
                long t2 = 0, t3 = 0;
                if (initOk)
                {
                    if (s.GcBeforeTimedPhases) Collect();
                    t2 = Stopwatch.GetTimestamp();
                    ok = sv.Search();
                    t3 = Stopwatch.GetTimestamp();
                }

                var ar = new AttemptRecord
                {
                    ConfigId = rr.ConfigId, Benchmark = rr.Benchmark, LevelId = rr.LevelId, LevelOrder = rr.LevelOrder,
                    Tileset = rr.Tileset, SizeLabel = rr.SizeLabel, MX = g.MX, MY = g.MY, MZ = g.MZ, TotalCells = rr.TotalCells,
                    Solver = sv.Name, Variant = sv.Variant, Phase = phase, RunIndex = r, AttemptIndex = a,
                    SolverSeed = solverSeed, InstanceSeed = instSeed,
                    FreeCells = inst.FreeCells, CspCells = sv.SolverCspCells, LayerCells = inst.LayerCells,
                    BoundaryCells = inst.BoundaryCells, FixedTileCells = inst.FixedTileCells, FixedTilesRequested = inst.FixedTilesRequested,
                    UndecidedAfterInit = undecided, InitOk = initOk, Success = ok,
                    FailPhase = ok ? "none" : (initOk ? "search" : "init"),
                    Decisions = initOk ? sv.Decisions : 0,
                    TInitMs = Ms(t1 - t0), TSearchMs = initOk ? Ms(t3 - t2) : 0.0,
                };
                sink.Attempt(ar);

                double tAttempt = ar.TInitMs + ar.TSearchMs;
                rr.Attempts++;
                rr.TSolveMs += tAttempt;
                rr.TInitMs += ar.TInitMs;
                rr.TSearchMs += ar.TSearchMs;
                rr.FreeCells = inst.FreeCells; rr.CspCells = sv.SolverCspCells;
                rr.LayerCells = inst.LayerCells; rr.BoundaryCells = inst.BoundaryCells; rr.FixedTileCells = inst.FixedTileCells;

                if (ok)
                {
                    rr.Solved = true;
                    rr.FirstAttemptSuccess = a == 0;
                    rr.TSuccessAttemptMs = tAttempt;
                    rr.UndecidedAfterInit = undecided;
                    rr.Decisions = ar.Decisions;
                    sv.ReadSolution(solution);
                    rr.SolutionHash = Hash(solution);
                    if (s.ValidateSolutions)
                    {
                        rr.AdjacencyViolations = Validate(ts, inst, solution);
                        rr.SolutionValid = rr.AdjacencyViolations == 0;
                        if (!rr.SolutionValid)
                            sink.Log("[ERROR] Solución inválida: " + rr.ConfigId + " " + sv.Name + " run " + r + " violaciones=" + rr.AdjacencyViolations);
                    }
                    else rr.SolutionValid = true;
                    break;
                }

                rr.Contradictions++;
                if (initOk) rr.ContradictionsSearch++; else rr.ContradictionsInit++;
                rr.TFailedAttemptsMs += tAttempt;
            }

            if (!rr.Solved)
                sink.Log("[WARN] Run sin solución tras " + s.MaxAttemptsPerRun + " intentos: " + rr.ConfigId + " " + sv.Name + " run " + r);
            return rr;
        }

        public static string Hash(int[] sol)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < sol.Length; i++) { h ^= (ulong)(uint)sol[i]; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        /// <summary>
        /// Comprueba la solución (fuera del cronómetro): celdas preasignadas
        /// intactas, celdas libres con tile del dominio y TODAS las adyacencias
        /// (incluidas las de pares preasignados) admitidas por el modelo.
        /// </summary>
        public static int Validate(CompiledTileset ts, ProblemInstance inst, int[] sol)
        {
            int v = 0;
            for (int i = 0; i < inst.N; i++)
            {
                int a = sol[i];
                if (inst.Fixed[i] >= 0) { if (a != inst.Fixed[i]) v++; continue; }
                if (a < 0 || !ts.InDomain[a]) { v++; continue; }
            }
            for (int y = 0; y < inst.MY; y++)
                for (int z = 0; z < inst.MZ; z++)
                    for (int x = 0; x < inst.MX; x++)
                    {
                        int i = inst.Index(x, y, z);
                        int a = sol[i];
                        if (a < 0) continue;
                        for (int d = 0; d < 6; d++)
                        {
                            int x2 = x + Dir.DX[d], y2 = y + Dir.DY[d], z2 = z + Dir.DZ[d];
                            if (x2 < 0 || x2 >= inst.MX || y2 < 0 || y2 >= inst.MY || z2 < 0 || z2 >= inst.MZ) continue;
                            int j = inst.Index(x2, y2, z2);
                            int b = sol[j];
                            if (b < 0) continue;
                            if (!ts.IsAllowed(d, a, b)) v++;
                        }
                    }
            return v;
        }

        // ── Resumen ─────────────────────────────────────────────────────

        private static string Summarize(ConfigGroup g, IWFCSolverCore sv, List<RunRecord> runs, List<RunRecord> warm)
        {
            int n = runs.Count;
            int solved = runs.Count(x => x.Solved);
            int attempts = runs.Sum(x => x.Attempts);
            int contr = runs.Sum(x => x.Contradictions);
            int k1 = runs.Count(x => x.FirstAttemptSuccess);
            double p1 = n > 0 ? (double)k1 / n : double.NaN;
            double lo, hi; Wilson(k1, n, out lo, out hi);
            double pooled = attempts > 0 ? (double)solved / attempts : double.NaN;

            var tSolve = runs.Select(x => x.TSolveMs).ToList();
            var tSucc = runs.Where(x => x.Solved).Select(x => x.TSuccessAttemptMs).ToList();
            var tInit = runs.Select(x => x.TInitMs).ToList();
            var tSearch = runs.Select(x => x.TSearchMs).ToList();
            var perCell = runs.Select(x => x.TimePerFreeCellUs).ToList();
            var perCellSucc = runs.Where(x => x.Solved).Select(x => x.SuccessAttemptPerFreeCellUs).ToList();
            double failedMean = contr > 0 ? runs.Sum(x => x.TFailedAttemptsMs) / contr : double.NaN;

            double q1 = Stats.Quantile(tSolve, 0.25), q3 = Stats.Quantile(tSolve, 0.75);
            string warmTimes = string.Join("|", warm.Select(x => Csv.F(x.TSolveMs)).ToArray());

            RunRecord f = runs.Count > 0 ? runs[0] : null;
            return Csv.Join(g.ConfigId, g.Level.Benchmark, g.Level.Id, g.Level.Order, g.TilesetName, g.SizeLabel, g.MX, g.MY, g.MZ, g.MX * g.MY * g.MZ,
                sv.Name, sv.Variant,
                n, solved, attempts, contr, runs.Sum(x => x.ContradictionsInit), runs.Sum(x => x.ContradictionsSearch),
                Csv.F(p1), Csv.F(lo), Csv.F(hi), Csv.F(pooled),
                Csv.F(runs.Average(x => (double)x.FreeCells)), Csv.F(runs.Average(x => (double)x.CspCells)),
                Csv.F(runs.Where(x => x.Solved && x.UndecidedAfterInit >= 0).Select(x => (double)x.UndecidedAfterInit).DefaultIfEmpty(double.NaN).Average()),
                Csv.F(runs.Where(x => x.Solved).Select(x => (double)x.Decisions).DefaultIfEmpty(double.NaN).Average()),
                Csv.F(tSolve.Sum()), Csv.F(Stats.Mean(tSolve)), Csv.F(Stats.Quantile(tSolve, 0.5)), Csv.F(q1), Csv.F(q3), Csv.F(q3 - q1),
                Csv.F(Stats.Sd(tSolve)), Csv.F(tSolve.Count > 0 ? tSolve.Min() : double.NaN), Csv.F(tSolve.Count > 0 ? tSolve.Max() : double.NaN),
                Csv.F(Stats.Mean(tSucc)), Csv.F(Stats.Quantile(tSucc, 0.5)),
                Csv.F(Stats.Quantile(tInit, 0.5)), Csv.F(Stats.Quantile(tSearch, 0.5)), Csv.F(failedMean),
                Csv.F(Stats.Mean(perCell)), Csv.F(Stats.Quantile(perCell, 0.5)), Csv.F(Stats.Quantile(perCellSucc, 0.5)),
                warmTimes, f != null ? Csv.F(f.TSolveMs) : "", runs.All(x => !x.Solved || x.SolutionValid) ? 1 : 0);
        }

        private static void Wilson(int k, int n, out double lo, out double hi)
        {
            if (n == 0) { lo = hi = double.NaN; return; }
            const double z = 1.959963984540054;
            double p = (double)k / n, z2 = z * z;
            double den = 1 + z2 / n;
            double c = (p + z2 / (2 * n)) / den;
            double h = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / den;
            lo = Math.Max(0, c - h); hi = Math.Min(1, c + h);
        }
    }

    public static class Stats
    {
        public static double Mean(List<double> v) { return v.Count == 0 ? double.NaN : v.Average(); }

        public static double Sd(List<double> v)
        {
            if (v.Count < 2) return double.NaN;
            double m = v.Average();
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
        }

        /// <summary>Cuantil tipo 7 (R por defecto, numpy 'linear').</summary>
        public static double Quantile(List<double> v, double q)
        {
            if (v.Count == 0) return double.NaN;
            var s = v.OrderBy(x => x).ToList();
            double h = (s.Count - 1) * q;
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(lo + 1, s.Count - 1);
            return s[lo] + (h - lo) * (s[hi] - s[lo]);
        }
    }

    /// <summary>
    /// Formato de los CSV. Por defecto estándar (separador ',' y decimal '.').
    /// Para Excel con configuración regional española: separador ';' y decimal ','
    /// (RuntimeBenchmarkRunner.csvFormat = ExcelSpanish).
    /// </summary>
    public static class Csv
    {
        public static char Separator = ',';
        public static bool DecimalComma = false;

        public static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            string s = v.ToString("0.######", CultureInfo.InvariantCulture);
            return DecimalComma ? s.Replace('.', ',') : s;
        }

        /// <summary>Cabecera definida con comas → separador configurado.</summary>
        public static string Header(string commaSeparated)
        {
            return commaSeparated.Replace(',', Separator);
        }

        public static string Join(params object[] values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(Separator);
                object o = values[i];
                string s = o is IFormattable ? ((IFormattable)o).ToString(null, CultureInfo.InvariantCulture) : (o == null ? "" : o.ToString());
                if (s.IndexOf(Separator) >= 0 || s.IndexOf('"') >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(s);
            }
            return sb.ToString();
        }
    }

    /// <summary>Escribe attempts.csv, runs.csv, summary.csv y log.txt en una carpeta nueva.</summary>
    public sealed class CsvResultSink : IResultSink, IDisposable
    {
        private readonly StreamWriter attempts, runs, summary, log;
        public readonly string Folder;
        public Action<string> Echo;

        public CsvResultSink(string folder)
        {
            Folder = folder;
            Directory.CreateDirectory(folder);
            var enc = new UTF8Encoding(false);
            attempts = new StreamWriter(Path.Combine(folder, "attempts.csv"), false, enc);
            runs = new StreamWriter(Path.Combine(folder, "runs.csv"), false, enc);
            summary = new StreamWriter(Path.Combine(folder, "summary.csv"), false, enc);
            log = new StreamWriter(Path.Combine(folder, "log.txt"), false, enc);
            attempts.WriteLine(Csv.Header(AttemptRecord.Header));
            runs.WriteLine(Csv.Header(RunRecord.Header));
            summary.WriteLine(Csv.Header(RuntimeBenchmarkEngine.SummaryHeader));
        }

        public void Attempt(AttemptRecord r) { attempts.WriteLine(r.ToCsv()); }
        public void Run(RunRecord r) { runs.WriteLine(r.ToCsv()); runs.Flush(); attempts.Flush(); }
        public void Summary(string line) { summary.WriteLine(line); summary.Flush(); }
        public void Log(string m) { log.WriteLine(m); log.Flush(); if (Echo != null) Echo(m); }

        public void Dispose()
        {
            attempts.Dispose(); runs.Dispose(); summary.Dispose(); log.Dispose();
        }
    }
}
