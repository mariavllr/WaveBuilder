using UnityEngine;

public class Mission_PlaceTileType : Mission
{
    private string targetTile;

    public Mission_PlaceTileType(MissionData data, string targetTile) : base(data)
    {
        this.targetTile = targetTile;
    }

    public override void StartListening()
    {
        GameEvents.OnTileReleased += OnTilePlaced;
    }

    public override void StopListening()
    {
        GameEvents.OnTileReleased -= OnTilePlaced;
    }

    private void OnTilePlaced(Tile placedTile, Cell cell)
    {
        if (placedTile.tileType == targetTile)
        {
            Complete();
        }
    }
}
