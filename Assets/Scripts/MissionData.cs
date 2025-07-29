using UnityEngine;

[CreateAssetMenu(fileName = "MissionData", menuName = "Scriptable Objects/MissionData")]
public class MissionData : ScriptableObject
{
    public int missionID;
    public string missionName;
    public int givenPoints;
}
