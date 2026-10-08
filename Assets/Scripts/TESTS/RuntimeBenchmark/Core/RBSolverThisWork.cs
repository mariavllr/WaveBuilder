// ============================================================================
// RBSolverThisWork.cs  ("This work": solver AC-4 del framework)
//
// Port sin cambios algorítmicos de MyWFC.cs (versión headless del camino
// GENERATE_ALL/AC-4 de REFACTOR): AC-4, mínima entropía de Shannon con ruido
// de desempate, muestreo ponderado por Tile.probability y reinicio completo
// ante contradicción. Las celdas preasignadas (capas, límite, tiles fijas)
// se EXCLUYEN de las variables del CSP y su efecto se aplica como
// restricción unaria sobre sus vecinos libres.
//
// Diferencias respecto a MyWFC.cs (documentadas en RUNTIME_BENCHMARK.md):
//   1. Corrección: el baneo inicial de tiles sin vecino posible comprobaba la
//      existencia del vecino en la dirección d pero el soporte de la
//      dirección opuesta. Ahora usa la misma dirección para ambas cosas
//      (equivale al Clear() de Gumin y al Clear() AC-4 de DeBroglie).
//   2. Las celdas con una sola opción no se "colapsan" con una extracción
//      aleatoria: se dan por decididas (igual que REFACTOR, Gumin y DeBroglie).
//   3. Memoria reservada una vez por configuración (Prepare), no por intento.
//   4. Selector configurable: montículo (MyWFC) o barrido lineal O(N) (REFACTOR).
// ============================================================================

using System;
using System.Collections.Generic;

namespace WFCRuntimeBenchmark
{
    /// <summary>
    /// Contrato común de los solvers del benchmark de runtime. El cronómetro
    /// lo gestiona SIEMPRE el motor (RuntimeBenchmarkEngine): el solver solo
    /// separa el trabajo en fases.
    ///   Prepare / BeginRun / SetupAttempt / UndecidedAfterInit / ReadSolution → FUERA del cronómetro
    ///   InitAttempt (t_init) y Search (t_search)                              → DENTRO del cronómetro
    /// </summary>
    public interface IWFCSolverCore
    {
        string Name { get; }
        string Variant { get; }
        /// <summary>¿Puede representar este nivel de restricciones?</summary>
        bool Supports(LevelSpec level);
        /// <summary>Construcción del modelo/propagador y reserva de memoria (una vez por configuración).</summary>
        void Prepare(CompiledTileset ts, int mx, int my, int mz, LevelSpec level);
        /// <summary>Inicializa el RNG del run. Los reintentos del run continúan la misma secuencia.</summary>
        void BeginRun(int seed);
        /// <summary>Representación de la instancia (topología, máscara...). No resuelve nada.</summary>
        void SetupAttempt(ProblemInstance inst);
        /// <summary>Reset del estado + restricciones de la instancia + propagación inicial. False = contradicción.</summary>
        bool InitAttempt();
        /// <summary>Bucle observación–colapso–propagación. False = contradicción.</summary>
        bool Search();
        /// <summary>Celdas que son variables en la representación del solver.</summary>
        int SolverCspCells { get; }
        /// <summary>Celdas libres con más de una opción tras InitAttempt (diagnóstico).</summary>
        int UndecidedAfterInit();
        /// <summary>Decisiones (observaciones con más de una opción) del último Search.</summary>
        long Decisions { get; }
        /// <summary>Tile id por celda (índice x + z*MX + y*MX*MZ), incluidas las preasignadas.</summary>
        void ReadSolution(int[] tilePerCell);
    }

    public enum ThisWorkSelector { Heap, Linear }

    public sealed class ThisWorkSolver : IWFCSolverCore
    {
        private readonly ThisWorkSelector selector;
        public ThisWorkSolver(ThisWorkSelector selector = ThisWorkSelector.Heap) { this.selector = selector; }

        public string Name { get { return "ThisWork"; } }
        public string Variant { get { return selector == ThisWorkSelector.Heap ? "heap" : "linear"; } }
        public bool Supports(LevelSpec level) { return true; }

        private CompiledTileset ts;
        private int T, MX, MY, MZ, N;
        private int[][] propagator;
        private double[] weights, weightLogWeights;
        private double sumOfWeights, sumOfWeightLogWeights, startingEntropy;

        private bool[] wave;
        private int[] compatible;
        private int[] observed;
        private int[] sumsOfOnes;
        private double[] sumsOfWeights_c, sumsOfWeightLogWeights_c, entropies;
        private int[] stackCell, stackTile;
        private int stacksize;
        private bool contradiction;

        private int[] nb;             // vecino en dir d o -1 (fuera del volumen); estático por tamaño
        private int[] freeNeighbor;   // vecino libre en dir d o -1 (fuera o preasignado); por instancia
        private bool[] isFixed;
        private int[] fixedTile;
        private bool[] banMark;
        private int[] fixedPairs;

        private int[] heap, heapPos;
        private double[] noise;
        private int heapSize;
        private bool heapActive;

        private Random rng;
        private ProblemInstance inst;
        private long decisions;

        public int SolverCspCells { get { return inst == null ? 0 : inst.FreeCells; } }
        public long Decisions { get { return decisions; } }

        // ── Preparación (fuera del cronómetro) ───────────────────────────

        public void Prepare(CompiledTileset ts, int mx, int my, int mz, LevelSpec level)
        {
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

            wave = new bool[N * T];
            compatible = new int[N * T * 6];
            observed = new int[N];
            sumsOfOnes = new int[N];
            sumsOfWeights_c = new double[N];
            sumsOfWeightLogWeights_c = new double[N];
            entropies = new double[N];
            stackCell = new int[N * T];
            stackTile = new int[N * T];
            isFixed = new bool[N];
            fixedTile = new int[N];
            freeNeighbor = new int[N * 6];
            banMark = new bool[T];
            heap = new int[N]; heapPos = new int[N]; noise = new double[N];

            nb = new int[N * 6];
            for (int i = 0; i < N; i++)
            {
                int x = i % MX, z = (i / MX) % MZ, y = i / (MX * MZ);
                for (int d = 0; d < 6; d++)
                {
                    int x2 = x + Dir.DX[d], y2 = y + Dir.DY[d], z2 = z + Dir.DZ[d];
                    nb[i * 6 + d] = (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ)
                        ? -1 : x2 + z2 * MX + y2 * MX * MZ;
                }
            }
        }

        public void BeginRun(int seed) { rng = new Random(seed); }

        /// <summary>Topología de la instancia: qué celdas son variables y sus vecinos libres.</summary>
        public void SetupAttempt(ProblemInstance instance)
        {
            inst = instance;
            for (int i = 0; i < N; i++)
            {
                fixedTile[i] = inst.Fixed[i];
                isFixed[i] = inst.Fixed[i] >= 0;
            }
            for (int k = 0; k < N * 6; k++)
            {
                int j = nb[k];
                freeNeighbor[k] = (j >= 0 && !isFixed[j]) ? j : -1;
            }
            fixedPairs = inst.AdjacentFixedPairs();
        }

        // ── t_init (dentro del cronómetro) ────────────────────────────────

        public bool InitAttempt()
        {
            stacksize = 0;
            contradiction = false;
            heapActive = false;
            decisions = 0;

            // PASO 0: compatibilidad entre celdas preasignadas adyacentes
            if (!ProblemInstance.FixedPairsConsistent(ts, fixedTile, fixedPairs)) { contradiction = true; return false; }

            // PASO 1: dominio inicial
            for (int i = 0; i < N; i++)
            {
                int b = i * T;
                observed[i] = -1;
                if (!isFixed[i])
                {
                    for (int t = 0; t < T; t++) wave[b + t] = true;
                    sumsOfOnes[i] = T;
                    sumsOfWeights_c[i] = sumOfWeights;
                    sumsOfWeightLogWeights_c[i] = sumOfWeightLogWeights;
                    entropies[i] = startingEntropy;
                }
                else
                {
                    // Celda preasignada: fuera del CSP. Se marca su tile si está en el dominio.
                    int fx = ts.DomainIndexOf[fixedTile[i]];
                    for (int t = 0; t < T; t++) wave[b + t] = (t == fx);
                    sumsOfOnes[i] = 1;
                    sumsOfWeights_c[i] = fx >= 0 ? weights[fx] : 0;
                    sumsOfWeightLogWeights_c[i] = fx >= 0 ? weightLogWeights[fx] : 0;
                    entropies[i] = 0;
                    observed[i] = fx;
                }
            }

            // PASO 2 y 3: soporte AC-4 de las celdas libres y baneo de las tiles sin soporte.
            // compatible[(i,t),d] = soporte de t en i procedente del vecino j = i + DIR[OPP[d]].
            for (int i = 0; i < N; i++)
            {
                if (isFixed[i]) continue;
                int nbBase = i * 6;
                for (int t = 0; t < T; t++)
                {
                    bool ban = false;
                    int cb = (i * T + t) * 6;
                    for (int d = 0; d < 6; d++)
                    {
                        int od = Dir.OPP[d];
                        int j = nb[nbBase + od];
                        int c;
                        if (j < 0) c = propagator[od * T + t].Length;                       // borde: sin restricción
                        else if (isFixed[j]) c = ts.SupportFromTile[d][fixedTile[j]][t] ? 1 : 0; // vecino preasignado
                        else c = propagator[od * T + t].Length;                            // vecino libre (dominio completo)
                        compatible[cb + d] = c;
                        if (j >= 0 && c == 0) ban = true;
                    }
                    banMark[t] = ban;
                }
                for (int t = 0; t < T; t++)
                    if (banMark[t] && wave[i * T + t])
                    {
                        Ban(i, t);
                        if (contradiction) return false;
                    }
            }

            if (stacksize > 0) return Propagate();
            return true;
        }

        // ── t_search (dentro del cronómetro) ──────────────────────────────

        public bool Search()
        {
            if (contradiction) return false;
            if (selector == ThisWorkSelector.Heap) return SearchHeap();
            return SearchLinear();
        }

        private bool SearchHeap()
        {
            BuildHeap();
            while (heapSize > 0)
            {
                int node = PopMin();
                if (sumsOfOnes[node] <= 1) continue; // ya decidida por la propagación
                CollapseCell(node);
                if (!Propagate()) return false;
            }
            return true;
        }

        private bool SearchLinear()
        {
            while (true)
            {
                // Igual que REFACTOR.SelectCellAC4: barrido O(N) con ruido 1e-6 por candidata.
                double minE = double.MaxValue;
                int argmin = -1;
                for (int i = 0; i < N; i++)
                {
                    if (isFixed[i] || observed[i] >= 0 || sumsOfOnes[i] <= 1) continue;
                    double e = entropies[i] + 1e-6 * rng.NextDouble();
                    if (e < minE) { minE = e; argmin = i; }
                }
                if (argmin < 0) return true;
                CollapseCell(argmin);
                if (!Propagate()) return false;
            }
        }

        private void CollapseCell(int i)
        {
            decisions++;
            int b = i * T;
            double total = 0;
            for (int t = 0; t < T; t++) if (wave[b + t]) total += weights[t];

            double threshold = rng.NextDouble() * total;
            double cumulative = 0;
            int chosen = -1;
            for (int t = 0; t < T; t++)
            {
                if (!wave[b + t]) continue;
                chosen = t;
                cumulative += weights[t];
                if (cumulative >= threshold) break;
            }
            for (int t = 0; t < T; t++)
                if (wave[b + t] && t != chosen) Ban(i, t);
            observed[i] = chosen;
        }

        private bool Propagate()
        {
            while (stacksize > 0 && !contradiction)
            {
                stacksize--;
                int i1 = stackCell[stacksize], t1 = stackTile[stacksize];
                int nbBase = i1 * 6;
                for (int d = 0; d < 6; d++)
                {
                    int i2 = freeNeighbor[nbBase + d];
                    if (i2 < 0) continue;
                    int[] supported = propagator[d * T + t1];
                    int b2 = i2 * T;
                    for (int l = 0; l < supported.Length; l++)
                    {
                        int t2 = supported[l];
                        if (--compatible[(b2 + t2) * 6 + d] == 0 && wave[b2 + t2])
                        {
                            Ban(i2, t2);
                            if (contradiction) return false;
                        }
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

            sumsOfOnes[i]--;
            sumsOfWeights_c[i] -= weights[t];
            sumsOfWeightLogWeights_c[i] -= weightLogWeights[t];
            if (sumsOfOnes[i] == 0) { contradiction = true; return; }

            if (heapActive && heapPos[i] < 0) return;
            double s = sumsOfWeights_c[i];
            entropies[i] = s > 0 ? Math.Log(s) - sumsOfWeightLogWeights_c[i] / s : 0;
            if (heapActive) HeapUpdate(i);
        }

        // ── Montículo indexado (idéntico a MyWFC) ─────────────────────────

        private double HeapKey(int i) { return entropies[i] + noise[i]; }

        private void BuildHeap()
        {
            heapSize = 0;
            for (int i = 0; i < N; i++)
            {
                heapPos[i] = -1;
                if (isFixed[i] || observed[i] >= 0 || sumsOfOnes[i] <= 1) continue;
                noise[i] = 1e-6 * rng.NextDouble();
                heapPos[i] = heapSize;
                heap[heapSize++] = i;
            }
            for (int k = heapSize / 2 - 1; k >= 0; k--) SiftDown(k);
            heapActive = true;
        }

        private int PopMin()
        {
            int top = heap[0];
            heapPos[top] = -1;
            heapSize--;
            if (heapSize > 0)
            {
                int last = heap[heapSize];
                heap[0] = last; heapPos[last] = 0;
                SiftDown(0);
            }
            return top;
        }

        private void HeapUpdate(int i)
        {
            int p = heapPos[i];
            if (p < 0) return;
            SiftUp(p);
            SiftDown(heapPos[i]);
        }

        private void SiftUp(int p)
        {
            int cell = heap[p];
            double key = HeapKey(cell);
            while (p > 0)
            {
                int parent = (p - 1) >> 1;
                int pc = heap[parent];
                if (HeapKey(pc) <= key) break;
                heap[p] = pc; heapPos[pc] = p;
                p = parent;
            }
            heap[p] = cell; heapPos[cell] = p;
        }

        private void SiftDown(int p)
        {
            int cell = heap[p];
            double key = HeapKey(cell);
            while (true)
            {
                int l = 2 * p + 1;
                if (l >= heapSize) break;
                int r = l + 1;
                int c = (r < heapSize && HeapKey(heap[r]) < HeapKey(heap[l])) ? r : l;
                int cc = heap[c];
                if (key <= HeapKey(cc)) break;
                heap[p] = cc; heapPos[cc] = p;
                p = c;
            }
            heap[p] = cell; heapPos[cell] = p;
        }

        // ── Diagnóstico y lectura (fuera del cronómetro) ──────────────────

        public int UndecidedAfterInit()
        {
            int c = 0;
            for (int i = 0; i < N; i++) if (!isFixed[i] && sumsOfOnes[i] > 1) c++;
            return c;
        }

        public void ReadSolution(int[] tilePerCell)
        {
            for (int i = 0; i < N; i++)
            {
                if (isFixed[i]) { tilePerCell[i] = fixedTile[i]; continue; }
                int chosen = -1;
                if (sumsOfOnes[i] == 1)
                    for (int t = 0; t < T; t++) if (wave[i * T + t]) { chosen = t; break; }
                tilePerCell[i] = chosen >= 0 ? ts.DomainTiles[chosen] : -1;
            }
        }
    }
}
