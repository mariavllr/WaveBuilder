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


public enum StopwatchTest
{
    ALL_GENERATION,
    CUBE_GENERATION,
    TILE_PROPAGATION
}

public class WaveFunctionGame : MonoBehaviour
{
    [SerializeField] private int iterations = 0;
    [SerializeField] private bool GENERATE_ALL = false;
    [SerializeField] private bool animations = true;
    [SerializeField] private float animationDuration = 0.1f;
    [SerializeField] private float animationDelay = 0.01f;


    [Header("Game")]
    [SerializeField] CardGenerator cardGenerator;

    //cubo inicial
    [SerializeField] private int initialCubeSize;
    private int cubeStartX, cubeEndX;
    private int cubeStartY, cubeEndY;
    private int cubeStartZ, cubeEndZ;
    public int centerCubeCells;
    private bool collapseOneOptionThisIteration = true;

    List<Cell> validCells = new List<Cell>();
    bool cubeStep = true;
    public GameObject actualTileDragged;
    public Material previewMaterial;
    public float alphaCube = 0.1f;
    private System.Random _rng = new System.Random();

    public HashSet<(string tileType, Vector3 rotation)> globalValidTiles = new(); //para trackear las tiles validas en el mapa en cada iteracion

    public bool skipEntireTileRemoved = false; //Para que funcione fusionar varias tiles en una nueva

    //sounds
    public AudioSource audioSource;
    public AudioClip changeCellSound;
    public AudioClip collapseCellSound;


    [Header("Map generation")]
    [SerializeField] public int dimensionsX, dimensionsZ, dimensionsY;
    [SerializeField] Tile floorTile;                     //Tile for the floor
    [SerializeField] Tile emptyTile;                     //Tile for the ceiling
    [SerializeField] Tile limitTile;                    //Tile for the borders of the map
    [SerializeField] private Tile[] tileObjects;         //All the tiles that can be used to generate the map
    [SerializeField] int cellSize;
    [SerializeField] GameObject newTilesContainer;          //When rotation tiles are generated, the new gameobjects need to be stored somewhere

    [Header("Grid")]
    [SerializeField] public List<Cell> gridComponents;   //A list with all the cells inside the grid
    [SerializeField] private Cell cellObj;                //They can be collapsed or not. Tiles are their children.

    [Header("Global Constraints")]
    public bool probabilityConstraint = true;
    public bool excludedNeighborConstraint = true;
    public bool floorCeilingConstraint = true;
    public bool fixedTilesConstraint = true;
    public bool borderConstraint = true;

    [Header("Optimization")]
    [SerializeField] private bool useOptimization; //OLD: used to propagate only on frontier, not necessary now
    [SerializeField] private bool OneTileCollapseOptimization;
    [SerializeField] private bool randomGeneration;

    [Header("Debug")]
    public int placedTiles = 0;
    public int mapsGenerated = 1;
    [SerializeField] private TextMeshProUGUI placedTilesText;
    [SerializeField] private TextMeshProUGUI mapsGeneratedText;

    public TextMeshProUGUI timerText;
    private float elapsedTime;
    public bool isRunning = true;

    public bool tutorial = false; //Si hay tutorial, no se generara el mapa hasta que el tutorial acabe
    public bool stopOnIncompatibility = false;

    //para testear el rendimiento
    public bool STOPWATCH;
    public StopwatchTest testType;
    

    //Events
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
        //Si el modo es JUEGO, siempre debe estar activo OneTileCollapseOptimization y no debe estar activo useOptimization
        if (!GENERATE_ALL)
        {
            OneTileCollapseOptimization = true;
            useOptimization = false;
        }

        //PREPROCESSING
        ClearNeighbours(ref tileObjects);
        CreateRemainingCells(ref tileObjects);
        DefineNeighbourTiles(ref tileObjects, ref tileObjects);
        //Eliminar el limite para que no pueda colocarse en el mapa mas
        tileObjects = tileObjects.Where(tile => tile.tileType != "limit").ToArray();

        newTilesContainer.SetActive(false); // Hide the new tiles container in the editor
        gridComponents = new List<Cell>();
        audioSource = GetComponent<AudioSource>();
        Init();
    }


    private void Init()
    {
        //Setup camera
        CameraControl cameraControl = FindAnyObjectByType<CameraControl>();
        if (cameraControl != null)
            cameraControl.SetupCamera(dimensionsX, dimensionsZ, dimensionsY, cellSize);


        centerCubeCells = 0;
        iterations = 0;

        //INITIALIZE
        InitializeGrid();
        collapseOneOptionThisIteration = true;

        if (borderConstraint) DefineMapLimits();
        if (floorCeilingConstraint)
        {
            CreateSolidFloor();
            CreateSolidCeiling();
        }

        if (fixedTilesConstraint) CreateFixedTiles();

        //Propagar cambios de tiles predefinidas
        foreach (Cell c in gridComponents)
            if (c.collapsed) PropagateFromCell(c);

        if (!GENERATE_ALL) GetCenterCube();

        //A�ADIR TODAS LAS FICHAS AL CARD GENERATOR
        cardGenerator.tilesList = tileObjects.ToList();

        for (int i = cardGenerator.tilesList.Count - 1; i >= 0; i--)
        {
            Tile element = cardGenerator.tilesList[i];
            if (element.tileType == "limit" || element.tileType == "empty_limit" || element.tileType == "solid" || element.tileType == "empty" || element.tileType == "cornerExtBorder" || element.tileType == "border"
                || element.tileType == "cornerIntBorder" || element.tileType == "cornerExt_border_sand" || element.tileType == "borderSand" || element.tileType == "cornerInt_border_sand")
            {
                cardGenerator.tilesList.Remove(element);
            }
        }

        //COMIENZA EL TEST DE RENDIMIENTO
        if (STOPWATCH && GENERATE_ALL && testType == StopwatchTest.ALL_GENERATION || testType == StopwatchTest.CUBE_GENERATION)
        {
            if(onStartGeneration != null)
            {
                onStartGeneration();
            }
        }


        if (!tutorial)
        {
            ResumeTimer();

            //START WFC
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
    }

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
    /// Generates the tile variations needed to get the full set of possible tiles
    /// based of the initial set of tiles
    /// </summary>
    /// <param name="tileArray"></param> Array of all pre-existing tiles
    private void CreateRemainingCells(ref Tile[] tileArray)
    {
        List<Tile> newTiles = new List<Tile>();
        foreach (Tile tile in tileArray)
        {
            // Clockwise by default
            if (tile.rotateRight)
            {
                Tile tileRotated = CreateNewTileVariation(tile, "_RotateRight");
                RotateBorders90(tile, tileRotated);
                tileRotated.rotation = new Vector3(0f, 90f, 0f);
                newTiles.Add(tileRotated);
            }

            if (tile.rotate180)
            {
                Tile tileRotated = CreateNewTileVariation(tile, "_Rotate180");
                RotateBorders180(tile, tileRotated);
                tileRotated.rotation = new Vector3(0f, 180f, 0f);
                newTiles.Add(tileRotated);
            }

            if (tile.rotateLeft)
            {
                Tile tileRotated = CreateNewTileVariation(tile, "_RotateLeft");
                RotateBorders270(tile, tileRotated);
                tileRotated.rotation = new Vector3(0f, 270f, 0f);
                newTiles.Add(tileRotated);
            }
        }

        if (newTiles.Count != 0)
        {
            Tile[] aux = tileArray.Concat(newTiles.ToArray()).ToArray();
            tileArray = aux;
        }
    }

    /// <summary>
    /// Updates the sockets and excluded neighbours of a tile that has been rotated 90 degrees
    /// </summary>
    /// <param name="originalTile"></param> Non-rotated tile
    /// <param name="tileRotated"></param> Rotated tile
    private void RotateBorders90(Tile originalTile, Tile tileRotated)
    {
        tileRotated.rightSocket = originalTile.upSocket;
        tileRotated.leftSocket = originalTile.downSocket;
        tileRotated.upSocket = originalTile.leftSocket;
        tileRotated.downSocket = originalTile.rightSocket;

        tileRotated.aboveSocket = originalTile.aboveSocket;
        tileRotated.aboveSocket.rotationIndex = 90;
        tileRotated.belowSocket = originalTile.belowSocket;
        tileRotated.belowSocket.rotationIndex = 90;

        //excluded neighbours
        tileRotated.excludedNeighboursRight = originalTile.excludedNeighboursUp;
        tileRotated.excludedNeighboursLeft = originalTile.excludedNeighboursDown;
        tileRotated.excludedNeighboursUp = originalTile.excludedNeighboursLeft;
        tileRotated.excludedNeighboursDown = originalTile.excludedNeighboursRight;

        if (tileRotated.useSkirts)
        {

            // Guardar referencias actuales de tileRotated
            var n = tileRotated.skirtNorth;
            var s = tileRotated.skirtSouth;
            var e = tileRotated.skirtEast;
            var w = tileRotated.skirtWest;
            var ne = tileRotated.skirtCornerNE;
            var nw = tileRotated.skirtCornerNW;
            var se = tileRotated.skirtCornerSE;
            var sw = tileRotated.skirtCornerSW;

            tileRotated.skirtNorth = w;
            tileRotated.skirtEast = n;
            tileRotated.skirtSouth = e;
            tileRotated.skirtWest = s;
            tileRotated.skirtCornerNE = nw;
            tileRotated.skirtCornerSE = ne;
            tileRotated.skirtCornerSW = se;
            tileRotated.skirtCornerNW = sw;
        }

    }

    /// <summary>
    /// Updates the sockets and excluded neighbours of a tile that has been rotated 180 degrees
    /// </summary>
    /// <param name="originalTile"></param> Non-rotated tile
    /// <param name="tileRotated"></param> Rotated tile
    private void RotateBorders180(Tile originalTile, Tile tileRotated)
    {
        tileRotated.rightSocket = originalTile.leftSocket;
        tileRotated.leftSocket = originalTile.rightSocket;
        tileRotated.upSocket = originalTile.downSocket;
        tileRotated.downSocket = originalTile.upSocket;
        tileRotated.aboveSocket = originalTile.aboveSocket;
        tileRotated.aboveSocket.rotationIndex = 180;
        tileRotated.belowSocket = originalTile.belowSocket;
        tileRotated.belowSocket.rotationIndex = 180;

        //excluded neighbours
        tileRotated.excludedNeighboursLeft = originalTile.excludedNeighboursRight;
        tileRotated.excludedNeighboursRight = originalTile.excludedNeighboursLeft;
        tileRotated.excludedNeighboursUp = originalTile.excludedNeighboursDown;
        tileRotated.excludedNeighboursDown = originalTile.excludedNeighboursUp;

        if (tileRotated.useSkirts)
        {
            var n = tileRotated.skirtNorth;
            var s = tileRotated.skirtSouth;
            var e = tileRotated.skirtEast;
            var w = tileRotated.skirtWest;
            var ne = tileRotated.skirtCornerNE;
            var nw = tileRotated.skirtCornerNW;
            var se = tileRotated.skirtCornerSE;
            var sw = tileRotated.skirtCornerSW;

            tileRotated.skirtNorth = s;
            tileRotated.skirtEast = w;
            tileRotated.skirtSouth = n;
            tileRotated.skirtWest = e;
            tileRotated.skirtCornerNE = sw;
            tileRotated.skirtCornerSE = nw;
            tileRotated.skirtCornerSW = ne;
            tileRotated.skirtCornerNW = se;
        }

    }

    /// <summary>
    /// Updates the sockets and excluded neighbours of a tile that has been rotated 270 degrees
    /// </summary>
    /// <param name="originalTile"></param> Non-rotated tile
    /// <param name="tileRotated"></param> Rotated tile
    private void RotateBorders270(Tile originalTile, Tile tileRotated)
    {
        tileRotated.rightSocket = originalTile.downSocket;
        tileRotated.leftSocket = originalTile.upSocket;
        tileRotated.upSocket = originalTile.rightSocket;
        tileRotated.downSocket = originalTile.leftSocket;
        tileRotated.aboveSocket = originalTile.aboveSocket;
        tileRotated.aboveSocket.rotationIndex = 270;
        tileRotated.belowSocket = originalTile.belowSocket;
        tileRotated.belowSocket.rotationIndex = 270;

        //excluded neighbours
        tileRotated.excludedNeighboursRight = originalTile.excludedNeighboursDown;
        tileRotated.excludedNeighboursLeft = originalTile.excludedNeighboursUp;
        tileRotated.excludedNeighboursUp = originalTile.excludedNeighboursRight;
        tileRotated.excludedNeighboursDown = originalTile.excludedNeighboursLeft;
        if (tileRotated.useSkirts)
        {
            var n = tileRotated.skirtNorth;
            var s = tileRotated.skirtSouth;
            var e = tileRotated.skirtEast;
            var w = tileRotated.skirtWest;
            var ne = tileRotated.skirtCornerNE;
            var nw = tileRotated.skirtCornerNW;
            var se = tileRotated.skirtCornerSE;
            var sw = tileRotated.skirtCornerSW;

            tileRotated.skirtNorth = e;
            tileRotated.skirtEast = s;
            tileRotated.skirtSouth = w;
            tileRotated.skirtWest = n;
            tileRotated.skirtCornerNE = se;
            tileRotated.skirtCornerSE = sw;
            tileRotated.skirtCornerSW = nw;
            tileRotated.skirtCornerNW = ne;
        }
    }

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
    /// Defines the neighbour tiles of each tile in the array
    /// </summary>
    /// <param name="tileArray"></param> Array of tiles
    /// <param name="otherTileArray"></param> Array of tiles to compare with
    public void DefineNeighbourTiles(ref Tile[] tileArray, ref Tile[] otherTileArray)
    {
        foreach (Tile tile in tileArray)
        {
            foreach (Tile otherTile in otherTileArray)
            {
                // HORIZONTAL FACES: Same socket and be symmetric OR one flip and the other not
                // It also checks f the excluded list of each face does not include the other tile, and vice versa

                // Up neighbours 
                if (SocketsMatch(otherTile.downSocket, tile.upSocket)
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursUp.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursDown.Contains(tile.tileType))))
                {
                    if (tile.upSocket.isSymmetric || otherTile.downSocket.isSymmetric
                    || (otherTile.downSocket.isFlipped && !tile.upSocket.isFlipped)
                    || (!otherTile.downSocket.isFlipped && tile.upSocket.isFlipped))
                        tile.upNeighbours.Add(otherTile);
                }
                // Down neighbours 
                if (SocketsMatch(otherTile.upSocket, tile.downSocket)
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursDown.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursUp.Contains(tile.tileType))))
                {
                    if (otherTile.upSocket.isSymmetric || tile.downSocket.isSymmetric
                    || (otherTile.upSocket.isFlipped && !tile.downSocket.isFlipped)
                    || (!otherTile.upSocket.isFlipped && tile.downSocket.isFlipped))
                        tile.downNeighbours.Add(otherTile);
                }
                // Right neighbours 
                if (SocketsMatch(otherTile.leftSocket, tile.rightSocket)
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursRight.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursLeft.Contains(tile.tileType))))
                {
                    if (otherTile.leftSocket.isSymmetric || tile.rightSocket.isSymmetric
                    || (otherTile.leftSocket.isFlipped && !tile.rightSocket.isFlipped)
                    || (!otherTile.leftSocket.isFlipped && tile.rightSocket.isFlipped))
                        tile.rightNeighbours.Add(otherTile);
                }
                // Left neighbours 
                if (SocketsMatch(otherTile.rightSocket, tile.leftSocket)
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursLeft.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursRight.Contains(tile.tileType))))
                {
                    if (otherTile.rightSocket.isSymmetric || tile.leftSocket.isSymmetric
                        || (otherTile.rightSocket.isFlipped && !tile.leftSocket.isFlipped)
                        || (!otherTile.rightSocket.isFlipped && tile.leftSocket.isFlipped))
                        tile.leftNeighbours.Add(otherTile);
                }

                // VERTICAL FACES: both faces must have invariable rotation or the same rotation index

                // Below neighbours
                if (SocketsMatch(otherTile.belowSocket, tile.aboveSocket))
                {
                    if ((otherTile.belowSocket.rotationallyInvariant
                        && tile.aboveSocket.rotationallyInvariant)
                        || (otherTile.belowSocket.rotationIndex == tile.aboveSocket.rotationIndex))
                        tile.aboveNeighbours.Add(otherTile);
                }

                // Above neighbours
                if (SocketsMatch(otherTile.aboveSocket, tile.belowSocket))
                {
                    if ((otherTile.aboveSocket.rotationallyInvariant
                        && tile.belowSocket.rotationallyInvariant)
                        || (otherTile.aboveSocket.rotationIndex == tile.belowSocket.rotationIndex))
                        tile.belowNeighbours.Add(otherTile);
                }
            }
        }
    }

    /// <summary>
    /// Creates the grid full of cells
    /// </summary>
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

        for (int y = cubeStartY; y < cubeEndY; y++)
            for (int z = cubeStartZ; z < cubeEndZ; z++)
                for (int x = cubeStartX; x < cubeEndX; x++)
                {
                    int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                    if (index < 0 || index >= gridComponents.Count) continue;
                    gridComponents[index].centerCubeCell = true;
                    centerCubeCells++;
                }
    }

    /// <summary>
    /// Fills the first layer of the map with a solid tile to avoid empty spaces
    /// </summary>
    void CreateSolidFloor()
    {
        int y = 0;
        for (int z = 0; z < dimensionsZ; z++)
        {
            for (int x = 0; x < dimensionsX; x++)
            {
                var index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                Cell cellToCollapse = gridComponents[index];
                cellToCollapse.tileOptions = new Tile[] { floorTile };
                cellToCollapse.collapsed = true;
                DestroyTileChildren(cellToCollapse);

                Tile instantiatedTile = Instantiate(floorTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
                if (instantiatedTile.rotation != Vector3.zero)
                {
                    instantiatedTile.gameObject.transform.Rotate(floorTile.rotation, Space.Self);
                }

                instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
                instantiatedTile.gameObject.SetActive(true);
                iterations++;
            }
        }
    }

    /// <summary>
    /// Fills the last layer of the map with a solid tile to avoid empty spaces
    /// </summary>
    void CreateSolidCeiling()
    {
        int y = dimensionsY - 1;
        for (int z = 0; z < dimensionsZ; z++)
        {
            for (int x = 0; x < dimensionsX; x++)
            {
                var index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                Cell cellToCollapse = gridComponents[index];
                cellToCollapse.tileOptions = new Tile[] { emptyTile };
                cellToCollapse.collapsed = true;
                DestroyTileChildren(cellToCollapse);

                Tile instantiatedTile = Instantiate(emptyTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
                if (instantiatedTile.rotation != Vector3.zero)
                {
                    instantiatedTile.gameObject.transform.Rotate(emptyTile.rotation, Space.Self);
                }

                instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
                instantiatedTile.gameObject.SetActive(true);
                iterations++;
            }
        }
    }

    /// <summary>
    /// Define the borders of the map as "limit" to avoid strange borders
    /// </summary>
    void DefineMapLimits()
    {
        int y = 1; // justo encima del suelo (suelo es y=0)

        for (int z = 0; z < dimensionsZ; z++)
        {
            for (int x = 0; x < dimensionsX; x++)
            {
                // �es borde en X o Z?
                bool isBorder = (x == 0 || x == dimensionsX - 1 || z == 0 || z == dimensionsZ - 1);

                if (isBorder)
                {
                    int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                    Cell cellToCollapse = gridComponents[index];

                    // Marcar como borde
                    cellToCollapse.tileOptions = new Tile[] { limitTile };
                    cellToCollapse.collapsed = true;

                    //Necesario para que los alrededores del limite sean visitables si el modo es GENERAR TODO EL MAPA y se usa la optimizacion de frontera
                    //En modo juego, los limites del mapa NO deberian ser visitables
                    if(useOptimization && GENERATE_ALL) GetNeighboursCloseToCollapsedCell(cellToCollapse);

                    // limpiar hijos previos
                    DestroyTileChildren(cellToCollapse);

                    // Instanciar la tile "border"
                    Tile instantiatedTile = Instantiate(limitTile,
                                                        cellToCollapse.transform.position,
                                                        Quaternion.identity,
                                                        cellToCollapse.transform);

                    if (instantiatedTile.rotation != Vector3.zero)
                    {
                        instantiatedTile.gameObject.transform.Rotate(limitTile.rotation, Space.Self);
                    }

                    instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
                    instantiatedTile.gameObject.SetActive(true);

                    iterations++;
                }
            }
        }
    }

    /// <summary>
    /// Creates tiles that are defined as fixed in the map
    /// </summary>
    void CreateFixedTiles()
    {
        foreach (Tile tile in tileObjects)
        {
            //If tile.fixedTile is > 0, that is the number of that tile that has to appear in the map. Else, that tile is not fixed
            if (tile.fixedTile > 0)
            {
                int fixedTilesToPlace = tile.fixedTile;

                for (int i = 0; i < fixedTilesToPlace; i++)
                {
                    // Find a random cell that is not collapsed yet
                    List<Cell> availableCells = gridComponents.Where(c => !c.collapsed).ToList();
                    if (availableCells.Count == 0)
                    {
                        Debug.LogWarning("No more available cells to place fixed tiles.");
                        return;
                    }
                    Cell cellToCollapse = availableCells[Random.Range(0, availableCells.Count)];
                    cellToCollapse.collapsed = true;

                    // Make the neighbours of the collapsed cell visitable for optimization purposes
                    //GetNeighboursCloseToCollapsedCell(cellToCollapse);

                    cellToCollapse.tileOptions = new Tile[] { tile };
                    // limpiar hijos previos
                    DestroyTileChildren(cellToCollapse);

                    Tile instantiatedTile = Instantiate(tile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
                    if (instantiatedTile.rotation != Vector3.zero)
                    {
                        instantiatedTile.gameObject.transform.Rotate(tile.rotation, Space.Self);
                    }
                    instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
                    instantiatedTile.gameObject.SetActive(true);
                    iterations++;
                }
            }
        }
    }



    /// <summary>
    /// Reorders the grid based on the entropy of the cells, collapsing the one with less entropy
    /// </summary>
    IEnumerator CheckEntropy()
    {
        List<Cell> tempGrid;

        if (cubeStep)
        {
            tempGrid = new List<Cell>(initialCubeSize * initialCubeSize * (dimensionsY - 2));
            for (int y = cubeStartY; y < cubeEndY; y++)
                for (int z = cubeStartZ; z < cubeEndZ; z++)
                    for (int x = cubeStartX; x < cubeEndX; x++)
                    {
                        int idx = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                        Cell c = gridComponents[idx];
                        if (!c.collapsed) tempGrid.Add(c);
                    }
        }
        else
        {
            tempGrid = new List<Cell>(gridComponents);
            tempGrid.RemoveAll(c => c.collapsed);
        }

        if (tempGrid.Count == 0) { Debug.Log("No hay mas cells"); yield break; }
        //------------This is done to ensure that the cell with less entropy is selected-----------------
        // The result of this calculation determines the order of the elements in the sorted list.
        // If the result is negative, it means a should come before b; if positive, it means a should come after b;
        // and if zero, their order remains unchanged.
        int stopIndex = tempGrid.Count;
        if (!randomGeneration)
        {
            tempGrid.Sort((a, b) => { return a.tileOptions.Length - b.tileOptions.Length; });

            // Removes all the cells with more options than the first one
            // This is done to ensure that only the cells with less entropy are selected
            int arrLength = tempGrid[0].tileOptions.Length;

            for (int i = 1; i < tempGrid.Count; i++)
            {
                if (tempGrid[i].tileOptions.Length > arrLength)
                {
                    stopIndex = i;
                    break;
                }
            }
        }

        yield return new WaitForSeconds(0f); // Debugging purposes

        CollapseCell(ref tempGrid, stopIndex);
    }

    /// <summary>
    /// Collapses a cell and updates the grid
    /// </summary>
    /// <param name="tempGrid"></param>
    /// <param name="stopIndex"></param>
    void CollapseCell(ref List<Cell> tempGrid, int stopIndex)
    {
        Cell cellToCollapse;
        cellToCollapse = tempGrid[Random.Range(0, stopIndex)];

      
        // Make the neighbours of the collapsed cell visitable for optimization purposes
        GetNeighboursCloseToCollapsedCell(cellToCollapse);

        // Choose a tile for that cell
        //List<(Tile tile, int weight)> weightedTiles = cellToCollapse.tileOptions.Select(tile => (tile, tile.probability)).ToList();

        Tile selectedTile;
        if (probabilityConstraint)
        {
            selectedTile = ChooseTile(cellToCollapse.tileOptions);
        }

        else
        {
            selectedTile = ChooseRandomTile(cellToCollapse.tileOptions.ToList());
        }

        if (selectedTile is null)
        {
            Debug.LogError("INCOMPATIBILITY!");
            if(onIncompatibility != null)
            {
                onIncompatibility();
            }
            //incompatibility = true;

            //Si hay una incompatibilidad, se regenera SIN parar el tiempo
             /*if (STOPWATCH)
             {
                 inc_counter++;
             }*/

             if(!stopOnIncompatibility) Regenerate();
             return;

        }


        cellToCollapse.previousEntropy = cellToCollapse.tileOptions.Length;
        cellToCollapse.tileOptions = new Tile[] { selectedTile };
        //cellToCollapse.lastTriedTile = selectedTile;
        Tile foundTile = cellToCollapse.tileOptions[0];

        DestroyTileChildren(cellToCollapse);

        Tile instantiatedTile = Instantiate(foundTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
        if (instantiatedTile.rotation != Vector3.zero)
        {
            instantiatedTile.gameObject.transform.Rotate(foundTile.rotation, Space.Self);
        }

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);

        cellToCollapse.collapsed = true;

        
        RefreshSkirtsAround(cellToCollapse);

        if (cubeStep)
        {
            PropagateFromCell(cellToCollapse);  // AC-3 global, se para al converger
            UpdateGenerationCube();
        }
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
    /// Updates all the cells in the grid
    /// </summary>
    void UpdateGenerationCube()
    {
        /*for (int y = cubeStartY; y < cubeEndY; y++)
            for (int z = cubeStartZ; z < cubeEndZ; z++)
                for (int x = cubeStartX; x < cubeEndX; x++)
                    CheckNeighbours(x, y, z, ref gridComponents);

        iterations++;*/

        if (iterations <= centerCubeCells)
            StartCoroutine(CheckEntropy());
        else
        {
            print("END GENERATION CUBE");
            cubeStep = false;
            //ACABA TEST RENDIMIENTO GENERAR CUBO INICIAL
            if (testType == StopwatchTest.CUBE_GENERATION && STOPWATCH && !GENERATE_ALL && onEndGeneration != null)
                onEndGeneration();
            else
            {
                collapseOneOptionThisIteration = false;
                UpdateGeneration();
            }

            collapseOneOptionThisIteration = false;
            UpdateGeneration();
        }
    }

    public void UpdateGeneration()
    {
        foreach (Cell cell in gridComponents)
            cell.haSidoVisitado = false;


        //---------MODO GENERAR TODO EL MAPA---------

        if (GENERATE_ALL)
        {
            // Flujo original: un unico pase, sin bucle
            List<Cell> newGenerationCell = new List<Cell>(gridComponents);


            for (int y = 0; y < dimensionsY; y++)
            {
                for (int z = 0; z < dimensionsZ; z++)
                {
                    for (int x = 0; x < dimensionsX; x++)
                    {
                        CheckNeighbours(x, y, z, ref newGenerationCell);

                        //OPTIMIZACION: Si la celda tiene solo una opcion, que se colapse

                        if (OneTileCollapseOptimization)
                        {
                            var index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                            //bool allNeighborsCollapsed = newGenerationCell[index].neighbors.Values.All(neighbor => neighbor.collapsed);

                            if (!newGenerationCell[index].collapsed && newGenerationCell[index].tileOptions.Length == 1
                                && newGenerationCell[index].visitable && newGenerationCell[index].previousEntropy == 1)
                            {
                                CollapseCellWithOneTileOption(newGenerationCell, index);
                            }
                        }
                    }
                }
            }

            gridComponents = newGenerationCell;
            if (GENERATE_ALL) iterations++;

            UpdateGlobalValidTiles();

            //Si generando el mapa completo aun no se ha terminado, seguir con la siguiente
            if (iterations <= (dimensionsX * dimensionsY * dimensionsZ) && GENERATE_ALL)
            {
                StartCoroutine(CheckEntropy());
            }

            //ACABA TEST RENDIMIENTO GENERAR TODO EL MAPA
            else if (STOPWATCH && GENERATE_ALL && testType == StopwatchTest.ALL_GENERATION)
            {
                if (onEndGeneration != null)
                {
                    onEndGeneration();
                }
            }
        }

        //----------MODO JUEGO------------

        else
        {
            //TEST COLOCAR UNA FICHA
            if (onStartGeneration != null && STOPWATCH && testType == StopwatchTest.TILE_PROPAGATION)
            {
                onStartGeneration();
            }

            // Flujo juego: bucle hasta convergencia
            bool anyChanged = true;
            int safetyLimit = 100;

            while (anyChanged && safetyLimit-- > 0)
            {
                anyChanged = false;
                List<Cell> newGenerationCell = new List<Cell>(gridComponents);

                for (int y = 0; y < dimensionsY; y++)
                    for (int z = 0; z < dimensionsZ; z++)
                        for (int x = 0; x < dimensionsX; x++)
                        {
                            int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                            int prevLength = gridComponents[index].tileOptions.Length;

                            CheckNeighbours(x, y, z, ref newGenerationCell);

                            if (!gridComponents[index].collapsed &&
                                newGenerationCell[index].tileOptions.Length != prevLength)
                                anyChanged = true;
                        }

                gridComponents = newGenerationCell;
            }

            UpdateGlobalValidTiles();

            //FINALIZA TEST COLOCAR UNA FICHA
            if (onEndGeneration != null && STOPWATCH && testType == StopwatchTest.TILE_PROPAGATION)
            {
                onEndGeneration();
            }
            // Colapsos forzados con animacion, solo en modo juego
            if (OneTileCollapseOptimization && collapseOneOptionThisIteration)
                StartCoroutine(CollapseEntropyOneCells());
            else collapseOneOptionThisIteration = true;
        }
    }

    IEnumerator CollapseEntropyOneCells()
    {
        // Recoge todas las celdas forzadas de una vez
        List<Cell> toCollapse = gridComponents
            .Where(c => !c.collapsed && c.tileOptions.Length == 1)
            .ToList();

        if (toCollapse.Count == 0) yield break;

        foreach (Cell cell in toCollapse)
        {
            if (cell.collapsed) continue;

            CollapseCellWithOneTileOption(gridComponents, cell.index);

            // Pequeno delay entre colapsos para efecto visual encadenado
            if (animations) yield return new WaitForSeconds(animationDelay);
        }

        // Tras colapsar todo, propagar de nuevo y buscar nuevos forzados
        // (los colapsos anteriores pueden haber creado nuevas entropias 1)
        bool newForcedCells = gridComponents.Any(c => !c.collapsed && c.visitable && c.tileOptions.Length == 1);
        if (newForcedCells)
        {
            UpdateGeneration(); // Propagacion + nueva ronda de animaciones
        }
    }

    void CollapseCellWithOneTileOption(List<Cell> cells, int index)
    {
        Cell cellToCollapse = cells[index];
        GetNeighboursCloseToCollapsedCell(cellToCollapse);

        Tile foundTile = cellToCollapse.tileOptions[0];

        DestroyTileChildren(cellToCollapse);

        Tile instantiatedTile = Instantiate(foundTile,
            cellToCollapse.transform.position, Quaternion.identity,
            cellToCollapse.transform);

        if (instantiatedTile.rotation != Vector3.zero)
            instantiatedTile.gameObject.transform.Rotate(foundTile.rotation, Space.Self);

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);

        // Efecto visual igual que el colapso manual del jugador
        if(animations) instantiatedTile.transform.DOJump(instantiatedTile.transform.position,
            jumpPower: 0.3f, numJumps: 1, duration: animationDuration).SetEase(Ease.OutBounce);


        cellToCollapse.collapsed = true;
        iterations++;
        RefreshSkirtsAround(cellToCollapse); // anadir
    }


    //ESTE METODO PERMITE TENER UNA LISTA GLOBAL DE TILES VALIDAS EN TODO EL MAPA PARA PODER SACAR EN CARDGENERATOR SOLO TILES VALIDAS (que pueden colocarse en al menos 1 celda)
    private void UpdateGlobalValidTiles()
    {
        globalValidTiles.Clear();

        foreach (Cell cell in gridComponents)
        {
            if (!cell.collapsed && cell.visitable)
            {
                foreach (Tile t in cell.tileOptions)
                {
                    globalValidTiles.Add((t.tileType, t.rotation));
                }
            }
        }
    }

    /// <summary>
    /// looks and update the options in every cell of the given list looking at the neighbours
    /// </summary>
    /// <param name="x"></param> x coordinate of the cell
    /// <param name="y"></param> y coordinate of the cell
    /// <param name="z"></param> z coordinate of the cell
    /// <param name="newGenerationCell"></param> List of cells to be updated
    void CheckNeighbours(int x, int y, int z, ref List<Cell> newGenerationCell)
    {
        int up, down, left, right, above, below;
        var index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
        right = (x + 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
        left = (x - 1) + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
        up = x + ((z + 1) * dimensionsX) + (y * dimensionsX * dimensionsZ);
        down = x + ((z - 1) * dimensionsX) + (y * dimensionsX * dimensionsZ);
        above = x + (z * dimensionsX) + ((y + 1) * dimensionsX * dimensionsZ);
        below = x + (z * dimensionsX) + ((y - 1) * dimensionsX * dimensionsZ);

        if (gridComponents[index].collapsed || (!gridComponents[index].visitable && useOptimization))
        {
            newGenerationCell[index] = gridComponents[index];
        }

        else
        {
            //define neighbors inside Cell


            //Check neighbors
            gridComponents[index].haSidoVisitado = true;
            List<Tile> options = new List<Tile>(tileObjects);


            // Checks the down cell
            if (z > 0)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[down].tileOptions)
                    validSet.UnionWith(opt.upNeighbours);
                CheckValidity(options, validSet, index);
            }
            // Checks the right cell
            if (x < dimensionsX - 1)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[right].tileOptions)
                    validSet.UnionWith(opt.leftNeighbours);
                CheckValidity(options, validSet, index);
            }
            // Checks the up cell
            if (z < dimensionsZ - 1)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[up].tileOptions)
                    validSet.UnionWith(opt.downNeighbours);
                CheckValidity(options, validSet, index);
            }
            // Checks the left cell
            if (x > 0)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[left].tileOptions)
                    validSet.UnionWith(opt.rightNeighbours);
                CheckValidity(options, validSet, index);
            }
            // Checks the cell below
            if (y > 0)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[below].tileOptions)
                    validSet.UnionWith(opt.aboveNeighbours);
                CheckValidity(options, validSet, index);
            }
            // Checks the cell above
            if (y < dimensionsY - 1)
            {
                HashSet<Tile> validSet = new HashSet<Tile>();
                foreach (Tile opt in gridComponents[above].tileOptions)
                    validSet.UnionWith(opt.belowNeighbours);
                CheckValidity(options, validSet, index);
            }

            // Log options after validity check
            // Debug.Log($"Options after CheckValidity for Cell[{index}]: {string.Join(", ", options.Select(o => o.tileType))}");

            Tile[] newTileList = new Tile[options.Count];

            for (int i = 0; i < options.Count; i++)
            {
                newTileList[i] = options[i];
            }

            newGenerationCell[index].RecreateCell(newTileList);

        }

        /// <summary>
        /// Removes all the options from the optionList that are not in the validOption list
        /// </summary>
        /// <param name="optionList"></param> List of options to be checked
        /// <param name="validOption"></param> List of valid options
        void CheckValidity(List<Tile> optionList, HashSet<Tile> validSet, int indexCell)
        {
            var optionCopy = optionList.ToList();
            optionList.Clear();
            foreach (var option in optionCopy)
            {
                if (validSet.Contains(option) && option.tileType != "limit")
                {
                    optionList.Add(option);
                }
            }
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


    //-----------------TILE EVENTS-------------------

    private void OnTileDrag(Tile draggedTile)
    {
        actualTileDragged = draggedTile.gameObject;
        //Cuando el jugador escoge una tile, tenemos que mostrar s�lo las celdas donde puede encajar
        List<Cell> tempGrid = new List<Cell>(gridComponents);

        tempGrid.RemoveAll(c => c.collapsed);
        tempGrid.RemoveAll(c => !c.visitable);

        //todas las del mismo tipo y rotacion
        validCells = tempGrid
        .Where(cell => cell.tileOptions
            .Any(tile => tile.tileType == draggedTile.tileType && tile.rotation == draggedTile.rotation))
        .ToList();

        foreach (Cell cell in validCells)
        {
            cell.MakeVisible(true);
            cell.ChangeAlpha(0.1f); // todas empiezan semitransparentes
        }

        draggedTile.GetComponent<DragObject>()?.SetValidCells(validCells);
    }

    public void OnTileRotation(Vector3 rotation, Tile tileRotated)
    {
        foreach (Cell cell in gridComponents)
        {
            if (!cell.collapsed) cell.MakeVisible(false);
        }

        OnTileDrag(tileRotated);
    }


    //---------------COLOCAR TILE EN CELDA---------------
    private void OnTileRemoved(Tile tile, Cell closest)
    {
        if (skipEntireTileRemoved)
        {
            skipEntireTileRemoved = false;
            Destroy(tile.gameObject);
            foreach (Cell cell in validCells) cell.MakeVisible(false); 
            return;
        }

        GameObject tileRemoved = tile.gameObject;
        actualTileDragged = null;
        Cell cellToCollapse = closest;
        if (cellToCollapse == null)
        {
            Debug.Log("No hay celdas!");
            return;
        }


        cellToCollapse.collapsed = true;

        // Make the neighbours of the collapsed cell visitable for optimization purposes
        GetNeighboursCloseToCollapsedCell(cellToCollapse);

        Tile selectedTile = tileRemoved.GetComponent<Tile>();

        Tile persistentTile = tileObjects.FirstOrDefault(t => t.tileType == selectedTile.tileType && t.rotation == selectedTile.rotation);

        if (persistentTile == null)
        {
            Debug.LogError($"No se encontro tile persistente para {selectedTile.tileType}");
            persistentTile = selectedTile; // fallback
        }

        cellToCollapse.tileOptions = new Tile[] { persistentTile }; // <-- referencia persistente
        Tile foundTile = cellToCollapse.tileOptions[0];


        DestroyTileChildren(cellToCollapse);

        Tile instantiatedTile = Instantiate(foundTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
        if (instantiatedTile.rotation != Vector3.zero)
        {
            //Rotar la tile
            instantiatedTile.gameObject.transform.Rotate(foundTile.rotation, Space.Self);

        }

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);

        

        //Desactivar ser arrastrado
        DragObject drag = instantiatedTile.GetComponent<DragObject>();
        if (drag != null)
        {
            Destroy(drag);
        }

        // Efecto de rebote con DOTween
        if(animations) instantiatedTile.transform.DOJump(instantiatedTile.transform.position, jumpPower: 0.5f, numJumps: 1, duration: 0.3f).SetEase(Ease.InOutFlash);


        foreach (Cell cell in validCells)
        {
            cell.MakeVisible(false);
        }

        Destroy(tileRemoved);

        placedTiles++;

        placedTilesText.text = "Fichas: " + placedTiles.ToString();

        //Ahora:

          RefreshSkirtsAround(cellToCollapse);
          PropagateFromCell(cellToCollapse);  // solo propaga desde donde se colocó
          UpdateGlobalValidTiles();
          if (OneTileCollapseOptimization && collapseOneOptionThisIteration)
              StartCoroutine(CollapseEntropyOneCells());
          else collapseOneOptionThisIteration = true;

        /* //Antes:
        RefreshSkirtsAround(cellToCollapse);
        UpdateGeneration();
        */

    }


    //---------ELIMINAR TILE EN LA PAPELERA-------------

    private void OnTileDeleted()
    {
        if (actualTileDragged != null) Destroy(actualTileDragged);
        foreach (Cell cell in gridComponents)
        {
            if (!cell.collapsed) cell.MakeVisible(false);
        }
    }


    //---------PUNTOS-------------


 /*   Cell FindClosestCell(GameObject origin, List<Cell> cells)
    {
        Cell closest = null;
        float minDistSq = Mathf.Infinity;
        Vector3 originPos = origin.transform.position;

        foreach (Cell cell in cells)
        {
            float distSq = (cell.transform.position - originPos).sqrMagnitude;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closest = cell;
            }
        }
        return closest;
    }*/

    /// <summary>
    /// Force a specific tile on a specific cell. Replace one if it exist.
    /// </summary>
    public void ForcePlaceTile(Cell cellToCollapse, Tile persistentTile)
    {
        DestroyTileChildren(cellToCollapse);

        cellToCollapse.tileOptions = new Tile[] { persistentTile };
        Tile instantiatedTile = Instantiate(persistentTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);

        if (instantiatedTile.rotation != Vector3.zero)
            instantiatedTile.gameObject.transform.Rotate(persistentTile.rotation, Space.Self);

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);
        cellToCollapse.collapsed = true;

        skipEntireTileRemoved = false;

        // Efecto visual de rebote con DOTween
        if (animations) instantiatedTile.transform.DOJump(instantiatedTile.transform.position, jumpPower: 0.8f, numJumps: 1, duration: 0.5f).SetEase(Ease.OutBack);

        UpdateGeneration();
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




    // PROPAGACION DE RESTRICCIONES CON AC-3
    private void PropagateFromCell(Cell placedCell)
    {
        if (onStartGeneration != null && STOPWATCH)
        {
            onStartGeneration();
        }
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
        if (onEndGeneration != null && STOPWATCH)
        {
            onEndGeneration();
        }
    }

    private List<Tile> ComputeValidOptions(int x, int y, int z)
    {
        List<Tile> options = new List<Tile>(tileObjects);
        var index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);

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
}