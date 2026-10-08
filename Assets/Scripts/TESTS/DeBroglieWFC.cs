using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using DeBroglie;
using DeBroglie.Constraints;
using DeBroglie.Models;
using DeBroglie.Topo;
using DeBroglie.Wfc;

// Aliases para evitar choque con tipos propios del proyecto:
//   - Direction ya existe en Cell.cs como enum (Up, Down, Left, Right, Above, Below).
//   - Tile ya existe en Tile.cs como MonoBehaviour.
using DBDirection = DeBroglie.Topo.Direction;
using DBTile = DeBroglie.Tile;
using Resolution = DeBroglie.Resolution;

/// <summary>
/// Adaptador de DeBroglie como competidor independiente en el benchmark.
///
/// Posicionamiento:
/// ----------------
/// DeBroglie es la base algorítmica
/// open-source que sustenta Tessera, el plugin comercial de Unity para
/// WFC 3D citado en el estado del arte. Esta implementación es totalmente
/// independiente de WaveFunctionGame_REFACTOR: obtiene su tileset
/// directamente desde TilePreprocessor, gestiona su propio output visual
/// y replica las restricciones globales desde los datos del tileset.
///
/// Independencia respecto a REFACTOR:
/// -----------------------------------
///   - Tileset: obtenido via TilePreprocessor.Preprocess(), no de REFACTOR.
///   - Dimensiones: propias (deben coincidir con REFACTOR para benchmark).
///   - Output visual: propio (InstantiateTiles, similar a GuminWFC).
///   - Restricciones: replicadas desde los datos de los tiles, no de gridComponents.
///   - Eventos: propios, de instancia (contrato IWFCGenerator: OnStart/OnEnd/
///     OnIncompatibility). Ya NO reutiliza los eventos estáticos de REFACTOR.
///
/// Restricciones globales (applyGlobalConstraints = true):
/// --------------------------------------------------------
///   - LIMIT (bordes): Select manual de LIMIT en el perímetro de y=1, igual que
///     MyWFC. No se usa BorderConstraint porque este afectaría toda la columna
///     vertical de cada cara, mientras que LIMIT solo existe en y=1.
///   - Floor y ceiling: Select de las capas físicas y=0 e y=dimensionsY-1,
///     respectivamente, igual que MyWFC.
///   - Fixed tiles: propagator.Select() en posiciones aleatorias para tiles
///     con fixedTile > 0 en el tileset.
///
/// Cronómetro:
/// -----------
/// Idéntico al de REFACTOR. Mide únicamente propagator.Run(). Toda la
/// construcción del modelo, topología, constraints y restricciones previas
/// al bucle ocurren fuera del cronómetro.
/// </summary>
public class DeBroglieWFC : MonoBehaviour, IWFCGenerator, IWFCSolveTimer
{
    // Eventos de instancia (contrato IWFCGenerator). Antes se reutilizaban los
    // eventos estáticos de WaveFunctionGame_REFACTOR; ahora DeBroglie es autónomo.
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

    /// <summary>
    /// Celdas con más de una opción tras construir el propagador y aplicar las
    /// restricciones globales (fuera del cronómetro). Incluye las capas y=0 e
    /// y=dimY-1 mientras la propagación no las haya reducido a una sola tile.
    /// </summary>
    private int CountCellsToDecide(TilePropagator propagator)
    {
        var sets = propagator.ToValueSets<Tile>();
        int count = 0;
        for (int x = 0; x < dimensionsX; x++)
            for (int y = 0; y < dimensionsY; y++)
                for (int z = 0; z < dimensionsZ; z++)
                {
                    var s = sets.Get(x, y, z);
                    if (s != null && s.Count > 1) count++;
                }
        return count;
    }

    private void CollectGarbageOutsideTimer()
    {
        if (!collectGarbageBeforeSolve) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Header("Etiqueta del experimento (columna del CSV)")]
    public string algorithmLabel = "debroglie_full";

    // IWFCGenerator
    public int DimensionsX => dimensionsX;
    public int DimensionsY => dimensionsY;
    public int DimensionsZ => dimensionsZ;
    public string AlgorithmLabel => algorithmLabel;
    public Tile[] TileObjects => tileObjects;

    [Header("Preprocesado de tiles")]
    [Tooltip("TilePreprocessor compartido por todos los solvers.")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Input – tiles base (sin rotar)")]
    [Tooltip("Array de tiles base. TilePreprocessor añadirá variantes rotadas al primer Generate().")]
    public Tile[] tileObjects;

    [Header("Dimensiones del grid 3D")]
    [Tooltip("Deben coincidir con las de REFACTOR para que el benchmark sea comparable.")]
    public int dimensionsX = 10;
    public int dimensionsY = 5;
    public int dimensionsZ = 10;
    public float gridSize = 1f;

    [Header("Restricciones globales")]
    [Tooltip("Si true, replica en DeBroglie las restricciones globales del framework " +
             "(LIMIT vía BorderConstraint, floor/ceiling vía Ban, fixed tiles vía Select). " +
             "Si false, AC-4 puro sin restricciones.")]
    [SerializeField] private bool applyGlobalConstraints = true;

    [Header("Tiles de infraestructura (para restricciones de suelo/techo)")]
    [Tooltip("Tile de suelo sólido. Si se asigna, y=1 solo admite tiles " +
             "cuyo belowNeighbours incluye esta tile.")]
    [SerializeField] private Tile floorTile;
    [Tooltip("Tile de techo vacío. Si se asigna, y=dimensionsY-2 solo admite tiles " +
             "cuyo aboveNeighbours incluye esta tile.")]
    [SerializeField] private Tile ceilingTile;
    [Tooltip("Tile LIMIT fijada en el anillo de y=1. Si queda vacía, se busca la tile base con tileType = limit.")]
    [SerializeField] private Tile limitTile;

    [Header("Salida visual")]
    [Tooltip("Transform padre bajo el que se instancian los tiles del resultado.")]
    public Transform outputParent;

    [Header("Reproducibilidad")]
    [Tooltip("Si > 0, fija la semilla del RNG. Usa 0 para semilla independiente " +
             "en cada Generate(), recomendado para reportar media ± σ.")]
    [SerializeField] private int fixedSeed = 0;

    [Header("Reintentos ante contradicción")]
    [Tooltip("Máximo de reintentos por generación antes de abortar. Evita bucles " +
             "infinitos si el tileset es irresoluble. Debe ser holgado para no " +
             "truncar mediciones legítimas.")]
    [SerializeField] private int maxRetries = 10000;

    // ── estado interno ───────────────────────────────────────────────
    private Dictionary<Tile, DBTile> toDB;
    private HashSet<Tile> limitTiles;
    private bool tilesetPreprocessed = false;
    private Tile[] _resolvedTiles;

    // ════════════════════════════════════════════════════════════════
    //  Detección de tiles virtuales LIMIT
    //  (consistente con REFACTOR.PreprocessTileSet: filtra por tileType)
    // ════════════════════════════════════════════════════════════════

    private static bool IsLimit(Tile t) =>
        t != null && t.tileType != null &&
        t.tileType.ToLowerInvariant() == "limit";

    // ════════════════════════════════════════════════════════════════
    //  Unity Lifecycle
    // ════════════════════════════════════════════════════════════════

    // PREPROCESADO DIFERIDO
    // El preprocesado NO se hace en Awake(): TilePreprocessor escribe las listas
    // de vecinos dentro de las tiles base, que son los mismos prefabs para los
    // tres solvers. Si varios solvers preprocesaran en Awake (MyWFC siempre
    // está activo porque comparte GameObject con el arnés), el último en
    // ejecutarse sobrescribiría las listas de los demás con referencias a SUS
    // variantes rotadas, y los otros solvers perderían adyacencias. Por eso
    // solo preprocesa el solver al que se le pide generar, en su primer
    // Generate(), fuera del cronómetro.
    private void Awake()
    {
        if (tilePreprocessor == null)
            Debug.LogError("[DeBroglieWFC] TilePreprocessor no asignado en el Inspector.");
    }

    private bool EnsurePreprocessed()
    {
        if (tilesetPreprocessed) return true;
        if (tilePreprocessor == null)
        {
            Debug.LogError("[DeBroglieWFC] TilePreprocessor no asignado en el Inspector.");
            return false;
        }

        // Expande tileObjects con variantes rotadas y calcula la tabla de
        // vecinos. LIMIT se mantiene en las listas de vecinos para el borde,
        // pero se excluye del modelo.
        tilePreprocessor.Preprocess(ref tileObjects);
        tilesetPreprocessed = true;
        return true;
    }

    // ════════════════════════════════════════════════════════════════
    //  API pública
    // ════════════════════════════════════════════════════════════════

    public void Generate()
    {
        if (!EnsurePreprocessed()) return;
        ClearOutput();      // idempotente: permite repetir Generate() en el benchmark
        GenerateInternal();
    }

    public void Regenerate()
    {
        ClearOutput();
        GenerateInternal();
    }

    // ════════════════════════════════════════════════════════════════
    //  Pipeline de generación
    // ════════════════════════════════════════════════════════════════

    private void GenerateInternal()
    {
        LastSolveTime = 0;
        LastCellsToDecide = 0;

        // 1. Construcción del modelo y la topología (FUERA del cronómetro).
        IndexTiles();
        AdjacentModel model = BuildAdjacentModel();
        GridTopology topology = BuildTopology();

        int attempt = 0;
        Resolution result = Resolution.Contradiction;
        TilePropagator propagator = null;
        double solveTime = 0;
        int cellsToDecide = 0;   // del intento en curso; tras el bucle, el del intento con éxito

        OnStart?.Invoke();

        while (attempt < maxRetries)
        {
            // 2. Preparación del intento (FUERA del cronómetro): propagador y
            //    restricciones globales. En DeBroglie, Select/Ban propagan de
            //    inmediato, así que la propagación inicial de las restricciones
            //    queda fuera, igual que InitWave() en MyWFC y GuminWFC.
            propagator = BuildPropagator(model, topology);

            if (applyGlobalConstraints)
            {
                ApplyInfrastructureConstraints(propagator);
                ApplyFixedTiles(propagator);
            }
            cellsToDecide = CountCellsToDecide(propagator);
            CollectGarbageOutsideTimer();

            // 3. Resolución (DENTRO del cronómetro)
            solveWatch.Restart();
            result = propagator.Run();
            solveWatch.Stop();
            solveTime += solveWatch.Elapsed.TotalSeconds;

            if (result == Resolution.Decided)
                break;

            // Contradicción: se notifica y se reintenta. El tiempo de resolución
            // de este intento ya está sumado en solveTime.
            OnIncompatibility?.Invoke();
            attempt++;
        }

        // 4. Resultado e instanciación (FUERA del cronómetro).
        if (result == Resolution.Decided)
        {
            LastSolveTime = solveTime;
            LastCellsToDecide = cellsToDecide;
            StoreResolvedTiles(propagator);          // poblar antes del evento
            OnEnd?.Invoke();
            InstantiateTiles(propagator);
        }
        else
        {
            // Se agotaron los reintentos: no se dispara OnEnd y la medición
            // queda incompleta, sin contaminar las estadísticas.
            Debug.LogError($"[DeBroglieWFC] Sin solución tras {maxRetries} reintentos. " +
                           "Revisa la resolubilidad del tileset o aumenta maxRetries.");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Indexado: construye los mapeos Tile ↔ DBTile
    //  e identifica las tiles LIMIT
    // ════════════════════════════════════════════════════════════════

    private void IndexTiles()
    {
        toDB = new Dictionary<Tile, DBTile>();
        limitTiles = new HashSet<Tile>();

        // LIMIT debe formar parte del modelo para poder fijarlo físicamente en
        // el anillo, igual que MyWFC. Después se prohíbe en cualquier otra celda.
        foreach (Tile t in tileObjects)
        {
            toDB[t] = new DBTile(t);
            if (IsLimit(t)) limitTiles.Add(t);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Modelo de adyacencia
    // ════════════════════════════════════════════════════════════════

    private AdjacentModel BuildAdjacentModel()
    {
        var directionMap = new (System.Func<Tile, List<Tile>>, DBDirection)[]
        {
            (t => t.rightNeighbours, DBDirection.XPlus),
            (t => t.leftNeighbours,  DBDirection.XMinus),
            (t => t.upNeighbours,    DBDirection.ZPlus),
            (t => t.downNeighbours,  DBDirection.ZMinus),
            (t => t.aboveNeighbours, DBDirection.YPlus),
            (t => t.belowNeighbours, DBDirection.YMinus),
        };

        var model = new AdjacentModel(DirectionSet.Cartesian3d);

        foreach (Tile src in tileObjects)
        {
            if (!toDB.ContainsKey(src)) continue;

            model.SetFrequency(toDB[src], src.probability);

            foreach (var (selector, dir) in directionMap)
            {
                var neighbours = selector(src);
                if (neighbours == null) continue;
                foreach (Tile dst in neighbours)
                {
                    if (!toDB.ContainsKey(dst)) continue;
                    model.AddAdjacency(toDB[src], toDB[dst], dir);
                }
            }
        }

        return model;
    }

    private GridTopology BuildTopology()
    {
        // width=X, height=Y, depth=Z. periodic=false: mapas no toroidales.
        return new GridTopology(dimensionsX, dimensionsY, dimensionsZ, periodic: false);
    }

    // ════════════════════════════════════════════════════════════════
    //  Propagator y restricciones de infraestructura
    // ════════════════════════════════════════════════════════════════

    private TilePropagator BuildPropagator(AdjacentModel model, GridTopology topology)
    {
        var rng = fixedSeed > 0 ? new System.Random(fixedSeed) : new System.Random();

        // Sin Constraints nativos: LIMIT se aplica manualmente,
        // porque BorderConstraint de DeBroglie restringe la columna vertical
        // COMPLETA de cada cara (todo Y), mientras que en REFACTOR el marcador
        // LIMIT solo existe en la capa y=1 (ver DefineMapLimits, que itera
        // únicamente GetLayerCells(1)). Usar BorderConstraint aquí restringiría
        // incorrectamente también y=0, y=2..dimensionsY-2 e y=dimensionsY-1 a
        // tiles "aceptoras de LIMIT", que normalmente no son compatibles con
        // tiles de interior → contradicción determinista en cualquier mapa
        // con dimensionsY > 2.
        var options = new TilePropagatorOptions
        {
            BacktrackType = BacktrackType.None,
            ModelConstraintAlgorithm = ModelConstraintAlgorithm.Ac4,
            IndexPickerType = IndexPickerType.HeapMinEntropy,
            TilePickerType = TilePickerType.Weighted,
            RandomDouble = rng.NextDouble,
        };

        return new TilePropagator(model, topology, options);
    }

    /// <summary>
    /// Replica las celdas fijas de MyWFC: suelo en y=0, techo en y=Y-1 y
    /// LIMIT en el anillo de y=1. LIMIT se prohíbe fuera del anillo para que
    /// no entre en el dominio de celdas libres.
    /// </summary>
    private void ApplyInfrastructureConstraints(TilePropagator propagator)
    {
        if (floorTile != null && toDB.TryGetValue(floorTile, out DBTile floor))
            for (int x = 0; x < dimensionsX; x++)
                for (int z = 0; z < dimensionsZ; z++)
                    propagator.Select(x, 0, z, floor);
        else if (floorTile != null)
            Debug.LogError("[DeBroglieWFC] floorTile no está incluido en tileObjects.");

        if (ceilingTile != null && toDB.TryGetValue(ceilingTile, out DBTile ceiling))
            for (int x = 0; x < dimensionsX; x++)
                for (int z = 0; z < dimensionsZ; z++)
                    propagator.Select(x, dimensionsY - 1, z, ceiling);
        else if (ceilingTile != null)
            Debug.LogError("[DeBroglieWFC] ceilingTile no está incluido en tileObjects.");

        Tile selectedLimit = limitTile != null && toDB.ContainsKey(limitTile)
            ? limitTile
            : tileObjects.FirstOrDefault(t => IsLimit(t) && t.rotation == Vector3.zero)
              ?? limitTiles.FirstOrDefault();
        if (selectedLimit == null || !toDB.TryGetValue(selectedLimit, out DBTile limit))
        {
            Debug.LogError("[DeBroglieWFC] No se encontró una limitTile válida en tileObjects.");
            return;
        }

        for (int x = 0; x < dimensionsX; x++)
            for (int y = 0; y < dimensionsY; y++)
                for (int z = 0; z < dimensionsZ; z++)
                {
                    bool limitRing = y == 1 &&
                        (x == 0 || x == dimensionsX - 1 || z == 0 || z == dimensionsZ - 1);
                    if (limitRing)
                        propagator.Select(x, y, z, limit);
                    else
                        foreach (Tile candidate in limitTiles)
                            propagator.Ban(x, y, z, toDB[candidate]);
                }
    }

    // ════════════════════════════════════════════════════════════════
    //  Fixed tiles
    //
    //  Replica la lógica de REFACTOR.CreateFixedTiles(): para cada tile
    //  con fixedTile > 0, la coloca en posiciones aleatorias del mapa
    //  usando propagator.Select(). Equivalente al PlaceInfrastructureTile
    //  de REFACTOR pero expresado en el API de DeBroglie.
    // ════════════════════════════════════════════════════════════════

    // TILES FIJAS: especificación común a MyWFC y DeBroglieWFC.
    // Una tile base con fixedTile = k produce exactamente k tiles fijas, cada una
    // con una rotación elegida al azar entre la base y sus variantes.
    // Posiciones: capas 1..dimY-2, excluido el perímetro de y=1, que en MyWFC
    // ocupa el anillo LIMIT (mismo conjunto de celdas candidatas en ambos).
    private void ApplyFixedTiles(TilePropagator propagator)
    {
        var rng = fixedSeed > 0 ? new System.Random(fixedSeed + 1) : new System.Random();
        var usedPositions = new HashSet<(int, int, int)>();

        foreach (Tile proto in tileObjects)
        {
            if (IsLimit(proto) || !toDB.ContainsKey(proto)) continue;
            if (proto.fixedTile <= 0) continue;
            if (proto.rotation != Vector3.zero) continue;   // solo tiles base

            List<Tile> variants = GetRotationVariants(proto).FindAll(v => toDB.ContainsKey(v));

            for (int i = 0; i < proto.fixedTile; i++)
            {
                // Hasta 200 intentos para encontrar una celda candidata libre.
                bool placed = false;
                for (int attempt = 0; attempt < 200 && !placed; attempt++)
                {
                    int x = rng.Next(0, dimensionsX);
                    int y = rng.Next(1, Mathf.Max(1, dimensionsY - 1));
                    int z = rng.Next(0, dimensionsZ);

                    bool ringY1 = y == 1 && (x == 0 || x == dimensionsX - 1 ||
                                             z == 0 || z == dimensionsZ - 1);
                    if (ringY1 || usedPositions.Contains((x, y, z))) continue;

                    usedPositions.Add((x, y, z));
                    Tile chosen = variants[rng.Next(0, variants.Count)];
                    propagator.Select(x, y, z, toDB[chosen]);
                    placed = true;
                }
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

    // ════════════════════════════════════════════════════════════════
    //  Instanciación visual
    // ════════════════════════════════════════════════════════════════

    private void InstantiateTiles(TilePropagator propagator)
    {
        ClearOutput();

        var output = propagator.ToValueArray<Tile>();
        Vector3 origin = outputParent != null ? outputParent.position : transform.position;

        for (int x = 0; x < dimensionsX; x++)
            for (int y = 0; y < dimensionsY; y++)
                for (int z = 0; z < dimensionsZ; z++)
                {
                    Tile tileRef = output.Get(x, y, z);
                    if (tileRef == null) continue;

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
            Destroy(outputParent.GetChild(i).gameObject);
    }

    // ════════════════════════════════════════════════════════════════
    //  API pública para WFCQualityMetrics
    // ════════════════════════════════════════════════════════════════

    /// <summary>Tile resuelta en el índice lineal i = x + z*dimX + y*dimX*dimZ. Null si no hay dato.</summary>
    public Tile GetResolvedTile(int index)
    {
        if (_resolvedTiles == null || index < 0 || index >= _resolvedTiles.Length) return null;
        return _resolvedTiles[index];
    }

    public bool IsInfrastructureTile(Tile tile) => tile != null && tile.isInfrastructureTile;

    /// <summary>Guarda el output resuelto en el array plano _resolvedTiles.
    /// Índice: x + z*dimX + y*dimX*dimZ (igual que REFACTOR y GuminWFC).</summary>
    private void StoreResolvedTiles(TilePropagator propagator)
    {
        var output = propagator.ToValueArray<Tile>();
        int total = dimensionsX * dimensionsY * dimensionsZ;
        _resolvedTiles = new Tile[total];
        for (int x = 0; x < dimensionsX; x++)
            for (int y = 0; y < dimensionsY; y++)
                for (int z = 0; z < dimensionsZ; z++)
                    _resolvedTiles[x + z * dimensionsX + y * dimensionsX * dimensionsZ] = output.Get(x, y, z);
    }
}
