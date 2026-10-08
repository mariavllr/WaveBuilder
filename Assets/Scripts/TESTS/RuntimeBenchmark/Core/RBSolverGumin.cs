// ============================================================================
// RBSolverGumin.cs  (Gumin, Simple Tiled Model, extensión 3D)
//
// Port fiel de Model.cs de mxgmn/WaveFunctionCollapse (Init, Clear, Run,
// NextUnobservedNode con heurística Entropy, Observe, Propagate, Ban),
// extendido a 6 direcciones ortogonales y volumen no periódico.
//
// Correcciones respecto al GuminWFC.cs usado en el artículo (lo hacían
// menos fiel al original y lo penalizaban en tiempo):
//   1. NextUnobservedNode ignora las celdas con remainingValues <= 1 y solo
//      sortea ruido cuando entropy <= min (como el original). El port previo
//      "colapsaba" también las celdas ya decididas, con un barrido O(N)
//      adicional por cada una.
//   2. Clear() banea las tiles sin ningún vecino admisible en una dirección
//      con vecino existente y propaga (como el original).
//   3. Memoria reservada una sola vez (Init); cada intento solo hace Clear().
//
// Desviación deliberada (común a los tres solvers): la contradicción se
// detecta explícitamente (dominio vacío) y detiene la propagación. El
// original devuelve sumsOfOnes[0] > 0 al final de Propagate(), que no
// detecta contradicciones que no alcanzan la celda 0.
//
// Gumin no dispone de celdas preasignadas: solo se usa en el Benchmark A.
// ============================================================================

using System;

namespace WFCRuntimeBenchmark
{
    public sealed class GuminSolver : IWFCSolverCore
    {
        public string Name { get { return "Gumin"; } }
        public string Variant { get { return "simple-tiled-3d"; } }
        public bool Supports(LevelSpec level) { return !level.AnyPreassigned; }

        private CompiledTileset ts;
        private int T, MX, MY, MZ, N;
        private int[][] propagator;
        private double[] weights, weightLogWeights, distribution;
        private double sumOfWeights, sumOfWeightLogWeights, startingEntropy;

        private bool[] wave;
        private int[] compatible;
        private int[] observed;
        private int[] sumsOfOnes;
        private double[] sumsOfWeights, sumsOfWeightLogWeights, entropies;
        private int[] stackCell, stackTile;
        private int stacksize;
        private bool contradiction;

        private Random random;
        private long decisions;

        public int SolverCspCells { get { return N; } }
        public long Decisions { get { return decisions; } }

        // ── Init (fuera del cronómetro) ───────────────────────────────────

        public void Prepare(CompiledTileset ts, int mx, int my, int mz, LevelSpec level)
        {
            if (!Supports(level)) throw new InvalidOperationException("Gumin no soporta celdas preasignadas.");
            this.ts = ts;
            T = ts.T; MX = mx; MY = my; MZ = mz; N = mx * my * mz;
            propagator = ts.DomainPropagator;

            weights = new double[T];
            weightLogWeights = new double[T];
            sumOfWeights = 0; sumOfWeightLogWeights = 0;
            for (int t = 0; t < T; t++)
            {
                weights[t] = ts.DomainWeights[t];
                weightLogWeights[t] = weights[t] * Math.Log(weights[t]);
                sumOfWeights += weights[t];
                sumOfWeightLogWeights += weightLogWeights[t];
            }
            startingEntropy = Math.Log(sumOfWeights) - sumOfWeightLogWeights / sumOfWeights;
            distribution = new double[T];

            wave = new bool[N * T];
            compatible = new int[N * T * 6];
            observed = new int[N];
            sumsOfOnes = new int[N];
            sumsOfWeights = new double[N];
            sumsOfWeightLogWeights = new double[N];
            entropies = new double[N];
            stackCell = new int[N * T];
            stackTile = new int[N * T];
        }

        public void BeginRun(int seed) { random = new Random(seed); }

        public void SetupAttempt(ProblemInstance inst)
        {
            if (inst.PreassignedCells > 0) throw new InvalidOperationException("Gumin no soporta celdas preasignadas.");
        }

        // ── Clear (t_init) ────────────────────────────────────────────────

        public bool InitAttempt()
        {
            stacksize = 0;
            contradiction = false;
            decisions = 0;

            for (int i = 0; i < N; i++)
            {
                for (int t = 0; t < T; t++)
                {
                    wave[i * T + t] = true;
                    int cb = (i * T + t) * 6;
                    for (int d = 0; d < 6; d++) compatible[cb + d] = propagator[Dir.OPP[d] * T + t].Length;
                }
                sumsOfOnes[i] = T;
                sumsOfWeights[i] = sumOfWeights;
                sumsOfWeightLogWeights[i] = sumOfWeightLogWeights;
                entropies[i] = startingEntropy;
                observed[i] = -1;
            }

            // Tiles sin ningún vecino admisible en una dirección donde SÍ hay vecino.
            for (int i = 0; i < N; i++)
            {
                int x = i % MX, z = (i / MX) % MZ, y = i / (MX * MZ);
                for (int t = 0; t < T; t++)
                {
                    bool noNeighbors = false;
                    for (int d = 0; d < 6 && !noNeighbors; d++)
                    {
                        int x2 = x + Dir.DX[d], y2 = y + Dir.DY[d], z2 = z + Dir.DZ[d];
                        bool inside = x2 >= 0 && x2 < MX && y2 >= 0 && y2 < MY && z2 >= 0 && z2 < MZ;
                        if (inside && propagator[d * T + t].Length == 0) noNeighbors = true;
                    }
                    if (noNeighbors && wave[i * T + t]) Ban(i, t);
                }
            }

            if (stacksize > 0) Propagate();
            return !contradiction;
        }

        // ── Run (t_search) ────────────────────────────────────────────────

        public bool Search()
        {
            if (contradiction) return false;
            while (true)
            {
                int node = NextUnobservedNode();
                if (node >= 0)
                {
                    Observe(node);
                    if (!Propagate()) return false;
                }
                else
                {
                    for (int i = 0; i < N; i++)
                        for (int t = 0; t < T; t++)
                            if (wave[i * T + t]) { observed[i] = t; break; }
                    return true;
                }
            }
        }

        private int NextUnobservedNode()
        {
            double min = 1E+4;
            int argmin = -1;
            for (int i = 0; i < N; i++)
            {
                int remainingValues = sumsOfOnes[i];
                double entropy = entropies[i];
                if (remainingValues > 1 && entropy <= min)
                {
                    double noise = 1E-6 * random.NextDouble();
                    if (entropy + noise < min)
                    {
                        min = entropy + noise;
                        argmin = i;
                    }
                }
            }
            return argmin;
        }

        private void Observe(int node)
        {
            decisions++;
            int b = node * T;
            for (int t = 0; t < T; t++) distribution[t] = wave[b + t] ? weights[t] : 0.0;
            int r = RandomIndex(distribution, random.NextDouble());
            for (int t = 0; t < T; t++)
                if (wave[b + t] != (t == r)) Ban(node, t);
        }

        // Extensions.Random(double[] weights, double r) de Gumin
        private static int RandomIndex(double[] w, double r)
        {
            double sum = 0;
            for (int i = 0; i < w.Length; i++) sum += w[i];
            double threshold = r * sum;
            double partialSum = 0;
            for (int i = 0; i < w.Length; i++)
            {
                partialSum += w[i];
                if (partialSum >= threshold) return i;
            }
            return 0;
        }

        private bool Propagate()
        {
            while (stacksize > 0)
            {
                if (contradiction) break;
                stacksize--;
                int i1 = stackCell[stacksize], t1 = stackTile[stacksize];

                int x1 = i1 % MX, z1 = (i1 / MX) % MZ, y1 = i1 / (MX * MZ);
                for (int d = 0; d < 6; d++)
                {
                    int x2 = x1 + Dir.DX[d], y2 = y1 + Dir.DY[d], z2 = z1 + Dir.DZ[d];
                    if (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ) continue;
                    int i2 = x2 + z2 * MX + y2 * MX * MZ;
                    int[] p = propagator[d * T + t1];
                    for (int l = 0; l < p.Length; l++)
                    {
                        int t2 = p[l];
                        int ci = (i2 * T + t2) * 6 + d;
                        compatible[ci]--;
                        if (compatible[ci] == 0) Ban(i2, t2);
                    }
                }
            }
            return !contradiction;
        }

        private void Ban(int i, int t)
        {
            wave[i * T + t] = false;
            int cb = (i * T + t) * 6;
            for (int d = 0; d < 6; d++) compatible[cb + d] = 0;
            stackCell[stacksize] = i; stackTile[stacksize] = t; stacksize++;

            sumsOfOnes[i] -= 1;
            sumsOfWeights[i] -= weights[t];
            sumsOfWeightLogWeights[i] -= weightLogWeights[t];

            double sum = sumsOfWeights[i];
            entropies[i] = Math.Log(sum) - sumsOfWeightLogWeights[i] / sum;
            if (sumsOfOnes[i] == 0) contradiction = true;
        }

        // ── Diagnóstico y lectura ─────────────────────────────────────────

        public int UndecidedAfterInit()
        {
            int c = 0;
            for (int i = 0; i < N; i++) if (sumsOfOnes[i] > 1) c++;
            return c;
        }

        public void ReadSolution(int[] tilePerCell)
        {
            for (int i = 0; i < N; i++)
                tilePerCell[i] = observed[i] >= 0 ? ts.DomainTiles[observed[i]] : -1;
        }
    }
}
