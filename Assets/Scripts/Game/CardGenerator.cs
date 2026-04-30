using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Rendering;
using static CameraControl;
using Random = UnityEngine.Random;

public class CardGenerator : MonoBehaviour
{
    private WaveFunctionGame_REFACTOR wfc;
    [SerializeField] public List<Tile> tilesList;
    public Queue<Tile> tileQueue;
    public int queueSize;
    public float distance;
    private bool isDragging = false;
    public float dragCooldown = 0.3f;
    public float timerCooldown = 0;
    private int numberOfGeneratedTiles = 0;

    private LocalKeyword SelectableKeyword;

    [Header("UI 3D Settings")]
    public Camera mainCamera;
    [Tooltip("Posicion en pantalla (0 a 1). X=0.85 es a la derecha, Y=0.8 es arriba")]
    public Vector2 screenAnchor = new Vector2(0.85f, 0.8f);
    [Tooltip("Distancia hacia adelante desde la camara para que no la atraviese")]
    public float distanceFromCamera = 20f;

    private void Start()
    {
        if (mainCamera == null) mainCamera = Camera.main;

        // Actualizamos la posicion justo antes de generar la cola por primera vez
        UpdateScreenPosition();

        tileQueue = new Queue<Tile>();
        wfc = FindAnyObjectByType<WaveFunctionGame_REFACTOR>();
        InicializeTileQueue();
    }

    private void OnEnable()
    {
        GameEvents.OnTileDragged += OnTileDragged;
        GameEvents.OnTileReleased += OnTileRemoved;
        GameEvents.OnDeleteTile += OnDeleteTile;
        WaveFunctionGame_REFACTOR.onRegenerate += OnMapRegenerated;
        CameraControl.onCameraRotated += OnCameraRotated;
    }

    private void OnDisable()
    {
        GameEvents.OnTileDragged -= OnTileDragged;
        GameEvents.OnTileReleased -= OnTileRemoved;
        GameEvents.OnDeleteTile -= OnDeleteTile;
        WaveFunctionGame_REFACTOR.onRegenerate -= OnMapRegenerated;
        CameraControl.onCameraRotated -= OnCameraRotated;
    }

    private void OnDestroy()
    {
        GameEvents.OnTileReleased -= OnTileRemoved; 
        GameEvents.OnTileDragged -= OnTileDragged;
        GameEvents.OnDeleteTile -= OnDeleteTile;
        WaveFunctionGame_REFACTOR.onRegenerate -= OnMapRegenerated;
        CameraControl.onCameraRotated -= OnCameraRotated;
    }


    private void Update()
    {
        if (timerCooldown > 0) timerCooldown -= Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Space) && isDragging)
        {
            RotateTile();
        }
    }

    //Rehacer cola si cambia el mapa
    private void OnMapRegenerated()
    {
        isDragging = false;
        timerCooldown = 0;

        while (tileQueue.Count > 0)
        {
            Tile t = tileQueue.Dequeue();
            if (t != null) Destroy(t.gameObject);
        }

        numberOfGeneratedTiles = 0;
        InicializeTileQueue();
    }

    //------POSICION EN PANTALLA---------
    private void LateUpdate()
    {
        UpdateScreenPosition();
    }


    private void UpdateScreenPosition()
    {
        if (mainCamera == null) return;

        // 1. Mantiene el CardGenerator fijo en la esquina derecha de la pantalla
        Vector3 targetPos = mainCamera.ViewportToWorldPoint(new Vector3(screenAnchor.x, screenAnchor.y, distanceFromCamera));
        transform.position = targetPos;

        // 2. Rota el CardGenerator igual que la camara en el eje Y.
        // Esto hace que las tiles parezcan estaticas (no giran cuando giras el mundo).
        transform.rotation = Quaternion.Euler(0, mainCamera.transform.eulerAngles.y, 0);
    }



    //------COLA DE TILES--------
    private void InicializeTileQueue()
    {  
        for (int i = 0; i < queueSize; i++)
        {
            Tile tile = EnqueueTile(tileQueue);          
        }

        Tile first = tileQueue.First();
        first.gameObject.AddComponent<DragObject>();
        MakeTileSelectable(true, first);
    }

    private void MakeTileSelectable(bool selectable, Tile tile)
    {
        // Incluye el renderer principal y los de las skirts (hijas)
        // El 'true' incluye también renderers en objetos inactivos
        Renderer[] renderers = tile.GetComponentsInChildren<Renderer>(true);

        foreach (Renderer rend in renderers)
        {
            // .materials clona el array -> instancias únicas para ESTA tile.
            // No afecta al asset compartido ni a otras tiles de la escena.
            Material[] materials = rend.materials;

            foreach (Material mat in materials)
            {
                try
                {
                    LocalKeyword keyword = new LocalKeyword(mat.shader, "_SELECTABLE");
                    mat.SetKeyword(keyword, selectable);
                }
                catch (Exception)
                {
                    throw;
                }               
            }
        }
    }

    private Tile GetRandomTile()
    {
        // Primera tanda: pesos por tipo sobre la lista completa
        if (numberOfGeneratedTiles < queueSize)
        {
            numberOfGeneratedTiles++;
            return wfc.ChooseTile(tilesList);
        }

        // Filtrado por tiles válidas actualmente en el mapa
        List<Tile> validForNow = tilesList
            .Where(tile => wfc.globalValidTiles.Contains((tile.tileType, tile.rotation)))
            .ToList();

        // Fallback si no hay ninguna válida
        if (validForNow.Count == 0)
        {
            Debug.LogError("[CARD GENERATOR] NO TILES: No hay tiles válidas actualmente. Eligiendo una aleatoria ponderada...");
            return wfc.ChooseTile(tilesList);
        }

        return wfc.ChooseTile(validForNow);
    }


    private Tile EnqueueTile(Queue<Tile> queue, Tile specificTile = null)
    {
        Tile tileToEnqueue;
        //Caso 1: Tile random
        if(specificTile == null)
        {
            tileToEnqueue = GetRandomTile();
        }

        //Caso 2: tile especifica
        else
        {
            tileToEnqueue = specificTile;
        }

        tileToEnqueue.gameObject.SetActive(true);

        // Si la cola no esta vacia, colocar la nueva tile debajo de la ultima
        Vector3 newTilePosition;
        if (queue.Count > 0)
        {
            Tile lastTile = tileQueue.Last();
            newTilePosition = lastTile.transform.position - new Vector3(0, distance, 0);
        }
        else
        {
            // Si la cola esta vacia, colocarla en la posicion base
            newTilePosition = transform.position;
        }

        Tile instantiatedTile = Instantiate(tileToEnqueue, newTilePosition, Quaternion.identity, transform);
        if (instantiatedTile.rotation != Vector3.zero)
        {
            instantiatedTile.gameObject.transform.Rotate(instantiatedTile.rotation, Space.Self);
        }

        queue.Enqueue(instantiatedTile);

        //EFECTO REBOTE
        float delayBetweenBounces = 0.1f;
        int index = 0;

        foreach (Tile tile in tileQueue)
        {
            float delay = index * delayBetweenBounces;

            tile.transform
                .DOJump(tile.transform.position, jumpPower: 0.25f, numJumps: 1, duration: 0.3f)
                .SetEase(Ease.InOutFlash)
                .SetDelay(delay);

            index++;
        }

        MakeTileSelectable(false, instantiatedTile);
        return instantiatedTile;
    }

    private void MoveUpQueue()
    {

        foreach (Tile tile in tileQueue)
        {
            tile.transform.position += new Vector3(0, distance, 0);
        }

        MakeTileSelectable(true, tileQueue.First());
    }

    private void OnTileDragged(Tile tile)
    {
        isDragging = true;
        timerCooldown = dragCooldown;
    }


    //-----------LOGICA PARA QUE SIEMPRE SALGAN TILES POSIBLES DE COLOCAR---------
    public void ValidateFirstTile()
    {
        if (tileQueue.Count == 0) return;

        Tile tile = tileQueue.First();

        bool stillValid = wfc.gridComponents
            .Any(cell => !cell.collapsed && cell.visitable &&
                         cell.tileOptions.Any(opt =>
                             opt.tileType == tile.tileType)); //LA ROTACION NO IMPORTA PARA SABER SI ES VALIDA O NO, el jugador puede rotarla

        if (!stillValid)
        {
            Debug.Log("[CARD GENERATOR] REEMPLAZAR PRIMERA: La primera tile ya no es válida, reemplazando...");
            ReplaceFirstTile();
        }
    }

    public void ReplaceFirstTile()
    {
        if (tileQueue.Count == 0) return;

        Tile oldTile = tileQueue.First();

        //Animacion de la tile antigua (se encoge antes de ser destruida)
        oldTile.transform
            .DOScale(Vector3.zero, 0.5f)
            .SetEase(Ease.InBack)
            .OnComplete(() =>
            {
                Destroy(oldTile.gameObject);

                //Crear una nueva cola temporal
                Queue<Tile> newQueue = new Queue<Tile>();

                //Crear nueva tile valida
                Tile newTile = GetRandomTile();
                newTile.gameObject.SetActive(true);
                Debug.Log(newTile.name);

                Tile instantiatedTile = Instantiate(newTile, transform.position, Quaternion.identity, transform);
                if (instantiatedTile.rotation != Vector3.zero)
                {
                    instantiatedTile.gameObject.transform.Rotate(instantiatedTile.rotation, Space.Self);
                }

                //Añadir que pueda ser arrastrada
                instantiatedTile.gameObject.AddComponent<DragObject>();

                //Efecto rebote de aparicion
                instantiatedTile.transform.localScale = Vector3.zero;
                instantiatedTile.transform
                    .DOScale(1.2f, 0.35f)
                    .SetEase(Ease.OutBack)
                    .OnComplete(() =>
                    {
                        instantiatedTile.transform.DOScale(1f, 0.15f);
                    });

                newQueue.Enqueue(instantiatedTile);
                MakeTileSelectable(true, instantiatedTile);

                //Meter las otras dos
                tileQueue.Dequeue();
                newQueue.Enqueue(tileQueue.First());

                tileQueue.Dequeue();
                newQueue.Enqueue(tileQueue.First());

                //Sustituimos la cola original
                tileQueue = newQueue;
            });
    }


    //------------EVENTOS-----------

    private void OnTileRemoved(Tile removedTile, Cell cell)
    {
        isDragging = false;
        tileQueue.Dequeue();

        MoveUpQueue();
        EnqueueTile(tileQueue);
        tileQueue.First().gameObject.AddComponent<DragObject>();

        ValidateFirstTile();
    }

    //Cuando rote, queremos que busque su tile rotada en la tile list. Siempre rotara +90 grados.
    public void RotateTile()
    {
        Tile actualTile = tileQueue.First();
        string tileName = actualTile.name;

        //Dividimos entre el nombre de la tile y su rotacion
        string currentTileType = actualTile.tileType;
        float currentRotation = actualTile.rotation.y;


        // Calcular nueva rotacion
        float newRotation = (currentRotation + 90) % 360;

        //Solucion bug donde tiles que solo necesitan una rotacion (de 0 a 90) da error al intentar rotar 180 o 270. Ejemplo: path
        if(actualTile.rotateRight && !actualTile.rotate180 && !actualTile.rotateLeft)
        {
            if (newRotation == 180) newRotation = 0;
            else if(newRotation == 270) newRotation = 90;
        }

        // Buscar la nueva tile en la lista
        Tile newTile = tilesList.Find(tile => tile.tileType == currentTileType && tile.rotation.y == newRotation);

        if (newTile != null)
        {
            //Ya tenemos la tile rotada. Hay que sustituirla
            actualTile.name = newTile.name;
            actualTile.tileType = newTile.tileType;
            actualTile.probability = newTile.probability;
            actualTile.rotation = newTile.rotation;

            actualTile.upNeighbours = newTile.upNeighbours;
            actualTile.rightNeighbours = newTile.rightNeighbours;
            actualTile.downNeighbours = newTile.downNeighbours;
            actualTile.leftNeighbours = newTile.leftNeighbours;
            actualTile.aboveNeighbours = newTile.aboveNeighbours;
            actualTile.belowNeighbours = newTile.belowNeighbours;

            actualTile.gameObject.transform.Rotate(new Vector3(0, 90, 0), Space.Self);

        }
        else
        {
            Debug.LogError($"ROTATING TILE: Tile with name {tileName} and rotation {newRotation} not found.");
        }

        GameEvents.TileRotated(actualTile.rotation, actualTile);
    }

    private void OnDeleteTile()
    {
        isDragging = false;
        tileQueue.Dequeue();

        MoveUpQueue();
        EnqueueTile(tileQueue);
        tileQueue.First().gameObject.AddComponent<DragObject>();
    }

    //---------------------------------CAMARA---------------------------------

    /// <summary>
    /// Rota los datos lógicos de una tile a su variante equivalente, sin tocar
    /// su transform. Usado para compensar giros de cámara y mantener coherencia
    /// entre lo que el jugador ve en la mano y cómo se coloca la ficha.
    /// </summary>
    private void RotateTileLogicOnly(Tile actualTile, int steps)
    {
        steps = ((steps % 4) + 4) % 4;
        if (steps == 0) return;

        float currentRotation = actualTile.rotation.y;
        float newRotation = (currentRotation + 90f * steps) % 360f;

        // Mismo ajuste que en RotateTile para tiles con rotaciones limitadas
        if (actualTile.rotateRight && !actualTile.rotate180 && !actualTile.rotateLeft)
        {
            if (newRotation == 180) newRotation = 0;
            else if (newRotation == 270) newRotation = 90;
        }

        Tile newTile = tilesList.Find(t => t.tileType == actualTile.tileType && t.rotation.y == newRotation);

        if (newTile == null)
        {
            Debug.LogError($"[CAMERA ROTATION] No se encontró variante {actualTile.tileType} con rotación {newRotation}");
            return;
        }

        // Sustituir datos lógicos SIN tocar transform
        actualTile.name = newTile.name;
        actualTile.tileType = newTile.tileType;
        actualTile.probability = newTile.probability;
        actualTile.rotation = newTile.rotation;
        actualTile.upNeighbours = newTile.upNeighbours;
        actualTile.rightNeighbours = newTile.rightNeighbours;
        actualTile.downNeighbours = newTile.downNeighbours;
        actualTile.leftNeighbours = newTile.leftNeighbours;
        actualTile.aboveNeighbours = newTile.aboveNeighbours;
        actualTile.belowNeighbours = newTile.belowNeighbours;
    }

    /// <summary>
    /// Respuesta al giro de cámara: actualiza lógicamente todas las tiles de la cola
    /// y reemite TileRotated si hay drag activo para que se refresquen celdas válidas.
    /// </summary>
    private void OnCameraRotated(int steps)
    {
        foreach (Tile tile in tileQueue)
        {
            RotateTileLogicOnly(tile, steps);
        }

        if (isDragging && tileQueue.Count > 0)
        {
            Tile first = tileQueue.First();
            GameEvents.TileRotated(first.rotation, first);
        }
    }

    //DEBUG
    private void PrintStack()
    {
        print("PRINTING QUEUE:");
        foreach (Tile tile in tileQueue)
        {
            print(tile.name);
        }
    }
}
