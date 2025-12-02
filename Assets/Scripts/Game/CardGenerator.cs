using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using System;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;
using System.Collections;

public class CardGenerator : MonoBehaviour
{
    private WaveFunctionGame wfc;
    [SerializeField] public List<Tile> tilesList;
    public Queue<Tile> tileQueue;
    public int queueSize;
    public float distance;
    private float offset = 0;
    private bool isDragging = false;
    public float dragCooldown = 0.3f;
    public float timerCooldown = 0;
    private int numberOfGeneratedTiles = 0;

    private LocalKeyword SelectableKeyword;


    private void Start()
    {
        tileQueue = new Queue<Tile>();
        wfc = FindAnyObjectByType<WaveFunctionGame>();
        InicializeTileQueue();
    }

    private void OnEnable()
    {
        GameEvents.OnTileDragged += OnTileDragged;
        GameEvents.OnTileReleased += OnTileRemoved;
        GameEvents.OnDeleteTile += OnDeleteTile;
    }

    private void OnDestroy()
    {
        GameEvents.OnTileReleased -= OnTileRemoved; 
        GameEvents.OnTileDragged -= OnTileDragged;
        GameEvents.OnDeleteTile -= OnDeleteTile; 
    }


    private void Update()
    {
        if (timerCooldown > 0) timerCooldown -= Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Space) && isDragging)
        {
            RotateTile();
        }
    }

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
        Material[] materials = tile.GetComponent<MeshRenderer>().materials;

        foreach (Material mat in materials)
        {
            //SelectableKeyword = new LocalKeyword(mat.shader, "_SELECTABLE");
            //if(!selectable) mat.SetKeyword(SelectableKeyword, false);
            //else mat.SetKeyword(SelectableKeyword, true);
        }
    }

    private Tile GetRandomTile()
    {
        //en la primera tanda que salgan full random
        if (numberOfGeneratedTiles < queueSize)
        {
            List<(Tile tile, int weight)> weightedTiles = tilesList.Select(tile => (tile, tile.probability)).ToList();
            numberOfGeneratedTiles++;
            return ChooseTile(weightedTiles);
        }

        else
        {
        //Filtrado por las tiles válidas actualmente en el mapa
                List<Tile> validForNow = tilesList
                 .Where(tile => wfc.globalValidTiles.Contains((tile.tileType, tile.rotation)))
                 .ToList();

            if (validForNow.Count == 0)
            {
                    Debug.LogError("[CARD GENERATOR] NO TILES: No hay tiles válidas actualmente. Eligiendo una aleatoria...");
                    List<(Tile tile, int weight)> weightedTiles = tilesList.Select(tile => (tile, tile.probability)).ToList();
                    return ChooseTile(weightedTiles);
            }

            return validForNow[Random.Range(0, validForNow.Count)];
        }

        
        //De manera random completamente
        //return tilesList[Random.Range(0, tilesList.Count)];

        //Con pesos
        // Choose a tile for that cell
      //  List<(Tile tile, int weight)> weightedTiles = validForNow.Select(tile => (tile, tile.probability)).ToList();
      //  return ChooseTile(weightedTiles);

        
    }

 

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

        // Si la cola no está vacía, colocar la nueva tile debajo de la última
        Vector3 newTilePosition;
        if (queue.Count > 0)
        {
            Tile lastTile = tileQueue.Last();
            newTilePosition = lastTile.transform.position - new Vector3(0, distance, 0);
        }
        else
        {
            // Si la cola está vacía, colocarla en la posición base
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
                             opt.tileType == tile.tileType)); //LA ROTACIÓN NO IMPORTA PARA SABER SI ES VÁLIDA O NO, el jugador puede rotarla

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

        //Animación de la tile antigua (se encoge antes de ser destruida)
        oldTile.transform
            .DOScale(Vector3.zero, 0.5f)
            .SetEase(Ease.InBack)
            .OnComplete(() =>
            {
                Destroy(oldTile.gameObject);

                //Crear una nueva cola temporal
                Queue<Tile> newQueue = new Queue<Tile>();

                //Crear nueva tile válida
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

                //Efecto rebote de aparición
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

    //Cuando rote, queremos que busque su tile rotada en la tile list. Siempre rotará +90 grados.
    public void RotateTile()
    {
        Tile actualTile = tileQueue.First();
        string tileName = actualTile.name;

        //Dividimos entre el nombre de la tile y su rotacion
        string currentTileType = actualTile.tileType;
        float currentRotation = actualTile.rotation.y;


        // Calcular nueva rotación
        float newRotation = (currentRotation + 90) % 360;

        //Solución bug donde tiles que solo necesitan una rotacion (de 0 a 90) da error al intentar rotar 180 o 270. Ejemplo: path
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
