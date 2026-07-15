using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using DG.Tweening;
using TMPro;
using Random = UnityEngine.Random;
using UnityEngine.UI;



public class WaveFunctionGame_REFACTOR : MonoBehaviour
{
    // ============================================================
    // CONFIGURACIÓN DEL INSPECTOR
    // ============================================================

    [Header("Mode")]
    public bool generateOnStart = true;
    [SerializeField] private bool GENERATE_ALL = false;
    [SerializeField] private bool randomGeneration;
    [SerializeField] private bool stopOnIncompatibility = false;
    [SerializeField] public bool tutorial = false;
    public bool useOptimization;
    public bool OneTileCollapseOptimization;

    [Header("Map dimensions")]
    [SerializeField] public int dimensionsX, dimensionsY, dimensionsZ;
    [SerializeField] private int totalCells;
    [SerializeField] private int cellSize;
    [SerializeField] private int initialCubeSize;


    [Header("Tile set")]
    public bool tilesetPreprocessed = false;
    [SerializeField] public Tile[] tileObjects;
    [SerializeField] private Tile floorTile;
    [SerializeField] private Tile emptyTile;
    [SerializeField] private Tile limitTile;
    [SerializeField] private Cell cellObj;
    //[SerializeField] private GameObject newTilesContainer;
    public Material previewMaterial;

    [Header("Preprocesado")]
    [SerializeField] private TilePreprocessor tilePreprocessor;

    [Header("Global Constraints")]
    public bool probabilityConstraint = true;
    public bool excludedNeighborConstraint = true;
    public bool floorCeilingConstraint = true;
    public bool fixedTilesConstraint = true;
    public bool borderConstraint = true;

    [Header("Animation")]
    [SerializeField] private bool animations = true;
    [SerializeField] private float animationDuration = 0.1f;
    [SerializeField] private float animationDelay = 0.01f;

    public float alphaCube = 0.1f;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip changeCellSound;
    public AudioClip collapseCellSound;

    [Header("UI references")]
    [SerializeField] private CardGenerator cardGenerator;
    [SerializeField] private GameObject tutorialObject;
    [SerializeField] private TextMeshProUGUI placedTilesText;
    [SerializeField] private TextMeshProUGUI mapsGeneratedText;
    public TextMeshProUGUI timerText;
    public GameObject finishPanel;
    public Button pauseBtn;
    public Button resumeBtn;

    // ============================================================
    // ESTADO INTERNO
    // ============================================================

    [Header("Runtime state")]
    [SerializeField] public List<Cell> gridComponents;
    public HashSet<(string tileType, Vector3 rotation)> globalValidTiles = new();
    public GameObject actualTileDragged;
    public bool skipEntireTileRemoved = false;

    private List<Cell> validCells = new List<Cell>();
    private System.Random _rng = new System.Random();
    private bool cubeStep = true;
    private bool collapseOneOptionThisIteration = true;

    // Cubo central
    private int cubeStartX, cubeEndX;
    private int cubeStartY, cubeEndY;
    private int cubeStartZ, cubeEndZ;
    public int centerCubeCells;
    private int cubeCellsRemaining;

    // Contadores
    [SerializeField] private int iterations = 0;
    [SerializeField] private int collapsedCells = 0;
    public int placedTiles = 0;
    public int mapsGenerated = 1;


    // Cronómetro
    private float elapsedTime;
    public bool isRunning = true;

    // ============================================================
    // EVENTOS
    // ============================================================

    public delegate void OnRegenerate();
    public delegate void OnIncompatibility();
    public delegate void OnStartGeneration();
    public delegate void OnEndGeneration();

    public static event OnRegenerate onRegenerate;
    public static event OnIncompatibility onIncompatibility;

    // Test: generación completa (GENERATE_ALL)
    public static event OnStartGeneration onStartGeneration;
    public static event OnEndGeneration onEndGeneration;

    // Test: cubo inicial (modo juego)
    public static event OnStartGeneration onStartCubeGeneration;
    public static event OnEndGeneration onEndCubeGeneration;

    // Test: tiempo de respuesta al colocar una ficha (modo juego)
    public static event OnStartGeneration onStartTilePropagation;
    public static event OnEndGeneration onEndTilePropagation;

    // ============================================================
    // AC-4 — CONSTANTES Y CAMPOS (solo activos en GENERATE_ALL)
    // ============================================================

    // Direcciones ortogonales: 0=Right+X, 1=Left-X, 2=Fwd+Z, 3=Back-Z, 4=Above+Y, 5=Below-Y
    private static readonly int[] AC4_DX = { 1, -1, 0, 0, 0, 0 };
    private static readonly int[] AC4_DY = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] AC4_DZ = { 0, 0, 1, -1, 0, 0 };
    private static readonly int[] AC4_OPP = { 1, 0, 3, 2, 5, 4 };

    private int AC4_T;            // tileObjects.Length
    private bool[] AC4_wave;         // [cellIdx * T + tileIdx]  ¿es posible aún?
    private int[] AC4_compatible;   // [(cellIdx * T + tileIdx) * 6 + dir]  contador soporte
    private int[] AC4_domain;       // opciones restantes por celda
    private double[] AC4_entropy;     // entropía de Shannon por celda
    private double[] AC4_sumW;        // suma de pesos por celda
    private double[] AC4_sumWLogW;    // suma w·log(w) por celda
    private double[] AC4_tileW;       // peso por tile
    private double[] AC4_tileWLogW;   // w·log(w) por tile
    private double AC4_totalW;      // suma global de pesos
    private double AC4_totalWLogW;
    private double AC4_startEntropy;
    private int[][] AC4_propagator;  // [dir * T + tileIdx] → índices de vecinos válidos
    private (int cell, int tile)[] AC4_stack; // stack de banes pendientes
    private int AC4_stackSize;
    private bool AC4_contradiction;
    private Dictionary<Tile, int> AC4_tileIndex; // Tile → índice en tileObjects



    private void OnEnable()
    {
        GameEvents.OnTileDragged += OnTileDrag;
        GameEvents.OnTileReleased += OnTileRemoved;
        GameEvents.OnTileRotated += OnTileRotation;
        GameEvents.OnDeleteTile += OnTileDeleted;
        GameEvents.OnGameFinished += FinishGame;
    }

    private void OnDestroy()
    {
        GameEvents.OnTileDragged -= OnTileDrag;
        GameEvents.OnTileReleased -= OnTileRemoved;
        GameEvents.OnTileRotated -= OnTileRotation;
        GameEvents.OnDeleteTile -= OnTileDeleted;
        GameEvents.OnGameFinished -= FinishGame;
    }

    public static void InvokeStartGeneration() => onStartGeneration?.Invoke();
    public static void InvokeEndGeneration() => onEndGeneration?.Invoke();
    public static void InvokeIncompatibility() => onIncompatibility?.Invoke();


    void Awake()
    {
        if (generateOnStart)
        {
            ValidateConfiguration();
            audioSource = GetComponent<AudioSource>();
            PreprocessTileSet();

            gridComponents = new List<Cell>();
            BuildAC4Propagator(); // precalcular propagador una sola vez tras el preprocesado
        }
    }

    void Start()
    {
        if(generateOnStart) Init();
    }

    /// <summary>
    /// En modo juego, el colapso de celdas con una sola opción debe estar activo (es lo
    /// que produce el efecto cascada visual) y la optimización de frontera no tiene sentido (solo aplica al modo GENERATE_ALL).
    /// </summary>
    private void ValidateConfiguration()
    {
        if (!GENERATE_ALL)
        {
            OneTileCollapseOptimization = true;
            useOptimization = false;
        }
    }

    /// <summary>
    /// Preprocesamiento del conjunto de tiles.
    /// </summary>
    public void PreprocessTileSet()
    {
        tilePreprocessor.excludedNeighborConstraint = excludedNeighborConstraint;
        tilePreprocessor.Preprocess(ref tileObjects);

        // REFACTOR filtra LIMIT de su dominio de colapso. Las listas de
        // vecinos ya contienen las referencias a LIMIT para que el borde
        // funcione correctamente; el solver simplemente no colapsa tiles LIMIT.
        tileObjects = tileObjects.Where(t => t.tileType != "limit").ToArray();

        tilesetPreprocessed = true;
    }

    //---------------------------------INICIALIZACION------------------------------------------------------------------

    /// <summary>
    /// Punto de entrada de cada regeneración del mapa. Se invoca desde Awake
    /// la primera vez y desde Regenerate() en sucesivas. Orquesta la
    /// construcción del grid, la aplicación de restricciones globales,
    /// la configuración del CardGenerator y el arranque del bucle WFC.
    /// </summary>
    private void Init()
    {
        totalCells = dimensionsX * dimensionsY * dimensionsZ;
        ResetState();
        SetupCamera();

        InitializeGrid();
        ApplyGlobalConstraints();

        if (GENERATE_ALL) InitAC4FromCellState(); // inicializar AC-4 tras las restricciones globales

        if (!GENERATE_ALL) GetCenterCube();

        ConfigureCardGenerator();
        onStartGeneration?.Invoke();
        StartGeneration();
    }

    private void ResetState()
    {
        centerCubeCells = 0;
        iterations = 0;
        collapseOneOptionThisIteration = true;
    }

    private void SetupCamera()
    {
        CameraControl cameraControl = FindAnyObjectByType<CameraControl>();
        if (cameraControl != null)
            cameraControl.SetupCamera(dimensionsX, dimensionsZ, dimensionsY, cellSize);
    }

    void InitializeGrid()
    {
        //First, create the grid
        for (int y = 0; y < dimensionsY; y++)
        {
            for (int z = 0; z < dimensionsZ; z++)
            {
                for (int x = 0; x < dimensionsX; x++)
                {
                    Cell newCell = Instantiate(cellObj, new Vector3(x * cellSize, y * cellSize, z * cellSize), Quaternion.identity, gameObject.transform);
                    newCell.CreateCell(false, tileObjects, x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ), new Vector3Int(x, y, z));
                    gridComponents.Add(newCell);
                }
            }
        }
    }

    private void GetCenterCube()
    {
        centerCubeCells += iterations;

        int cubeSizeX = initialCubeSize;
        int cubeSizeZ = initialCubeSize;

        cubeStartY = 1;
        cubeEndY = dimensionsY - 1;

        cubeStartX = (dimensionsX - cubeSizeX) / 2;
        cubeStartZ = (dimensionsZ - cubeSizeZ) / 2;
        cubeEndX = cubeStartX + cubeSizeX;
        cubeEndZ = cubeStartZ + cubeSizeZ;
        cubeCellsRemaining = 0;

        for (int y = cubeStartY; y < cubeEndY; y++)
            for (int z = cubeStartZ; z < cubeEndZ; z++)
                for (int x = cubeStartX; x < cubeEndX; x++)
                {
                    int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                    if (index < 0 || index >= gridComponents.Count) continue;

                    Cell c = gridComponents[index];
                    c.centerCubeCell = true;

                    // No contamos celdas ya colapsadas por las constraints globales
                    // (puede ocurrir si una tile fija cae dentro del cubo).
                    if (!c.collapsed) cubeCellsRemaining++;


                }
    }

    /// <summary>
    /// Aplica las restricciones globales que estén activadas en el inspector.
    /// </summary>
    private void ApplyGlobalConstraints()
    {
        if (borderConstraint) DefineMapLimits();
        if (floorCeilingConstraint)
        {
            CreateSolidFloor();
            CreateSolidCeiling();
        }
        if (fixedTilesConstraint) CreateFixedTiles();

        //Propagar sus cambios
        foreach (Cell c in gridComponents)
            if (c.collapsed) PropagateFromCell(c);
    }

    /// <summary>
    /// Llena la lista de tiles disponibles del CardGenerator excluyendo las
    /// tiles de infraestructura
    /// </summary>
    private void ConfigureCardGenerator()
    {
        if (cardGenerator == null) return;
        cardGenerator.tilesList = tileObjects
            .Where(t => !IsInfrastructureTile(t.tileType))
            .ToList();
    }

    /// <summary>
    /// Identifica tiles que solo deben colocarse de forma automática
    /// (suelo, techo, bordes, esquinas de borde) y no aparecer en el
    /// CardGenerator.
    /// 
    /// TODO: idealmente sustituir por un flag bool isInfrastructure en
    /// Tile.cs, para no mantener esta lista de tipos hardcoded.
    /// </summary>
    public bool IsInfrastructureTile(string tileType)
    {
        return tileType == "limit" || tileType == "empty_limit"
            || tileType == "solid" || tileType == "empty"
            || tileType == "border" || tileType == "borderSand"
            || tileType == "cornerExtBorder" || tileType == "cornerIntBorder"
            || tileType == "cornerExt_border_sand" || tileType == "cornerInt_border_sand";
    }

    /// <summary>
    /// Arranque del bucle WFC según el modo:
    ///   - tutorial: oculta la cola de tiles y muestra el panel de tutorial.
    ///               StartGame() reanudará desde aquí cuando el jugador termine.
    ///   - GENERATE_ALL: lanza directamente el bucle de generación automática.
    ///   - juego: arranca la fase de generación del cubo central; al terminar,
    ///            UpdateGenerationCube cede el control al modo juego.
    /// </summary>
    private void StartGeneration()
    {
        if (tutorial)
        {
            cardGenerator.gameObject.SetActive(false);
            tutorialObject.SetActive(true);
            return;
        }

        ResumeTimer();

        if (GENERATE_ALL)
        {
            cubeStep = false;
            RunGenerationSync();
        }
        else
        {
            cubeStep = true;
            RunCubeGenerationSync();
        }
    }



    //------------------------------------------------BUCLE UPDATE-------------------------------------------

    private void Update()
    {
        if (isRunning)
        {
            elapsedTime += Time.deltaTime;

            int hours = Mathf.FloorToInt(elapsedTime / 3600);
            int minutes = Mathf.FloorToInt((elapsedTime % 3600) / 60);
            int seconds = Mathf.FloorToInt(elapsedTime % 60);

            if (timerText != null)
                timerText.text = $"{hours:00}:{minutes:00}:{seconds:00}";
        }

        if (collapsedCells >= totalCells)
        {
            GameEvents.GameFinished();
        }
    }

    public void PauseTimer() { isRunning = false; if (pauseBtn != null) pauseBtn.interactable = false; }
    public void ResumeTimer() { isRunning = true; if (pauseBtn != null) pauseBtn.interactable = true; }

    public void StartGame() { cubeStep = true; tutorial = false; ResumeTimer(); RunCubeGenerationSync(); }
    public void ExitGame() => Application.Quit();

    private void FinishGame()
    {
        if (finishPanel != null) finishPanel.SetActive(true);
        if (pauseBtn != null) pauseBtn.interactable = false;
        if (resumeBtn != null) resumeBtn.interactable = false;
        PauseTimer();
    }

    //-------------------------CREAR TILES DE CAPAS DE INFAESTRUCTURA--------------------

    //FUNCIONES AUXILIARES
    /// <summary>
    /// Coloca una tile de infraestructura sobre una celda durante la fase de inicialización del mapa.
    /// Solo actualiza el estado lógico; la instanciación la realiza BatchInstantiateTiles().
    /// </summary>
    private void PlaceInfrastructureTile(Cell cell, Tile tile, bool expandFrontier = false)
    {
        cell.tileOptions = new Tile[] { tile };
        cell.collapsed = true;

        if (expandFrontier) GetNeighboursCloseToCollapsedCell(cell);

        iterations++;
    }

    /// <summary>
    /// Itera sobre las celdas de una capa horizontal completa del grid.
    /// Útil para rellenar suelo, techo o cualquier capa intermedia.
    /// </summary>
    private IEnumerable<Cell> GetLayerCells(int y)
    {
        for (int z = 0; z < dimensionsZ; z++)
            for (int x = 0; x < dimensionsX; x++)
            {
                int idx = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                yield return gridComponents[idx];
            }
    }

    /// <summary>
    /// Determina si una celda con coordenadas (x, z) está en el perímetro
    /// del mapa en el plano horizontal.
    /// </summary>
    private bool IsOnHorizontalBorder(int x, int z)
    {
        return x == 0 || x == dimensionsX - 1
            || z == 0 || z == dimensionsZ - 1;
    }

    /// <summary>
    /// Selecciona aleatoriamente una celda no colapsada del grid. Devuelve
    /// null si no quedan celdas libres.
    /// </summary>
    private Cell PickRandomFreeCell()
    {
        List<Cell> free = gridComponents.Where(c => !c.collapsed).ToList();
        if (free.Count == 0) return null;
        return free[_rng.Next(0, free.Count)];
    }


    //FUNCIONES PRINCIPALES
    /// <summary>
    /// Rellena la capa inferior del mapa (y = 0) con la tile sólida de
    /// suelo, garantizando que no haya espacios vacíos por debajo del
    /// terreno jugable.
    /// </summary>
    private void CreateSolidFloor()
    {
        foreach (Cell c in GetLayerCells(0))
            PlaceInfrastructureTile(c, floorTile);
    }

    /// <summary>
    /// Rellena la capa superior del mapa con la tile vacía de techo,
    /// cerrando el espacio jugable por arriba.
    /// </summary>
    private void CreateSolidCeiling()
    {
        foreach (Cell c in GetLayerCells(dimensionsY - 1))
            PlaceInfrastructureTile(c, emptyTile);
    }

    /// <summary>
    /// Coloca tiles "limit" en el perímetro de la capa y = 1 (justo
    /// encima del suelo). 
    /// </summary>
    private void DefineMapLimits()
    {
        bool expandFrontier = useOptimization && GENERATE_ALL;

        foreach (Cell c in GetLayerCells(1))
        {
            if (!IsOnHorizontalBorder(c.coords.x, c.coords.z)) continue;
            PlaceInfrastructureTile(c, limitTile, expandFrontier);
        }
    }

    /// <summary>
    /// Coloca las tiles definidas como fijas en el inspector (campo
    /// fixedTile > 0 en el ScriptableObject de Tile) en posiciones
    /// aleatorias del mapa. Cada tile fija se coloca el número de
    /// veces indicado por fixedTile.
    /// </summary>
    private void CreateFixedTiles()
    {
        foreach (Tile prototype in tileObjects)
        {
            if (prototype.fixedTile <= 0) continue;
            PlaceFixedTileCopies(prototype, prototype.fixedTile);
        }
    }

    /// <summary>
    /// Coloca un número determinado de copias de una tile fija en
    /// posiciones aleatorias del mapa. 
    /// </summary>
    private void PlaceFixedTileCopies(Tile tile, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Cell target = PickRandomFreeCell();
            if (target == null)
            {
                Debug.LogWarning($"[WFC] No quedan celdas libres para tile fija {tile.tileType}.");
                return;
            }
            PlaceInfrastructureTile(target, tile);
        }
    }


    //----------------------------------------------------SELECCIONAR UNA CELDA CON MINIMA ENTROPIA Y COLAPSARLA (CHECK ENTROPY & COLLAPSE CELL)------------

    /// <summary>
    /// Selecciona la celda con menor entropía del grid según la heurística
    /// MRV (Minimum Remaining Values)
    /// En la fase de cubo inicial (cubeStep) restringe la búsqueda al cubo
    /// central. Si randomGeneration está activo, devuelve una celda aleatoria
    /// sin tener en cuenta la entropía.
    /// </summary>
    private Cell SelectCellWithMinimumEntropy()
    {
        if (GENERATE_ALL) return SelectCellAC4();

        // Modo juego: MRV con Tile[]
        List<Cell> candidates = GetSelectableCells();
        if (candidates.Count == 0) return null;

        if (randomGeneration)
            return candidates[_rng.Next(0, candidates.Count)];

        int minEntropy = int.MaxValue;
        foreach (Cell c in candidates)
            if (c.tileOptions.Length < minEntropy)
                minEntropy = c.tileOptions.Length;

        /* List<Cell> tied = candidates
             .Where(c => c.tileOptions.Length == minEntropy)
             .ToList();

         return tied[_rng.Next(0, tied.Count)];*/

        List<Cell> tied = candidates
            .Where(c => c.tileOptions.Length == minEntropy)
            .ToList();

        float minShannon = float.MaxValue;
        List<Cell> bestCells = new();

        foreach (Cell cell in tied)
        {
            float entropy = ComputeShannonEntropy(cell);

            if (entropy < minShannon - 0.0001f)
            {
                minShannon = entropy;
                bestCells.Clear();
                bestCells.Add(cell);
            }
            else if (Mathf.Abs(entropy - minShannon) < 0.0001f)
            {
                bestCells.Add(cell);
            }
        }

        return bestCells[_rng.Next(bestCells.Count)];
    }

    //para que los pesos influyan un poco en la minima entropia
    private float ComputeShannonEntropy(Cell cell)
    {
        float totalWeight = cell.tileOptions.Sum(t => (float)t.probability);

        if (totalWeight <= 0f)
            return 0f;

        float entropy = 0f;

        foreach (Tile tile in cell.tileOptions)
        {
            float p = tile.probability / totalWeight;

            if (p > 0f)
                entropy -= p * Mathf.Log(p);
        }

        return entropy;
    }


    /// <summary>
    /// Devuelve las celdas no colapsadas susceptibles de selección.
    /// En la fase de cubo se restringe al rango central; en GENERATE_ALL
    /// se considera el grid completo.
    /// </summary>
    private List<Cell> GetSelectableCells()
    {
        if (!cubeStep)
            return gridComponents.Where(c => !c.collapsed).ToList();

        List<Cell> cells = new List<Cell>(initialCubeSize * initialCubeSize * (dimensionsY - 2));
        for (int y = cubeStartY; y < cubeEndY; y++)
            for (int z = cubeStartZ; z < cubeEndZ; z++)
                for (int x = cubeStartX; x < cubeEndX; x++)
                {
                    int idx = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                    if (!gridComponents[idx].collapsed) cells.Add(gridComponents[idx]);
                }
        return cells;
    }

    /// <summary>
    /// Elige una tile de forma ponderada por probability, repartiendo el peso
    /// de cada tipo equitativamente entre sus variantes rotadas.
    /// </summary>
    /// <param name="candidates">Tiles candidatas (array, lista o cualquier IEnumerable)</param>
    public Tile ChooseTile(IEnumerable<Tile> candidates)
    {
        if (candidates == null) return null;

        var candidateList = candidates.ToList();
        if (candidateList.Count == 0) return null;

        // Agrupamos por tipo para repartir la probability entre variantes
        var groupedByType = candidateList.GroupBy(t => t.tileType);

        const int scale = 1000;
        List<(Tile tile, int weight)> weightedTiles = new List<(Tile, int)>();
        int totalWeight = 0;

        foreach (var group in groupedByType)
        {
            int variantCount = group.Count();
            int typeProbability = group.First().probability;
            int weightPerVariant = (typeProbability * scale) / variantCount;

            foreach (Tile variant in group)
            {
                weightedTiles.Add((variant, weightPerVariant));
                totalWeight += weightPerVariant;
            }
        }

        if (totalWeight <= 0) return null;

        int randomNumber = _rng.Next(0, totalWeight);
        foreach (var (tile, weight) in weightedTiles)
        {
            if (randomNumber < weight) return tile;
            randomNumber -= weight;
        }

        return null; // No debería ocurrir si totalWeight > 0
    }

    Tile ChooseRandomTile(List<Tile> tiles)
    {
        int randomNumber = _rng.Next(0, tiles.Count - 1);

        Tile t = tiles[randomNumber];

        if (t != null) return t;

        return null; // This should not happen if the list is not empty
    }

    /// <summary>
    /// Colapsa una celda eligiendo una tile de su dominio actual. La
    /// selección es ponderada por probability si probabilityConstraint
    /// está activo, o uniforme en caso contrario.
    /// </summary>
    private bool CollapseCell(Cell cell)
    {
        // En GENERATE_ALL usamos el path AC-4 (sin allocations, selección por entropía de Shannon)
        if (GENERATE_ALL) return CollapseCellAC4(cell);

        // Modo juego: path original AC-3 con Tile[]
        Tile selectedTile = probabilityConstraint
            ? ChooseTile(cell.tileOptions)
            : ChooseRandomTile(cell.tileOptions.ToList());

        if (selectedTile == null)
        {
            HandleIncompatibility();
            return false;
        }

        ApplyCollapse(cell, selectedTile);
        GetNeighboursCloseToCollapsedCell(cell);
        return true;
    }

    /// <summary>
    /// Aplica el colapso sobre la celda. Solo actualiza el estado lógico:
    /// la instanciación visual siempre se difiere a BatchInstantiateTiles().
    /// PlaceTileOnCell y ForcePlaceTile se encargan de instanciar cuando
    /// la acción la inicia el jugador (feedback inmediato necesario).
    /// </summary>
    private void ApplyCollapse(Cell cell, Tile selectedTile)
    {
        cell.previousEntropy = cell.tileOptions.Length;
        cell.tileOptions = new Tile[] { selectedTile };
        cell.collapsed = true;

        if (cell.centerCubeCell) cubeCellsRemaining--;
    }

    /// <summary>
    /// Instancia visualmente una tile dentro de una celda aplicando su
    /// rotación y su offset de posición.
    /// </summary>
    private void InstantiateTileInCell(Tile tile, Cell cell)
    {
        Tile instance = Instantiate(tile, cell.transform.position,
                                    Quaternion.identity, cell.transform);

        if (tile.rotation != Vector3.zero)
            instance.gameObject.transform.Rotate(tile.rotation, Space.Self);

        instance.gameObject.transform.position += tile.positionOffset;
        instance.gameObject.SetActive(true);
        collapsedCells++;
    }

    /// <summary>
    /// Gestión centralizada de incompatibilidades. Notifica el evento
    /// onIncompatibility y, salvo que se haya pedido detenerse al primer
    /// fallo (stopOnIncompatibility), regenera el mapa entero.
    /// </summary>
    private void HandleIncompatibility()
    {
        Debug.LogError("[WFC] INCOMPATIBILIDAD: ninguna tile válida para la celda.");
        onIncompatibility?.Invoke();

        if (!stopOnIncompatibility) Regenerate();
    }

    /// <summary>
    /// Makes the neighbours wiithin a given distance og the collapsed cell visitable for optimization purposes or for game mechanic purposes
    /// (not always looking at every cell)
    /// </summary>
    /// <param name="cell"></param> Collapsed cell
    /// 
    /// NOTA: Si se usa para la optimizacion al generar todo el mapa, siempre se llama a esta funcion cuando algo colapsa, incluso tiles invisibles.
    /// 
    /// Sin embargo, si se usa en modo juego, la optimizacion no es necesaria pero queremos que solo se puedan colocar nuevas tiles
    /// en celdas adyacentes a lo ya colapsado, solo celdas visibles. Para evitar que se marque como visitable los bordes invisibles del mapa debido a la tile de limite,
    /// se debe marcar esa frontera como no visitable.
    private void GetNeighboursCloseToCollapsedCell(Cell cell)
    {
        // Las tiles de infraestructura no expanden la frontera del jugador si esta en modo juego
        if (!GENERATE_ALL)
        {
            if (cell.tileOptions.Length > 0)
            {
                string type = cell.tileOptions[0].tileType;
                if (type == "empty" || type == "solid" || type == "limit")
                    return;
            }
        }


        int up, down, left, right, above, below;
        up = cell.index + dimensionsX;
        down = cell.index - dimensionsX;
        left = cell.index - 1;
        right = cell.index + 1;
        above = cell.index + (dimensionsX * dimensionsZ);
        below = cell.index - (dimensionsX * dimensionsZ);
        cell.visitable = true;

        // Verificar que los indices estan en rango antes de acceder a gridComponents
        if (up >= 0 && up < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != dimensionsZ - 1)
        {
            gridComponents[up].MakeVisitable();
        }

        if (down >= 0 && down < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != 0)
        {
            gridComponents[down].MakeVisitable();
        }

        if (left >= 0 && left < gridComponents.Count && cell.index % dimensionsX != 0)
        {
            gridComponents[left].MakeVisitable();
        }

        if (right >= 0 && right < gridComponents.Count && (cell.index + 1) % dimensionsX != 0)
        {
            gridComponents[right].MakeVisitable();
        }

        if (above >= 0 && above < gridComponents.Count && (cell.index / (dimensionsX * dimensionsZ)) != dimensionsY - 1)
        {
            gridComponents[above].MakeVisitable();
        }

        if (below >= 0 && below < gridComponents.Count && (cell.index / (dimensionsX * dimensionsZ)) != 0)
        {
            gridComponents[below].MakeVisitable();
        }

        // Calcular diagonales 2D solo si est�n dentro de rango
        int upLeft = up - 1;
        int upRight = up + 1;
        int downLeft = down - 1;
        int downRight = down + 1;

        if (upLeft >= 0 && upLeft < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != dimensionsZ - 1 && cell.index % dimensionsX != 0)
        {
            gridComponents[upLeft].MakeVisitable();
        }

        if (upRight >= 0 && upRight < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != dimensionsZ - 1 && (cell.index + 1) % dimensionsX != 0)
        {
            gridComponents[upRight].MakeVisitable();
        }

        if (downLeft >= 0 && downLeft < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != 0 && cell.index % dimensionsX != 0)
        {
            gridComponents[downLeft].MakeVisitable();
        }

        if (downRight >= 0 && downRight < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != 0 && (cell.index + 1) % dimensionsX != 0)
        {
            gridComponents[downRight].MakeVisitable();
        }

        // Diagonales en 3D
        int aboveUp = above + dimensionsX;
        int aboveDown = above - dimensionsX;
        int belowUp = below + dimensionsX;
        int belowDown = below - dimensionsX;

        if (above >= 0 && above < gridComponents.Count)
        {
            if (aboveUp >= 0 && aboveUp < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != dimensionsZ - 1)
            {
                gridComponents[aboveUp].MakeVisitable();
            }

            if (aboveDown >= 0 && aboveDown < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != 0)
            {
                gridComponents[aboveDown].MakeVisitable();
            }
        }

        if (below >= 0 && below < gridComponents.Count)
        {
            if (belowUp >= 0 && belowUp < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != dimensionsZ - 1)
            {
                gridComponents[belowUp].MakeVisitable();
            }

            if (belowDown >= 0 && belowDown < gridComponents.Count && ((cell.index / dimensionsX) % dimensionsZ) != 0)
            {
                gridComponents[belowDown].MakeVisitable();
            }
        }
    }


    //------------------------------------------------------BUCLES PRINCIPALES (GENERACIÓN)-----------------------------------------------

    /// <summary>
    /// Bucle principal del algoritmo WFC en modo GENERATE_ALL.
    /// Sustituye la cadena UpdateGeneration → StartCoroutine(CheckEntropy) → UpdateGeneration
    /// por un while plano que no crece en pila. Sin corrutinas, sin yield.
    /// La instanciación visual se difiere al final mediante BatchInstantiateTiles().
    /// </summary>
    private void RunGenerationSync()
    {
        int total = dimensionsX * dimensionsY * dimensionsZ;

        while (true)
        {
            iterations++;

            if (iterations > total)
            {
                OnGenerationComplete();
                return;
            }

            Cell cell = SelectCellWithMinimumEntropy(); // → SelectCellAC4()
            if (cell == null) break;

            if (!CollapseCell(cell)) return;           // → CollapseCellAC4()

            // Propagación AC-4: sin allocations, detecta contradicción inmediatamente
            if (!PropagateAC4())
            {
                HandleIncompatibility();
                return;
            }
        }

        OnGenerationComplete();
    }

    /// <summary>
    /// Llamado cuando RunGenerationSync completa un mapa con éxito.
    /// Dispara el evento onEndGeneration (para CalculateExecutionTime si está
    /// en la escena) y activa el loop de benchmark para el siguiente mapa.
    /// </summary>
    private void OnGenerationComplete()
    {
        onEndGeneration?.Invoke();
        BatchInstantiateTiles();
    }

    /// <summary>
    /// Bucle síncrono para la fase de generación del cubo central (modo juego).
    /// Sustituye UpdateGenerationCube + StartCoroutine(CheckEntropy).
    /// Cuando el cubo termina, BatchInstantiateTiles instancia todos sus tiles de golpe.
    /// </summary>
    private void RunCubeGenerationSync()
    {
        onStartCubeGeneration?.Invoke();

        while (cubeCellsRemaining > 0)
        {
            Cell cell = SelectCellWithMinimumEntropy();
            if (cell == null) break;

            if (!CollapseCell(cell)) return; // incompatibilidad

            PropagateFromCell(cell);
        }

        cubeStep = false;
        collapseOneOptionThisIteration = false;
        UpdateGlobalValidTiles();

        onEndCubeGeneration?.Invoke();
        BatchInstantiateTiles();
    }


    //----------------------------------------------------------COLAPSOS EN CASCADA (MODO JUEGO)------------------------------------------------

    /// <summary>
    /// Colapsa síncronamente todas las celdas con dominio unitario que hayan
    /// quedado tras una propagación. Sustituye la corrutina CollapseUnitaryCellsInCascade.
    /// Sin delay entre colapsos: todos los collapses ocurren en el mismo frame.
    /// La instanciación visual se hace en lote desde TriggerCascadeIfEnabled
    /// con BatchInstantiateTiles() al terminar.
    /// </summary>
    private void CollapseUnitaryCellsSync()
    {
        while (true)
        {
            Cell next = FindNextUnitaryCell();
            if (next == null) break;

            ApplyForcedCollapse(next);
            PropagateFromCell(next);
            UpdateGlobalValidTiles();
        }
    }

    /// <summary>
    /// Localiza la próxima celda con dominio reducido a una única tile,
    /// no colapsada y visitable. La condición de visitable garantiza que
    /// la cascada no se propague hacia celdas de borde invisibles.
    /// </summary>
    private Cell FindNextUnitaryCell()
    {
        foreach (Cell c in gridComponents)
            if (!c.collapsed && c.tileOptions.Length == 1)
                return c;
        return null;
    }

    /// <summary>
    /// Aplica un colapso forzado sobre una celda cuyo dominio ya ha
    /// quedado reducido a una única tile durante la propagación. No hay
    /// elección de tile: simplemente se confirma la única opción posible.
    /// 
    /// Reutiliza ApplyCollapse para mantener una única implementación de
    /// la operación de colapso y añade el efecto visual de rebote propio
    /// de la cascada del modo juego.
    /// </summary>
    private void ApplyForcedCollapse(Cell cell)
    {
        Tile onlyOption = cell.tileOptions[0];

        GetNeighboursCloseToCollapsedCell(cell);
        ApplyCollapse(cell, onlyOption);

        iterations++;
    }

    /// <summary>
    /// Efecto visual de rebote sobre la tile recién instanciada en una
    /// celda. Centralizado para que los distintos puntos del flujo
    /// (cascada, colocación del jugador, ForcePlaceTile) usen una única
    /// rutina con parámetros configurables.
    /// </summary>
    private void PlayCollapseBounce(Cell cell, float jumpPower, float duration)
    {
        Transform t = cell.transform.GetComponentInChildren<Tile>()?.transform;
        if (t == null) return;

        t.DOJump(t.position, jumpPower, numJumps: 1, duration)
         .SetEase(Ease.OutBounce);
    }

    /// <summary>
    /// Refresca el conjunto de pares (tipo, rotación) de tiles que aún
    /// pueden colocarse en al menos una celda visitable y no colapsada
    /// del mapa. El CardGenerator consulta este conjunto para no ofrecer
    /// al jugador tiles que ya no encajan en ningún sitio, garantizando
    /// así que cada carta sea siempre colocable.
    /// 
    /// Solo se mantiene en modo juego; en GENERATE_ALL no hay
    /// CardGenerator y el cómputo se omite.
    /// </summary>
    private void UpdateGlobalValidTiles()
    {
        if (GENERATE_ALL) return;

        globalValidTiles.Clear();

        foreach (Cell cell in gridComponents)
        {
            if (cell.collapsed || !cell.visitable) continue;

            foreach (Tile t in cell.tileOptions)
                globalValidTiles.Add((t.tileType, t.rotation));
        }
    }

    //--------------------------------------------METODO DE PROPAGACION CON ARC CONSISTENCY 3---------------------------------------------

    private void PropagateFromCell(Cell placedCell)
    {
        var queue = new Queue<int>();
        var inQueue = new HashSet<int>();

        // Semilla: los 6 vecinos directos de la celda colocada
        EnqueueNeighbors(placedCell.index, placedCell.coords.x,
                         placedCell.coords.y, placedCell.coords.z,
                         queue, inQueue);

        int safety = gridComponents.Count * 2;

        while (queue.Count > 0 && safety-- > 0)
        {
            int idx = queue.Dequeue();
            inQueue.Remove(idx);

            Cell cell = gridComponents[idx];
            if (cell.collapsed) continue;

            int prevLen = cell.tileOptions.Length;

            // Recomputar dominio en sitio (sin copia del grid completo)
            int x = cell.coords.x;
            int y = cell.coords.y;
            int z = cell.coords.z;

            List<Tile> options = ComputeValidOptions(x, y, z);
            cell.tileOptions = options.ToArray();

            // Si el dominio se redujo, los vecinos pueden verse afectados
            if (cell.tileOptions.Length < prevLen)
            {
                EnqueueNeighbors(idx, x, y, z, queue, inQueue);
            }
        }
    }

    //-------------------------------------------------ACTUALIZAR VECINOS (CHECK NEIGHBORS)-----------------------------------------------

    /// <summary>
    /// PREVIOUS CHECK NEIGHBORS
    /// looks and update the options in every cell of the given list looking at the neighbours
    /// </summary>
    /// <param name="x"></param> x coordinate of the cell
    /// <param name="y"></param> y coordinate of the cell
    /// <param name="z"></param> z coordinate of the cell
    /// <param name="newGenerationCell"></param> List of cells to be updated


    private List<Tile> ComputeValidOptions(int x, int y, int z)
    {
        List<Tile> options = new List<Tile>(tileObjects);

        void FilterBy(int neighborIdx, Func<Tile, List<Tile>> getValid)
        {
            HashSet<Tile> validSet = new HashSet<Tile>();
            foreach (Tile opt in gridComponents[neighborIdx].tileOptions)
                validSet.UnionWith(getValid(opt));
            options.RemoveAll(o => !validSet.Contains(o) || o.tileType == "limit");
        }

        if (z > 0) FilterBy(x + ((z - 1) * dimensionsX) + (y * dimensionsX * dimensionsZ), o => o.upNeighbours);
        if (z < dimensionsZ - 1) FilterBy(x + ((z + 1) * dimensionsX) + (y * dimensionsX * dimensionsZ), o => o.downNeighbours);
        if (x > 0) FilterBy((x - 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ), o => o.rightNeighbours);
        if (x < dimensionsX - 1) FilterBy((x + 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ), o => o.leftNeighbours);
        if (y > 0) FilterBy(x + (z * dimensionsX) + ((y - 1) * dimensionsX * dimensionsZ), o => o.aboveNeighbours);
        if (y < dimensionsY - 1) FilterBy(x + (z * dimensionsX) + ((y + 1) * dimensionsX * dimensionsZ), o => o.belowNeighbours);

        return options;
    }
    /// <summary>
    /// Devuelve true si la celda en coordenadas (x, y, z) está dentro del cubo
    /// central cuya generación es independiente del mapa.
    /// </summary>
    private bool IsInsideCube(int x, int y, int z)
    {
        return x >= cubeStartX && x < cubeEndX
            && y >= cubeStartY && y < cubeEndY
            && z >= cubeStartZ && z < cubeEndZ;
    }


    private void EnqueueNeighbors(int idx, int x, int y, int z,
                               Queue<int> queue, HashSet<int> inQueue)
    {
        void TryEnqueue(int ni)
        {
            if (ni >= 0 && ni < gridComponents.Count &&
                !gridComponents[ni].collapsed && !inQueue.Contains(ni))
            {
                queue.Enqueue(ni);
                inQueue.Add(ni);
            }
        }

        if (z > 0) TryEnqueue(x + ((z - 1) * dimensionsX) + (y * dimensionsX * dimensionsZ));
        if (z < dimensionsZ - 1) TryEnqueue(x + ((z + 1) * dimensionsX) + (y * dimensionsX * dimensionsZ));
        if (x > 0) TryEnqueue((x - 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ));
        if (x < dimensionsX - 1) TryEnqueue((x + 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ));
        if (y > 0) TryEnqueue(x + (z * dimensionsX) + ((y - 1) * dimensionsX * dimensionsZ));
        if (y < dimensionsY - 1) TryEnqueue(x + (z * dimensionsX) + ((y + 1) * dimensionsX * dimensionsZ));
    }


    //---------------SKIRTS---------------------


    void RefreshSkirtsAround(Cell cell)
    {
        RefreshSkirtsForCell(cell);

        int i = cell.index;
        int cellX = i % dimensionsX;
        int cellZ = (i / dimensionsX) % dimensionsZ;

        List<int> neighborOffsets = new List<int>();
        neighborOffsets.Add(dimensionsX);
        neighborOffsets.Add(-dimensionsX);
        neighborOffsets.Add(1);
        neighborOffsets.Add(-1);
        if (cellZ < dimensionsZ - 1 && cellX < dimensionsX - 1) neighborOffsets.Add(dimensionsX + 1);
        if (cellZ < dimensionsZ - 1 && cellX > 0) neighborOffsets.Add(dimensionsX - 1);
        if (cellZ > 0 && cellX < dimensionsX - 1) neighborOffsets.Add(-dimensionsX + 1);
        if (cellZ > 0 && cellX > 0) neighborOffsets.Add(-dimensionsX - 1);

        foreach (int offset in neighborOffsets)
        {
            int neighborIndex = i + offset;
            if (neighborIndex >= 0 && neighborIndex < gridComponents.Count)
                if (gridComponents[neighborIndex].collapsed)
                    RefreshSkirtsForCell(gridComponents[neighborIndex]);
        }
    }



    void RefreshSkirtsForCell(Cell cell)
    {
        Tile tileInstance = cell.GetComponentInChildren<Tile>();
        if (tileInstance == null) return;
        if (!tileInstance.useSkirts) return;

        int i = cell.index;

        // Posici�n de la celda en X y Z dentro del grid
        int cellX = i % dimensionsX;
        int cellZ = (i / dimensionsX) % dimensionsZ;

        // L�mites
        bool atNorthEdge = cellZ == dimensionsZ - 1;
        bool atSouthEdge = cellZ == 0;
        bool atEastEdge = cellX == dimensionsX - 1;
        bool atWestEdge = cellX == 0;

        // �ndices cardinales
        int northIdx = i + dimensionsX;
        int southIdx = i - dimensionsX;
        int eastIdx = i + 1;
        int westIdx = i - 1;

        // �ndices diagonales
        int neIdx = i + dimensionsX + 1;
        int nwIdx = i + dimensionsX - 1;
        int seIdx = i - dimensionsX + 1;
        int swIdx = i - dimensionsX - 1;

        // Cardinales: si est� en el borde del mapa, lo tratamos como s�lido
        // para no mostrar falda hacia el exterior
        bool hasN = atNorthEdge || IsSolidCollapsed(northIdx);
        bool hasS = atSouthEdge || IsSolidCollapsed(southIdx);
        bool hasE = atEastEdge || IsSolidCollapsed(eastIdx);
        bool hasW = atWestEdge || IsSolidCollapsed(westIdx);

        // Diagonales: solo v�lidas si ninguno de sus dos cardinales est� en borde
        bool hasNE = (!atNorthEdge && !atEastEdge) && IsSolidCollapsed(neIdx);
        bool hasNW = (!atNorthEdge && !atWestEdge) && IsSolidCollapsed(nwIdx);
        bool hasSE = (!atSouthEdge && !atEastEdge) && IsSolidCollapsed(seIdx);
        bool hasSW = (!atSouthEdge && !atWestEdge) && IsSolidCollapsed(swIdx);

        tileInstance.RefreshSkirts(hasN, hasS, hasE, hasW, hasNE, hasNW, hasSE, hasSW);
    }

    bool IsSolidCollapsed(int index)
    {
        if (index < 0 || index >= gridComponents.Count) return false;
        Cell c = gridComponents[index];
        if (!c.collapsed || c.tileOptions.Length == 0) return false;

        string type = c.tileOptions[0].tileType;
        // Las tiles invisibles no tapan huecos
        return type != "empty" && type != "air" && type != "solid" && type != "limit";
    }


    //--------------------------------------------------------------------------TILE EVENTS-----------------------------------------------------------------------------------------------------------------

    //--------------------------------EL JUGADOR ARRASTRA UNA TILE---------------------------
    /// <summary>
    /// Manejador del evento de arrastre de tile por parte del jugador.
    /// Identifica las celdas donde la tile encajaría (mismo tipo y rotación
    /// presentes en su dominio actual), activa la preview semitransparente
    /// sobre ellas y notifica al DragObject para que pueda hacer hit-test
    /// durante el arrastre.
    /// </summary>
    private void OnTileDrag(Tile draggedTile)
    {
        actualTileDragged = draggedTile.gameObject;

        validCells = FindCellsAcceptingTile(draggedTile);

        ShowPlacementPreview(validCells);

        draggedTile.GetComponent<DragObject>()?.SetValidCells(validCells);
    }

    /// <summary>
    /// Devuelve las celdas no colapsadas y visitables cuyo dominio contiene
    /// una tile con el mismo tipo y la misma rotación que la arrastrada.
    /// Es decir, las celdas donde el jugador podría soltar la tile y el
    /// algoritmo aceptaría la colocación.
    /// </summary>
    private List<Cell> FindCellsAcceptingTile(Tile draggedTile)
    {
        return gridComponents
            .Where(c => !c.collapsed && c.visitable)
            .Where(c => c.tileOptions.Any(opt =>
                opt.tileType == draggedTile.tileType &&
                opt.rotation == draggedTile.rotation))
            .ToList();
    }

    /// <summary>
    /// Activa la preview visual sobre las celdas candidatas, dejándolas
    /// todas semitransparentes. El DragObject ajustará dinámicamente la
    /// opacidad de la celda más cercana al cursor durante el arrastre.
    /// </summary>
    private void ShowPlacementPreview(List<Cell> cells)
    {
        foreach (Cell cell in cells)
        {
            cell.MakeVisible(true);
            cell.ChangeAlpha(alphaCube);
        }
    }

    /// <summary>
    /// Oculta la preview de todas las celdas que estaban resaltadas durante
    /// el último arrastre. Se invoca cuando el jugador suelta o cancela.
    /// </summary>
    private void HidePlacementPreview()
    {
        foreach (Cell cell in validCells) cell.MakeVisible(false);
    }


    //-----------------------------------------COLOCAR TILE EN CELDA-----------------------------------
    /// <summary>
    /// Manejador del evento de soltar tile por parte del jugador. Aplica
    /// el colapso de la celda destino con la tile arrastrada, propaga las
    /// restricciones mediante AC-3 desde esa celda y dispara la cascada
    /// de colapsos forzados que produce el efecto visual encadenado.
    /// </summary>
    private void OnTileRemoved(Tile draggedTile, Cell targetCell)
    {
        // Caso especial: petición externa de cancelar la colocación
        // (usado por el sistema de fusión de tiles para abortar el flujo)
        if (skipEntireTileRemoved)
        {
            AbortPlacement(draggedTile);
            return;
        }

        actualTileDragged = null;

        if (targetCell == null)
        {
            Debug.Log("[WFC] No hay celda destino válida.");
            return;
        }

        Tile persistentTile = ResolvePersistentTile(draggedTile);
        if (persistentTile == null) return;

        PlaceTileOnCell(persistentTile, targetCell);
        DiscardDraggedInstance(draggedTile);
        HidePlacementPreview();

        RegisterPlacedTile();

        onStartTilePropagation?.Invoke();
        PropagateFromCell(targetCell);
        UpdateGlobalValidTiles();
        onEndTilePropagation?.Invoke();

        TriggerCascadeIfEnabled();


    }
    /// <summary>
    /// Aborta una colocación en curso por petición externa (skipEntireTileRemoved).
    /// Destruye la instancia arrastrada y oculta la preview, sin modificar el grid.
    /// </summary>
    private void AbortPlacement(Tile draggedTile)
    {
        skipEntireTileRemoved = false;
        Destroy(draggedTile.gameObject);
        HidePlacementPreview();
    }

    /// <summary>
    /// Localiza la tile persistente del conjunto preprocesado (tileObjects)
    /// que coincide en tipo y rotación con la arrastrada. Las tiles del
    /// CardGenerator son instancias temporales; el grid debe almacenar
    /// referencias persistentes para que la propagación funcione con el
    /// mismo objeto que está en las listas de vecinos.
    /// </summary>
    private Tile ResolvePersistentTile(Tile draggedTile)
    {
        Tile persistent = tileObjects.FirstOrDefault(t =>
            t.tileType == draggedTile.tileType &&
            t.rotation == draggedTile.rotation);

        if (persistent == null)
            Debug.LogError($"[WFC] No se encontró tile persistente para {draggedTile.tileType}.");

        return persistent;
    }

    /// <summary>
    /// Aplica el colapso de una celda con una tile elegida por el jugador.
    /// ApplyCollapse solo actualiza el estado; aquí se instancia visualmente
    /// de forma inmediata porque es una acción del jugador que requiere feedback.
    /// </summary>
    private void PlaceTileOnCell(Tile persistentTile, Cell cell)
    {
        ApplyCollapse(cell, persistentTile);
        GetNeighboursCloseToCollapsedCell(cell);

        DestroyTileChildren(cell);
        InstantiateTileInCell(persistentTile, cell);
        RefreshSkirtsAround(cell);
    }

    /// <summary>
    /// Limpia la instancia que el jugador estaba arrastrando: desactiva
    /// el componente DragObject (por si quedaba colgado) y destruye el
    /// GameObject. La instancia colocada en el grid es una nueva,
    /// creada por ApplyCollapse a partir de la tile persistente.
    /// </summary>
    private void DiscardDraggedInstance(Tile draggedTile)
    {
        DragObject drag = draggedTile.GetComponent<DragObject>();
        if (drag != null) Destroy(drag);

        Destroy(draggedTile.gameObject);
    }

    /// <summary>
    /// Incrementa el contador de tiles colocadas y refresca la UI.
    /// 
    /// TODO: idealmente esta UI debería actualizarse por evento
    /// (suscripción a un GameEvents.OnTilePlaced) para no acoplar
    /// el motor WFC con el HUD de partida. Mientras tanto, queda
    /// aislado en este método como punto único de actualización.
    /// </summary>
    private void RegisterPlacedTile()
    {
        placedTiles++;
        placedTilesText.text = $"Fichas: {placedTiles}";
    }

    /// <summary>
    /// Lanza la cascada de colapsos forzados si la optimización está
    /// activa y la fase actual lo permite. El flag
    /// collapseOneOptionThisIteration se reinicia tras consumirse, para
    /// que el siguiente ciclo vuelva al comportamiento por defecto.
    /// </summary>
    private void TriggerCascadeIfEnabled()
    {
        if (OneTileCollapseOptimization && collapseOneOptionThisIteration)
        {
            CollapseUnitaryCellsSync();
            // Instanciar visualmente las tiles colapsadas en cascada
            BatchInstantiateTiles();
        }
        else
        {
            collapseOneOptionThisIteration = true;
        }
    }

    //------------------------------------------------ROTAR TILE------------------------------------------

    public void OnTileRotation(Vector3 rotation, Tile rotatedTile)
    {
        HidePlacementPreview();
        OnTileDrag(rotatedTile);
    }

    //------------------------------------------------ELIMINAR TILE EN LA PAPELERA--------------------------------------

    private void OnTileDeleted()
    {
        if (actualTileDragged != null) Destroy(actualTileDragged);
        foreach (Cell cell in gridComponents)
        {
            if (!cell.collapsed) cell.MakeVisible(false);
        }
    }

    /// <summary>
    /// Fuerza la colocación de una tile concreta sobre una celda, reemplazando
    /// cualquier contenido previo. 
    /// </summary>
    public void ForcePlaceTile(Cell targetCell, Tile persistentTile)
    {
        skipEntireTileRemoved = false;

        ApplyCollapse(targetCell, persistentTile);
        GetNeighboursCloseToCollapsedCell(targetCell);

        DestroyTileChildren(targetCell);
        InstantiateTileInCell(persistentTile, targetCell);
        RefreshSkirtsAround(targetCell);

        PropagateFromCell(targetCell);
        UpdateGlobalValidTiles();
        TriggerCascadeIfEnabled();
    }

    /// <summary>
    /// Resets a collapsed cell to an uncollapsed state, deleting any previous tile
    /// </summary>
    public void ResetCell(Cell cell)
    {
        DestroyTileChildren(cell);
        cell.collapsed = false;
        cell.tileOptions = tileObjects.ToArray(); // copia, no referencia
        cell.previousEntropy = tileObjects.Length;
        cell.visitable = true;
    }

    //Metodo para destruir los hijos de una celda sin destruir el grid cube u otros elementos, solo la tile
    private void DestroyTileChildren(Cell cell)
    {
        foreach (Transform child in cell.transform)
        {
            if (child.GetComponent<Tile>() != null)
                Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// Regenerates the map
    /// </summary>
    public void Regenerate()
    {
        if (onRegenerate != null)
        {
            onRegenerate();
        }

        StopAllCoroutines();

        if (!isRunning) ResumeTimer();
        if (finishPanel != null) finishPanel.SetActive(false);
        collapsedCells = 0;
        if (pauseBtn != null) pauseBtn.interactable = true;
        if (resumeBtn != null) resumeBtn.interactable = true;

        // Clear the grid
        for (int i = gameObject.transform.childCount - 1; i >= 0; i--)
        {
            Destroy(gameObject.transform.GetChild(i).gameObject);
        }
        gridComponents.Clear();

        mapsGenerated++;
        if (mapsGeneratedText != null)
            mapsGeneratedText.text = $"Nº mapas: {mapsGenerated}";

        Init();
    }

    /// <summary>
    /// Instancia visualmente todos los tiles colapsados que aún no tienen
    /// representación en escena. Se llama siempre al final de cualquier bucle
    /// de generación (RunGenerationSync, RunCubeGenerationSync) y tras la
    /// cascada de colapsos forzados (TriggerCascadeIfEnabled).
    ///
    /// Idempotente: la comprobación GetComponentInChildren garantiza que nunca
    /// duplica tiles ya instanciados (el jugador puede haber instanciado su tile
    /// antes de que la cascada llegue a esa celda).
    /// </summary>
    public void BatchInstantiateTiles()
    {
        foreach (Cell cell in gridComponents)
        {
            if (cell.GetComponentInChildren<Tile>() != null) continue;

            Tile tileToPlace = null;

            if (GENERATE_ALL && AC4_wave != null)
            {
                // Leer el tile resuelto directamente del wave (sin acceder a cell.tileOptions)
                int i = cell.index;
                for (int t = 0; t < AC4_T; t++)
                    if (AC4_wave[i * AC4_T + t]) { tileToPlace = tileObjects[t]; break; }
            }
            else
            {
                if (!cell.collapsed || cell.tileOptions.Length == 0) continue;
                tileToPlace = cell.tileOptions[0];
            }

            if (tileToPlace != null)
            {
                InstantiateTileInCell(tileToPlace, cell);
                RefreshSkirtsAround(cell);
            }
        }
    }

    // ============================================================
    // AC-4 — IMPLEMENTACIÓN COMPLETA
    // Equivalencia con GuminWFC: BuildPropagator → BuildAC4Propagator,
    // Clear → InitAC4FromCellState, Ban → BanAC4, Propagate → PropagateAC4,
    // NextUnobservedNode → SelectCellAC4, Observe → CollapseCellAC4.
    //
    // Diferencias respecto a Gumin:
    //  · Inicialización desde el estado post-restricciones (no desde cero).
    //  · Celdas de infraestructura (solid, empty, limit) se tratan como fijas.
    //  · cell.collapsed se mantiene para compatibilidad con modo juego.
    // ============================================================

    /// <summary>
    /// Construye el propagador AC-4 y los pesos de entropía de Shannon.
    /// Se llama UNA VEZ en Awake() tras PreprocessTileSet(), no en cada regeneración.
    /// </summary>
    private void BuildAC4Propagator()
    {
        if (tileObjects == null || tileObjects.Length == 0) return;

        AC4_T = tileObjects.Length;

        AC4_tileIndex = new Dictionary<Tile, int>(AC4_T);
        for (int t = 0; t < AC4_T; t++) AC4_tileIndex[tileObjects[t]] = t;

        AC4_tileW = new double[AC4_T];
        AC4_tileWLogW = new double[AC4_T];
        AC4_totalW = 0;
        AC4_totalWLogW = 0;
        for (int t = 0; t < AC4_T; t++)
        {
            double w = Math.Max(tileObjects[t].probability, 1);
            AC4_tileW[t] = w;
            AC4_tileWLogW[t] = w * Math.Log(w);
            AC4_totalW += w;
            AC4_totalWLogW += AC4_tileWLogW[t];
        }
        AC4_startEntropy = Math.Log(AC4_totalW) - AC4_totalWLogW / AC4_totalW;

        AC4_propagator = new int[6 * AC4_T][];
        for (int t = 0; t < AC4_T; t++)
        {
            Tile tile = tileObjects[t];
            AC4_propagator[0 * AC4_T + t] = AC4ToIndices(tile.rightNeighbours);
            AC4_propagator[1 * AC4_T + t] = AC4ToIndices(tile.leftNeighbours);
            AC4_propagator[2 * AC4_T + t] = AC4ToIndices(tile.upNeighbours);
            AC4_propagator[3 * AC4_T + t] = AC4ToIndices(tile.downNeighbours);
            AC4_propagator[4 * AC4_T + t] = AC4ToIndices(tile.aboveNeighbours);
            AC4_propagator[5 * AC4_T + t] = AC4ToIndices(tile.belowNeighbours);
        }
    }

    private int[] AC4ToIndices(List<Tile> neighbours)
    {
        var result = new List<int>(neighbours.Count);
        foreach (Tile n in neighbours)
            if (AC4_tileIndex.TryGetValue(n, out int idx))
                result.Add(idx);
        return result.ToArray();
    }

    /// <summary>
    /// Inicializa wave[] y compatible[] desde el estado de cell.tileOptions
    /// DESPUÉS de que ApplyGlobalConstraints() haya aplicado sus propagaciones AC-3.
    /// Llamar una vez por regeneración, justo antes de RunGenerationSync().
    /// </summary>
    private void InitAC4FromCellState()
    {
        int N = gridComponents.Count;
        int T = AC4_T;

        AC4_wave = new bool[N * T];
        AC4_compatible = new int[N * T * 6];
        AC4_domain = new int[N];
        AC4_entropy = new double[N];
        AC4_sumW = new double[N];
        AC4_sumWLogW = new double[N];
        AC4_stack = new (int, int)[N * T];
        AC4_stackSize = 0;
        AC4_contradiction = false;

        // PASO 1: inicializar wave desde cell.tileOptions (resultado de AC-3 + restricciones globales)
        for (int i = 0; i < N; i++)
        {
            Cell cell = gridComponents[i];
            var optSet = new HashSet<Tile>(cell.tileOptions); // O(|opts|) lookup
            int count = 0;
            double sumW = 0, sumWLogW = 0;

            for (int t = 0; t < T; t++)
            {
                bool valid = optSet.Contains(tileObjects[t]);
                AC4_wave[i * T + t] = valid;
                if (valid) { count++; sumW += AC4_tileW[t]; sumWLogW += AC4_tileWLogW[t]; }
            }

            // Celdas colapsadas por infraestructura tienen domain = 1 aunque
            // su tile (ej. limit) no esté en tileObjects
            AC4_domain[i] = cell.collapsed ? 1 : count;
            AC4_sumW[i] = sumW;
            AC4_sumWLogW[i] = sumWLogW;
            AC4_entropy[i] = (count > 1 && sumW > 0)
                ? Math.Log(sumW) - sumWLogW / sumW : 0;
        }

        // PASO 2: inicializar compatible[] según el estado actual del wave
        for (int i = 0; i < N; i++)
        {
            int x1 = i % dimensionsX;
            int z1 = (i / dimensionsX) % dimensionsZ;
            int y1 = i / (dimensionsX * dimensionsZ);

            for (int t = 0; t < T; t++)
            {
                for (int d = 0; d < 6; d++)
                {
                    int oppDir = AC4_OPP[d];
                    int x2 = x1 + AC4_DX[oppDir];
                    int y2 = y1 + AC4_DY[oppDir];
                    int z2 = z1 + AC4_DZ[oppDir];
                    int compIdx = (i * T + t) * 6 + d;

                    if (x2 < 0 || x2 >= dimensionsX || y2 < 0 || y2 >= dimensionsY || z2 < 0 || z2 >= dimensionsZ)
                    {
                        // Frontera: soporte completo (no hay vecino exterior que lo invalide)
                        AC4_compatible[compIdx] = AC4_propagator[oppDir * T + t].Length;
                        continue;
                    }

                    int j = x2 + z2 * dimensionsX + y2 * dimensionsX * dimensionsZ;
                    Cell jCell = gridComponents[j];

                    if (jCell.collapsed)
                    {
                        // Celda fija (solid, empty, limit…): verificar si su tile soporta a t
                        Tile jTile = jCell.tileOptions.Length > 0 ? jCell.tileOptions[0] : null;
                        if (jTile == null) { AC4_compatible[compIdx] = 0; continue; }
                        bool supports = AC4GetNeighboursForDir(jTile, d).Contains(tileObjects[t]);
                        AC4_compatible[compIdx] = supports ? 1 : 0;
                    }
                    else
                    {
                        // Celda normal: contar tiles del propagador que aún son válidos en j
                        int count = 0;
                        int[] supporters = AC4_propagator[oppDir * T + t];
                        for (int l = 0; l < supporters.Length; l++)
                            if (AC4_wave[j * T + supporters[l]]) count++;
                        AC4_compatible[compIdx] = count;
                    }
                }
            }
        }

        // PASO 3: propagación inicial para tiles sin soporte en alguna dirección no frontera
        for (int i = 0; i < N; i++)
        {
            if (gridComponents[i].collapsed) continue;
            int x1 = i % dimensionsX;
            int z1 = (i / dimensionsX) % dimensionsZ;
            int y1 = i / (dimensionsX * dimensionsZ);

            for (int t = 0; t < T; t++)
            {
                if (!AC4_wave[i * T + t]) continue;
                for (int d = 0; d < 6; d++)
                {
                    int x2 = x1 + AC4_DX[d]; int y2 = y1 + AC4_DY[d]; int z2 = z1 + AC4_DZ[d];
                    bool boundary = x2 < 0 || x2 >= dimensionsX || y2 < 0 || y2 >= dimensionsY || z2 < 0 || z2 >= dimensionsZ;
                    if (!boundary && AC4_compatible[(i * T + t) * 6 + d] == 0) { BanAC4(i, t); break; }
                }
            }
        }

        if (AC4_stackSize > 0) PropagateAC4();
    }

    private List<Tile> AC4GetNeighboursForDir(Tile tile, int dir)
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

    /// <summary>
    /// Elimina tile t de la celda i del wave AC-4.
    /// Actualiza contadores de soporte, entropía incremental y encola para propagación.
    /// </summary>
    private void BanAC4(int i, int t)
    {
        AC4_wave[i * AC4_T + t] = false;
        int baseComp = (i * AC4_T + t) * 6;
        for (int d = 0; d < 6; d++) AC4_compatible[baseComp + d] = 0;

        AC4_stack[AC4_stackSize++] = (i, t);

        AC4_domain[i]--;
        AC4_sumW[i] -= AC4_tileW[t];
        AC4_sumWLogW[i] -= AC4_tileWLogW[t];

        if (AC4_domain[i] == 0)
            AC4_contradiction = true;
        else
        {
            double s = AC4_sumW[i];
            AC4_entropy[i] = s > 0 ? Math.Log(s) - AC4_sumWLogW[i] / s : 0;
        }
    }

    /// <summary>
    /// Propagación AC-4: procesa el stack de tiles baneados, decrementa los
    /// contadores de soporte de los vecinos y banea aquellos que llegan a 0.
    /// Sin allocations en el bucle principal. Devuelve false si hay contradicción.
    /// </summary>
    private bool PropagateAC4()
    {
        int T = AC4_T;
        while (AC4_stackSize > 0 && !AC4_contradiction)
        {
            var (i1, t1) = AC4_stack[--AC4_stackSize];
            int x1 = i1 % dimensionsX;
            int z1 = (i1 / dimensionsX) % dimensionsZ;
            int y1 = i1 / (dimensionsX * dimensionsZ);

            for (int d = 0; d < 6; d++)
            {
                int x2 = x1 + AC4_DX[d]; int y2 = y1 + AC4_DY[d]; int z2 = z1 + AC4_DZ[d];
                if (x2 < 0 || x2 >= dimensionsX || y2 < 0 || y2 >= dimensionsY || z2 < 0 || z2 >= dimensionsZ) continue;

                int i2 = x2 + z2 * dimensionsX + y2 * dimensionsX * dimensionsZ;
                if (gridComponents[i2].collapsed) continue; // fijo → no modificar

                int[] supported = AC4_propagator[d * T + t1];
                for (int l = 0; l < supported.Length; l++)
                {
                    int t2 = supported[l];
                    ref int comp = ref AC4_compatible[(i2 * T + t2) * 6 + d];
                    comp--;
                    if (comp == 0 && AC4_wave[i2 * T + t2]) BanAC4(i2, t2);
                }
            }
        }
        return !AC4_contradiction;
    }

    /// <summary>
    /// Selecciona la celda de menor entropía de Shannon escaneando todas las
    /// celdas en O(N) con acceso directo a arrays. Sin allocations.
    /// Sustituye a SelectCellWithMinimumEntropy() en modo GENERATE_ALL.
    /// </summary>
    private Cell SelectCellAC4()
    {
        double minE = double.MaxValue;
        int minIdx = -1;

        for (int i = 0; i < gridComponents.Count; i++)
        {
            Cell cell = gridComponents[i];
            if (cell.collapsed || AC4_domain[i] <= 1) continue;

            double e = AC4_entropy[i] + 1E-6 * _rng.NextDouble(); // tie-breaking estocástico
            if (e < minE) { minE = e; minIdx = i; }
        }

        return minIdx >= 0 ? gridComponents[minIdx] : null;
    }

    /// <summary>
    /// Colapsa la celda por muestreo ponderado sobre el wave AC-4.
    /// Banea todos los tiles rechazados (alimenta PropagateAC4). Sin allocations.
    /// Sustituye a CollapseCell() en modo GENERATE_ALL.
    /// </summary>
    private bool CollapseCellAC4(Cell cell)
    {
        int i = cell.index;
        int T = AC4_T;

        double threshold = _rng.NextDouble() * AC4_sumW[i];
        double cumulative = 0;
        int chosen = -1;

        for (int t = 0; t < T; t++)
        {
            if (!AC4_wave[i * T + t]) continue;
            cumulative += AC4_tileW[t];
            if (cumulative >= threshold) { chosen = t; break; }
        }
        if (chosen < 0) // fallback numérico
            for (int t = T - 1; t >= 0; t--)
                if (AC4_wave[i * T + t]) { chosen = t; break; }

        if (chosen < 0) { HandleIncompatibility(); return false; }

        for (int t = 0; t < T; t++)
            if (AC4_wave[i * T + t] && t != chosen)
                BanAC4(i, t);

        cell.collapsed = true;
        GetNeighboursCloseToCollapsedCell(cell);
        return true;
    }

    /// <summary>
    //    /// Devuelve la tile resuelta en la celda cellIndex tras la generación.
    //    /// En modo GENERATE_ALL lee de AC4_wave; en modo juego lee de cell.tileOptions.
    //    /// Devuelve null para celdas no colapsadas.
    //    /// </summary>
    public Tile GetResolvedTile(int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= gridComponents.Count) return null;
        Cell cell = gridComponents[cellIndex];

        if (GENERATE_ALL && AC4_wave != null)
        {
            // Tiles jugables: leer desde AC4_wave
            for (int t = 0; t < AC4_T; t++)
                if (AC4_wave[cellIndex * AC4_T + t]) return tileObjects[t];
            // Tiles de infraestructura (solid, empty, limit): no están en AC4_wave
            // → leer de cell.tileOptions como fallback
        }

        return (cell.collapsed && cell.tileOptions.Length > 0)
            ? cell.tileOptions[0]
            : null;
    }

}