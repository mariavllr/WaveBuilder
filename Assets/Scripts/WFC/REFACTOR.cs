using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using DG.Tweening;
using TMPro;
using System;
using Random = UnityEngine.Random;
using Unity.VisualScripting.Antlr3.Runtime.Tree;


public enum StopwatchTest
{
    ALL_GENERATION,
    CUBE_GENERATION,
    TILE_PROPAGATION
}

public class WaveFunctionGame_REFACTOR : MonoBehaviour
{
    // ============================================================
    // CONFIGURACIÓN DEL INSPECTOR
    // ============================================================

    [Header("Mode")]
    [SerializeField] private bool GENERATE_ALL = false;
    [SerializeField] private bool randomGeneration;
    [SerializeField] private bool stopOnIncompatibility = false;
    [SerializeField] public bool tutorial = false;
    public bool useOptimization;
    public bool OneTileCollapseOptimization;

    [Header("Map dimensions")]
    [SerializeField] public int dimensionsX, dimensionsY, dimensionsZ;
    [SerializeField] private int cellSize;
    [SerializeField] private int initialCubeSize;


    [Header("Tile set")]
    [SerializeField] private Tile[] tileObjects;
    [SerializeField] private Tile floorTile;
    [SerializeField] private Tile emptyTile;
    [SerializeField] private Tile limitTile;
    [SerializeField] private Cell cellObj;
    [SerializeField] private GameObject newTilesContainer;
    public Material previewMaterial;

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
    [SerializeField] private bool renderEachFrame;
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

    [Header("Performance testing")]
    public bool STOPWATCH;
    public StopwatchTest testType;

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
    public static event OnStartGeneration onStartGeneration;
    public static event OnEndGeneration onEndGeneration;



    private void OnEnable()
    {
        GameEvents.OnTileDragged += OnTileDrag;
        GameEvents.OnTileReleased += OnTileRemoved;
        GameEvents.OnTileRotated += OnTileRotation;
        GameEvents.OnDeleteTile += OnTileDeleted;
    }

    private void OnDestroy()
    {
        GameEvents.OnTileDragged -= OnTileDrag;
        GameEvents.OnTileReleased -= OnTileRemoved;
        GameEvents.OnTileRotated -= OnTileRotation;
        GameEvents.OnDeleteTile -= OnTileDeleted;
    }


    void Awake()
    {
        //Aquí solo va lo que no debe rehacerse en cada regeneración del mapa:
        ValidateConfiguration();
        audioSource = GetComponent<AudioSource>();
        PreprocessTileSet();

        gridComponents = new List<Cell>();
        Init();
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
    private void PreprocessTileSet()
    {
        ClearNeighbours(ref tileObjects);
        CreateRemainingCells(ref tileObjects);
        DefineNeighbourTiles(ref tileObjects, ref tileObjects);

        tileObjects = tileObjects.Where(t => t.tileType != "limit").ToArray();

        newTilesContainer.SetActive(false);
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
        ResetState();
        SetupCamera();

        InitializeGrid();
        ApplyGlobalConstraints();

        if (!GENERATE_ALL) GetCenterCube();

        ConfigureCardGenerator();
        SignalGenerationStartIfStopwatch();
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
    private bool IsInfrastructureTile(string tileType)
    {
        return tileType == "limit" || tileType == "empty_limit"
            || tileType == "solid" || tileType == "empty"
            || tileType == "border" || tileType == "borderSand"
            || tileType == "cornerExtBorder" || tileType == "cornerIntBorder"
            || tileType == "cornerExt_border_sand" || tileType == "cornerInt_border_sand";
    }

    //------------STOPWATCH----------------

    /// <summary>
    /// Dispara el evento onStartGeneration si el cronómetro está activo y
    /// el tipo de test corresponde a una fase que se está iniciando ahora.
    /// </summary>
    private void SignalGenerationStartIfStopwatch()
    {

        //COMIENZA EL TEST DE RENDIMIENTO
        if (STOPWATCH && GENERATE_ALL && testType == StopwatchTest.ALL_GENERATION || testType == StopwatchTest.CUBE_GENERATION)
        {
            if (onStartGeneration != null)
            {
                onStartGeneration();
            }
        }
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
            UpdateGeneration();
        }
        else
        {
            cubeStep = true;
            UpdateGenerationCube();
        }
    }

  

    //------------------------------------------------BUCLE UPDATE-------------------------------------------

    private void Update()
    {
        //TIMER
        if (isRunning)
        {
            elapsedTime += Time.deltaTime;

            int hours = Mathf.FloorToInt(elapsedTime / 3600);
            int minutes = Mathf.FloorToInt((elapsedTime % 3600) / 60);
            int seconds = Mathf.FloorToInt(elapsedTime % 60);

            timerText.text = $"{hours:00}:{minutes:00}:{seconds:00}";
        }
    }

    public void PauseTimer() => isRunning = false;
    public void ResumeTimer() => isRunning = true;

    public void StartGame() { cubeStep = true; tutorial = false; ResumeTimer(); UpdateGenerationCube(); }
    public void ExitGame() => Application.Quit();


    //-------------------------------------------PREPROCESADO CON SOCKETS-------------------------------------

    /// <summary>
    /// Clears all the tiles' neighbours
    /// </summary>
    /// <param name="tiLeArray"></param> Array of tiles that need to be cleared
    private void ClearNeighbours(ref Tile[] tileArray)
    {
        foreach (Tile tile in tileArray)
        {
            tile.upNeighbours.Clear();
            tile.rightNeighbours.Clear();
            tile.downNeighbours.Clear();
            tile.leftNeighbours.Clear();
            tile.aboveNeighbours.Clear();
            tile.belowNeighbours.Clear();
        }
    }

    /// <summary>
    /// Generates a new tile variation based on a given tile
    /// </summary>
    /// <param name="tile"></param> Tile to be used as base
    /// <param name="nameVariation"></param> Suffix added to the new tile variation
    private Tile CreateNewTileVariation(Tile tile, string nameVariation)
    {
        GameObject newTile = Instantiate(tile.gameObject, newTilesContainer.transform);
        newTile.name = tile.gameObject.name + nameVariation;
        newTile.tag = tile.gameObject.tag;
        newTile.SetActive(false);

        Tile tileRotated = newTile.GetComponent<Tile>();
        tileRotated.tileType = tile.tileType;
        tileRotated.probability = tile.probability;
        tileRotated.positionOffset = tile.positionOffset;
        tileRotated.rotateRight = tile.rotateRight;
        tileRotated.rotate180 = tile.rotate180;
        tileRotated.rotateLeft = tile.rotateLeft;

        // useSkirts y todas las referencias de skirts ya est�n
        // correctamente remapeadas por el Instantiate

        return tileRotated;
    }

    /// <summary>
    /// Genera las variantes rotadas (90°, 180°, 270°) de cada tile del
    /// conjunto base, en función de las flags rotateRight/rotate180/rotateLeft declaradas en el inspector.
    /// </summary>
    private void CreateRemainingCells(ref Tile[] tileArray)
    {
        List<Tile> generated = new List<Tile>();

        foreach (Tile tile in tileArray)
        {
            if (tile.rotateRight) generated.Add(BuildRotatedVariant(tile, "_RotateRight", 90, 1));
            if (tile.rotate180) generated.Add(BuildRotatedVariant(tile, "_Rotate180", 180, 2));
            if (tile.rotateLeft) generated.Add(BuildRotatedVariant(tile, "_RotateLeft", 270, 3));
        }

        if (generated.Count > 0)
            tileArray = tileArray.Concat(generated).ToArray();
    }

    /// <summary>
    /// Crea una variante rotada de una tile y le aplica la transformación
    /// de sockets, exclusiones y faldas correspondiente.
    /// </summary>
    private Tile BuildRotatedVariant(Tile original, string suffix, float yDegrees, int quarterSteps)
    {
        Tile rotated = CreateNewTileVariation(original, suffix);
        RotateBorders(original, rotated, quarterSteps);
        rotated.rotation = new Vector3(0f, yDegrees, 0f);
        return rotated;
    }

    /// <summary>
    /// Rota los sockets, exclusiones y faldas de una tile k pasos de 90°
    /// en sentido horario.
    /// Fórmula geométrica: para una rotación de k pasos, el lado del índice
    /// i de la tile rotada toma el valor del lado (i − k) mod 4 de la
    /// original. Numeración horaria de lados: U=0, R=1, D=2, L=3 para
    /// sockets/exclusiones; N=0, E=1, S=2, W=3 para faldas cardinales;
    /// NE=0, SE=1, SW=2, NW=3 para faldas diagonales.
    /// 
    /// Los sockets verticales (above/below) no rotan en plano: solo se
    /// les actualiza el rotationIndex.
    /// </summary>
    private void RotateBorders(Tile original, Tile rotated, int k)
    {
        int steps = ((k % 4) + 4) % 4;
        if (steps == 0) return;

        // Sockets horizontales (U, R, D, L)
        var srcSockets = new[] { original.upSocket, original.rightSocket,
                              original.downSocket, original.leftSocket };
        rotated.upSocket = srcSockets[Wrap(0 - steps)];
        rotated.rightSocket = srcSockets[Wrap(1 - steps)];
        rotated.downSocket = srcSockets[Wrap(2 - steps)];
        rotated.leftSocket = srcSockets[Wrap(3 - steps)];

        // Sockets verticales: solo se reetiqueta la rotación
        rotated.aboveSocket = original.aboveSocket;
        rotated.aboveSocket.rotationIndex = steps * 90;
        rotated.belowSocket = original.belowSocket;
        rotated.belowSocket.rotationIndex = steps * 90;

        // Exclusiones horizontales
        var srcExcl = new[] { original.excludedNeighboursUp,    original.excludedNeighboursRight,
                          original.excludedNeighboursDown,  original.excludedNeighboursLeft };
        rotated.excludedNeighboursUp = srcExcl[Wrap(0 - steps)];
        rotated.excludedNeighboursRight = srcExcl[Wrap(1 - steps)];
        rotated.excludedNeighboursDown = srcExcl[Wrap(2 - steps)];
        rotated.excludedNeighboursLeft = srcExcl[Wrap(3 - steps)];

        if (rotated.useSkirts) RotateSkirts(original, rotated, steps);
    }

    /// <summary>
    /// Rotación específica de las faldas (cardinales y diagonales).
    /// </summary>
    private void RotateSkirts(Tile original, Tile rotated, int steps)
    {
        var srcCard = new[] { original.skirtNorth, original.skirtEast,
                          original.skirtSouth, original.skirtWest };
        rotated.skirtNorth = srcCard[Wrap(0 - steps)];
        rotated.skirtEast = srcCard[Wrap(1 - steps)];
        rotated.skirtSouth = srcCard[Wrap(2 - steps)];
        rotated.skirtWest = srcCard[Wrap(3 - steps)];

        var srcDiag = new[] { original.skirtCornerNE, original.skirtCornerSE,
                          original.skirtCornerSW, original.skirtCornerNW };
        rotated.skirtCornerNE = srcDiag[Wrap(0 - steps)];
        rotated.skirtCornerSE = srcDiag[Wrap(1 - steps)];
        rotated.skirtCornerSW = srcDiag[Wrap(2 - steps)];
        rotated.skirtCornerNW = srcDiag[Wrap(3 - steps)];
    }

    /// <summary>
    /// Aritmética modular para rotaciones de cuatro lados. Devuelve el
    /// índice (i mod 4) garantizando un resultado en [0, 3], incluso si i
    /// es negativo.
    /// </summary>
    private static int Wrap(int i) => ((i % 4) + 4) % 4;



    /// <summary>
    /// Compara dos sockets soportando tanto el sistema antiguo (Enum) como el nuevo (ScriptableObject)
    /// </summary>
    bool SocketsMatch(Tile.Socket socketA, Tile.Socket socketB)
    {
        // Caso 1: Ambos usan el nuevo sistema (ScriptableObjects)
        if (socketA.HasCustomDefinition && socketB.HasCustomDefinition)
        {
            return socketA.socketDefinition == socketB.socketDefinition;
        }

        // Caso 2: Ambos usan el sistema antiguo (Enum)
        // Solo si NINGUNO tiene definici�n custom
        if (!socketA.HasCustomDefinition && !socketB.HasCustomDefinition)
        {
            return socketA.socket_name == socketB.socket_name;
        }

        // Caso 3: Mezcla de sistemas (Uno nuevo y uno viejo) -> No conectan nunca
        return false;
    }


    /// <summary>
    /// Calcula la tabla completa de adyacencias permitidas entre tiles
    /// del conjunto, comparando sockets, exclusiones bidireccionales y
    /// reglas de simetría/flip (caras horizontales) o de rotación
    /// invariante (caras verticales).
    /// 
    /// Para cada par (tile, otherTile) y cada una de las seis direcciones
    /// del cubo, si la conexión es válida se añade otherTile a la lista
    /// de vecinos correspondiente de tile.
    /// </summary>
    public void DefineNeighbourTiles(ref Tile[] tileArray, ref Tile[] otherTileArray)
    {
        foreach (Tile a in tileArray)
            foreach (Tile b in otherTileArray)
            {
                // Caras horizontales: regla de simetría/flip
                if (CanConnectHorizontal(a.upSocket, b.downSocket,
                    a.excludedNeighboursUp, b.excludedNeighboursDown, a.tileType, b.tileType))
                    a.upNeighbours.Add(b);

                if (CanConnectHorizontal(a.downSocket, b.upSocket,
                    a.excludedNeighboursDown, b.excludedNeighboursUp, a.tileType, b.tileType))
                    a.downNeighbours.Add(b);

                if (CanConnectHorizontal(a.rightSocket, b.leftSocket,
                    a.excludedNeighboursRight, b.excludedNeighboursLeft, a.tileType, b.tileType))
                    a.rightNeighbours.Add(b);

                if (CanConnectHorizontal(a.leftSocket, b.rightSocket,
                    a.excludedNeighboursLeft, b.excludedNeighboursRight, a.tileType, b.tileType))
                    a.leftNeighbours.Add(b);

                // Caras verticales: regla de rotación invariante
                if (CanConnectVertical(a.aboveSocket, b.belowSocket))
                    a.aboveNeighbours.Add(b);

                if (CanConnectVertical(a.belowSocket, b.aboveSocket))
                    a.belowNeighbours.Add(b);
            }
    }

    /// <summary>
    /// Determina si dos tiles pueden ser vecinas en una dirección horizontal.
    /// Tres condiciones: los sockets coinciden, las exclusiones bidireccionales
    /// se respetan (si el constraint está activo), y la combinación de simetría
    /// y flip permite la conexión geométrica.
    /// </summary>
    private bool CanConnectHorizontal(
        Tile.Socket socketA, Tile.Socket socketB,
        List<string> excludedA, List<string> excludedB,
        string typeA, string typeB)
    {
        if (!SocketsMatch(socketA, socketB)) return false;

        if (excludedNeighborConstraint
            && (excludedA.Contains(typeB) || excludedB.Contains(typeA)))
            return false;

        return socketA.isSymmetric || socketB.isSymmetric
            || (socketA.isFlipped != socketB.isFlipped);
    }

    /// <summary>
    /// Determina si dos tiles pueden ser vecinas en una dirección vertical.
    /// Las caras superior e inferior no aplican simetría ni flip: o ambas
    /// son rotacionalmente invariantes, o sus rotationIndex coinciden.
    /// </summary>
    private bool CanConnectVertical(Tile.Socket socketA, Tile.Socket socketB)
    {
        if (!SocketsMatch(socketA, socketB)) return false;

        return (socketA.rotationallyInvariant && socketB.rotationallyInvariant)
            || (socketA.rotationIndex == socketB.rotationIndex);
    }


    //-------------------------CREAR TILES DE CAPAS DE INFAESTRUCTURA--------------------

    //FUNCIONES AUXILIARES
    /// <summary>
    /// Coloca una tile de infraestructura sobre una celda durante la fase de inicialización del mapa. 
    /// </summary>
    private void PlaceInfrastructureTile(Cell cell, Tile tile, bool expandFrontier = false)
    {
        cell.tileOptions = new Tile[] { tile };
        cell.collapsed = true;

        DestroyTileChildren(cell);
        InstantiateTileInCell(tile, cell);

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
        List<Cell> candidates = GetSelectableCells();
        if (candidates.Count == 0) return null;

        if (randomGeneration)
            return candidates[_rng.Next(0, candidates.Count)];

        // MRV: un único barrido lineal para localizar la entropía mínima
        int minEntropy = int.MaxValue;
        foreach (Cell c in candidates)
            if (c.tileOptions.Length < minEntropy)
                minEntropy = c.tileOptions.Length;

        // Desempate aleatorio entre las celdas con esa entropía
        List<Cell> tied = candidates
            .Where(c => c.tileOptions.Length == minEntropy)
            .ToList();

        return tied[_rng.Next(0, tied.Count)];
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
        int randomNumber = _rng.Next(0, tiles.Count);

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
    /// Aplica el colapso sobre la celda
    /// </summary>
    private void ApplyCollapse(Cell cell, Tile selectedTile)
    {
        cell.previousEntropy = cell.tileOptions.Length;
        cell.tileOptions = new Tile[] { selectedTile };
        cell.collapsed = true;

        if (cell.centerCubeCell) cubeCellsRemaining--;

        DestroyTileChildren(cell);
        InstantiateTileInCell(selectedTile, cell);
        RefreshSkirtsAround(cell);
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
    /// Ejecuta una iteración completa del bucle WFC: selecciona una celda
    /// con MRV, la colapsa, propaga las restricciones con AC-3 y delega
    /// en el orquestador correspondiente (UpdateGenerationCube en la fase
    /// de cubo, UpdateGeneration en GENERATE_ALL) para encadenar la
    /// siguiente iteración.
    /// 
    /// Se implementa como corutina para ceder un frame entre iteraciones,
    /// distribuyendo la generación a lo largo del tiempo y evitando
    /// bloqueos perceptibles en grids grandes.
    /// </summary>
    private IEnumerator CheckEntropy()
    {
        Cell cell = SelectCellWithMinimumEntropy();
        if (cell == null)
        {
            Debug.Log("[WFC] No quedan celdas seleccionables.");
            yield break;
        }

        if(renderEachFrame) yield return null; // ceder un frame para que Unity renderice

        if (!CollapseCell(cell)) yield break; // incompatibilidad ya gestionada

        PropagateFromCell(cell);

        if (cubeStep)
            UpdateGenerationCube();
        else if (GENERATE_ALL)
            UpdateGeneration();
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


    //------------------------------------------------------BUCLES PRINCIPALES (UPDATE GENERATION)-----------------------------------------------

    /// <summary>
    /// Orquestador del bucle principal de WFC en modo generación automática
    /// (GENERATE_ALL). Decide si seguir iterando o terminar.
    /// 
    /// La propagación de restricciones se delega a PropagateFromCell (AC-3),
    /// que se llama desde CollapseCell tras cada colapso. Esta función NO
    /// hace barridos completos del grid: solo se encarga del control de flujo.
    /// 
    /// En modo juego (GENERATE_ALL = false) esta función no debe llamarse.
    /// La secuencia tras una acción del jugador es:
    ///     PropagateFromCell(cell) -> UpdateGlobalValidTiles() -> CollapseEntropyOneCells()
    /// y se ejecuta directamente en OnTileRemoved / ForcePlaceTile.
    /// </summary>
    public void UpdateGeneration()
    {
        if (!GENERATE_ALL)
        {
            Debug.LogWarning("[WFC] UpdateGeneration solo debe llamarse en modo GENERATE_ALL.");
            return;
        }

        // Refrescar el conjunto global de tiles válidas tras la última propagación
        UpdateGlobalValidTiles();

        iterations++;

        // Criterio de parada: hemos colapsado todas las celdas posibles
        int totalCells = dimensionsX * dimensionsY * dimensionsZ;
        if (iterations > totalCells)
        {
            if (STOPWATCH && testType == StopwatchTest.ALL_GENERATION && onEndGeneration != null)
                onEndGeneration();
            return;
        }

        // Siguiente iteración: seleccionar y colapsar la celda con menor entropía.
        // CheckEntropy llama a CollapseCell, que a su vez propaga con AC-3
        // y vuelve a llamar aquí para la siguiente iteración.
        StartCoroutine(CheckEntropy());
    }

    /// <summary>
    /// Orquestador del bucle de generación del cubo inicial en modo juego.
    /// Cuando se han colapsado todas las celdas del cubo central, marca el
    /// fin de la fase y entrega el control al modo juego propiamente dicho.
    /// </summary>
    void UpdateGenerationCube()
    {
        if (cubeCellsRemaining > 0)
        {
            StartCoroutine(CheckEntropy());
            return;
        }

        // Fin de la fase de cubo
        Debug.Log("[WFC] Fin de la generación del cubo inicial.");
        cubeStep = false;

        // Test de rendimiento del cubo
        if (STOPWATCH && testType == StopwatchTest.CUBE_GENERATION
            && !GENERATE_ALL && onEndGeneration != null)
        {
            onEndGeneration();
            return;
        }

        // Transición al modo juego: la primera ronda de colapsos en cascada
        // no se ejecuta para que el jugador empiece con un estado limpio.
        collapseOneOptionThisIteration = false;
        UpdateGlobalValidTiles();
    }


    //----------------------------------------------------------COLAPSOS EN CASCADA------------------------------------------------

    /// <summary>
    /// Cascada de colapsos forzados sobre celdas con entropía unitaria.
    /// 
    /// Tras cada colocación del jugador, la propagación AC-3 puede dejar
    /// celdas con una única tile válida en su dominio. Esta corutina las
    /// colapsa de forma encadenada con un retardo entre colapsos, lo que
    /// produce el efecto visual característico del modo juego.
    /// 
    /// Cada iteración localiza una celda unitaria, la colapsa y propaga
    /// las restricciones desde ella antes de buscar la siguiente. De este
    /// modo, las celdas que pasen a entropía 1 como consecuencia del
    /// colapso anterior se incorporan automáticamente a la cascada, sin
    /// necesidad de recolectar ni recurrir.
    /// 
    /// Termina cuando ya no quedan celdas unitarias visitables.
    /// </summary>
    private IEnumerator CollapseUnitaryCellsInCascade()
    {
        while (true)
        {
            Cell next = FindNextUnitaryCell();
            if (next == null) yield break;

            ApplyForcedCollapse(next);
            PropagateFromCell(next);
            UpdateGlobalValidTiles();

            if (animations)
                yield return new WaitForSeconds(animationDelay);
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
            if (!c.collapsed && c.visitable && c.tileOptions.Length == 1)
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

        if (animations) PlayCollapseBounce(cell, jumpPower: 0.3f, duration: animationDuration);
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

        // INICIO TEST COLOCAR UNA FICHA
        if (STOPWATCH && testType == StopwatchTest.TILE_PROPAGATION && onStartGeneration != null)
            onStartGeneration();

        PropagateFromCell(targetCell);
        UpdateGlobalValidTiles();

        // FIN TEST COLOCAR UNA FICHA
        if (STOPWATCH && testType == StopwatchTest.TILE_PROPAGATION && onEndGeneration != null)
            onEndGeneration();

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
    /// Reutiliza la rutina canónica ApplyCollapse para mantener un único
    /// punto de implementación, y añade el efecto visual de rebote propio
    /// de la colocación manual (más pronunciado que el de la cascada).
    /// 
    /// El orden importa: ApplyCollapse reduce el dominio antes de instanciar,
    /// lo que garantiza que GetNeighboursCloseToCollapsedCell lea el tipo
    /// correcto al expandir la frontera.
    /// </summary>
    private void PlaceTileOnCell(Tile persistentTile, Cell cell)
    {
        ApplyCollapse(cell, persistentTile);
        GetNeighboursCloseToCollapsedCell(cell);

        if (animations)
            PlayCollapseBounce(cell, jumpPower: 0.5f, duration: 0.3f);
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
            StartCoroutine(CollapseUnitaryCellsInCascade());
        else
            collapseOneOptionThisIteration = true;
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
        // Red de seguridad: el sistema de fusión activa skipEntireTileRemoved
        // para abortar el OnTileRemoved que precede a la llamada. Lo reseteamos
        // aquí para evitar que un fallo del sistema de fusión deje la flag
        // colgada e ignore la siguiente colocación legítima del jugador.
        skipEntireTileRemoved = false;

        ApplyCollapse(targetCell, persistentTile);
        GetNeighboursCloseToCollapsedCell(targetCell);

        if (animations)
            PlayCollapseBounce(targetCell, jumpPower: 0.8f, duration: 0.5f);

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

        // Clear the grid
        for (int i = gameObject.transform.childCount - 1; i >= 0; i--)
        {
            Destroy(gameObject.transform.GetChild(i).gameObject);
        }
        gridComponents.Clear();

        mapsGenerated++;
        mapsGeneratedText.text = $"Nº mapas: {mapsGenerated}";

        Init();
    }

}