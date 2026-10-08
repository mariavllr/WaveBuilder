// ============================================================================
// MyWFC.cs
//
// Implementación propia de Wave Function Collapse en 3D con propagación AC-4
//
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public class MyWFC : MonoBehaviour, IWFCGenerator, IWFCSolveTimer
{
    // =========================================================================
    // INSPECTOR
    // =========================================================================

    [Header("Preprocesado de tiles")]
    [Tooltip("TilePreprocessor compartido por todos los solvers. " +
             "Genera variantes rotadas y calcula la tabla de vecinos.")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Input – tiles base (sin rotar)")]
    public Tile[] tileObjects;

    [Header("Tiles de infraestructura (para las restricciones globales)")]
    [SerializeField] private Tile floorTile;
    [SerializeField] private Tile emptyTile;
    [SerializeField] private Tile limitTile;

    [Header("Dimensiones del grid 3D")]
    public int dimensionsX = 10;
    public int dimensionsY = 3;
    public int dimensionsZ = 10;
    public float gridSize = 1f;

    [Header("Parámetros del algoritmo")]
    [Tooltip("Semilla aleatoria. 0 = semilla del sistema (no determinista).")]
    public int seed = 0;
    [Tooltip("Límite de reinicios por incompatibilidad antes de abortar.")]
    public int maxRetries = 100;

    [Header("Restricciones (config del experimento)")]
    [Tooltip("Muestreo ponderado por probability. Off = uniforme.")]
    public bool probabilityConstraint = true;
    [Tooltip("Exclusiones de vecinos (se aplica en TilePreprocessor).")]
    public bool excludedNeighborConstraint = true;
    [Tooltip("Suelo sólido (y=0) y techo vacío (y=dimY-1).")]
    public bool floorCeilingConstraint = true;
    [Tooltip("Tiles fijas (Tile.fixedTile > 0) en posiciones aleatorias.")]
    public bool fixedTilesConstraint = true;
    [Tooltip("Anillo de 'limit' en el perímetro de y=1.")]
    public bool borderConstraint = true;

    [Header("Etiqueta del experimento (columna del CSV)")]
    public string algorithmLabel = "mi_wfc_full";

    [Header("Salida visual")]
    [Tooltip("Transform padre bajo el que se instanciarán los tiles del resultado.")]
    public Transform outputParent;
    public bool generateOnStart = false;

    // =========================================================================
    // EVENTOS (de instancia — contrato IWFCGenerator)
    // =========================================================================

    public event Action OnStart;
    public event Action OnEnd;
    public event Action OnIncompatibility;

    // ── Cronómetro interno de resolución (IWFCSolveTimer) ────────────────
    // Contrato de medición común a MyWFC, GuminWFC y DeBroglieWFC:
    //   FUERA del cronómetro: construcción del propagador/modelo, aplicación de
    //   las restricciones globales e inicialización del estado (incluida la
    //   propagación inicial de esas restricciones), GC.Collect(), lectura del
    //   resultado e instanciación visual.
    //   DENTRO: únicamente el bucle observación–colapso–propagación de cada
    //   intento. El tiempo de los intentos fallidos se suma al de la generación.
    [Header("Medición")]
    [Tooltip("Ejecuta GC.Collect() antes de cada intento, fuera del cronómetro, " +
             "para que la recolección de basura no caiga dentro de la medición.")]
    public bool collectGarbageBeforeSolve = true;

    private readonly System.Diagnostics.Stopwatch solveWatch = new System.Diagnostics.Stopwatch();

    /// <summary>Segundos de resolución de la última generación exitosa (suma de sus intentos).</summary>
    public double LastSolveTime { get; private set; }

    /// <summary>
    /// Celdas con más de una opción al empezar la resolución del intento que
    /// tuvo éxito (contadas fuera del cronómetro): las que el solver tiene que
    /// decidir realmente. Excluye las celdas fijadas o ya determinadas por la
    /// propagación inicial de las restricciones.
    /// </summary>
    public int LastCellsToDecide { get; private set; }

    /// <summary>Celdas libres con más de una opción tras InitWave() (fuera del cronómetro).</summary>
    private int CountCellsToDecide()
    {
        int count = 0;
        for (int i = 0; i < N; i++)
            if (fixedCellTile[i] == null && observed[i] < 0 && sumsOfOnes[i] > 1) count++;
        return count;
    }

    private void CollectGarbageOutsideTimer()
    {
        if (!collectGarbageBeforeSolve) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // =========================================================================
    // IWFCGenerator
    // =========================================================================

    public int DimensionsX => dimensionsX;
    public int DimensionsY => dimensionsY;
    public int DimensionsZ => dimensionsZ;
    public string AlgorithmLabel => algorithmLabel;
    public Tile[] TileObjects => tileObjects;

    /// <summary>True si la tile es de infraestructura (no jugable).</summary>
    public bool IsInfrastructureTile(Tile tile) => tile != null && tile.isInfrastructureTile;

    /// <summary>Número de reinicios por incompatibilidad en la última llamada.</summary>
    public int FailCount { get; private set; }

    // =========================================================================
    // ESTADO INTERNO (AC-4)
    // =========================================================================

    private int T, MX, MY, MZ, N;
    private bool[] wave;                 // [i * T + t]
    private int[][] propagator;          // [d * T + t] → índices compatibles con t en dir d
    private int[] compatible;            // [(i * T + t) * 6 + d]
    private int[] observed;              // tile resuelta por celda, -1 si no observada
    private Tile[] fixedCellTile;        // tile fija (infra) por celda, null si libre

    private double[] weights, weightLogWeights, distribution;
    private double sumOfWeights, sumOfWeightLogWeights, startingEntropy;
    private int[] sumsOfOnes;
    private double[] sumsOfWeights_c, sumsOfWeightLogWeights_c, entropies;
    private (int cellIdx, int tileIdx)[] stack;
    private int stacksize;
    private bool contradiction;

    // Estructuras de la versión optimizada (ver SELECTOR DE MÍNIMA ENTROPÍA)
    private bool[] isFixed;          // celda fijada por las restricciones globales
    private int[] freeNeighbor;      // [i*6+d] vecino libre en dir d, o -1 (fuera o fijo)
    private int[] heap;              // celdas candidatas ordenadas por entropía + ruido
    private int[] heapPos;           // posición de cada celda en heap; -1 si no está
    private double[] noise;          // ruido de desempate por celda (fijo en cada intento)
    private int heapSize;
    private bool heapActive;         // false durante InitWave: el montículo aún no existe
    private System.Random rng;

    private Dictionary<Tile, int> tileIndex;
    private bool tilesetPreprocessed = false;

    // =========================================================================
    // DIRECCIONES 3D
    //   0 Right +X | 1 Left -X | 2 Fwd +Z | 3 Back -Z | 4 Above +Y | 5 Below -Y
    //   índice lineal: i = x + z*MX + y*MX*MZ
    // =========================================================================

    private static readonly int[] DX = { 1, -1, 0, 0, 0, 0 };
    private static readonly int[] DY = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] DZ = { 0, 0, 1, -1, 0, 0 };
    private static readonly int[] OPPOSITE = { 1, 0, 3, 2, 5, 4 };

    // =========================================================================
    // UNITY LIFECYCLE
    // =========================================================================

    // PREPROCESADO DIFERIDO
    // El preprocesado NO se hace en Awake(): TilePreprocessor escribe las listas
    // de vecinos dentro de las tiles base, que son los mismos prefabs para los
    // tres solvers. Si varios solvers preprocesaran en Awake (MyWFC siempre
    // está activo porque comparte GameObject con el arnés), el último en
    // ejecutarse sobrescribiría las listas de los demás con referencias a SUS
    // variantes rotadas, y los otros solvers perderían adyacencias. Por eso
    // solo preprocesa el solver al que se le pide generar, en su primer
    // Generate(), fuera del cronómetro.
    void Awake()
    {
        if (tilePreprocessor == null)
            Debug.LogError("[MyWFC] TilePreprocessor no asignado en el Inspector.");
    }

    private bool EnsurePreprocessed()
    {
        if (tilesetPreprocessed) return true;
        if (tilePreprocessor == null)
        {
            Debug.LogError("[MyWFC] TilePreprocessor no asignado en el Inspector.");
            return false;
        }

        // Expande variantes rotadas y calcula la tabla de vecinos
        // (respetando excludedNeighborConstraint).
        tilePreprocessor.excludedNeighborConstraint = excludedNeighborConstraint;
        tilePreprocessor.Preprocess(ref tileObjects);

        // Filtrar LIMIT del dominio de colapso (se mantiene como tile fija de
        // borde; sus listas *Neighbours siguen usándose para el soporte).
        tileObjects = Array.FindAll(tileObjects, t => t.tileType != "limit");

        tilesetPreprocessed = true;
        return true;
    }

    void Start()
    {
        if (generateOnStart) Generate();
    }

    // =========================================================================
    // API PÚBLICA
    // =========================================================================

    public void Generate()
    {
        if (!EnsurePreprocessed()) return;
        GenerateSync();
    }

    /// <summary>Tile resuelta en el índice lineal i. Null si no observada.</summary>
    public Tile GetResolvedTile(int index)
    {
        if (index < 0 || index >= N) return null;
        if (fixedCellTile != null && fixedCellTile[index] != null) return fixedCellTile[index];
        if (observed == null) return null;
        int t = observed[index];
        return (t >= 0 && t < tileObjects.Length) ? tileObjects[t] : null;
    }

    // ─── Pipeline síncrono con reintentos (equivale al bucle de Program.cs de Gumin) ───

    private void GenerateSync()
    {
        FailCount = 0;
        LastSolveTime = 0;
        LastCellsToDecide = 0;

        if (!BuildPropagator())
        {
            Debug.LogError("[MyWFC] El propagador no pudo construirse (tileset vacío o inválido).");
            return;
        }
        rng = seed != 0 ? new System.Random(seed) : new System.Random();

        OnStart?.Invoke();
        double solveTime = 0;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            // Preparación del intento (FUERA del cronómetro)
            ApplyGlobalConstraints(); // re-aleatoriza las tiles fijas en cada intento
            InitWave();               // estado inicial + propagación de las restricciones
            int cellsToDecide = CountCellsToDecide();
            CollectGarbageOutsideTimer();

            // Resolución (DENTRO del cronómetro)
            solveWatch.Restart();
            bool success = RunAlgorithm();
            solveWatch.Stop();
            solveTime += solveWatch.Elapsed.TotalSeconds;

            if (success)
            {
                LastSolveTime = solveTime;
                LastCellsToDecide = cellsToDecide;
                OnEnd?.Invoke();
                InstantiateTiles();
                return;
            }

            FailCount++;
            OnIncompatibility?.Invoke();
        }

        Debug.LogError($"[MyWFC] Agotados {maxRetries} reintentos sin solución.");
    }

    // =========================================================================
    // CONSTRUCCIÓN DEL PROPAGADOR (una sola vez por Generate)
    // =========================================================================

    private bool BuildPropagator()
    {
        if (tileObjects == null || tileObjects.Length == 0)
        {
            Debug.LogError("[MyWFC] tileObjects está vacío. ¿Se llamó a TilePreprocessor.Preprocess()?");
            return false;
        }

        T = tileObjects.Length;

        tileIndex = new Dictionary<Tile, int>(T);
        for (int i = 0; i < T; i++) tileIndex[tileObjects[i]] = i;

        propagator = new int[6 * T][];
        var tempSets = new List<int>[6 * T];
        for (int i = 0; i < 6 * T; i++) tempSets[i] = new List<int>();

        for (int t = 0; t < T; t++)
        {
            Tile tile = tileObjects[t];
            AddNeighboursToDir(tile.rightNeighbours, 0, t, tempSets);
            AddNeighboursToDir(tile.leftNeighbours, 1, t, tempSets);
            AddNeighboursToDir(tile.upNeighbours, 2, t, tempSets);
            AddNeighboursToDir(tile.downNeighbours, 3, t, tempSets);
            AddNeighboursToDir(tile.aboveNeighbours, 4, t, tempSets);
            AddNeighboursToDir(tile.belowNeighbours, 5, t, tempSets);
        }
        for (int i = 0; i < 6 * T; i++) propagator[i] = tempSets[i].ToArray();

        // Pesos de Shannon
        weights = new double[T];
        weightLogWeights = new double[T];
        sumOfWeights = 0;
        sumOfWeightLogWeights = 0;
        for (int t = 0; t < T; t++)
        {
            weights[t] = tileObjects[t].probability > 0 ? tileObjects[t].probability : 1;
            weightLogWeights[t] = weights[t] * Math.Log(weights[t]);
            sumOfWeights += weights[t];
            sumOfWeightLogWeights += weightLogWeights[t];
        }
        startingEntropy = Math.Log(sumOfWeights) - sumOfWeightLogWeights / sumOfWeights;
        distribution = new double[T];

        return true;
    }

    private void AddNeighboursToDir(List<Tile> neighbours, int dir, int t, List<int>[] sets)
    {
        if (neighbours == null) return;
        foreach (Tile n in neighbours)
            if (tileIndex.TryGetValue(n, out int nIdx))
                sets[OPPOSITE[dir] * T + nIdx].Add(t);
    }

    /// <summary>Lista de vecinos de una tile en la dirección d (para soporte de celdas fijas).</summary>
    private List<Tile> GetNeighboursForDir(Tile tile, int dir)
    {
        switch (dir)
        {
            case 0: return tile.rightNeighbours;
            case 1: return tile.leftNeighbours;
            case 2: return tile.upNeighbours;
            case 3: return tile.downNeighbours;
            case 4: return tile.aboveNeighbours;
            case 5: return tile.belowNeighbours;
            default: return new List<Tile>();
        }
    }

    // =========================================================================
    // RESTRICCIONES GLOBALES (headless — sobre fixedCellTile[])
    //   Equivale a ApplyGlobalConstraints/PlaceInfrastructureTile de REFACTOR,
    //   pero fijando celdas en un array en vez de en objetos Cell.
    // =========================================================================

    private void ApplyGlobalConstraints()
    {
        MX = dimensionsX; MY = dimensionsY; MZ = dimensionsZ;
        N = MX * MY * MZ;
        fixedCellTile = new Tile[N];

        if (borderConstraint && limitTile != null) DefineMapLimits();
        if (floorCeilingConstraint)
        {
            if (floorTile != null) FillLayer(0, floorTile);
            if (emptyTile != null) FillLayer(MY - 1, emptyTile);
        }
        if (fixedTilesConstraint) CreateFixedTiles();
    }

    private void FillLayer(int y, Tile tile)
    {
        for (int z = 0; z < MZ; z++)
            for (int x = 0; x < MX; x++)
                fixedCellTile[x + z * MX + y * MX * MZ] = tile;
    }

    /// <summary>Anillo de 'limit' en el perímetro horizontal de la capa y=1.</summary>
    private void DefineMapLimits()
    {
        if (MY < 2) return;
        int y = 1;
        for (int z = 0; z < MZ; z++)
            for (int x = 0; x < MX; x++)
            {
                bool border = x == 0 || x == MX - 1 || z == 0 || z == MZ - 1;
                if (border) fixedCellTile[x + z * MX + y * MX * MZ] = limitTile;
            }
    }

    /// <summary>Coloca las tiles con Tile.fixedTile > 0 en celdas libres al azar.</summary>
    // TILES FIJAS: especificación común a MyWFC y DeBroglieWFC.
    // Una tile base con fixedTile = k produce exactamente k tiles fijas, cada una
    // con una rotación elegida al azar entre la base y sus variantes. Las
    // variantes rotadas heredan fixedTile al clonarse (Instantiate), así que solo
    // se recorren las tiles base (rotation == zero) para no multiplicar las copias.
    private void CreateFixedTiles()
    {
        foreach (Tile prototype in tileObjects)
        {
            if (prototype.fixedTile <= 0) continue;
            if (prototype.rotation != Vector3.zero) continue;   // solo tiles base

            List<Tile> variants = GetRotationVariants(prototype);
            for (int k = 0; k < prototype.fixedTile; k++)
            {
                int target = PickRandomFreeCell();
                if (target < 0) break;
                fixedCellTile[target] = variants[rng.Next(0, variants.Count)];
            }
        }
    }

    /// <summary>
    /// Tile base y sus variantes rotadas (TilePreprocessor las nombra
    /// "&lt;base&gt;_RotateRight", "_Rotate180", "_RotateLeft").
    /// </summary>
    private List<Tile> GetRotationVariants(Tile baseTile)
    {
        string prefix = baseTile.gameObject.name + "_Rotate";
        var variants = new List<Tile> { baseTile };
        foreach (Tile t in tileObjects)
            if (t != baseTile && t.gameObject.name.StartsWith(prefix))
                variants.Add(t);
        return variants;
    }

    private int PickRandomFreeCell()
    {
        var free = new List<int>(N);
        for (int i = 0; i < N; i++)
            if (fixedCellTile[i] == null) free.Add(i);
        if (free.Count == 0) return -1;
        return free[rng.Next(0, free.Count)];
    }

    // =========================================================================
    // INICIALIZACIÓN
    // =========================================================================

    private void InitWave()
    {
        wave = new bool[N * T];
        compatible = new int[N * T * 6];
        observed = new int[N];
        sumsOfOnes = new int[N];
        sumsOfWeights_c = new double[N];
        sumsOfWeightLogWeights_c = new double[N];
        entropies = new double[N];
        stack = new (int, int)[N * T];
        stacksize = 0;
        contradiction = false;

        // PASO 0: topología. Tabla de vecinos libres por celda y dirección
        // (equivale a la GridTopology de DeBroglie, que también se construye
        // fuera del cronómetro) y estado inicial del montículo de selección.
        heapActive = false;
        if (heap == null || heap.Length != N)
        {
            heap = new int[N];
            heapPos = new int[N];
            noise = new double[N];
            isFixed = new bool[N];
            freeNeighbor = new int[N * 6];
        }
        for (int i = 0; i < N; i++)
        {
            heapPos[i] = -1;
            isFixed[i] = fixedCellTile[i] != null;
        }
        for (int i = 0; i < N; i++)
        {
            int x1 = i % MX;
            int z1 = (i / MX) % MZ;
            int y1 = i / (MX * MZ);
            for (int d = 0; d < 6; d++)
            {
                int x2 = x1 + DX[d], y2 = y1 + DY[d], z2 = z1 + DZ[d];
                int j = (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ)
                    ? -1 : x2 + z2 * MX + y2 * MX * MZ;
                freeNeighbor[i * 6 + d] = (j >= 0 && !isFixed[j]) ? j : -1;
            }
        }

        // PASO 1: wave desde el estado (libre / fija)
        for (int i = 0; i < N; i++)
        {
            observed[i] = -1;
            Tile fx = fixedCellTile[i];

            if (fx == null)
            {
                // Celda libre: todo el dominio permitido
                for (int t = 0; t < T; t++) wave[i * T + t] = true;
                sumsOfOnes[i] = T;
                sumsOfWeights_c[i] = sumOfWeights;
                sumsOfWeightLogWeights_c[i] = sumOfWeightLogWeights;
                entropies[i] = startingEntropy;
            }
            else
            {
                // Celda fija: dominio 1. Marca el bit si la tile está en el dominio
                // de colapso (floor/empty); si no (limit), wave queda todo false.
                int fxIdx = tileIndex.TryGetValue(fx, out int idx) ? idx : -1;
                double sumW = 0, sumWLogW = 0;
                for (int t = 0; t < T; t++)
                {
                    bool on = (t == fxIdx);
                    wave[i * T + t] = on;
                    if (on) { sumW += weights[t]; sumWLogW += weightLogWeights[t]; }
                }
                sumsOfOnes[i] = 1;
                sumsOfWeights_c[i] = sumW;
                sumsOfWeightLogWeights_c[i] = sumWLogW;
                entropies[i] = 0;
                observed[i] = fxIdx; // -1 para limit (se instancia desde fixedCellTile[])
            }
        }

        // PASO 2: contadores de soporte compatible[]
        for (int i = 0; i < N; i++)
        {
            int x1 = i % MX;
            int z1 = (i / MX) % MZ;
            int y1 = i / (MX * MZ);

            for (int t = 0; t < T; t++)
            {
                for (int d = 0; d < 6; d++)
                {
                    int oppDir = OPPOSITE[d];
                    int x2 = x1 + DX[oppDir];
                    int y2 = y1 + DY[oppDir];
                    int z2 = z1 + DZ[oppDir];
                    int compIdx = (i * T + t) * 6 + d;

                    if (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ)
                    {
                        // Frontera: soporte completo
                        compatible[compIdx] = propagator[oppDir * T + t].Length;
                        continue;
                    }

                    int j = x2 + z2 * MX + y2 * MX * MZ;
                    Tile jFixed = fixedCellTile[j];

                    if (jFixed != null)
                    {
                        // Vecino fijo: ¿su tile soporta a t en dir d?
                        bool supports = GetNeighboursForDir(jFixed, d).Contains(tileObjects[t]);
                        compatible[compIdx] = supports ? 1 : 0;
                    }
                    else
                    {
                        int count = 0;
                        int[] supporters = propagator[oppDir * T + t];
                        for (int l = 0; l < supporters.Length; l++)
                            if (wave[j * T + supporters[l]]) count++;
                        compatible[compIdx] = count;
                    }
                }
            }
        }

        // PASO 3: banear tiles de celdas libres sin soporte en alguna dir no frontera
        for (int i = 0; i < N; i++)
        {
            if (fixedCellTile[i] != null) continue;
            int x1 = i % MX;
            int z1 = (i / MX) % MZ;
            int y1 = i / (MX * MZ);

            for (int t = 0; t < T; t++)
            {
                if (!wave[i * T + t]) continue;
                for (int d = 0; d < 6; d++)
                {
                    int x2 = x1 + DX[d]; int y2 = y1 + DY[d]; int z2 = z1 + DZ[d];
                    bool boundary = x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ;
                    if (!boundary && compatible[(i * T + t) * 6 + d] == 0) { Ban(i, t); break; }
                }
            }
        }

        if (stacksize > 0) Propagate();
    }

    // =========================================================================
    // BUCLE PRINCIPAL
    // =========================================================================

    // Versión optimizada. Mismo algoritmo (AC-4, mínima entropía de Shannon con
    // desempate aleatorio, muestreo ponderado, reinicio completo ante
    // contradicción); cambian las estructuras de datos:
    //   · Selección: montículo binario indexado, O(log N) por actualización,
    //     en lugar de recorrer las N celdas en cada colapso (O(N) por paso).
    //   · Desempate: ruido 1e-6·U[0,1) fijo por celda en cada intento, en lugar
    //     de sortear un número aleatorio por celda en cada recorrido.
    //   · Entropía: solo se recalcula para celdas que siguen siendo candidatas.
    //   · Propagación: vecinos precalculados (sin div/mod ni comprobación de
    //     límites) y salida inmediata al detectar una contradicción.
    private bool RunAlgorithm()
    {
        // La propagación inicial (InitWave) ya pudo detectar una contradicción.
        if (contradiction) return false;

        BuildHeap(); // forma parte de la resolución: dentro del cronómetro

        while (heapSize > 0)
        {
            int node = PopMin();
            CollapseCell(node);
            if (!Propagate()) return false;
        }
        return true; // todas las celdas candidatas colapsadas
    }

    private void CollapseCell(int i)
    {
        int b = i * T;

        double total = 0;
        for (int t = 0; t < T; t++)
            if (wave[b + t]) total += probabilityConstraint ? weights[t] : 1.0;

        // Muestreo ponderado por probability, o uniforme si el flag está desactivado
        double threshold = rng.NextDouble() * total;
        double cumulative = 0;
        int chosen = -1;
        for (int t = 0; t < T; t++)
        {
            if (!wave[b + t]) continue;
            chosen = t; // si el redondeo impide alcanzar el umbral, queda la última permitida
            cumulative += probabilityConstraint ? weights[t] : 1.0;
            if (cumulative >= threshold) break;
        }

        for (int t = 0; t < T; t++)
            if (wave[b + t] && t != chosen)
                Ban(i, t);

        observed[i] = chosen;
    }

    // =========================================================================
    // SELECTOR DE MÍNIMA ENTROPÍA: montículo binario indexado
    // =========================================================================

    private double HeapKey(int i) => entropies[i] + noise[i];

    private void BuildHeap()
    {
        heapSize = 0;
        for (int i = 0; i < N; i++)
        {
            heapPos[i] = -1;
            if (observed[i] >= 0 || isFixed[i]) continue; // colapsada o fija
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
            heap[0] = last;
            heapPos[last] = 0;
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

    // =========================================================================
    // PROPAGACIÓN AC-4
    // =========================================================================

    private bool Propagate()
    {
        while (stacksize > 0 && !contradiction)
        {
            (int i1, int t1) = stack[--stacksize];
            int nb = i1 * 6;

            for (int d = 0; d < 6; d++)
            {
                int i2 = freeNeighbor[nb + d];
                if (i2 < 0) continue; // fuera del volumen o celda fija

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

        int baseComp = (i * T + t) * 6;
        for (int d = 0; d < 6; d++) compatible[baseComp + d] = 0;

        stack[stacksize++] = (i, t);

        sumsOfOnes[i]--;
        sumsOfWeights_c[i] -= weights[t];
        sumsOfWeightLogWeights_c[i] -= weightLogWeights[t];

        if (sumsOfOnes[i] == 0) { contradiction = true; return; }

        // La entropía solo interesa a las celdas que siguen siendo candidatas
        // (en InitWave, antes de existir el montículo, se calcula siempre).
        if (heapActive && heapPos[i] < 0) return;
        double s = sumsOfWeights_c[i];
        entropies[i] = s > 0 ? Math.Log(s) - sumsOfWeightLogWeights_c[i] / s : 0;
        if (heapActive) HeapUpdate(i);
    }

    // =========================================================================
    // INSTANCIACIÓN VISUAL (no cuenta en el cronómetro)
    // =========================================================================

    private void InstantiateTiles()
    {
        ClearOutput();

        Vector3 origin = (outputParent != null) ? outputParent.position : transform.position;

        for (int i = 0; i < N; i++)
        {
            Tile tileRef = GetResolvedTile(i);
            if (tileRef == null) continue;

            int x = i % MX;
            int z = (i / MX) % MZ;
            int y = i / (MX * MZ);

            Vector3 worldPos = origin + new Vector3(x * gridSize, y * gridSize, z * gridSize);

            Tile instance = Instantiate(tileRef, worldPos, Quaternion.identity, outputParent);

            if (tileRef.rotation != Vector3.zero)
                instance.transform.Rotate(tileRef.rotation, Space.Self);

            instance.transform.position += tileRef.positionOffset;
            instance.gameObject.SetActive(true);
        }
    }

    private void ClearOutput()
    {
        if (outputParent == null) return;
        for (int i = outputParent.childCount - 1; i >= 0; i--)
            DestroyImmediate(outputParent.GetChild(i).gameObject);
    }
}
