// ============================================================================
// RBSingleRun.cs
//
// Ejecución de UN run (reinicios hasta solución) fuera del motor de
// benchmark, para uso del solver publicado (MyWFC). Reproduce exactamente el
// protocolo de RuntimeBenchmarkEngine.ExecuteRun: mismas semillas, misma
// generación de instancias, mismas fases y misma ventana temporal
// (t_init + t_search por intento, sumado sobre los intentos). Con las mismas
// semillas, el run r de MyWFC produce la misma solución que el run r
// "measured" del benchmark (verificado en el arnés de pruebas).
// ============================================================================

using System;
using System.Diagnostics;

namespace WFCRuntimeBenchmark
{
    public sealed class SingleRunResult
    {
        public bool Solved;
        public int Attempts, Contradictions, ContradictionsInit, ContradictionsSearch;
        public int SolverSeed;
        public double TSolveMs, TInitMs, TSearchMs, TSuccessAttemptMs;
        public int UndecidedAfterInit = -1;
        public long Decisions;
        public ProblemInstance Instance;     // instancia del intento con éxito (o del último)
        public int[] Solution;               // id de tile por celda (x + z*MX + y*MX*MZ); null si no se resolvió
        public int AdjacencyViolations;
        public string SolutionHash = "";
    }

    public static class SingleRun
    {
        /// <summary>Semilla del solver con la misma derivación que el benchmark.</summary>
        public static int BenchmarkSolverSeed(ulong baseSeed, string tileset, int mx, int my, int mz, string phase, int run)
        {
            return Seeds.Derive(baseSeed, "solver|" + tileset + "|" + mx + "x" + my + "x" + mz + "|" + phase + "|" + run);
        }

        /// <summary>Semilla de la instancia (posiciones de tiles fijas) con la misma derivación que el benchmark.</summary>
        public static int BenchmarkInstanceSeed(ulong baseSeed, string tileset, int mx, int my, int mz, string phase, int run, int attempt)
        {
            return Seeds.Derive(baseSeed, "instance|" + tileset + "|" + mx + "x" + my + "x" + mz + "|" + phase + "|" + run + "|" + attempt);
        }

        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <param name="instanceSeed">Semilla de la instancia para el intento a.</param>
        /// <param name="onAttemptFailed">Se invoca (fuera del cronómetro) tras cada intento fallido.</param>
        public static SingleRunResult Execute(CompiledTileset ts, IWFCSolverCore solver, LevelSpec level, int mx, int my, int mz,
            int solverSeed, Func<int, int> instanceSeed, int maxAttempts, bool gcBeforeTimedPhases, bool measureUndecided,
            Action onAttemptFailed)
        {
            var r = new SingleRunResult { SolverSeed = solverSeed };
            solver.BeginRun(solverSeed);

            for (int a = 0; a < maxAttempts; a++)
            {
                ProblemInstance inst = InstanceBuilder.Build(ts, mx, my, mz, level, new Random(instanceSeed(a)));
                solver.SetupAttempt(inst);
                r.Instance = inst;

                if (gcBeforeTimedPhases) Collect();
                long t0 = Stopwatch.GetTimestamp();
                bool initOk = solver.InitAttempt();
                long t1 = Stopwatch.GetTimestamp();

                int undecided = -1;
                if (initOk && measureUndecided) undecided = solver.UndecidedAfterInit();

                bool ok = false;
                long t2 = 0, t3 = 0;
                if (initOk)
                {
                    if (gcBeforeTimedPhases) Collect();
                    t2 = Stopwatch.GetTimestamp();
                    ok = solver.Search();
                    t3 = Stopwatch.GetTimestamp();
                }

                double tInit = Ms(t1 - t0), tSearch = initOk ? Ms(t3 - t2) : 0.0;
                r.Attempts++;
                r.TInitMs += tInit;
                r.TSearchMs += tSearch;
                r.TSolveMs += tInit + tSearch;

                if (ok)
                {
                    r.Solved = true;
                    r.TSuccessAttemptMs = tInit + tSearch;
                    r.UndecidedAfterInit = undecided;
                    r.Decisions = solver.Decisions;
                    r.Solution = new int[inst.N];
                    solver.ReadSolution(r.Solution);
                    r.SolutionHash = RuntimeBenchmarkEngine.Hash(r.Solution);
                    r.AdjacencyViolations = RuntimeBenchmarkEngine.Validate(ts, inst, r.Solution);
                    return r;
                }

                r.Contradictions++;
                if (initOk) r.ContradictionsSearch++; else r.ContradictionsInit++;
                if (onAttemptFailed != null) onAttemptFailed();
            }
            return r;
        }
    }
}
