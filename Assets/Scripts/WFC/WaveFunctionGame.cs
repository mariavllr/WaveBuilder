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

[System.Serializable]
public class CollapseRecord
{
    public int cellIndex;
    public Tile[] previousOptions;
    public Tile chosenTile;
}

public class WaveFunctionGame : MonoBehaviour
{
    [SerializeField] private int iterations = 0;
    [SerializeField] private bool GENERATE_ALL = false;


    [Header("Game")]
    [SerializeField] CardGenerator cardGenerator;
    [SerializeField] private int initialCubeSize;
    public int centerCubeCells;
    List<Cell> validCells = new List<Cell>();
    bool cubeStep = true;
    public GameObject actualTileDragged;
    public Material previewMaterial;
    public float alphaCube = 0.1f;

    public HashSet<(string tileType, Vector3 rotation)> globalValidTiles = new(); //para trackear las tiles validas en el mapa en cada iteracion

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
    [SerializeField] private bool useOptimization;
    [SerializeField] private bool OneTileCollapseOptimization;
    [SerializeField] private bool randomGeneration;

    //Backtracking
   /* Stack<CollapseRecord> collapseHistory = new Stack<CollapseRecord>();
    public int maxBacktracks = 1000;
    private int backtracks = 0;*/

    [Header("Debug")]
    public int placedTiles = 0;
    [SerializeField] private TextMeshProUGUI placedTilesText;

    public TextMeshProUGUI timerText;
    private float elapsedTime;
    public bool isRunning = true;

    public bool tutorial = false; //Si hay tutorial, no se generara el mapa hasta que el tutorial acabe
    public bool stopOnIncompatibility = false;

    //para testear el rendimiento
    public bool STOPWATCH;
    


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
    }

    void Awake()
    {
        //PREPROCESSING
        ClearNeighbours(ref tileObjects);
        CreateRemainingCells(ref tileObjects);
        DefineNeighbourTiles(ref tileObjects, ref tileObjects);

        newTilesContainer.SetActive(false); // Hide the new tiles container in the editor
        gridComponents = new List<Cell>();
        audioSource = GetComponent<AudioSource>();
        Init();
    }


    private void Init()
    {
        centerCubeCells = 0;
        iterations = 0;


        //INITIALIZE
        InitializeGrid();

        if (borderConstraint) DefineMapLimits();
        if (floorCeilingConstraint)
        {
            CreateSolidFloor();
            CreateSolidCeiling();
        }

        if (fixedTilesConstraint) CreateFixedTiles();

        if (!GENERATE_ALL) GetCenterCube();

        //AÑADIR TODAS LAS FICHAS AL CARD GENERATOR
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

        if (STOPWATCH && GENERATE_ALL)
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

    public void StartGame() { UpdateGenerationCube(); tutorial = false; }
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
        string name = tile.gameObject.name + nameVariation;
        GameObject newTile = new GameObject(name);
        newTile.gameObject.tag = tile.gameObject.tag;
        newTile.SetActive(false);
        newTile.transform.parent = newTilesContainer.transform;
        // newTile.hideFlags = HideFlags.HideInHierarchy;

        MeshFilter meshFilter = newTile.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = tile.gameObject.GetComponent<MeshFilter>().sharedMesh;
        MeshRenderer meshRenderer = newTile.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterials = tile.gameObject.GetComponent<MeshRenderer>().sharedMaterials;

        Tile tileRotated = newTile.AddComponent<Tile>();
        tileRotated.tileType = tile.tileType;
        tileRotated.probability = tile.probability;
        tileRotated.positionOffset = tile.positionOffset;
        tileRotated.rotateRight = tile.rotateRight;
        tileRotated.rotate180 = tile.rotate180;
        tileRotated.rotateLeft = tile.rotateLeft;

        BoxCollider boxCollider = newTile.AddComponent<BoxCollider>();

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
                if (otherTile.downSocket.socket_name == tile.upSocket.socket_name
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursUp.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursDown.Contains(tile.tileType))))
                {
                    if (tile.upSocket.isSymmetric || otherTile.downSocket.isSymmetric
                    || (otherTile.downSocket.isFlipped && !tile.upSocket.isFlipped)
                    || (!otherTile.downSocket.isFlipped && tile.upSocket.isFlipped))
                        tile.upNeighbours.Add(otherTile);
                }
                // Down neighbours 
                if (otherTile.upSocket.socket_name == tile.downSocket.socket_name
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursDown.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursUp.Contains(tile.tileType))))
                {
                    if (otherTile.upSocket.isSymmetric || tile.downSocket.isSymmetric
                    || (otherTile.upSocket.isFlipped && !tile.downSocket.isFlipped)
                    || (!otherTile.upSocket.isFlipped && tile.downSocket.isFlipped))
                        tile.downNeighbours.Add(otherTile);
                }
                // Right neighbours 
                if (otherTile.leftSocket.socket_name == tile.rightSocket.socket_name
                && (!excludedNeighborConstraint || (!tile.excludedNeighboursRight.Contains(otherTile.tileType)
                && !otherTile.excludedNeighboursLeft.Contains(tile.tileType))))
                {
                    if (otherTile.leftSocket.isSymmetric || tile.rightSocket.isSymmetric
                    || (otherTile.leftSocket.isFlipped && !tile.rightSocket.isFlipped)
                    || (!otherTile.leftSocket.isFlipped && tile.rightSocket.isFlipped))
                        tile.rightNeighbours.Add(otherTile);
                }
                // Left neighbours 
                if (otherTile.rightSocket.socket_name == tile.leftSocket.socket_name
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
                if (otherTile.belowSocket.socket_name == tile.aboveSocket.socket_name)
                {
                    if ((otherTile.belowSocket.rotationallyInvariant
                        && tile.aboveSocket.rotationallyInvariant)
                        || (otherTile.belowSocket.rotationIndex == tile.aboveSocket.rotationIndex))
                        tile.aboveNeighbours.Add(otherTile);
                }

                // Above neighbours
                if (otherTile.aboveSocket.socket_name == tile.belowSocket.socket_name)
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

        //Then, save the neighbors for each cell
       /* if (OneTileCollapseOptimization)
        {
            // Crear un diccionario para acceso rápido
            Dictionary<Vector3Int, Cell> lookup = new Dictionary<Vector3Int, Cell>();
            foreach (Cell c in gridComponents)
                lookup[c.coords] = c;

            foreach (Cell cell in gridComponents)
            {
                cell.neighbors.Clear();

                // up (z+1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(0, 0, 1), out Cell up))
                    cell.neighbors[Direction.Up] = up;

                // down  (z-1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(0, 0, -1), out Cell down))
                    cell.neighbors[Direction.Down] = down;

                // right (x+1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(1, 0, 0), out Cell right))
                    cell.neighbors[Direction.Right] = right;

                // left (x-1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(-1, 0, 0), out Cell left))
                    cell.neighbors[Direction.Left] = left;

                // above (y+1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(0, 1, 0), out Cell above))
                    cell.neighbors[Direction.Above] = above;

                // below (y-1)
                if (lookup.TryGetValue(cell.coords + new Vector3Int(0, -1, 0), out Cell below))
                    cell.neighbors[Direction.Below] = below;
            }
        }*/

    }


    private void GetCenterCube()
    {
        //Primero, al contador de cells del cubo hay que sumarle las cells que ya se habían visitado (suelo y cielo)
        centerCubeCells += iterations;

        // El tamaño del cubo en X y Z es el dado por initialCubeSize
        int cubeSizeX = initialCubeSize;
        int cubeSizeZ = initialCubeSize;

        // El tamaño en Y es toda la altura menos cielo y suelo
        int cubeStartY = 1; // Excluye el suelo (y = 0)
        int cubeEndY = dimensionsY - 1; // Excluye el cielo (y = dimensionsY - 1)

        // Calcular el centro del cubo en X y Z
        int startX = (dimensionsX - cubeSizeX) / 2;
        int startZ = (dimensionsZ - cubeSizeZ) / 2;
        int endX = startX + cubeSizeX;
        int endZ = startZ + cubeSizeZ;

        // Añadir cells del cubo central a la nueva lista
        for (int y = cubeStartY; y < cubeEndY; y++)
        {
            for (int z = startZ; z < endZ; z++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);

                    if (index > gridComponents.Count - 1 || index < 0)
                    {
                        continue;
                    }

                    gridComponents[index].centerCubeCell = true;
                    centerCubeCells++;
                }
            }
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
                if (cellToCollapse.transform.childCount != 0)
                {
                    foreach (Transform child in cellToCollapse.transform)
                    {
                        Destroy(child.gameObject);
                    }
                }

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
                if (cellToCollapse.transform.childCount != 0)
                {
                    foreach (Transform child in cellToCollapse.transform)
                    {
                        Destroy(child.gameObject);
                    }
                }

                Tile instantiatedTile = Instantiate(emptyTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
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
    /// Define the borders of the map as "limit" to avoid strange borders
    /// </summary>
    void DefineMapLimits()
    {
        int y = 1; // justo encima del suelo (suelo es y=0)

        for (int z = 0; z < dimensionsZ; z++)
        {
            for (int x = 0; x < dimensionsX; x++)
            {
                // ¿es borde en X o Z?
                bool isBorder = (x == 0 || x == dimensionsX - 1 || z == 0 || z == dimensionsZ - 1);

                if (isBorder)
                {
                    int index = x + (z * dimensionsX) + (y * dimensionsX * dimensionsZ);
                    Cell cellToCollapse = gridComponents[index];

                    // Marcar como borde
                    cellToCollapse.tileOptions = new Tile[] { limitTile };
                    cellToCollapse.collapsed = true;

                    //Necesario para que los alrededores del limite sean visitables
                    GetNeighboursCloseToCollapsedCell(cellToCollapse);

                    // limpiar hijos previos
                    if (cellToCollapse.transform.childCount != 0)
                    {
                        foreach (Transform child in cellToCollapse.transform)
                        {
                            Destroy(child.gameObject);
                        }
                    }

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
                    GetNeighboursCloseToCollapsedCell(cellToCollapse);
                    cellToCollapse.tileOptions = new Tile[] { tile };
                    // limpiar hijos previos
                    if (cellToCollapse.transform.childCount != 0)
                    {
                        foreach (Transform child in cellToCollapse.transform)
                        {
                            Destroy(child.gameObject);
                        }
                    }
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
        List<Cell> tempGrid = new List<Cell>(gridComponents);

        tempGrid.RemoveAll(c => c.collapsed);
        if (cubeStep) tempGrid.RemoveAll(c => !c.centerCubeCell);


        if (tempGrid.Count == 0)
        {
            Debug.Log("No hay mas cells");
            yield break;
        }
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
        List<(Tile tile, int weight)> weightedTiles = cellToCollapse.tileOptions.Select(tile => (tile, tile.probability)).ToList();

        Tile selectedTile;
        if (probabilityConstraint)
        {
            selectedTile = ChooseTile(weightedTiles);
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

            // [BACKTRACKING] Manejar la incompatibilidad
            //HandleConflict();
            //return;
        }

        //--------Backtracking, save state-----------
        /* collapseHistory.Push(new CollapseRecord
         {
             cellIndex = cellToCollapse.index,
             previousOptions = (Tile[])cellToCollapse.tileOptions.Clone(),
             chosenTile = selectedTile
         });

         */
        //-------------------------------------------

        cellToCollapse.previousEntropy = cellToCollapse.tileOptions.Length;
        cellToCollapse.tileOptions = new Tile[] { selectedTile };
        //cellToCollapse.lastTriedTile = selectedTile;
        Tile foundTile = cellToCollapse.tileOptions[0];

        if (cellToCollapse.transform.childCount != 0)
        {
            foreach (Transform child in cellToCollapse.transform)
            {
                Destroy(child.gameObject);
            }
        }

        Tile instantiatedTile = Instantiate(foundTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
        if (instantiatedTile.rotation != Vector3.zero)
        {
            instantiatedTile.gameObject.transform.Rotate(foundTile.rotation, Space.Self);
        }

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);

        cellToCollapse.collapsed = true;
       // backtracks = 0; // Reset backtrack counter on successful collapse

        if (cubeStep) UpdateGenerationCube();
        else if (GENERATE_ALL) UpdateGeneration();
    }

   /* void HandleConflict()
    {
        backtracks++;

        if (collapseHistory.Count == 0 || backtracks == maxBacktracks)
        {
            backtracks = 0;
            Debug.Log("No hay decisiones para deshacer o se ha alcanzado el maximo de backtracks. Regenerando...");
            if (STOPWATCH) inc_counter++;
            if (!stopOnIncompatibility) Regenerate();
            return;
        }

        // Deshacemos la ÚLTIMA decisión
        CollapseRecord last = collapseHistory.Pop();
        Cell cell = gridComponents[last.cellIndex];

        Debug.Log($"[BACKTRACKING]: Revirtiendo celda {cell.index}");

        // Restaurar el dominio anterior
        cell.collapsed = false;
        cell.tileOptions = (Tile[])last.previousOptions.Clone();

        // Borrar instanciados
        foreach (Transform child in cell.transform)
            GameObject.Destroy(child.gameObject);
        iterations--;

        // Quitar de las opciones el tile que falló
        cell.tileOptions = cell.tileOptions.Where(t => t != last.chosenTile).ToArray();

        // Si ya no quedan opciones, seguir retrocediendo
        if (cell.tileOptions.Length == 0)
        {
            Debug.LogWarning("Celda sin opciones: seguimos retrocediendo...");
            HandleConflict();    // Recursivo: retrocede más
            return;
        }

        // Restaurar vecinos indirectamente propagará restricciones
        UpdateGeneration();

    }
    
    private void UncollapseCell(int index)
    {
        if (backtrackStack.ContainsKey(index) && backtrackStack[index].Count > 0)
        {
            backtracks++;
            iterations--;
            if (backtracks == maxBacktracks) return;

            CellSnapshot snapshot = backtrackStack[index].Pop();
            Cell cell = gridComponents[index];

            // Restaurar dominio (si existe snapshot)
            if (snapshot.tileOptions != null)
                cell.tileOptions = (Tile[])snapshot.tileOptions.Clone();

            // Forzar que NO esté colapsada después del rollback
            cell.collapsed = false;

            // Eliminar visual (si existe)
            if (cell.transform.childCount != 0)
            {
                foreach (Transform child in cell.transform)
                {
                    Destroy(child.gameObject);
                }
            }

            // Marcar para re-procesado
            cell.visitable = true;
            cell.haSidoVisitado = false;
        }
        else
        {
            
        }
    }*/


    /// <summary>
    /// Makes the neighbours wiithin a given distance og the collapsed cell visitable for optimization purposes
    /// (not always looking at every cell)
    /// </summary>
    /// <param name="cell"></param> Collapsed cell
    private void GetNeighboursCloseToCollapsedCell(Cell cell)
    {
        int up, down, left, right, above, below;
        up = cell.index + dimensionsX;
        down = cell.index - dimensionsX;
        left = cell.index - 1;
        right = cell.index + 1;
        above = cell.index + (dimensionsX * dimensionsZ);
        below = cell.index - (dimensionsX * dimensionsZ);

        cell.visitable = true;

        // Verificar que los índices están en rango antes de acceder a gridComponents
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

        // Calcular diagonales 2D solo si están dentro de rango
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
    /// Chooses a tile based on the weights of the tiles
    /// </summary>
    /// <param name="weightedTiles"></param> List of tiles with their corresponding weights
    /// <returns></returns> The chosen tile
    Tile ChooseTile(List<(Tile tile, int weight)> weightedTiles)
    {
        // Calculate the total weight
        int totalWeight = weightedTiles.Sum(item => item.weight);

        // Generate a random number between 0 and totalWeight - 1
        System.Random random = new System.Random();
        int randomNumber = random.Next(0, totalWeight);

        // Iterate through the tiles and find the one corresponding to the random number
        foreach (var (tile, weight) in weightedTiles)
        {
            if (randomNumber < weight) return tile;
            randomNumber -= weight;
        }
        return null; // This should not happen if the list is not empty
    }

    Tile ChooseRandomTile(List<Tile> tiles)
    {
        System.Random random = new System.Random();
        int randomNumber = random.Next(0, tiles.Count - 1);

        Tile t = tiles[randomNumber];

        if (t != null) return t;

        return null; // This should not happen if the list is not empty
    }

    /// <summary>
    /// Updates all the cells in the grid
    /// </summary>
    void UpdateGenerationCube()
    {
        List<Cell> newGenerationCell = new List<Cell>(gridComponents);

        for (int y = 0; y < dimensionsY; y++)
        {
            for (int z = 0; z < dimensionsZ; z++)
            {
                for (int x = 0; x < dimensionsX; x++)
                {
                    CheckNeighbours(x, y, z, ref newGenerationCell);

                }
            }
        }

        gridComponents = newGenerationCell;

        iterations++;

        if (iterations <= centerCubeCells)
        {
            StartCoroutine(CheckEntropy());
        }

        else
        {
            print("END");
            cubeStep = false;

            // stopwatch.Stop();
            //print($"Generation time: {stopwatch.Elapsed.TotalSeconds} ms");

        }
    }

    void UpdateGeneration()
    {
        foreach (Cell cell in gridComponents)
        {
            cell.haSidoVisitado = false;
        }

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

        StartCoroutine(UpdateGlobalValidTilesNextFrame());

        if (iterations <= (dimensionsX * dimensionsY * dimensionsZ) && GENERATE_ALL)
        {
            StartCoroutine(CheckEntropy());
        }

        else if (STOPWATCH && GENERATE_ALL)
        {
            if(onEndGeneration != null)
            {
                onEndGeneration();
            }
        }
    }

    void CollapseCellWithOneTileOption(List<Cell> newGenerationCell, int index)
    {

        Cell cellToCollapse = newGenerationCell[index];
        
        /* if (cellToCollapse.neighbors.TryGetValue(Direction.Up, out Cell up))
         {
             Debug.Log($"CELL {index}. Neighbor UP: {up.index}");
         }*/

       
        // Make the neighbours of the collapsed cell visitable for optimization purposes
        GetNeighboursCloseToCollapsedCell(cellToCollapse);

        Tile foundTile = cellToCollapse.tileOptions[0];

        //--------Backtracking, save state-----------
        /*collapseHistory.Push(new CollapseRecord
        {
            cellIndex = cellToCollapse.index,
            previousOptions = (Tile[])cellToCollapse.tileOptions.Clone(),
            chosenTile = foundTile
        });

        cellToCollapse.lastTriedTile = foundTile;*/
        //-------------------------------------------


        if (cellToCollapse.transform.childCount != 0)
        {
            foreach (Transform child in cellToCollapse.transform)
            {
                Destroy(child.gameObject);
            }
        }

        Tile instantiatedTile = Instantiate(foundTile, cellToCollapse.transform.position, Quaternion.identity, cellToCollapse.transform);
        if (instantiatedTile.rotation != Vector3.zero)
        {
            instantiatedTile.gameObject.transform.Rotate(foundTile.rotation, Space.Self);
        }

        instantiatedTile.gameObject.transform.position += instantiatedTile.positionOffset;
        instantiatedTile.gameObject.SetActive(true);
        iterations++;
        cellToCollapse.collapsed = true;
    }


    //ESTE METODO PERMITE TENER UNA LISTA GLOBAL DE TILES VALIDAS EN TODO EL MAPA PARA PODER SACAR EN CARDGENERATOR SOLO TILES VALIDAS (que pueden colocarse en al menos 1 celda)

    private IEnumerator UpdateGlobalValidTilesNextFrame()
    {
        yield return null; // Espera 1 frame por seguridad
        UpdateGlobalValidTiles();
    }

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
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[down].tileOptions)
                {
                    var valid = possibleOptions.upNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                //  Debug.Log($"Down Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
            }
            // Checks the right cell
            if (x < dimensionsX - 1)
            {
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[right].tileOptions)
                {
                    var valid = possibleOptions.leftNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                // Debug.Log($"Right Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
            }
            // Checks the up cell
            if (z < dimensionsZ - 1)
            {
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[up].tileOptions)
                {
                    var valid = possibleOptions.downNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                // Debug.Log($"Up Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
            }
            // Checks the left cell
            if (x > 0)
            {
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[left].tileOptions)
                {
                    var valid = possibleOptions.rightNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                // Debug.Log($"Left Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
            }
            // Checks the cell below
            if (y > 0)
            {
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[below].tileOptions)
                {
                    var valid = possibleOptions.aboveNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                // Debug.Log($"Below Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
            }
            // Checks the cell above
            if (y < dimensionsY - 1)
            {
                List<Tile> validOptions = new List<Tile>();
                foreach (Tile possibleOptions in gridComponents[above].tileOptions)
                {
                    var valid = possibleOptions.belowNeighbours;
                    validOptions = validOptions.Concat(valid).ToList();
                }
                // Debug.Log($"Above Valid Options for Cell[{index}]: {string.Join(", ", validOptions.Select(o => o.tileType))}");
                CheckValidity(options, validOptions, index);
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
        void CheckValidity(List<Tile> optionList, List<Tile> validOption, int indexCell)
        {
            HashSet<Tile> validSet = new HashSet<Tile>(validOption);

            var optionCopy = optionList.ToList(); // Copia para evitar modificar la original mientras iteramos

            optionList.Clear(); // Limpia la lista original antes de llenarla con los válidos

            foreach (var option in optionCopy)
            {
                if (validSet.Contains(option) && option.tileType != "limit")
                {
                    optionList.Add(option); // Solo añadimos los válidos
                }
            }
        }
    }


    //-----------------TILE EVENTS-------------------

    private void OnTileDrag(Tile draggedTile)
    {
        actualTileDragged = draggedTile.gameObject;
        //Cuando el jugador escoge una tile, tenemos que mostrar sólo las celdas donde puede encajar
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
        GameObject tileRemoved = tile.gameObject;
        actualTileDragged = null;
        Cell cellToCollapse = closest;
        if (cellToCollapse == null)
        {
            Debug.Log("No hay celdas!");

            Destroy(tileRemoved);
            UpdateGeneration();
            return;
        }


        cellToCollapse.collapsed = true;

        // Make the neighbours of the collapsed cell visitable for optimization purposes
        GetNeighboursCloseToCollapsedCell(cellToCollapse);

        Tile selectedTile = tileRemoved.GetComponent<Tile>();

        if (selectedTile is null)
        {
            Debug.LogError("NO TILE!");
            return;
        }

        cellToCollapse.tileOptions = new Tile[] { selectedTile };
        Tile foundTile = cellToCollapse.tileOptions[0];

        if (cellToCollapse.transform.childCount != 0)
        {
            foreach (Transform child in cellToCollapse.transform)
            {
                Destroy(child.gameObject);
            }
        }

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
        instantiatedTile.transform.DOJump(instantiatedTile.transform.position, jumpPower: 0.5f, numJumps: 1, duration: 0.3f).SetEase(Ease.InOutFlash);


        foreach (Cell cell in validCells)
        {
            cell.MakeVisible(false);
        }

        Destroy(tileRemoved);

        placedTiles++;

        placedTilesText.text = "Fichas: " + placedTiles.ToString();

        UpdateGeneration();
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

    int CheckCellPoints(Cell collapsedCell)
    {
        return -1;
    }



    Cell FindClosestCell(GameObject origin, List<Cell> cells)
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

        Init();
    }
}