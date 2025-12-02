using System;
using UnityEngine;

public static class GameEvents
{
    public static event Action<Tile> OnTileDragged;
    public static event Action<Tile, Cell> OnTileReleased;
    public static event Action OnDeleteTile;
    public static event Action<Vector3, Tile> OnTileRotated;
    public static event Action<MissionData> OnMissionCompleted;

    public static void TileDragged(Tile tile)
    {
        OnTileDragged?.Invoke(tile);
    }

    public static void TileReleased(Tile tile, Cell cell)
    {
        OnTileReleased?.Invoke(tile, cell);
    }

    public static void DeleteTile()
    {
        OnDeleteTile?.Invoke();
    }

    public static void TileRotated(Vector3 rotation, Tile tile)
    {
        OnTileRotated?.Invoke(rotation, tile);
    }

    public static void MissionCompleted(MissionData data)
    {
        OnMissionCompleted?.Invoke(data);
    }

}