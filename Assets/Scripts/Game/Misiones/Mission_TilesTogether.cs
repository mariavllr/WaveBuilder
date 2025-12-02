using System.Collections.Generic;
using System.Linq;
using UnityEngine;

//This mission requires the player to place a certain number of specific tiles adjacent to each other.
public class Mission_TilesTogether : Mission
{
    private List<Vector2Int> tiles = new List<Vector2Int>();
    private int tilesRequired;
    private string[] tileTypes;

    public Mission_TilesTogether(MissionData data, string[] tileTypes, int tilesRequired) : base(data)
    {
        this.tileTypes = tileTypes;
        this.tilesRequired = tilesRequired;
    }

    public override void StartListening()
    {
        GameEvents.OnTileReleased += OnTilePlaced;
    }

    public override void StopListening()
    {
        GameEvents.OnTileReleased -= OnTilePlaced;
    }

    private void OnTilePlaced(Tile tile, Cell cell)
    {
        if(!tileTypes.Contains(tile.tileType))
            return;

        tiles.Add(new Vector2Int(cell.coords.x, cell.coords.z));
        Debug.Log("Added tile " + tile.tileType + " at " + cell.coords);

        if (CheckForCluster())
            Complete();
    }

    private bool CheckForCluster()
    {
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>();

        foreach (var tile in tiles)
        {
            int connected = CountConnected(tile, visited);
            if (connected >= tilesRequired)
                return true;
        }

        return false;
    }

    private int CountConnected(Vector2Int start, HashSet<Vector2Int> visited)
    {
        Stack<Vector2Int> stack = new Stack<Vector2Int>();
        HashSet<Vector2Int> localVisited = new HashSet<Vector2Int>();

        stack.Push(start);
        localVisited.Add(start);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            foreach (var neighbor in GetNeighbors(current))
            {
                if (tiles.Contains(neighbor) && !localVisited.Contains(neighbor))
                {
                    stack.Push(neighbor);
                    localVisited.Add(neighbor);
                }
            }
        }

        return localVisited.Count;
    }

    private List<Vector2Int> GetNeighbors(Vector2Int pos)
    {
        return new List<Vector2Int>()
        {
            pos + Vector2Int.up,
            pos + Vector2Int.down,
            pos + Vector2Int.left,
            pos + Vector2Int.right
        };
    }
}

