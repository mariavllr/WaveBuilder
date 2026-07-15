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
///   - Única referencia de clase (no instancia): WaveFunctionGame_REFACTOR.Invoke*()
///     se usa para disparar los eventos estáticos que CalculateExecutionTime escucha.
///     Esto es una referencia de clase, no de objeto, y es aceptable en el diseño
///     del harness de benchmark.
///
/// Restricciones globales (applyGlobalConstraints = true):
/// --------------------------------------------------------
///   - LIMIT (bordes): Select manual restringido a la capa y=1 del perímetro
///     horizontal, replicando exactamente REFACTOR.DefineMapLimits(). No se usa
///     BorderConstraint nativo de DeBroglie porque este restringe la columna
///     vertical COMPLETA de cada cara (todo Y), mientras que LIMIT en REFACTOR
///     solo existe en y=1. Usar BorderConstraint aquí causaría contradicción
///     determinista en cualquier mapa con dimensionsY > 2.
///   - Floor: Ban en y=1 de tiles incompatibles con floorTile.belowNeighbours.
///     No se replica la capa física de floorTile (y=0); se restringe la capa
///     jugable inferior para que solo admita tiles compatibles con el suelo.
///   - Ceiling: análogo para y=dimensionsY-2.
///   - Fixed tiles: propagator.Select() en posiciones aleatorias para tiles
///     con fixedTile > 0 en el tileset.
///
/// Cronómetro:
/// -----------
/// Idéntico al de REFACTOR. Mide únicamente propagator.Run(). Toda la
/// construcción del modelo, topología, constraints y restricciones previas
/// al bucle ocurren fuera del cronómetro.
/// </summary>
public class DeBroglieWFC : MonoBehaviour
{
    [Header("Preprocesado de tiles")]
    [Tooltip("TilePreprocessor compartido por todos los solvers.")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Input – tiles base (sin rotar)")]
    [Tooltip("Array de tiles base. TilePreprocessor añadirá variantes rotadas en Awake.")]
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
    private Dictionary<DBDirection, List<DBTile>> acceptorsByDirection;
    private bool tilesetPreprocessed = false;
    private Tile[] _resolvedTiles;

    // Las seis direcciones del modelo Cartesian3d, declaradas explícitamente
    // para evitar Enum.GetValues que devuelve duplicados (WPlus/WMinus == ZPlus/ZMinus).
    private static readonly DBDirection[] Cartesian3dDirections = new[]
    {
        DBDirection.XPlus, DBDirection.XMinus,
        DBDirection.YPlus, DBDirection.YMinus,
        DBDirection.ZPlus, DBDirection.ZMinus,
    };

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

    private void Awake()
    {
        if (tilePreprocessor == null)
        {
            Debug.LogError("[DeBroglieWFC] TilePreprocessor no asignado en el Inspector.");
            return;
        }

        // Preprocess se llama una vez: expande tileObjects con variantes rotadas
        // y calcula la tabla de vecinos. LIMIT se mantiene en las listas de
        // vecinos para que BorderConstraint funcione, pero se excluye del modelo.
        tilePreprocessor.Preprocess(ref tileObjects);
        tilesetPreprocessed = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  API pública
    // ════════════════════════════════════════════════════════════════

    public void Generate()
    {
        if (!tilesetPreprocessed)
        {
            Debug.LogError("[DeBroglieWFC] Tileset no preprocesado. Asegúrate de que Awake() se haya ejecutado.");
            return;
        }
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
        // 1. Construcción del modelo y la topología (FUERA del cronómetro).
        //    Se hace una sola vez por llamada: equivale al preprocesado de
        //    REFACTOR, que tampoco se repite en cada regeneración.
        IndexTiles();
        AdjacentModel model = BuildAdjacentModel();
        GridTopology topology = BuildTopology();

        // 2. CRONÓMETRO: arranca antes del primer intento. El contrato es
        //    idéntico al de REFACTOR: el reloj cubre propagación + colapso,
        //    incluidos los reintentos por contradicción.
        WaveFunctionGame_REFACTOR.InvokeStartGeneration();

        int attempt = 0;
        Resolution result = Resolution.Contradiction;
        TilePropagator propagator = null;

        while (attempt < maxRetries)
        {
            // Cada intento reconstruye el propagator y reaplica las
            // restricciones globales, igual que REFACTOR.Regenerate() rehace
            // el estado y vuelve a llamar a ApplyGlobalConstraints().
            propagator = BuildPropagator(model, topology);

            if (applyGlobalConstraints)
            {
                ApplyMapLimits(propagator);
                ApplyFloorCeilingConstraints(propagator);
                ApplyFixedTiles(propagator);
            }

            result = propagator.Run();

            if (result == Resolution.Decided)
                break;

            // Contradicción: notifica (para el contador de fallos del
            // benchmark) y reintenta. El cronómetro sigue corriendo, por lo
            // que el tiempo de este intento fallido se acumula en la medición.
            WaveFunctionGame_REFACTOR.InvokeIncompatibility();
            Debug.LogWarning($"[DeBroglieWFC] Contradicción ({result}) en intento " +
                             $"{attempt + 1}. Reintentando.");
            attempt++;
        }

        // 3. Resultado e instanciación (FUERA del cronómetro).
        if (result == Resolution.Decided)
        {
            StoreResolvedTiles(propagator);          // poblar antes del evento
            WaveFunctionGame_REFACTOR.InvokeEndGeneration();
            InstantiateTiles(propagator);
            Debug.Log($"[DeBroglie] Generación exitosa tras {attempt + 1} intento(s).");
        }
        else
        {
            // Se agotaron los reintentos sin solución. No se dispara
            // EndGeneration, de modo que esta medición queda incompleta y no
            // contamina las estadísticas. Señal de tileset problemático.
            Debug.LogError($"[DeBroglieWFC] Sin solución tras {maxRetries} reintentos. " +
                           "Revisa la resolubilidad del tileset o aumenta maxRetries.");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Indexado: construye los mapeos Tile ↔ DBTile
    //  y recolecta LIMIT desde las listas de vecinos
    // ════════════════════════════════════════════════════════════════

    private void IndexTiles()
    {
        toDB = new Dictionary<Tile, DBTile>();
        limitTiles = new HashSet<Tile>();
        acceptorsByDirection = new Dictionary<DBDirection, List<DBTile>>();
        foreach (DBDirection d in Cartesian3dDirections)
            acceptorsByDirection[d] = new List<DBTile>();

        // Primera pasada: indexar tiles no-LIMIT y recolectar LIMIT desde vecinos.
        // Recordatorio: LIMIT no está en tileObjects (TilePreprocessor podría no haberla
        // filtrado aún), pero SÍ puede aparecer en las listas de vecinos de otras tiles.
        foreach (Tile t in tileObjects)
        {
            if (IsLimit(t)) { limitTiles.Add(t); continue; }
            toDB[t] = new DBTile(t);

            CollectLimitsFrom(t.rightNeighbours);
            CollectLimitsFrom(t.leftNeighbours);
            CollectLimitsFrom(t.upNeighbours);
            CollectLimitsFrom(t.downNeighbours);
            CollectLimitsFrom(t.aboveNeighbours);
            CollectLimitsFrom(t.belowNeighbours);
        }

        // Segunda pasada: para cada dirección, listar las tiles que aceptan
        // LIMIT en su lista d-th. Estas son las tiles válidas en la cara exterior
        // del mapa (usadas por BorderConstraint para replicar el mecanismo LIMIT).
        RegisterAcceptors(t => t.rightNeighbours, DBDirection.XPlus);
        RegisterAcceptors(t => t.leftNeighbours, DBDirection.XMinus);
        RegisterAcceptors(t => t.upNeighbours, DBDirection.ZPlus);
        RegisterAcceptors(t => t.downNeighbours, DBDirection.ZMinus);
        RegisterAcceptors(t => t.aboveNeighbours, DBDirection.YPlus);
        RegisterAcceptors(t => t.belowNeighbours, DBDirection.YMinus);
    }

    private void CollectLimitsFrom(List<Tile> neighbours)
    {
        if (neighbours == null) return;
        foreach (Tile n in neighbours)
            if (IsLimit(n)) limitTiles.Add(n);
    }

    private void RegisterAcceptors(System.Func<Tile, List<Tile>> selector, DBDirection d)
    {
        foreach (Tile t in tileObjects)
        {
            if (IsLimit(t) || !toDB.ContainsKey(t)) continue;
            var neighbours = selector(t);
            if (neighbours != null && neighbours.Any(IsLimit))
                acceptorsByDirection[d].Add(toDB[t]);
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
            if (IsLimit(src) || !toDB.ContainsKey(src)) continue;

            model.SetFrequency(toDB[src], src.probability);

            foreach (var (selector, dir) in directionMap)
            {
                var neighbours = selector(src);
                if (neighbours == null) continue;
                foreach (Tile dst in neighbours)
                {
                    if (IsLimit(dst) || !toDB.ContainsKey(dst)) continue;
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
    //  Propagator y restricción de LIMIT (Select manual, capa y=1)
    // ════════════════════════════════════════════════════════════════

    private TilePropagator BuildPropagator(AdjacentModel model, GridTopology topology)
    {
        var rng = fixedSeed > 0 ? new System.Random(fixedSeed) : new System.Random();

        // Sin Constraints nativos: LIMIT se aplica manualmente en ApplyMapLimits,
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
    /// Replica REFACTOR.DefineMapLimits(): restringe el perímetro horizontal
    /// de la capa y=1 (justo encima del suelo) a las tiles que aceptan LIMIT
    /// como vecino en la dirección hacia el exterior correspondiente.
    ///
    /// A diferencia de BorderConstraint (que opera sobre la columna vertical
    /// completa de cada cara), este método solo toca y=1, igual que REFACTOR.
    /// En celdas de esquina (que tocan dos caras a la vez, p.ej. x=0 y z=0),
    /// se llama a Select dos veces seguidas; como Select bannea todo lo que
    /// no esté en el conjunto dado, dos llamadas consecutivas intersectan
    /// ambos conjuntos. El resultado final es el dominio correcto: solo tiles
    /// de esquina (p.ej. "..._cornerExt") que aceptan LIMIT en AMBAS
    /// direcciones sobreviven, igual que en la tabla de adyacencia verificada.
    /// </summary>
    private void ApplyMapLimits(TilePropagator propagator)
    {
        if (dimensionsY <= 1) return; // no existe capa y=1 en mapas de 1 capa

        const int y = 1;
        for (int x = 0; x < dimensionsX; x++)
        {
            bool xMin = x == 0;
            bool xMax = x == dimensionsX - 1;

            for (int z = 0; z < dimensionsZ; z++)
            {
                bool zMin = z == 0;
                bool zMax = z == dimensionsZ - 1;

                if (!xMin && !xMax && !zMin && !zMax) continue; // celda interior: sin restricción

                if (xMin) propagator.Select(x, y, z, acceptorsByDirection[DBDirection.XMinus]);
                if (xMax) propagator.Select(x, y, z, acceptorsByDirection[DBDirection.XPlus]);
                if (zMin) propagator.Select(x, y, z, acceptorsByDirection[DBDirection.ZMinus]);
                if (zMax) propagator.Select(x, y, z, acceptorsByDirection[DBDirection.ZPlus]);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Restricciones de suelo y techo
    //
    //  REFACTOR coloca físicamente floorTile en y=0 y emptyTile en
    //  y=dimensionsY-1 (layers de infraestructura). DeBroglieWFC no replica
    //  esas capas físicas porque floorTile/ceilingTile no forman parte del
    //  modelo WFC; en su lugar restringe las capas jugables adyacentes
    //  (y=1 y y=dimensionsY-2) para que solo admitan tiles cuyos sockets
    //  son compatibles con el suelo/techo definido.
    // ════════════════════════════════════════════════════════════════

    private void ApplyFloorCeilingConstraints(TilePropagator propagator)
    {
        if (floorTile != null && dimensionsY > 1)
        {
            // Verificar que al menos una tile jugable acepta floorTile como vecino inferior.
            // Si ninguna lo acepta (porque floorTile no está en tileObjects de DeBroglieWFC
            // o sus sockets no emparejaron), prohibir todas las tiles de y=1 causaría
            // contradicción inmediata.
            bool anyAccepts = false;
            foreach (Tile t in tileObjects)
            {
                if (IsLimit(t) || !toDB.ContainsKey(t)) continue;
                if (t.belowNeighbours != null && t.belowNeighbours.Contains(floorTile))
                { anyAccepts = true; break; }
            }

            if (!anyAccepts)
            {
                Debug.LogWarning("[DeBroglieWFC] floorTile no aparece en belowNeighbours de ninguna " +
                                 "tile jugable. Asegúrate de incluir floorTile en tileObjects y de que " +
                                 "sus sockets sean compatibles. Se omite la restricción de suelo.");
            }
            else
            {
                for (int x = 0; x < dimensionsX; x++)
                    for (int z = 0; z < dimensionsZ; z++)
                        foreach (Tile t in tileObjects)
                        {
                            if (IsLimit(t) || !toDB.ContainsKey(t)) continue;
                            if (t.belowNeighbours == null || !t.belowNeighbours.Contains(floorTile))
                                propagator.Ban(x, 1, z, toDB[t]);
                        }
            }
        }

        if (ceilingTile != null && dimensionsY > 2)
        {
            bool anyAccepts = false;
            foreach (Tile t in tileObjects)
            {
                if (IsLimit(t) || !toDB.ContainsKey(t)) continue;
                if (t.aboveNeighbours != null && t.aboveNeighbours.Contains(ceilingTile))
                { anyAccepts = true; break; }
            }

            if (!anyAccepts)
            {
                Debug.LogWarning("[DeBroglieWFC] ceilingTile no aparece en aboveNeighbours de ninguna " +
                                 "tile jugable. Se omite la restricción de techo.");
            }
            else
            {
                int topPlayable = dimensionsY - 2;
                for (int x = 0; x < dimensionsX; x++)
                    for (int z = 0; z < dimensionsZ; z++)
                        foreach (Tile t in tileObjects)
                        {
                            if (IsLimit(t) || !toDB.ContainsKey(t)) continue;
                            if (t.aboveNeighbours == null || !t.aboveNeighbours.Contains(ceilingTile))
                                propagator.Ban(x, topPlayable, z, toDB[t]);
                        }
            }
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

    private void ApplyFixedTiles(TilePropagator propagator)
    {
        var rng = fixedSeed > 0 ? new System.Random(fixedSeed + 1) : new System.Random();
        var usedPositions = new HashSet<(int, int, int)>();

        foreach (Tile proto in tileObjects)
        {
            if (IsLimit(proto) || !toDB.ContainsKey(proto)) continue;
            if (proto.fixedTile <= 0) continue;
            // Los variantes rotados heredan fixedTile del original vía Instantiate().
            // Solo el tile original (rotation == zero) define cuántas instancias colocar.
            if (proto.rotation != Vector3.zero) continue;

            for (int i = 0; i < proto.fixedTile; i++)
            {
                // Hasta 200 intentos para encontrar una celda libre.
                bool placed = false;
                for (int attempt = 0; attempt < 200 && !placed; attempt++)
                {
                    int x = rng.Next(0, dimensionsX);
                    int y = rng.Next(1, Mathf.Max(1, dimensionsY - 1));
                    int z = rng.Next(0, dimensionsZ);

                    if (!usedPositions.Contains((x, y, z)))
                    {
                        usedPositions.Add((x, y, z));
                        propagator.Select(x, y, z, toDB[proto]);
                        placed = true;
                    }
                }

                if (!placed)
                    Debug.LogWarning($"[DeBroglieWFC] No se pudo colocar tile fija '{proto.tileType}' " +
                                     "(no quedan posiciones libres).");
            }
        }
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

    public bool IsInfrastructureTile(string tileType)
    {
        if (tileType == null) return false;
        var t = tileType.ToLowerInvariant();
        return t == "limit" || t == "floor" || t == "ceiling" || t == "empty" || t == "border";
    }

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