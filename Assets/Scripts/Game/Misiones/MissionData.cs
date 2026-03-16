using UnityEngine;

//Parametros especiales de mision
public enum MissionType { PlaceTile, TilesTogether}

[CreateAssetMenu(fileName = "MissionData", menuName = "Scriptable Objects/MissionData")]
public class MissionData : ScriptableObject
{
    public MissionType type;
    public int missionID;
    public string missionName; //Nombre interno de la mision
    public string missionDescription; //Nombre visible de la mision
    public int givenPoints;

    // Parámetros específicos (pueden dejarse vacíos si no se usan)
    public string[] targetTileTypes;
    public int amountRequired;
}
