using UnityEngine;

[CreateAssetMenu(fileName = "MissionData", menuName = "Scriptable Objects/MissionData")]
public class MissionData : ScriptableObject
{
    public int missionID;
    public string missionName; //Nombre interno de la mision
    public string missionDescription; //Nombre visible de la mision
    public int givenPoints;
}
