using System.Collections.Generic;
using UnityEngine;

//Esta estructura define los bonus que pueden tener las fichas al colocarse junto a otras específicas
[System.Serializable]
public struct AdjacencyBonus
{
    public string targetTileType; // Ej: "aserradero"
    public int bonusPoints;
}

[CreateAssetMenu(fileName = "ScoreData_", menuName = "Scriptable Objects/TileScoreData")]
//Esta clase define los puntos base que da cada tile por el hecho de ser colocada
public class TileScoreData : ScriptableObject
{
    public string tileType;       // Ej: "tree"
    public int basePoints;        // Ej: 1

    // Lista de sinergias que tiene esta ficha
    public List<AdjacencyBonus> adjacencyBonuses;
}