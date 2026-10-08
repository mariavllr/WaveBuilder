// ============================================================================
// MyWFC.cs
//
// Solver WFC 3D del framework (AC-4) como componente de Unity.
//
// Desde la revisión R2 este componente NO contiene una copia propia del
// algoritmo: compila el tileset con RBTilesetCompiler (TilePreprocessor del
// framework) y resuelve con el núcleo ThisWorkSolver
// (TESTS/RuntimeBenchmark/Core/RBSolverThisWork.cs), el mismo código que mide
// el benchmark de runtime del artículo. El código publicado y el evaluado son,
// por tanto, idénticos.
//
// Cambios de comportamiento respecto a la versión anterior de MyWFC.cs:
//   · Corregido el baneo inicial de tiles sin vecino posible (usaba la
//     dirección opuesta para comprobar el soporte).
//   · Se comprueba la compatibilidad entre celdas preasignadas adyacentes
//     (antes podía generar mapas con adyacencias inválidas, p. ej. una tile
//     fija junto al anillo LIMIT).
//   · Tiles fijas definidas en el Inspector (tile, cantidad, capa) en lugar de
//     Tile.fixedTile del prefab.
//   · Semillas deterministas: con seed = 0, la generación r usa la misma
//     semilla que el run r del benchmark (mismo tileset y tamaño), así que
//     produce el mismo mapa. Con seed != 0, todas las generaciones usan esa semilla.
//   · Los prefabs originales no se modifican (se preprocesan copias).
//
// Contrato IWFCGenerator e IWFCSolveTimer sin cambios (WFCQualityMetrics sigue funcionando).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WFCRuntimeBenchmark;

public class MyWFC : MonoBehaviour, IWFCGenerator, IWFCSolveTimer
{
    [Serializable]
    public class FixedTileEntry
    {
        [Tooltip("Tile base (prefab sin rotar). En cada colocación se elige al azar una de sus rotaciones.")]
        public Tile baseTile;
        [Min(1)] public int count = 1;
        [Tooltip("Capa y donde se coloca. -1 = cualquier celda libre.")]
        public int layer = 1;
    }

    // =========================================================================
    // INSPECTOR
    // =========================================================================

    [Header("Preprocesado de tiles")]
    [Tooltip("TilePreprocessor del framework (generación de variantes rotadas y adyacencias por sockets).")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Input – tiles base (sin rotar)")]
    public Tile[] tileObjects;

    [Header("Tiles de infraestructura (para las restricciones)")]
    [SerializeField] private Tile floorTile;
    [SerializeField] private Tile emptyTile;
    [SerializeField] private Tile limitTile;

    [Header("Dimensiones del grid 3D")]
    public int dimensionsX = 10;
    public int dimensionsY = 3;
    public int dimensionsZ = 10;
    public float gridSize = 1f;

    [Header("Parámetros del algoritmo")]
    [Tooltip("0 = semillas deterministas del benchmark (la generación r reproduce el run r). Otro valor = semilla fija para todas las generaciones.")]
    public int seed = 0;
    [Tooltip("Semilla base (la misma que RuntimeBenchmarkRunner.baseSeed).")]
    public long baseSeed = 20261007;
    [Tooltip("Nombre del tileset tal como aparece en el benchmark (nature, desert, farm). Interviene en la derivación de semillas.")]
    public string tilesetName = "nature";
    [Tooltip("Índice de la próxima generación (se incrementa en cada Generate).")]
    public int generationIndex = 0;
    [Tooltip("Límite de intentos (reinicios) por generación.")]
    public int maxRetries = 1000;
    [Tooltip("Selector de mínima entropía. Heap = montículo indexado (versión evaluada en el artículo).")]
    public ThisWorkSelector selector = ThisWorkSelector.Heap;

    [Header("Restricciones (config del experimento)")]
    [Tooltip("Muestreo ponderado por probability. Off = uniforme.")]
    public bool probabilityConstraint = true;
    [Tooltip("Negative rules (excludedNeighbours), aplicadas al inferir las adyacencias.")]
    public bool excludedNeighborConstraint = true;
    [Tooltip("Capas: suelo SOLID en y=0 y techo EMPTY en y=dimY-1.")]
    public bool floorCeilingConstraint = true;
    [Tooltip("Tiles fijas según la lista 'fixedTiles'.")]
    public bool fixedTilesConstraint = true;
    [Tooltip("Anillo de LIMIT en el perímetro de y=1.")]
    public bool borderConstraint = true;

    [Header("Tiles fijas (pre-assignment)")]
    public List<FixedTileEntry> fixedTiles = new List<FixedTileEntry>();

    [Header("Etiqueta del experimento (columna del CSV)")]
    public string algorithmLabel = "mi_wfc_full";

    [Header("Salida visual")]
    [Tooltip("Transform padre bajo el que se instanciarán los tiles del resultado.")]
    public Transform outputParent;
    public bool generateOnStart = false;

    [Header("Medición")]
    [Tooltip("GC.Collect() antes de cada fase cronometrada, fuera del cronómetro (igual que el benchmark).")]
    public bool collectGarbageBeforeSolve = true;

    // =========================================================================
    // EVENTOS (contrato IWFCGenerator)
    // =========================================================================

    public event Action OnStart;
    public event Action OnEnd;
    public event Action OnIncompatibility;

    // =========================================================================
    // IWFCGenerator / IWFCSolveTimer
    // =========================================================================

    public int DimensionsX => dimensionsX;
    public int DimensionsY => dimensionsY;
    public int DimensionsZ => dimensionsZ;
    public string AlgorithmLabel => algorithmLabel;
    /// <summary>Antes del primer Generate: tiles base del Inspector. Después: tiles del dominio (base + variantes, sin LIMIT).</summary>
    public Tile[] TileObjects => tileObjects;

    public bool IsInfrastructureTile(Tile tile) => tile != null && tile.isInfrastructureTile;

    /// <summary>Segundos de resolución de la última generación (Σ t_init + t_search de sus intentos).</summary>
    public double LastSolveTime { get; private set; }
    /// <summary>Celdas libres con más de una opción tras la propagación inicial del intento con éxito.</summary>
    public int LastCellsToDecide { get; private set; }
    /// <summary>Número de intentos fallidos (contradicciones) en la última generación.</summary>
    public int FailCount { get; private set; }
    /// <summary>Hash de la última solución (comparable con solution_hash de runs.csv).</summary>
    public string LastSolutionHash { get; private set; } = "";
    public int LastSolverSeed { get; private set; }

    // =========================================================================
    // ESTADO
    // =========================================================================

    private CompiledTileset compiled;
    private LevelSpec level;
    private ThisWorkSolver solver;
    private Tile[] allTiles;          // id de tile del CompiledTileset → Tile (copias preprocesadas)
    private int[] solution;
    private GameObject scratchRoot;
    private bool compiledOk;

    // =========================================================================
    // UNITY
    // =========================================================================

    void Awake()
    {
        if (tilePreprocessor == null)
            Debug.LogError("[MyWFC] TilePreprocessor no asignado en el Inspector.");
    }

    void Start()
    {
        if (generateOnStart) Generate();
    }

    // =========================================================================
    // COMPILACIÓN (una vez, en el primer Generate, fuera de toda medición)
    // =========================================================================

    private bool EnsureCompiled()
    {
        if (compiledOk) return true;
        if (tilePreprocessor == null) { Debug.LogError("[MyWFC] TilePreprocessor no asignado en el Inspector."); return false; }

        try
        {
            scratchRoot = new GameObject("MyWFC_Tiles");
            scratchRoot.SetActive(false);
            scratchRoot.transform.SetParent(transform, false);

            var fixedInputs = fixedTilesConstraint
                ? fixedTiles.Where(f => f.baseTile != null)
                            .Select(f => new RBTilesetCompiler.FixedInput { tile = f.baseTile, count = f.count, layer = f.layer }).ToList()
                : new List<RBTilesetCompiler.FixedInput>();

            compiled = RBTilesetCompiler.Compile(string.IsNullOrEmpty(tilesetName) ? name : tilesetName,
                tileObjects, floorTile, emptyTile, limitTile, fixedInputs,
                excludedNeighborConstraint, !probabilityConstraint,
                tilePreprocessor, scratchRoot.transform, true, out allTiles, "[MyWFC]");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return false;
        }

        bool boundary = borderConstraint;
        if (boundary && compiled.LimitTile < 0)
        {
            Debug.LogWarning("[MyWFC] borderConstraint activo pero sin limitTile: se desactiva.");
            boundary = false;
        }
        bool fixedOn = fixedTilesConstraint && compiled.FixedTiles.Count > 0;
        if (fixedTilesConstraint && !fixedOn)
            Debug.LogWarning("[MyWFC] fixedTilesConstraint activo pero la lista 'fixedTiles' está vacía.");

        level = new LevelSpec
        {
            Benchmark = "-", Id = "MYWFC", Order = -1,
            Layers = floorCeilingConstraint, Boundary = boundary, FixedTiles = fixedOn, NegativeRules = excludedNeighborConstraint,
        };

        solver = new ThisWorkSolver(selector);
        solver.Prepare(compiled, dimensionsX, dimensionsY, dimensionsZ, level);

        // Igual que antes: tras el preprocesado, TileObjects expone el dominio (base + variantes, sin LIMIT).
        tileObjects = allTiles.Where((t, i) => compiled.InDomain[i]).ToArray();
        compiledOk = true;
        return true;
    }

    // =========================================================================
    // API PÚBLICA
    // =========================================================================

    public void Generate()
    {
        if (!EnsureCompiled()) return;

        FailCount = 0;
        LastSolveTime = 0;
        LastCellsToDecide = 0;
        solution = null;

        int mx = dimensionsX, my = dimensionsY, mz = dimensionsZ;
        int run = generationIndex++;
        ulong bs = (ulong)baseSeed;
        string ts = compiled.Name;

        int solverSeed;
        Func<int, int> instanceSeed;
        if (seed == 0)
        {
            solverSeed = SingleRun.BenchmarkSolverSeed(bs, ts, mx, my, mz, "measured", run);
            instanceSeed = a => SingleRun.BenchmarkInstanceSeed(bs, ts, mx, my, mz, "measured", run, a);
        }
        else
        {
            solverSeed = seed;
            int s0 = seed;
            instanceSeed = a => Seeds.Derive((ulong)(uint)s0, "instance|" + a);
        }

        OnStart?.Invoke();
        SingleRunResult r = SingleRun.Execute(compiled, solver, level, mx, my, mz, solverSeed, instanceSeed,
            maxRetries, collectGarbageBeforeSolve, true,
            () => { FailCount++; OnIncompatibility?.Invoke(); });

        LastSolverSeed = solverSeed;
        LastSolveTime = r.TSolveMs / 1000.0;

        if (!r.Solved)
        {
            Debug.LogError($"[MyWFC] Agotados {maxRetries} intentos sin solución.");
            return;
        }
        if (r.AdjacencyViolations > 0)
            Debug.LogError($"[MyWFC] Solución con {r.AdjacencyViolations} violaciones de adyacencia (no debería ocurrir).");

        solution = r.Solution;
        LastCellsToDecide = r.UndecidedAfterInit;
        LastSolutionHash = r.SolutionHash;

        OnEnd?.Invoke();
        InstantiateTiles();
    }

    /// <summary>Tile resuelta en el índice lineal i = x + z*MX + y*MX*MZ (incluye las preasignadas). Null si no hay solución.</summary>
    public Tile GetResolvedTile(int index)
    {
        if (solution == null || index < 0 || index >= solution.Length) return null;
        int id = solution[index];
        return id >= 0 && id < allTiles.Length ? allTiles[id] : null;
    }

    // =========================================================================
    // INSTANCIACIÓN VISUAL (fuera de toda medición)
    // =========================================================================

    private void InstantiateTiles()
    {
        ClearOutput();
        Vector3 origin = (outputParent != null) ? outputParent.position : transform.position;
        int MX = dimensionsX, MZ = dimensionsZ;

        for (int i = 0; i < solution.Length; i++)
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
