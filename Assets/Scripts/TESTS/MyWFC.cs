// ============================================================================
// MyWFC.cs
//
// Implementación propia de Wave Function Collapse en 3D con propagación AC-4
//
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public class MyWFC : MonoBehaviour, IWFCGenerator
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

    void Awake()
    {
        if (tilePreprocessor == null)
        {
            Debug.LogError("[MyWFC] TilePreprocessor no asignado en el Inspector.");
            return;
        }

        // El preprocesado se hace una vez por sesión: expande variantes rotadas
        // y calcula la tabla de vecinos (respetando excludedNeighborConstraint).
        tilePreprocessor.excludedNeighborConstraint = excludedNeighborConstraint;
        tilePreprocessor.Preprocess(ref tileObjects);

        // Filtrar LIMIT del dominio de colapso (se mantiene como tile fija de
        // borde; sus listas *Neighbours siguen usándose para el soporte).
        tileObjects = Array.FindAll(tileObjects, t => t.tileType != "limit");

        tilesetPreprocessed = true;
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
        if (!tilesetPreprocessed)
        {
            Debug.LogError("[MyWFC] Tileset no preprocesado. Asegúrate de que Awake() se haya ejecutado.");
            return;
        }
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

        if (!BuildPropagator())
        {
            Debug.LogError("[MyWFC] El propagador no pudo construirse (tileset vacío o inválido).");
            return;
        }
        rng = seed != 0 ? new System.Random(seed) : new System.Random();

        OnStart?.Invoke();

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ApplyGlobalConstraints(); // re-aleatoriza las tiles fijas en cada intento
            InitWave();

            if (RunAlgorithm())
            {
                OnEnd?.Invoke();
                InstantiateTiles();
                return;
            }

            FailCount++;
            OnIncompatibility?.Invoke();
        }

        Debug.LogWarning($"[MyWFC] Agotados {maxRetries} reintentos sin solución.");
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
    private void CreateFixedTiles()
    {
        foreach (Tile prototype in tileObjects)
        {
            if (prototype.fixedTile <= 0) continue;
            for (int k = 0; k < prototype.fixedTile; k++)
            {
                int target = PickRandomFreeCell();
                if (target < 0)
                {
                    Debug.LogWarning($"[MyWFC] No quedan celdas libres para tile fija {prototype.tileType}.");
                    break;
                }
                fixedCellTile[target] = prototype;
            }
        }
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

    private bool RunAlgorithm()
    {
        // La propagación inicial (InitWave) ya pudo detectar una contradicción.
        if (contradiction) return false;

        while (true)
        {
            int node = SelectNextCell();
            if (node == -2) return false; // contradicción
            if (node == -1) return true;  // todo colapsado

            if (!CollapseCell(node)) return false;
            if (!Propagate()) return false;
        }
    }

    private int SelectNextCell()
    {
        double minEntropy = double.MaxValue;
        int argMin = -1;

        for (int i = 0; i < N; i++)
        {
            if (observed[i] >= 0 || fixedCellTile[i] != null) continue; // colapsada o fija
            int count = sumsOfOnes[i];
            if (count == 0) return -2;

            double e = entropies[i] + 1e-6 * rng.NextDouble();
            if (e < minEntropy) { minEntropy = e; argMin = i; }
        }
        return argMin;
    }

    private bool CollapseCell(int i)
    {
        for (int t = 0; t < T; t++)
        {
            if (!wave[i * T + t]) { distribution[t] = 0.0; continue; }
            // Ponderado por probability, o uniforme si el flag está desactivado
            distribution[t] = probabilityConstraint ? weights[t] : 1.0;
        }

        int chosen = SampleWeighted(distribution, rng.NextDouble());

        for (int t = 0; t < T; t++)
            if (wave[i * T + t] && t != chosen)
                Ban(i, t);

        observed[i] = chosen;
        return true;
    }

    private static int SampleWeighted(double[] dist, double r)
    {
        double total = 0;
        for (int i = 0; i < dist.Length; i++) total += dist[i];
        if (total <= 0) return dist.Length - 1;

        double threshold = r * total;
        double cumulative = 0;
        for (int i = 0; i < dist.Length; i++)
        {
            cumulative += dist[i];
            if (cumulative >= threshold) return i;
        }
        return dist.Length - 1;
    }

    // =========================================================================
    // PROPAGACIÓN AC-4
    // =========================================================================

    private bool Propagate()
    {
        while (stacksize > 0 && !contradiction)
        {
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
                if (fixedCellTile[i2] != null) continue;

                int[] supported = propagator[d * T + t1];
                for (int l = 0; l < supported.Length; l++)
                {
                    int t2 = supported[l];
                    ref int comp = ref compatible[(i2 * T + t2) * 6 + d];
                    comp--;
                    if (comp == 0 && wave[i2 * T + t2]) Ban(i2, t2);
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

        if (sumsOfOnes[i] == 0)
            contradiction = true;
        else
        {
            double s = sumsOfWeights_c[i];
            entropies[i] = s > 0 ? Math.Log(s) - sumsOfWeightLogWeights_c[i] / s : 0;
        }
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
