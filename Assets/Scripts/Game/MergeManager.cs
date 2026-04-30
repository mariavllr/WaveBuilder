using System.Collections.Generic;
using UnityEngine;

public class MergeManager : MonoBehaviour
{
    public static MergeManager Instance;

    [Header("Referencias")]
    public WaveFunctionGame_REFACTOR wfcGame;

    [Header("Configuración de Fusión")]
    public string targetTileType = "campfire";
    public int requiredAmount = 3;
    public Tile villageTilePrefab; 

    private void Awake()
    {
        Instance = this;
        GameEvents.OnTileReleased += CheckForMerge; //debe ocurrir antes, por eso el awake
    }

    private void OnDisable()
    {
        GameEvents.OnTileReleased -= CheckForMerge;
    }

    private void CheckForMerge(Tile placedTile, Cell placedCell)
    {
        if (placedTile.tileType != targetTileType) return;

        List<Cell> cluster = GetConnectedCluster(placedCell, targetTileType);


        if (cluster.Count >= requiredAmount)
        {
            Debug.Log($"¡Fusión activada! Has juntado {cluster.Count} tiendas.");
            wfcGame.skipEntireTileRemoved = true; // evita UpdateGeneration de OnTileRemoved
            ExecuteMerge(cluster, placedCell); 
        }
    }

    // --- MÉTODO DE FUSIÓN ---
    private void ExecuteMerge(List<Cell> cluster, Cell placedCell)
    {
        foreach (Cell cell in cluster)
        {
            if (cell != placedCell)
            {
                wfcGame.ResetCell(cell);
            }
        }

        wfcGame.ForcePlaceTile(placedCell, villageTilePrefab);
    }

    /// <summary>
    /// Algoritmo BFS para encontrar todas las celdas conectadas del mismo tipo
    /// </summary>
    private List<Cell> GetConnectedCluster(Cell startCell, string typeToMatch)
    {
        List<Cell> cluster = new List<Cell>();
        Queue<Cell> cellsToReivew = new Queue<Cell>();
        HashSet<Cell> visitedCells = new HashSet<Cell>();

        cellsToReivew.Enqueue(startCell);
        visitedCells.Add(startCell);

        while (cellsToReivew.Count > 0)
        {
            Cell currentCell = cellsToReivew.Dequeue();
            cluster.Add(currentCell);

            // Obtenemos sus vecinos directos (Norte, Sur, Este, Oeste)
            List<Cell> neighbors = GetAdjacentCells(currentCell);

            foreach (Cell neighbor in neighbors)
            {
                if (visitedCells.Contains(neighbor) || !neighbor.collapsed) continue;
                Tile neighborTile = neighbor.GetComponentInChildren<Tile>();
                if (neighborTile != null && neighborTile.tileType == typeToMatch)
                {
                    visitedCells.Add(neighbor);
                    cellsToReivew.Enqueue(neighbor);
                }
            }
        }

        return cluster;
    }

    /// <summary>
    /// Reutiliza las matemáticas rápidas de tu grid para sacar los vecinos directos (sin diagonales)
    /// </summary>
    private List<Cell> GetAdjacentCells(Cell centerCell)
    {
        List<Cell> validNeighbors = new List<Cell>(4);
        int index = centerCell.index;
        int dimX = wfcGame.dimensionsX;
        int dimZ = wfcGame.dimensionsZ;
        var grid = wfcGame.gridComponents;

        // Eje X (Izquierda / Derecha)
        if (index % dimX != 0) validNeighbors.Add(grid[index - 1]);
        if ((index + 1) % dimX != 0) validNeighbors.Add(grid[index + 1]);

        // Eje Z (Abajo / Arriba)
        if ((index / dimX) % dimZ != 0) validNeighbors.Add(grid[index - dimX]);
        if ((index / dimX) % dimZ != dimZ - 1) validNeighbors.Add(grid[index + dimX]);

        return validNeighbors;
    }
}