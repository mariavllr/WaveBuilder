using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using System;
using UnityEngine.Rendering;

public class CardGenerator : MonoBehaviour
{
    [SerializeField] public List<Tile> tilesList;
    public Queue<Tile> tileQueue;
    public int queueSize;
    public float distance;
    private float offset = 0;
    private bool isDragging = false;
    public float dragCooldown = 0.3f;
    public float timerCooldown = 0;

    private LocalKeyword SelectableKeyword;

    public static event Action<Vector3, Tile> OnTileRotated;

    private void Start()
    {
        tileQueue = new Queue<Tile>();
        InicializeTileQueue();
    }

    private void OnEnable()
    {
        DragObject.OnTileDragged += OnTileDragged;
        DragObject.OnTileReleased += OnTileRemoved;
        DeleteTile.OnDeleteTile += OnDeleteTile;
    }

    private void OnDestroy()
    {
        DragObject.OnTileReleased -= OnTileRemoved; 
        DragObject.OnTileDragged -= OnTileDragged;
        DeleteTile.OnDeleteTile -= OnDeleteTile; 
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
            Tile tile = EnqueueTile();          
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
            SelectableKeyword = new LocalKeyword(mat.shader, "_SELECTABLE");
            if(!selectable) mat.SetKeyword(SelectableKeyword, false);
            else mat.SetKeyword(SelectableKeyword, true);
        }
    }

    private Tile GetRandomTile()
    {
        //De manera random completamente
        //return tilesList[Random.Range(0, tilesList.Count)];

        //Con pesos
        // Choose a tile for that cell
        List<(Tile tile, int weight)> weightedTiles = tilesList.Select(tile => (tile, tile.probability)).ToList();
        return ChooseTile(weightedTiles);
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

    private Tile EnqueueTile()
    {
        Tile tileToEnqueue = GetRandomTile();
        tileToEnqueue.gameObject.SetActive(true);

        // Si la cola no está vacía, colocar la nueva tile debajo de la última
        Vector3 newTilePosition;
        if (tileQueue.Count > 0)
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

        tileQueue.Enqueue(instantiatedTile);

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

   private void OnTileRemoved(GameObject removedTile, Cell cell)
    {
        isDragging = false;
        tileQueue.Dequeue();

        MoveUpQueue();
        EnqueueTile();
        tileQueue.First().gameObject.AddComponent<DragObject>();
    }

    //Cuando rote, queremos que busque su tile rotada en la tile list. Siempre rotará +90 grados.
    private void RotateTile()
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



        OnTileRotated?.Invoke(actualTile.rotation, actualTile);
    }

    private void OnDeleteTile()
    {
        isDragging = false;
        tileQueue.Dequeue();

        MoveUpQueue();
        EnqueueTile();
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
