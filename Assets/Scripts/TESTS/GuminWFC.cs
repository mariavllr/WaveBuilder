// ============================================================================
// GuminWFC.cs
//
// Implementación fiel al algoritmo Wave Function Collapse de Maxim Gumin (2016)
// [https://github.com/mxgmn/WaveFunctionCollapse], extendida de 2D (4 dirs)
// a 3D (6 dirs ortogonales) y adaptada al entorno Unity del proyecto.
//
// Correspondencia directa con el código original de Gumin:
//   Model.Init()              → InitWave()
//   Model.Clear()             → Clear()
//   Model.Run()               → RunAlgorithm()
//   Model.NextUnobservedNode  → SelectNextCell()          [heurística Entropy]
//   Model.Observe()           → CollapseCell()
//   Model.Propagate()         → Propagate()               [AC-4]
//   Model.Ban()               → Ban()
//   SimpleTiledModel (ctor)   → BuildPropagator()
//
// Diferencias con el original (necesarias para la integración 3D):
//   · 4 direcciones → 6 direcciones ortogonales (±X, ±Y, ±Z).
//   · El input NO es un XML ni una imagen: se reciben directamente los Tile[]
//     del framework propio, cuyos campos *Neighbours ya están calculados.
//   · No hay periodicidad (volumen finito con bordes duros).
//   · La instanciación visual se hace mediante Instantiate() de Unity en lugar
//     de escribir un bitmap a disco.
//
// INDEPENDENCIA DE REFACTOR:
//   GuminWFC ya no depende de WaveFunctionGame_REFACTOR en ningún sentido.
//   Obtiene su tileset directamente desde TilePreprocessor, que es el punto
//   de entrada común para todos los solvers del framework.
//
// USO:
//   1. Añadir el componente TilePreprocessor al proyecto y asignarlo al slot.
//   2. Asignar tileObjects[] directamente en el inspector (los tiles base sin
//      rotar). TilePreprocessor los expandirá con variantes y calculará vecinos.
//   3. Asignar outputParent (un Transform vacío, separado del de REFACTOR).
//   4. Configurar dimensiones, gridSize y seed.
//   5. Llamar Generate() o activar generateOnStart.
//
// Copyright (C) 2016 Maxim Gumin, The MIT License (MIT)
// Adaptación 3D: proyecto WFC Framework (véase artículo adjunto)
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GuminWFC : MonoBehaviour
{
    // =========================================================================
    // INSPECTOR
    // =========================================================================

    [Header("Preprocesado de tiles")]
    [Tooltip("TilePreprocessor compartido por todos los solvers. " +
             "Genera variantes rotadas y calcula la tabla de vecinos.")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Input – tiles base (sin rotar)")]
    [Tooltip("Array de tiles base definidos en el inspector. " +
             "TilePreprocessor añadirá las variantes rotadas en Awake.")]
    public Tile[] tileObjects;

    [Header("Dimensiones del grid 3D")]
    public int dimensionsX = 10;
    public int dimensionsY = 3;
    public int dimensionsZ = 10;
    public float gridSize = 1f;

    [Header("Parámetros del algoritmo (Gumin)")]
    [Tooltip("Semilla aleatoria. 0 = semilla del sistema (no determinista).")]
    public int seed = 0;
    [Tooltip("Límite de reinicios por incompatibilidad antes de abortar.")]
    public int maxRetries = 100;

    [Header("Salida visual")]
    [Tooltip("Transform padre bajo el que se instanciarán los tiles del resultado.")]
    public Transform outputParent;
    public bool generateOnStart = false;

    // =========================================================================
    // EVENTOS
    // =========================================================================

    public delegate void OnStartGeneration();
    public delegate void OnIncompatibility();
    public delegate void OnEndGeneration();

    /// <summary>Disparado justo antes de comenzar cada generación completa.</summary>
    public static event OnStartGeneration onStartGeneration;
    /// <summary>Disparado cada vez que un intento falla por contradicción.</summary>
    public static event OnIncompatibility onIncompatibility;
    /// <summary>Disparado cuando la generación termina con éxito.</summary>
    public static event OnEndGeneration onEndGeneration;

    // =========================================================================
    // PROPIEDADES PÚBLICAS (consulta post-generación)
    // =========================================================================

    /// <summary>Número de reinicios por incompatibilidad en la última llamada.</summary>
    public int FailCount { get; private set; }

    // =========================================================================
    // ESTADO INTERNO – ESTRUCTURAS DE GUMIN (Model.cs)
    // =========================================================================

    private int T, MX, MY, MZ, N;
    private bool[] wave;
    private int[][] propagator;
    private int[] compatible;
    private int[] observed;
    private double[] weights;
    private double[] weightLogWeights;
    private double[] distribution;
    private double sumOfWeights, sumOfWeightLogWeights, startingEntropy;
    private int[] sumsOfOnes;
    private double[] sumsOfWeights_c;
    private double[] sumsOfWeightLogWeights_c;
    private double[] entropies;
    private (int cellIdx, int tileIdx)[] stack;
    private int stacksize;
    private bool contradiction;
    private System.Random rng;

    // Flag de preprocesado: se hace una sola vez en Awake.
    private bool tilesetPreprocessed = false;

    // =========================================================================
    // DIRECCIONES 3D
    // =========================================================================
    //
    // Índice │ Dirección │ Delta (x,y,z) │ Opuesto
    // ───────┼───────────┼───────────────┼────────
    //   0    │ Right +X  │ (+1, 0,  0)   │   1
    //   1    │ Left  -X  │ (-1, 0,  0)   │   0
    //   2    │ Fwd   +Z  │ ( 0, 0, +1)   │   3
    //   3    │ Back  -Z  │ ( 0, 0, -1)   │   2
    //   4    │ Above +Y  │ ( 0,+1,  0)   │   5
    //   5    │ Below -Y  │ ( 0,-1,  0)   │   4
    //
    // El índice lineal de una celda (x,y,z) es:
    //   i = x + z*MX + y*MX*MZ       (mismo orden que REFACTOR)

    private static readonly int[] DX = { 1, -1, 0, 0, 0, 0 };
    private static readonly int[] DY = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] DZ = { 0, 0, 1, -1, 0, 0 };
    private static readonly int[] OPPOSITE = { 1, 0, 3, 2, 5, 4 };

    // =========================================================================
    // UNITY LIFECYCLE
    // =========================================================================

    void Awake()
    {
        if (tilePreprocessor == null)
        {
            Debug.LogError("[GuminWFC] TilePreprocessor no asignado en el Inspector.");
            return;
        }

        // El preprocesado se hace una vez por sesión, igual que en REFACTOR.
        // Expande tileObjects con variantes rotadas y calcula la tabla de vecinos.
        tilePreprocessor.Preprocess(ref tileObjects);

        // Filtrar LIMIT del dominio de colapso, igual que REFACTOR.
        // Las referencias a LIMIT en las listas de vecinos se mantienen para
        // que el TilePreprocessor pueda usarlas, pero Gumin no las colapsa.
        tileObjects = System.Array.FindAll(tileObjects, t => t.tileType != "limit");

        tilesetPreprocessed = true;
    }

    void Start()
    {
        if (generateOnStart) Generate();
    }

    // =========================================================================
    // API PÚBLICA
    // =========================================================================

    /// <summary>
    /// Punto de entrada principal. Construye el propagador (una vez), reserva
    /// las estructuras internas e inicia la generación en una corrutina para
    /// no bloquear el hilo principal entre intentos.
    /// </summary>
    public void Generate()
    {
        if (!tilesetPreprocessed)
        {
            Debug.LogError("[GuminWFC] Tileset no preprocesado. Asegúrate de que Awake() se haya ejecutado.");
            return;
        }

        // Llamada directa y SÍNCRONA
        GenerateSync();
    }

    /// <summary>Tile resuelta en el índice lineal i = x + z*MX + y*MX*MZ. Null si no observada.</summary>
    public Tile GetResolvedTile(int index)
    {
        if (observed == null || index < 0 || index >= N) return null;
        int t = observed[index];
        return (t >= 0 && t < tileObjects.Length) ? tileObjects[t] : null;
    }

    /// <summary>GuminWFC no tiene tiles de infraestructura; todas las tiles son jugables.</summary>
    public bool IsInfrastructureTile(string tileType) => false;

    // ─── Pipeline de generación (síncrono) ─────────────────────────────────
    //
    //  Equivale al bucle de reintento del Program.cs original:
    //    for (int k = 0; k < N; k++) { if (model.Run(seed, limit)) break; }
    //  Cada intento reinicia el wave (InitWave, equivalente a Model.Clear) y
    //  usa una nueva realización aleatoria, de modo que reintentos sucesivos
    //  exploran caminos distintos del espacio de soluciones.

    private void GenerateSync()
    {
        FailCount = 0;

        if (!BuildPropagator())
        {
            Debug.LogError("[GuminWFC] El propagador no pudo construirse (tileset vacío o inválido).");
            return;
        }
        rng = seed != 0 ? new System.Random(seed) : new System.Random();

        onStartGeneration?.Invoke();

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            InitWave();

            bool success = RunAlgorithm();
            if (success)
            {
                onEndGeneration?.Invoke();
                InstantiateTiles();
                return;
            }

            FailCount++;
            onIncompatibility?.Invoke();
        }

        Debug.LogWarning($"[GuminWFC] Agotados {maxRetries} reintentos sin solución.");
    }


    // =========================================================================
    // CONSTRUCCIÓN DEL PROPAGADOR  (SimpleTiledModel ctor en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale al constructor de SimpleTiledModel. Precalcula la tabla de
    /// compatibilidad propagator[d][t] = {t' compatibles con t en dir d}.
    /// Se construye UNA sola vez a partir de los *Neighbours de cada Tile,
    /// que ya han sido rellenados por TilePreprocessor.
    /// </summary>
    private bool BuildPropagator()
    {
        if (tileObjects == null || tileObjects.Length == 0)
        {
            Debug.LogError("[GuminWFC] tileObjects está vacío. ¿Se llamó a TilePreprocessor.Preprocess()?");
            return false;
        }

        T = tileObjects.Length;

        // Índice inverso: Tile → índice en tileObjects
        var tileIndex = new Dictionary<Tile, int>(T);
        for (int i = 0; i < T; i++) tileIndex[tileObjects[i]] = i;

        // propagator[d * T + t] → array de índices de tiles compatibles con t en dir d
        propagator = new int[6 * T][];

        var tempSets = new List<int>[6 * T];
        for (int i = 0; i < 6 * T; i++) tempSets[i] = new List<int>();

        for (int t = 0; t < T; t++)
        {
            Tile tile = tileObjects[t];
            AddNeighboursToDir(tile.rightNeighbours, 0, t, tileIndex, tempSets);
            AddNeighboursToDir(tile.leftNeighbours, 1, t, tileIndex, tempSets);
            AddNeighboursToDir(tile.upNeighbours, 2, t, tileIndex, tempSets);
            AddNeighboursToDir(tile.downNeighbours, 3, t, tileIndex, tempSets);
            AddNeighboursToDir(tile.aboveNeighbours, 4, t, tileIndex, tempSets);
            AddNeighboursToDir(tile.belowNeighbours, 5, t, tileIndex, tempSets);
        }

        for (int i = 0; i < 6 * T; i++) propagator[i] = tempSets[i].ToArray();

        // Pesos para la entropía de Shannon
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

    private void AddNeighboursToDir(List<Tile> neighbours, int dir, int t,
        Dictionary<Tile, int> tileIndex, List<int>[] sets)
    {
        if (neighbours == null) return;
        foreach (Tile n in neighbours)
            if (tileIndex.TryGetValue(n, out int nIdx))
                sets[OPPOSITE[dir] * T + nIdx].Add(t);
    }

    // =========================================================================
    // INICIALIZACIÓN DEL WAVE  (Model.Init en Gumin)
    // =========================================================================

    private void InitWave()
    {
        MX = dimensionsX; MY = dimensionsY; MZ = dimensionsZ;
        N = MX * MY * MZ;

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

        for (int i = 0; i < N; i++)
        {
            observed[i] = -1;
            sumsOfOnes[i] = T;
            sumsOfWeights_c[i] = sumOfWeights;
            sumsOfWeightLogWeights_c[i] = sumOfWeightLogWeights;
            entropies[i] = startingEntropy;

            for (int t = 0; t < T; t++)
            {
                wave[i * T + t] = true;

                int x = i % MX;
                int z = (i / MX) % MZ;
                int y = i / (MX * MZ);

                for (int d = 0; d < 6; d++)
                {
                    compatible[(i * T + t) * 6 + d] = propagator[OPPOSITE[d] * T + t].Length;
                }
            }
        }
    }

    // =========================================================================
    // BUCLE PRINCIPAL  (Model.Run en Gumin)
    // =========================================================================

    private bool RunAlgorithm()
    {
        while (true)
        {
            int node = SelectNextCell();
            if (node == -2) return false; // contradicción
            if (node == -1) return true;  // todo colapsado

            if (!CollapseCell(node)) return false;
            if (!Propagate()) return false;
        }
    }

    // =========================================================================
    // SELECCIÓN DE CELDA  (Model.NextUnobservedNode en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.NextUnobservedNode(). Selecciona la celda no colapsada
    /// con menor entropía de Shannon (heurística MRV con ruido estocástico
    /// para evitar patrones deterministas).
    /// Devuelve -1 si todas las celdas están colapsadas (solución encontrada),
    /// o -2 si se detecta una contradicción.
    /// </summary>
    private int SelectNextCell()
    {
        double minEntropy = double.MaxValue;
        int argMin = -1;

        for (int i = 0; i < N; i++)
        {
            if (observed[i] >= 0) continue; // ya colapsada

            int count = sumsOfOnes[i];
            if (count == 0) return -2; // contradicción: dominio vacío

            double e = entropies[i] + 1e-6 * rng.NextDouble(); // tie-breaking estocástico
            if (e < minEntropy) { minEntropy = e; argMin = i; }
        }

        return argMin; // -1 si todas colapsadas
    }

    // =========================================================================
    // COLAPSO DE CELDA  (Model.Observe en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Observe(). Selecciona aleatoriamente un tile posible
    /// en la celda argMin, ponderado por weights, y prohíbe todos los demás.
    /// </summary>
    private bool CollapseCell(int i)
    {
        for (int t = 0; t < T; t++) distribution[t] = wave[i * T + t] ? weights[t] : 0.0;

        int chosen = SampleWeighted(distribution, rng.NextDouble());

        for (int t = 0; t < T; t++)
            if (wave[i * T + t] && t != chosen)
                Ban(i, t);

        observed[i] = chosen;
        return true;
    }

    // =========================================================================
    // UTILIDAD: MUESTREO PONDERADO  (Extensions.Random en Gumin)
    // =========================================================================

    /// <summary>
    /// Selección aleatoria ponderada fiel a la función distribution.Random()
    /// de Gumin (Extensions.cs). Devuelve el índice del elemento seleccionado.
    /// </summary>
    private static int SampleWeighted(double[] dist, double r)
    {
        double total = 0;
        for (int i = 0; i < dist.Length; i++) total += dist[i];

        double threshold = r * total;
        double cumulative = 0;
        for (int i = 0; i < dist.Length; i++)
        {
            cumulative += dist[i];
            if (cumulative >= threshold) return i;
        }

        return dist.Length - 1; // fallback numérico
    }

    // =========================================================================
    // PROPAGACIÓN AC-4  (Model.Propagate en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Propagate(). Implementa AC-4 en 6 direcciones.
    /// </summary>
    private bool Propagate()
    {
        while (stacksize > 0)
        {
            if (contradiction) break;

            (int i1, int t1) = stack[--stacksize];

            int x1 = i1 % MX;
            int z1 = (i1 / MX) % MZ;
            int y1 = i1 / (MX * MZ);

            for (int d = 0; d < 6; d++)
            {
                int x2 = x1 + DX[d];
                int y2 = y1 + DY[d];
                int z2 = z1 + DZ[d];

                if (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ) continue;

                int i2 = x2 + z2 * MX + y2 * MX * MZ;
                int[] supported = propagator[d * T + t1];

                for (int l = 0; l < supported.Length; l++)
                {
                    int t2 = supported[l];
                    ref int comp = ref compatible[(i2 * T + t2) * 6 + d];
                    comp--;
                    if (comp == 0) Ban(i2, t2);
                }
            }
        }

        return !contradiction;
    }

    // =========================================================================
    // BAN  (Model.Ban en Gumin)
    // =========================================================================

    private void Ban(int i, int t)
    {
        wave[i * T + t] = false;

        int baseComp = (i * T + t) * 6;
        for (int d = 0; d < 6; d++) compatible[baseComp + d] = 0;

        stack[stacksize++] = (i, t);

        sumsOfOnes[i]--;
        sumsOfWeights_c[i] -= weights[t];
        sumsOfWeightLogWeights_c[i] -= weightLogWeights[t];

        if (sumsOfOnes[i] == 0)
        {
            contradiction = true;
        }
        else
        {
            double s = sumsOfWeights_c[i];
            entropies[i] = (s > 0)
                ? Math.Log(s) - sumsOfWeightLogWeights_c[i] / s
                : 0;
        }
    }

    // =========================================================================
    // INSTANCIACIÓN VISUAL
    // =========================================================================

    private void InstantiateTiles()
    {
        ClearOutput();

        Vector3 origin = (outputParent != null)
            ? outputParent.position
            : transform.position;

        for (int i = 0; i < N; i++)
        {
            int tIdx = observed[i];
            if (tIdx < 0) continue;

            Tile tileRef = tileObjects[tIdx];

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